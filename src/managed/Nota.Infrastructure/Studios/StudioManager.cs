// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Two studios in one app (AGENT DAW P2): My Studio, a normal editable engine that is never touched by anything the shared
// master does, and Shared Studio, a second engine holding the approved master behind a read-only view. Only the active
// studio owns the audio device. Switching changes who plays; it never reloads or resets either project, so My Studio's
// unsaved edits and undo history survive a round trip. All members run on the engine's UI thread, like the engines.

using System;
using System.Diagnostics;
using Nota.Application;

namespace Nota.Infrastructure.Studios;

/// <summary>Where the last switch spent its time: applying a queued master, stopping the old studio's audio, starting the new one's.</summary>
public sealed record SwitchTiming(TimeSpan ApplyPending, TimeSpan StopOld, TimeSpan StartNew);

public sealed class StudioManager : IStudioHost, IDisposable
{
    private readonly IAudioEngine _my;
    private readonly IAudioEngine _sharedRaw;
    private readonly ICollaborationPort _collab;
    private (string ManifestJson, string BundleDir)? _pending;
    private bool _disposed;

    public StudioManager(Func<IAudioEngine> engineFactory, ICollaborationPort collab)
    {
        _my = engineFactory();
        _sharedRaw = engineFactory();
        Shared = ReadOnlyEngineProxy.Wrap(_sharedRaw);
        _collab = collab;
    }

    /// <summary>The producer's own project: fully editable, never changed by the shared master.</summary>
    public IAudioEngine My => _my;
    /// <summary>The approved master, read-only: listening and moving the playhead are allowed, edits throw <see cref="StudioReadOnlyException"/>.</summary>
    public IAudioEngine Shared { get; }
    public Studio Active { get; private set; } = Studio.My;
    public bool HasPendingSharedUpdate => _pending is not null;
    /// <summary>Breakdown of the most recent switch, for diagnosing a slow one.</summary>
    public SwitchTiming? LastSwitch { get; private set; }

    /// <summary>The studio that currently owns the audio device. Throws if the device cannot be opened (no output available).</summary>
    public IAudioEngine ActiveEngine => Active == Studio.My ? _my : Shared;

    /// <summary>Hands the audio device to <paramref name="target"/>: the other studio stops its audio thread first, so two
    /// engines never fight over the device. Returns how long that took. A queued master update is applied on the way in.</summary>
    public TimeSpan SwitchTo(Studio target, bool startAudio = true)
    {
        var sw = Stopwatch.StartNew();
        if (target == Active && (!startAudio || ActiveEngine.IsRunning)) return sw.Elapsed;
        var from = Active == Studio.My ? _my : _sharedRaw;
        var to = target == Studio.My ? _my : _sharedRaw;
        if (target == Studio.Shared) TryApplyPendingCore();   // entering Shared is a safe point: it is not playing yet
        var t0 = sw.Elapsed;
        if (from.IsRunning) { from.StopTransport(); from.Stop(); }
        var t1 = sw.Elapsed;
        Active = target;
        if (startAudio && !to.IsRunning) to.Start();
        LastSwitch = new SwitchTiming(t0, t1 - t0, sw.Elapsed - t1);
        return sw.Elapsed;
    }

    /// <summary>
    /// A new master arrived. Applied now if Shared Studio is not playing; otherwise queued so the music a person is
    /// listening to is never replaced mid-bar, and applied by <see cref="TryApplyPending"/> or the next switch into Shared.
    /// </summary>
    public SharedUpdateResult ApplySharedMaster(string manifestJson, string bundleDir)
    {
        var check = _collab.Check(manifestJson, bundleDir);
        if (!check.Ok) return new SharedUpdateResult(false, false, check.Errors.Concat(check.MissingBlobs.Select(m => $"missing asset {m}")).Concat(check.CorruptBlobs.Select(c => $"corrupt asset {c}")).ToArray(), check.Warnings, 0);
        if (_sharedRaw.IsPlaying)
        {
            _pending = (manifestJson, bundleDir);
            return new SharedUpdateResult(false, true, Array.Empty<string>(), check.Warnings, check.Tracks);
        }
        return Apply(manifestJson, bundleDir);
    }

    /// <summary>Applies a queued master update if Shared Studio has stopped. Returns null when there is nothing to do or it is still playing.</summary>
    public SharedUpdateResult? TryApplyPending() => TryApplyPendingCore();

    private SharedUpdateResult? TryApplyPendingCore()
    {
        if (_pending is null || _sharedRaw.IsPlaying) return null;
        var (json, dir) = _pending.Value;
        _pending = null;
        return Apply(json, dir);
    }

    private SharedUpdateResult Apply(string manifestJson, string bundleDir)
    {
        var r = _collab.Apply(_sharedRaw, manifestJson, bundleDir);
        return new SharedUpdateResult(r.Applied, false, r.Errors, r.Warnings, r.Tracks);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_my.IsRunning) _my.Stop(); } catch (Exception) { }
        try { if (_sharedRaw.IsRunning) _sharedRaw.Stop(); } catch (Exception) { }
        _my.Dispose();
        _sharedRaw.Dispose();
    }
}
