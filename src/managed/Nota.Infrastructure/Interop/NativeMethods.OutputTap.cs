// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

using System.Runtime.InteropServices;

namespace Nota.Infrastructure;

/// <summary>The rendered master mix for a host program (AGENT DAW Listen Together). See nota_engine.h, "Output tap".</summary>
internal static partial class NativeMethods
{
    [LibraryImport(Lib, EntryPoint = "nota_output_tap_enable")]
    internal static partial NotaResult OutputTapEnable(IntPtr engine, int enabled);

    [LibraryImport(Lib, EntryPoint = "nota_output_tap_read")]
    internal static partial int OutputTapRead(IntPtr engine, [Out] float[] outInterleavedStereo, int maxFrames);

    [LibraryImport(Lib, EntryPoint = "nota_output_tap_dropped")]
    internal static partial ulong OutputTapDropped(IntPtr engine);

    [LibraryImport(Lib, EntryPoint = "nota_output_tap_capacity_frames")]
    internal static partial int OutputTapCapacityFrames(IntPtr engine);
}
