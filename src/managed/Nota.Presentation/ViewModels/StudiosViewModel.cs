// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// The My Studio / Shared Studio switch (AGENT DAW P2) as observable state and commands. A view binds to this; it knows nothing
// about how the studios are hosted. Switching can fail (no audio device): the studio stays where it was and `Error` says why.

using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nota.Application;
using Nota.Application.Localization;

namespace Nota.Presentation;

public partial class StudiosViewModel(IStudioHost host) : ObservableObject
{
    private readonly IStudioHost _host = host;

    [ObservableProperty] private Studio _active = host.Active;
    [ObservableProperty] private bool _hasPendingUpdate = host.HasPendingSharedUpdate;
    /// <summary>Why the last switch did not happen; empty when it did.</summary>
    [ObservableProperty] private string _error = "";

    public bool IsShared => Active == Studio.Shared;
    public string Title => IsShared ? Loc.T("studios.shared") : Loc.T("studios.my");
    /// <summary>Shown while in Shared Studio, where edits are not possible.</summary>
    public string Banner => IsShared
        ? Loc.T("studios.banner_shared")
        : "";
    public string PendingText => HasPendingUpdate ? Loc.T("studios.pending_update") : "";

    partial void OnActiveChanged(Studio value) { OnPropertyChanged(nameof(IsShared)); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Banner)); }
    partial void OnHasPendingUpdateChanged(bool value) => OnPropertyChanged(nameof(PendingText));

    [RelayCommand(CanExecute = nameof(CanSwitchToShared))]
    private void SwitchToShared() => Switch(Studio.Shared);
    private bool CanSwitchToShared() => Active != Studio.Shared;

    [RelayCommand(CanExecute = nameof(CanSwitchToMy))]
    private void SwitchToMy() => Switch(Studio.My);
    private bool CanSwitchToMy() => Active != Studio.My;

    private void Switch(Studio target)
    {
        try
        {
            _host.SwitchTo(target);
            Error = "";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Error = Loc.F("studios.switch_failed", target == Studio.Shared ? Loc.T("studios.shared") : Loc.T("studios.my"), e.Message);
        }
        Refresh();
    }

    /// <summary>Re-reads the host (a master update arrived, or playback stopped and a queued one was applied).</summary>
    public void Refresh()
    {
        Active = _host.Active;
        HasPendingUpdate = _host.HasPendingSharedUpdate;
        SwitchToSharedCommand.NotifyCanExecuteChanged();
        SwitchToMyCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Call when Shared Studio's transport stops: applies a queued master and updates the indicator.</summary>
    public SharedUpdateResult? OnSharedStopped()
    {
        var r = _host.TryApplyPending();
        Refresh();
        return r;
    }
}
