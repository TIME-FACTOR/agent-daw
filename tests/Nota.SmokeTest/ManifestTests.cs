// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Project <-> collaboration manifest (AGENT DAW P0-4). Alone: `--manifest`. Set NOTA_MANIFEST_OUT=<file> to
// also write the exported manifest, which the protocol repo validates against its JSON Schema.

using System.Text.Json.Nodes;
using Nota.Infrastructure;

namespace Nota.SmokeTest;

internal static class ManifestTests
{
    private const string ProjectId = "11111111111111111111111111111111";

    public static IEnumerable<(bool ok, string label)> Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nota-manifest-" + Guid.NewGuid().ToString("N"));
        string wav = Path.Combine(Path.GetTempPath(), "nota_manifest_sine.wav");
        WavWriter.WriteSine(wav, seconds: 1.0, freq: 220, sampleRate: 44100);
        var results = new List<(bool, string)>();
        try
        {
            string[] engineIds;
            using (var e = new NotaEngine())
            {
                e.SetBpm(128);
                int drums = e.AddInstrumentTrack(); e.SetTrackName(drums, "Drums");
                int bass = e.AddInstrumentTrack(); e.SetTrackName(bass, "Bass");
                int vox = e.AddAudioTrack(); e.SetTrackName(vox, "Vox");
                int grp = e.AddGroupTrack(); e.SetTrackName(grp, "Rhythm");
                int dc = e.AddMidiClip(drums, 0, 4); e.SetClipName(drums, dc, "Beat");
                e.SetClipNotes(drums, dc, new[] { new NotaNote(36, 0.0, 1.0, 1.0f), new NotaNote(38, 1.0, 1.0, 0.9f) });
                int bc = e.AddMidiClip(bass, 4, 4); e.SetClipName(bass, bc, "Line");
                e.AddAudioClipEx(vox, wav, startBeat: 0, sourceOffsetFrames: 0, lengthFrames: 0, gain: 1f);
                e.AddSessionMidiClip(drums, 1, 4);
                e.SetTrackGroup(drums, grp);
                int comp = e.AddBuiltinDevice(vox, 1);
                e.SetDeviceSidechainSource(vox, comp, drums);
                e.SetTrackMidiSource(bass, drums);
                var ids = new List<string>();
                for (int i = 0; e.TryGetTrackInfo(i, out var ti); i++) ids.Add(e.GetTrackUid(ti.Id));
                engineIds = ids.ToArray();
                var w = new List<string>();
                ProjectService.Save(ProjectService.Capture(e, new TransportState(128, 1, false, false), w), dir, e);
            }
            var doc = ProjectService.Load(dir);
            string original = ProjectService.SerializeManifest(doc);
            var m = ProjectManifest.Export(doc, ProjectId, "v1", dir);
            if (Environment.GetEnvironmentVariable("NOTA_MANIFEST_OUT") is { Length: > 0 } outPath)
                File.WriteAllText(outPath, m.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            // 1. structure
            var root = m["root"]!.AsObject();
            var tracks = root["lists"]!["tracks"]!.AsArray();
            var allIds = new List<string>();
            void Collect(JsonNode? n) { if (n is JsonObject o) { if (o["id"] is JsonValue v && o["type"] is not null) allIds.Add(v.GetValue<string>()); foreach (var kv in o) Collect(kv.Value); } else if (n is JsonArray a) foreach (var x in a) Collect(x); }
            Collect(root);
            results.Add((m["schema"]!.GetValue<string>() == "agentdaw.manifest/1" && tracks.Count == 4 && allIds.All(ObjectIds.IsValid) && allIds.Distinct().Count() == allIds.Count,
                $"manifest has 4 track nodes and {allIds.Count} unique valid node ids"));
            results.Add((tracks.Select(t => t!["id"]!.GetValue<string>()).SequenceEqual(engineIds), "track node ids are the engine's stable ids, in order"));
            var blobs = root["blobs"]?.AsObject();
            results.Add((blobs is { Count: > 0 } && blobs.Any(kv => kv.Key.StartsWith("samples/")) && blobs.Any(kv => kv.Key.StartsWith("plugin-states/"))
                         && blobs.All(kv => kv.Value!.GetValue<string>().StartsWith("sha256:") && kv.Value!.GetValue<string>().Length == 71),
                $"blob table lists the bundle's audio and plugin state with sha256 ({blobs?.Count ?? 0} files)"));
            var json = m.ToJsonString();
            results.Add((!System.Text.RegularExpressions.Regex.IsMatch(json, "\"sidechainSource\"\\s*:\\s*[0-9]") && json.Contains("\"$track\""),
                "track-index references are replaced by {\"$track\": id}"));

            // 2. lossless round trip
            var back = ProjectManifest.Import(m);
            results.Add((ProjectService.SerializeManifest(back.Document) == original && back.Warnings.Count == 0, "Import(Export(project)) reproduces the project file exactly"));

            // 3. references follow their tracks when the order changes
            var reordered = (JsonObject)m.DeepClone();
            var rt = reordered["root"]!["lists"]!["tracks"]!.AsArray();
            var last = rt[rt.Count - 1]!; rt.RemoveAt(rt.Count - 1); rt.Insert(0, last);
            var rdoc = ProjectManifest.Import(reordered).Document;
            string NameAt(int i) => rdoc.Tracks[i].Name ?? "";
            var voxT = rdoc.Tracks.First(t => t.Name == "Vox");
            var drumsT = rdoc.Tracks.First(t => t.Name == "Drums");
            var bassT = rdoc.Tracks.First(t => t.Name == "Bass");
            results.Add((NameAt(0) == "Rhythm" && NameAt(voxT.Devices[0].SidechainSource) == "Drums"
                         && NameAt(drumsT.GroupId) == "Rhythm" && NameAt(bassT.MidiFrom) == "Drums",
                "after reordering tracks, sidechain, group parent and MIDI source still point at the same tracks"));

            // 4. a removed target clears the reference and says so
            var removed = (JsonObject)m.DeepClone();
            var rrt = removed["root"]!["lists"]!["tracks"]!.AsArray();
            var drumNode = rrt.First(t => t!["props"]!["name"]!.GetValue<string>() == "Drums")!;
            rrt.Remove(drumNode);
            var rem = ProjectManifest.Import(removed);
            var vox2 = rem.Document.Tracks.First(t => t.Name == "Vox");
            results.Add((vox2.Devices[0].SidechainSource == -1 && rem.Warnings.Count >= 2, $"removing a referenced track clears its references with warnings ({rem.Warnings.Count})"));

            // 5. the imported project loads into an engine with the same ids
            using (var e2 = new NotaEngine())
            {
                ProjectService.Apply(back.Document, e2, dir);
                var got = new List<string>();
                for (int i = 0; e2.TryGetTrackInfo(i, out var ti); i++) got.Add(e2.GetTrackUid(ti.Id));
                results.Add((got.SequenceEqual(engineIds), "the imported project applies to a fresh engine with the same ids"));
            }

            // 6. a file with no ids: export derives the same ids Apply assigns
            var legacy = ProjectService.Load(dir);
            foreach (var t in legacy.Tracks) { t.Id = null; foreach (var c in t.MidiClips) c.Id = null; foreach (var c in t.AudioClips) c.Id = null; foreach (var s in t.SessionSlots) s.Id = null; }
            var lm = ProjectManifest.Export(legacy, ProjectId);
            var lids = new List<string>(); Collect2(lm["root"], lids);
            using (var e3 = new NotaEngine())
            {
                ProjectService.Apply(legacy, e3, dir);
                var applied = new List<string>();
                for (int i = 0; e3.TryGetTrackInfo(i, out var ti); i++)
                {
                    applied.Add(e3.GetTrackUid(ti.Id));
                    for (int c = 0; c < ti.ClipCount; c++) applied.Add(e3.GetClipUid(ti.Id, c));
                }
                var manifestTrackAndClipIds = TrackAndClipIds(lm);
                results.Add((applied.OrderBy(x => x).SequenceEqual(manifestTrackAndClipIds.Where(x => applied.Contains(x)).OrderBy(x => x)) && applied.Count > 0 && applied.All(manifestTrackAndClipIds.Contains),
                    $"for a file without ids, export and Apply derive the same ids ({applied.Count} checked)"));
            }

            // 7. a path that climbs out of the bundle is never read
            var evil = ProjectService.Load(dir);
            var vt = evil.Tracks.First(t => t.AudioClips.Count > 0);
            vt.AudioClips[0].Sample = "samples/../../../../etc/hosts";
            var em = ProjectManifest.Export(evil, ProjectId, null, dir);
            results.Add((em["root"]!["blobs"] is null || !em["root"]!["blobs"]!.AsObject().ContainsKey("samples/../../../../etc/hosts"), "a sample path that escapes the bundle is not hashed"));

            // 9. a change authored outside the DAW (protocol library) lands in the engine with its ids.
            if (Environment.GetEnvironmentVariable("NOTA_MANIFEST_AFTER") is { Length: > 0 } afterPath && File.Exists(afterPath))
            {
                var after = ProjectManifest.Import((JsonObject)JsonNode.Parse(File.ReadAllText(afterPath))!);
                var ad = after.Document;
                var kick = ad.Tracks.FirstOrDefault(t => t.Name == "Kick");
                var aVox = ad.Tracks.First(t => t.Name == "Vox");
                results.Add((ad.Tracks.Count == 5 && kick is { Type: 1 } && kick.MidiClips.Count == 1 && kick.MidiClips[0].Notes.Length == 4
                             && ad.Tracks[aVox.Devices[0].SidechainSource].Name == "Drums" && after.Warnings.Count == 0,
                    "the externally authored kick proposal imports: new track and clip, existing references intact"));
                using var e4 = new NotaEngine();
                ProjectService.Apply(ad, e4, dir);
                int kt = -1;
                for (int i = 0; e4.TryGetTrackInfo(i, out var ti); i++) if (e4.GetTrackName(ti.Id) == "Kick") kt = ti.Id;
                results.Add((kt > 0 && e4.GetTrackUid(kt) == "eeeeeeeeeeeeeeeeeeeeeeeeeeeeee01" && e4.GetClipUid(kt, 0) == "eeeeeeeeeeeeeeeeeeeeeeeeeeeeee02"
                             && e4.GetClipNotes(kt, 0).Length == 4,
                    "...and loads into the engine with the ids the change set gave it"));
            }
            else Console.WriteLine("  [SKIP] externally authored change: set NOTA_MANIFEST_AFTER to protocol/examples/from-daw-rich.after-kick.json to run it");

            // 8. bad input is refused
            bool badId = false; try { ProjectManifest.Export(doc, "nope"); } catch (ArgumentException) { badId = true; }
            bool badSchema = false; try { ProjectManifest.Import(new JsonObject { ["schema"] = "x" }); } catch (InvalidDataException) { badSchema = true; }
            results.Add((badId && badSchema, "a bad project id or schema is refused"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        foreach (var r in results) yield return r;
    }

    private static void Collect2(JsonNode? n, List<string> into)
    {
        if (n is JsonObject o) { if (o["id"] is JsonValue v) into.Add(v.GetValue<string>()); foreach (var kv in o) Collect2(kv.Value, into); }
        else if (n is JsonArray a) foreach (var x in a) Collect2(x, into);
    }

    // Track and clip nodes only (midi/audio clips and session slots), not the project root.
    private static List<string> TrackAndClipIds(JsonObject manifest)
    {
        var ids = new List<string>();
        foreach (var t in manifest["root"]!["lists"]!["tracks"]!.AsArray())
        {
            ids.Add(t!["id"]!.GetValue<string>());
            if (t["lists"] is JsonObject l) foreach (var kv in l) foreach (var c in kv.Value!.AsArray()) ids.Add(c!["id"]!.GetValue<string>());
        }
        return ids;
    }
}
