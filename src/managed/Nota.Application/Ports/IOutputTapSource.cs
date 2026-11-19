// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// The master-bus output tap as a port (AGENT DAW Listen Together): what a local streaming endpoint needs from the engine.

namespace Nota.Application;

public interface IOutputTapSource
{
    /// <summary>Turning it on discards buffered audio and zeroes <see cref="OutputTapDropped"/>. One consumer thread only.</summary>
    void EnableOutputTap(bool enabled);
    /// <summary>Copies up to <paramref name="maxFrames"/> stereo interleaved float32 frames of the live mix; returns the frames copied.</summary>
    int ReadOutputTap(float[] interleavedStereo, int maxFrames);
    /// <summary>Frames the audio thread discarded because the consumer fell behind.</summary>
    ulong OutputTapDropped { get; }
    double SampleRate { get; }
}
