// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

namespace Nota.Infrastructure;

public sealed partial class NotaEngine
{
    // --- Stable identities (AGENT DAW P0) -----------------------------------

    private static string ReadUid(System.Func<byte[], int, int> fill)
    {
        var buf = new byte[40];
        int len = fill(buf, buf.Length);
        return len <= 0 ? "" : System.Text.Encoding.ASCII.GetString(buf, 0, System.Math.Min(len, 32));
    }

    /// <summary>The track's stable id: 32 lowercase hex chars ("" for an unknown track).</summary>
    public string GetTrackUid(int trackId)
    { ThrowIfDisposed(); return ReadUid((b, c) => NativeMethods.TrackGetUid(_handle, trackId, b, c)); }

    /// <summary>Restores a saved track id (no undo entry). False for an unknown track or a malformed id.</summary>
    public bool SetTrackUid(int trackId, string uidHex)
    { ThrowIfDisposed(); return NativeMethods.TrackSetUid(_handle, trackId, uidHex ?? "") == NativeMethods.NotaResult.Ok; }

    /// <summary>The clip's stable id: 32 lowercase hex chars ("" for an unknown clip). Session clips use
    /// <c>SessionClip.Index(scene)</c> like every other clip call.</summary>
    public string GetClipUid(int trackId, int clipIndex)
    { ThrowIfDisposed(); return ReadUid((b, c) => NativeMethods.ClipGetUid(_handle, trackId, clipIndex, b, c)); }

    /// <summary>Restores a saved clip id (no undo entry). False for an unknown clip or a malformed id.</summary>
    public bool SetClipUid(int trackId, int clipIndex, string uidHex)
    { ThrowIfDisposed(); return NativeMethods.ClipSetUid(_handle, trackId, clipIndex, uidHex ?? "") == NativeMethods.NotaResult.Ok; }
}
