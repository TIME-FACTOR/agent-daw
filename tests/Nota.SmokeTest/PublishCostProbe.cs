// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Cost of one structural edit (a publish) on a large project, so a change to the publish path
// (stable ids, AGENT DAW P0) can be compared with a build that lacks it. Uses only API both
// builds have. Alone: `dotnet run --project tests/Nota.SmokeTest -- --publish-cost`.

using System.Diagnostics;
using Nota.Infrastructure;

namespace Nota.SmokeTest;

internal static class PublishCostProbe
{
    public static void Run()
    {
        foreach (var (tracks, clipsPer) in new[] { (20, 10), (100, 20), (300, 30) })
        {
            using var e = new NotaEngine();
            e.SetBpm(120);
            var ids = new List<int>();
            for (int t = 0; t < tracks; t++)
            {
                int id = e.AddInstrumentTrack(); ids.Add(id);
                for (int c = 0; c < clipsPer; c++) e.AddMidiClip(id, c * 8, 4);
            }
            for (int i = 0; i < 20; i++) e.SetClipName(ids[0], 0, "warm" + i); // warm up
            const int N = 200;
            var samples = new double[N];
            for (int i = 0; i < N; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                e.SetClipName(ids[i % ids.Count], i % clipsPer, "n" + i);
                samples[i] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }
            Array.Sort(samples);
            Console.WriteLine($"PUBLISH-COST {tracks} tracks x {clipsPer} clips ({tracks * clipsPer} clips): median {samples[N / 2]:F3} ms, p95 {samples[(int)(N * 0.95)]:F3} ms, max {samples[N - 1]:F3} ms");
        }
    }
}
