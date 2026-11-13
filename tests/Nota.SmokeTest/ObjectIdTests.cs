// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Stable ids for tracks and clips (AGENT DAW P0): minted by the engine, kept through edits and
// undo, re-minted for copies, written to the project file, and derived deterministically for a
// file that has none. Also runnable alone: `dotnet run --project tests/Nota.SmokeTest -- --object-ids`.

using Nota.Application;
using Nota.Infrastructure;

namespace Nota.SmokeTest;

internal static class ObjectIdTests
{
    public static IEnumerable<(bool ok, string label)> Run()
    {
        // --- pure helpers -------------------------------------------------------------
        yield return (ObjectIds.IsValid("0123456789abcdef0123456789abcdef"), "a 32-digit lowercase hex id is valid");
        yield return (!ObjectIds.IsValid(null) && !ObjectIds.IsValid("") && !ObjectIds.IsValid(new string('0', 32))
                      && !ObjectIds.IsValid("0123456789ABCDEF0123456789ABCDEF") && !ObjectIds.IsValid(new string('a', 31)),
            "null, empty, all-zero, upper-case and short ids are invalid");
        yield return (ObjectIds.Derive("x") == ObjectIds.Derive("x") && ObjectIds.Derive("x") != ObjectIds.Derive("y")
                      && ObjectIds.IsValid(ObjectIds.Derive("x")),
            "Derive is pure and its output is a valid id");

        // --- engine mints, keeps, re-mints ---------------------------------------------
        using (var e = new NotaEngine())
        {
            e.SetBpm(120);
            int a = e.AddInstrumentTrack();
            int b = e.AddInstrumentTrack();
            string ua = e.GetTrackUid(a), ub = e.GetTrackUid(b);
            yield return (ObjectIds.IsValid(ua) && ObjectIds.IsValid(ub) && ua != ub, "every new track has its own valid id");
            yield return (e.GetTrackUid(9999) == "", "an unknown track has no id");

            int c0 = e.AddMidiClip(a, 0, 4), c1 = e.AddMidiClip(a, 8, 4);
            string u0 = e.GetClipUid(a, c0), u1 = e.GetClipUid(a, c1);
            yield return (ObjectIds.IsValid(u0) && ObjectIds.IsValid(u1) && u0 != u1, "every new clip has its own valid id");

            e.SetClipName(a, c0, "Verse");
            e.SetClipNotes(a, c0, new[] { new NotaNote(60, 0.0, 1.0, 1.0f) });
            e.SetTrackName(a, "Lead");
            yield return (e.GetClipUid(a, c0) == u0 && e.GetTrackUid(a) == ua, "renaming and editing notes keeps ids");

            // Duplicate and split make new objects.
            int dup = e.DuplicateClip(a, c0);
            string ud = dup >= 0 ? e.GetClipUid(a, dup) : "";
            yield return (dup >= 0 && ObjectIds.IsValid(ud) && ud != u0 && ud != u1 && e.GetClipUid(a, c0) == u0,
                "a duplicated clip gets a new id and the original keeps its own");

            // Clip indexes shift after the duplicate above, so find the clip by its id, not its old index.
            int c1Now = -1;
            e.TryGetTrackInfo(0, out var preSplit);
            for (int ci = 0; ci < preSplit.ClipCount; ci++) if (e.GetClipUid(a, ci) == u1) c1Now = ci;
            int right = c1Now >= 0 ? e.SplitClip(a, c1Now, 10.0) : -1;
            yield return (c1Now >= 0 && right >= 0 && e.GetClipUid(a, c1Now) == u1 && ObjectIds.IsValid(e.GetClipUid(a, right))
                          && e.GetClipUid(a, right) != u1,
                "a split keeps the id on the left half and gives the right half a new one");

            int dtrack = e.DuplicateTrack(a);
            yield return (dtrack > 0 && ObjectIds.IsValid(e.GetTrackUid(dtrack)) && e.GetTrackUid(dtrack) != ua
                          && e.GetTrackUid(a) == ua && e.GetClipUid(dtrack, 0) != e.GetClipUid(a, 0),
                "a duplicated track and its clips get new ids; the original keeps its own");
            e.Undo();

            // Undo restores the same objects with the same ids.
            e.Undo();
            bool back = false;
            e.TryGetTrackInfo(0, out var postUndo);
            for (int ci = 0; ci < postUndo.ClipCount; ci++) if (e.GetClipUid(a, ci) == u1) back = true;
            yield return (back && postUndo.ClipCount == preSplit.ClipCount, "undo of a split restores the clip with its id");

            // All ids in the project are unique.
            var all = new HashSet<string>();
            int total = 0;
            for (int ti = 0; e.TryGetTrackInfo(ti, out var info); ti++)
            {
                total++; all.Add(e.GetTrackUid(info.Id));
                for (int ci = 0; ci < info.ClipCount; ci++) { total++; all.Add(e.GetClipUid(info.Id, ci)); }
            }
            yield return (all.Count == total && !all.Contains(""), $"all {total} ids in the project are unique");

            // Restoring an id: accepted when well formed, refused otherwise; creates no undo entry.
            string want = ObjectIds.Derive("restore");
            yield return (!e.SetTrackUid(b, "nope") && !e.SetClipUid(a, c0, new string('0', 32)), "malformed or null ids are refused");
            bool set = e.SetTrackUid(b, want);
            yield return (set && e.GetTrackUid(b) == want, "a well-formed id is restored");
            // Restoring an id is not an edit: the next undo reverts the split before it.
            using (var e3 = new NotaEngine())
            {
                e3.SetBpm(120);
                int tt = e3.AddInstrumentTrack();
                int k0 = e3.AddMidiClip(tt, 0, 8), k1 = e3.AddMidiClip(tt, 16, 4);
                e3.SplitClip(tt, k0, 4.0);
                e3.TryGetTrackInfo(0, out var afterSplit);
                e3.SetClipUid(tt, k1 + 1, ObjectIds.Derive("restore-2"));
                e3.Undo();
                e3.TryGetTrackInfo(0, out var afterUndo);
                yield return (afterSplit.ClipCount == 3 && afterUndo.ClipCount == 2,
                    "restoring an id adds no undo step: undo reverts the edit before it");
            }

            // Setting an id another object holds: the later one is re-minted on the next publish.
            using var e2 = new NotaEngine();
            int p = e2.AddInstrumentTrack(), q = e2.AddInstrumentTrack();
            string held = e2.GetTrackUid(p);
            e2.SetTrackUid(q, held);
            e2.SetTrackName(q, "force publish");
            yield return (e2.GetTrackUid(p) == held && e2.GetTrackUid(q) != held && ObjectIds.IsValid(e2.GetTrackUid(q)),
                "an id set to one another object holds is re-minted: ids never collide");
        }

        // --- save / load round trip ----------------------------------------------------
        string dir = Path.Combine(Path.GetTempPath(), "nota-ids-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var report = new List<(bool, string)>();
        try
        {
            var ids = new List<string>();
            using (var src = new NotaEngine())
            {
                src.SetBpm(120);
                int t1 = src.AddInstrumentTrack(); src.SetTrackName(t1, "Drums");
                int t2 = src.AddInstrumentTrack(); src.SetTrackName(t2, "Bass");
                int m1 = src.AddMidiClip(t1, 0, 4); src.SetClipName(t1, m1, "Beat");
                int m2 = src.AddMidiClip(t2, 4, 4); src.SetClipName(t2, m2, "Line");
                ids.AddRange(new[] { src.GetTrackUid(t1), src.GetTrackUid(t2), src.GetClipUid(t1, m1), src.GetClipUid(t2, m2) });
                var w = new List<string>();
                var doc = ProjectService.Capture(src, new TransportState(120, 1, false, false), w);
                ProjectService.Save(doc, dir, src);
            }
            var loaded = ProjectService.Load(dir);
            using (var dst = new NotaEngine())
            {
                ProjectService.Apply(loaded, dst, dir);
                dst.TryGetTrackInfo(0, out var d1); dst.TryGetTrackInfo(1, out var d2);
                var got = new[] { dst.GetTrackUid(d1.Id), dst.GetTrackUid(d2.Id), dst.GetClipUid(d1.Id, 0), dst.GetClipUid(d2.Id, 0) };
                report.Add((ids.SequenceEqual(got), "ids survive save and load"));
            }
            // An id in the file the engine must reject (duplicate) is repaired, never fatal.
            loaded.Tracks[1].Id = loaded.Tracks[0].Id;
            using (var dst = new NotaEngine())
            {
                ProjectService.Apply(loaded, dst, dir);
                dst.TryGetTrackInfo(0, out var d1); dst.TryGetTrackInfo(1, out var d2);
                report.Add((dst.TrackCount == 2 && dst.GetTrackUid(d1.Id) != dst.GetTrackUid(d2.Id)
                            && ObjectIds.IsValid(dst.GetTrackUid(d2.Id)), "a file with a duplicated track id still loads, with distinct ids"));
            }
            // A file saved before ids existed: derived ids, identical for every opener.
            var legacy = ProjectService.Load(dir);
            foreach (var tr in legacy.Tracks)
            {
                tr.Id = null;
                foreach (var mc in tr.MidiClips) mc.Id = null;
            }
            string[] first, second;
            using (var x = new NotaEngine())
            {
                ProjectService.Apply(legacy, x, dir);
                first = Dump(x);
            }
            using (var y = new NotaEngine())
            {
                ProjectService.Apply(legacy, y, dir);
                second = Dump(y);
            }
            report.Add((first.Length == 4 && first.SequenceEqual(second) && first.All(ObjectIds.IsValid) && first.Distinct().Count() == 4,
                "a file with no ids gets the same derived ids on every machine"));
            // Ids are in the JSON as `id`, and an older reader's extension data would keep them.
            string json = ProjectService.SerializeManifest(ProjectService.Load(dir));
            report.Add((System.Text.RegularExpressions.Regex.IsMatch(json, "\"id\"\\s*:\\s*\"" + ids[0] + "\""), "the manifest carries each track's id as `id`"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        foreach (var r in report) yield return r;
    }

    private static string[] Dump(NotaEngine e)
    {
        var list = new List<string>();
        for (int i = 0; e.TryGetTrackInfo(i, out var ti); i++)
        {
            list.Add(e.GetTrackUid(ti.Id));
            for (int c = 0; c < ti.ClipCount; c++) list.Add(e.GetClipUid(ti.Id, c));
        }
        return list.ToArray();
    }
}
