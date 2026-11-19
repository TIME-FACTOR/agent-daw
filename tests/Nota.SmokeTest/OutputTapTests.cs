// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// The master output tap against the REAL engine (AGENT DAW spike S2): a known tone played through the live device
// path comes out of the tap. Alone: `--output-tap`. The ring itself is unit tested in tests/native/OutputTapTest.cpp.

using Nota.Infrastructure;

namespace Nota.SmokeTest;

internal static class OutputTapTests
{
    public static IEnumerable<(bool ok, string label)> Run()
    {
        var results = new List<(bool, string)>();
        string wav = Path.Combine(Path.GetTempPath(), "nota_tap_sine_330.wav");
        WavWriter.WriteSine(wav, seconds: 4.0, freq: 330, sampleRate: 44100);
        using var e = new NotaEngine();
        e.SetBpm(120);
        results.Add((e.OutputTapCapacityFrames >= 65536, $"the tap reports its capacity ({e.OutputTapCapacityFrames} frames)"));

        // Open the device first, as the app does, so the clip is loaded at the rate the device actually runs at.
        bool started = true;
        try { e.Start(); } catch (NotaEngineException) { started = false; }
        if (!started || !e.IsRunning)
        {
            Console.WriteLine("  [SKIP] live tap check: no audio output device could be opened here");
            foreach (var r in results) yield return r;
            yield break;
        }
        double sr = e.SampleRate;
        int t = e.AddAudioTrack();
        e.AddAudioClip(t, wav, 0.0);
        var buf = new float[4096 * 2];
        var l = new List<float>(); var r2 = new List<float>();
        void Drain() { int n; while ((n = e.ReadOutputTap(buf, 4096)) > 0) for (int i = 0; i < n; i++) { l.Add(buf[2 * i]); r2.Add(buf[2 * i + 1]); } }

        // Nothing is delivered until the tap is enabled.
        e.Play();
        Thread.Sleep(150);
        results.Add((e.ReadOutputTap(buf, 4096) == 0, "a tap that was never enabled delivers nothing"));
        e.StopTransport(); e.Seek(0);
        Thread.Sleep(100);

        e.EnableOutputTap(true);
        e.Play();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500) { Drain(); Thread.Sleep(10); }
        e.StopTransport();
        Thread.Sleep(100);
        Drain();
        e.EnableOutputTap(false);

        int total = l.Count;
        // Measure from the first audible sample: the transport takes a moment to start, and counting that silence would read low.
        int first = l.FindIndex(v => Math.Abs(v) > 0.01f);
        var seg = first >= 0 ? l.Skip(first).ToList() : new List<float>();
        bool finite = l.All(float.IsFinite) && r2.All(float.IsFinite);
        float peak = seg.Count > 0 ? seg.Max(Math.Abs) : 0;
        // Period from the first to the last rising zero crossing, so leading and trailing silence cannot bias it.
        var rising = new List<int>(); for (int i = 1; i < seg.Count; i++) if (seg[i - 1] < 0 && seg[i] >= 0) rising.Add(i);
        double hz = rising.Count > 2 ? (rising.Count - 1) / ((rising[^1] - rising[0]) / sr) : 0;
        // Continuity: while the tone plays, successive crossings are one period (about 145 frames) apart. A glitch or dropout would show as a long gap.
        int maxGap = 0; for (int i = 1; i < rising.Count; i++) maxGap = Math.Max(maxGap, rising[i] - rising[i - 1]);
        double periodFrames = sr / 330.0;
        results.Add((total > sr * 0.8, $"the tap delivered {total} frames ({total / sr:F2} s of audio) in a 1.5 s window"));
        results.Add((finite && peak > 0.05f && peak <= 1.0f, $"the audio is finite and audible (peak {peak:F3})"));
        results.Add((Math.Abs(hz - 330) < 1.0, $"it is the tone that was played: {hz:F2} Hz (expected 330)"));
        results.Add((rising.Count > 100 && maxGap <= periodFrames * 1.5, $"the stream is continuous: the longest gap between crossings is {maxGap} frames (one period is {periodFrames:F0}), so no dropouts or glitches"));
        results.Add((e.OutputTapDropped == 0, $"a consumer that keeps up loses nothing (dropped {e.OutputTapDropped})"));

        // A consumer that stops reading makes the audio thread drop and count, not block.
        e.EnableOutputTap(true);
        e.Seek(0); e.Play();
        Thread.Sleep((int)(1000.0 * e.OutputTapCapacityFrames / sr) + 1500);   // longer than the ring holds
        ulong dropped = e.OutputTapDropped;
        bool stillRunning = e.IsRunning && e.IsPlaying;
        e.StopTransport();
        results.Add((dropped > 0 && stillRunning, $"a consumer that never reads makes the audio thread drop and count ({dropped} frames) without stopping playback"));
        e.EnableOutputTap(true);
        results.Add((e.OutputTapDropped == 0 && e.ReadOutputTap(buf, 4096) == 0, "enabling again discards the backlog and the counter"));
        e.Stop();
        foreach (var r in results) yield return r;
    }
}
