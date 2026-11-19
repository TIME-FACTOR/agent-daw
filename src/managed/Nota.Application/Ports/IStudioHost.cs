// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Two studios in one app (AGENT DAW P2) as a port, so the presentation layer can drive the switch without knowing how
// the studios are hosted.

namespace Nota.Application;

public enum Studio { My, Shared }

public sealed record SharedUpdateResult(bool Applied, bool Queued, string[] Errors, string[] Warnings, int Tracks);

public interface IStudioHost
{
    Studio Active { get; }
    bool HasPendingSharedUpdate { get; }
    /// <summary>Hands the audio device to <paramref name="target"/>. Throws if the device cannot be opened.</summary>
    TimeSpan SwitchTo(Studio target, bool startAudio = true);
    SharedUpdateResult? TryApplyPending();
}
