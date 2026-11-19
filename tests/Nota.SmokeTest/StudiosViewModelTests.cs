// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// The My Studio / Shared Studio switch view-model (AGENT DAW P2). Deterministic: a fake host, no audio device. Alone: `--studios-vm`.

using Nota.Application;
using Nota.Presentation;

namespace Nota.SmokeTest;

internal static class StudiosViewModelTests
{
    private sealed class FakeHost : IStudioHost
    {
        public Studio Active { get; set; } = Studio.My;
        public bool HasPendingSharedUpdate { get; set; }
        public Exception? FailWith { get; set; }
        public int Switches;
        public TimeSpan SwitchTo(Studio target, bool startAudio = true)
        {
            if (FailWith is not null) throw FailWith;
            Switches++; Active = target;
            return TimeSpan.Zero;
        }
        public SharedUpdateResult? TryApplyPending()
        {
            if (!HasPendingSharedUpdate) return null;
            HasPendingSharedUpdate = false;
            return new SharedUpdateResult(true, false, Array.Empty<string>(), Array.Empty<string>(), 4);
        }
    }

    public static IEnumerable<(bool ok, string label)> Run()
    {
        var host = new FakeHost();
        var vm = new StudiosViewModel(host);
        var changed = new List<string>(); vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        int canExecuteEvents = 0; vm.SwitchToSharedCommand.CanExecuteChanged += (_, _) => canExecuteEvents++;

        yield return (vm.Active == Studio.My && !vm.IsShared && vm.Title == "My Studio" && vm.Banner == "" && vm.Error == "", "starts in My Studio with no banner and no error");
        yield return (vm.SwitchToSharedCommand.CanExecute(null) && !vm.SwitchToMyCommand.CanExecute(null), "you can switch to the other studio but not to the one you are in");

        vm.SwitchToSharedCommand.Execute(null);
        yield return (host.Active == Studio.Shared && vm.Active == Studio.Shared && vm.IsShared && vm.Title == "Shared Studio" && vm.Banner.Contains("proposal"),
            "switching to Shared Studio hands the device over and shows the read-only banner that points to proposals");
        yield return (changed.Contains(nameof(vm.IsShared)) && changed.Contains(nameof(vm.Title)) && changed.Contains(nameof(vm.Banner)) && canExecuteEvents > 0,
            "the view is told about every derived property and about the commands' availability");
        yield return (!vm.SwitchToSharedCommand.CanExecute(null) && vm.SwitchToMyCommand.CanExecute(null), "the commands swap availability after a switch");

        vm.SwitchToMyCommand.Execute(null);
        yield return (host.Active == Studio.My && vm.Banner == "" && host.Switches == 2, "switching back clears the banner");

        // a failure leaves the studio where it was and explains why; the next success clears the message
        host.FailWith = new InvalidOperationException("no output device");
        vm.SwitchToSharedCommand.Execute(null);
        yield return (vm.Active == Studio.My && host.Active == Studio.My && vm.Error.Contains("Shared Studio") && vm.Error.Contains("no output device"), "a failed switch stays put and says why");
        host.FailWith = null;
        vm.SwitchToSharedCommand.Execute(null);
        yield return (vm.Active == Studio.Shared && vm.Error == "", "the next successful switch clears the error");

        // a master that arrived while someone was listening
        host.HasPendingSharedUpdate = true;
        vm.Refresh();
        yield return (vm.HasPendingUpdate && vm.PendingText.Contains("newer master"), "a queued master update is shown");
        var applied = vm.OnSharedStopped();
        yield return (applied is { Applied: true, Tracks: 4 } && !vm.HasPendingUpdate && vm.PendingText == "", "when Shared Studio stops, the queued master is applied and the notice goes away");
        yield return (vm.OnSharedStopped() is null, "stopping with nothing queued does nothing");
    }
}
