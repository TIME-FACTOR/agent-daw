// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Stable ids for tracks and clips on the way in and out of a project file (AGENT DAW P0).
//
// The engine owns the ids (Uid.h): it mints one for every new track and clip, keeps it through
// edits and undo, and re-mints copies. A project file records them as `id` (32 lowercase hex).
//
// Compatibility, with no format-version bump:
//   * A build that predates ids keeps the field in the DTOs' [JsonExtensionData] and writes it back
//     unchanged, so ids survive a round trip through an older Nota.
//   * A file with no ids (saved by an older Nota) gets them here, DERIVED from the file's own content
//     rather than random. Two people who open the same old file get the same ids, so their first
//     saves still line up for a merge. A file that already has ids never goes through derivation.
//   * A malformed or repeated id is dropped to derivation / re-minted by the engine; it never fails a load.

using System;
using System.Security.Cryptography;
using System.Text;
using Nota.Application;

namespace Nota.Infrastructure;

public static class ObjectIds
{
    /// <summary>True for exactly 32 lowercase hex digits and not all zero.</summary>
    public static bool IsValid(string? id)
    {
        if (id is null || id.Length != 32) return false;
        bool nonZero = false;
        foreach (char c in id)
        {
            if (!(c is >= '0' and <= '9' || c is >= 'a' and <= 'f')) return false;
            if (c != '0') nonZero = true;
        }
        return nonZero;
    }

    /// <summary>An id derived from <paramref name="key"/>: SHA-256 truncated to 128 bits and stamped as an
    /// RFC 4122 version-5 style id. Pure: the same key gives the same id on every machine.</summary>
    public static string Derive(string key)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes("nota-legacy-id:v1|" + key));
        h[6] = (byte)((h[6] & 0x0F) | 0x50);
        h[8] = (byte)((h[8] & 0x3F) | 0x80);
        return Convert.ToHexString(h.AsSpan(0, 16)).ToLowerInvariant();
    }

    /// <summary>The id a track without a saved one gets: its place, type and name in the file.</summary>
    public static string LegacyTrackId(int docIndex, int type, string? name)
        => Derive($"track|{docIndex}|{type}|{name ?? ""}");

    /// <summary>The id a clip without a saved one gets: its track's id, its kind (a = audio, m = MIDI,
    /// s = Session), its place in the track's list and its start. <paramref name="start"/> is formatted with
    /// the invariant culture's round-trip format so it is identical on every machine.</summary>
    public static string LegacyClipId(string trackId, char kind, int index, double start, string? name)
        => Derive($"clip|{trackId}|{kind}|{index}|{start.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}|{name ?? ""}");

    /// <summary>Sets the track's id from the file (<paramref name="saved"/>) or derives one. Returns the id
    /// now in force: the engine's, which wins if it refused the saved one.</summary>
    public static string ApplyTrack(IAudioEngine engine, int trackId, string? saved, int docIndex, int type, string? name)
    {
        string want = IsValid(saved) ? saved! : LegacyTrackId(docIndex, type, name);
        engine.SetTrackUid(trackId, want);
        return engine.GetTrackUid(trackId) is { Length: > 0 } now ? now : want;
    }

    /// <summary>Sets a clip's id from the file or derives one from its track and place.</summary>
    public static void ApplyClip(IAudioEngine engine, int trackId, int clipIndex, string? saved,
                                 string trackUid, char kind, int index, double start, string? name)
    {
        string want = IsValid(saved) ? saved! : LegacyClipId(trackUid, kind, index, start, name);
        engine.SetClipUid(trackId, clipIndex, want);
    }

    /// <summary>The id to write for a track or clip, or null if the engine has none (older engine).</summary>
    public static string? ForSave(string uid) => IsValid(uid) ? uid : null;
}
