// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.

namespace Nota.Infrastructure;

public sealed partial class NotaEngine
{
    // --- Output tap (AGENT DAW Listen Together) ---------------------------------------------------------

    /// <summary>Turns the master output tap on or off. Turning it on discards buffered audio and zeroes <see cref="OutputTapDropped"/>.
    /// One consumer thread only; the audio thread never waits for it.</summary>
    public void EnableOutputTap(bool enabled)
    { ThrowIfDisposed(); NativeMethods.OutputTapEnable(_handle, enabled ? 1 : 0); }

    /// <summary>Copies up to <paramref name="maxFrames"/> frames of the live mix (stereo interleaved float32, at the engine's sample rate)
    /// into <paramref name="interleavedStereo"/> (which must hold 2 * maxFrames floats). Returns the frames copied; 0 means nothing new.</summary>
    public int ReadOutputTap(float[] interleavedStereo, int maxFrames)
    {
        ThrowIfDisposed();
        if (interleavedStereo is null || maxFrames <= 0 || interleavedStereo.Length < 2L * maxFrames) return 0;
        return NativeMethods.OutputTapRead(_handle, interleavedStereo, maxFrames);
    }

    /// <summary>Frames the audio thread discarded because the consumer fell behind. A rise means a gap in the stream.</summary>
    public ulong OutputTapDropped { get { ThrowIfDisposed(); return NativeMethods.OutputTapDropped(_handle); } }

    /// <summary>How many frames the tap can buffer (about 2.7 s at 48 kHz).</summary>
    public int OutputTapCapacityFrames { get { ThrowIfDisposed(); return NativeMethods.OutputTapCapacityFrames(_handle); } }
}
