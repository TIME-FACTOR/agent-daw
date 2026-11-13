// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System.Runtime.InteropServices;

namespace Nota.Infrastructure;

/// <summary>Stable track and clip identities (AGENT DAW P0). See nota_engine.h, "Stable identities".</summary>
internal static partial class NativeMethods
{
    [LibraryImport(Lib, EntryPoint = "nota_track_get_uid")]
    internal static partial int TrackGetUid(IntPtr engine, int trackId, [Out] byte[] outBuf, int cap);

    [LibraryImport(Lib, EntryPoint = "nota_track_set_uid", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial NotaResult TrackSetUid(IntPtr engine, int trackId, string uidHex);

    [LibraryImport(Lib, EntryPoint = "nota_clip_get_uid")]
    internal static partial int ClipGetUid(IntPtr engine, int trackId, int clipIndex, [Out] byte[] outBuf, int cap);

    [LibraryImport(Lib, EntryPoint = "nota_clip_set_uid", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial NotaResult ClipSetUid(IntPtr engine, int trackId, int clipIndex, string uidHex);
}
