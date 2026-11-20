// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Hostile input to the collaboration surface (AGENT DAW hardening): paths a manifest names, and thousands of random corruptions
// of a real manifest. Nothing may read outside the project, crash, or leave a half-applied project. Alone: `--hardening`.

using System.Text.Json.Nodes;
using Nota.Application;
using Nota.Infrastructure;

namespace Nota.SmokeTest;

internal static class HardeningTests
{
    private const string ProjectId = "55555555555555555555555555555555";

    private static List<string> Snapshot(NotaEngine e)
    {
        var l = new List<string>();
        for (int i = 0; e.TryGetTrackInfo(i, out var ti); i++)
        {
            l.Add($"track {e.GetTrackName(ti.Id)} {e.GetTrackUid(ti.Id)} clips={ti.ClipCount}");
            for (int c = 0; c < ti.ClipCount; c++) l.Add($"  clip {e.GetClipName(ti.Id, c)} {e.GetClipUid(ti.Id, c)}");
        }
        return l;
    }

    public static IEnumerable<(bool ok, string label)> Run()
    {
        var results = new List<(bool, string)>();
        string root = Path.Combine(Path.GetTempPath(), "nota-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string wav = Path.Combine(root, "sine.wav");
        WavWriter.WriteSine(wav, seconds: 0.5, freq: 330, sampleRate: 44100);
        var svc = new CollaborationService();
        try
        {
            // ---- the resolver, case by case -------------------------------------------------------------------------
            string bundle = Path.Combine(root, "song.nota");
            Directory.CreateDirectory(Path.Combine(bundle, "samples"));
            string[] refused = { "../x.wav", "samples/../../x.wav", "samples/../..", "..", "/etc/passwd", "\\\\server\\share\\x", "C:\\Windows\\x", "C:x", "samples/x.wav:stream", "", new string('a', 2000), "a\0b" };
            string[] allowed = { "samples/abc.wav", "samples\\abc.wav", "plugin-states/s.bin", "project.json", "samples/./abc.wav", "samples/sub/dir/x.wav" };
            results.Add((refused.All(r => BundlePaths.Resolve(bundle, r) is null), $"names that climb out, are rooted, name a drive or a stream, are empty, over-long or contain NUL are refused ({refused.Length} cases)"));
            results.Add((allowed.All(a => BundlePaths.Resolve(bundle, a) is { } p && p.StartsWith(Path.GetFullPath(bundle).TrimEnd('/')) || BundlePaths.Resolve(bundle, a) is { }), $"ordinary relative names resolve ({allowed.Length} cases)"));
            results.Add((BundlePaths.ResolveOrBlocked(bundle, "../x.wav").EndsWith(BundlePaths.Blocked) && !File.Exists(BundlePaths.ResolveOrBlocked(bundle, "../x.wav")), "a refused name becomes a path that does not exist"));

            // symbolic links: one inside the bundle that points out is refused; one that points inside is fine
            string outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "private");
            Directory.CreateDirectory(Path.Combine(bundle, "inner"));
            bool linked = true;
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(bundle, "samples", "escape"), outside);
                File.CreateSymbolicLink(Path.Combine(bundle, "samples", "fileescape.wav"), Path.Combine(outside, "secret.txt"));
                Directory.CreateSymbolicLink(Path.Combine(bundle, "samples", "stay"), Path.Combine(bundle, "inner"));
            }
            catch (Exception) { linked = false; }
            if (linked)
            {
                results.Add((BundlePaths.Resolve(bundle, "samples/escape/secret.txt") is null && BundlePaths.Resolve(bundle, "samples/fileescape.wav") is null, "a symbolic link inside the bundle that points outside it is refused (directory and file)"));
                results.Add((BundlePaths.Resolve(bundle, "samples/stay/x.wav") is not null, "a symbolic link that stays inside the bundle is fine"));
                string loop = Path.Combine(bundle, "samples", "loop");
                try { Directory.CreateSymbolicLink(loop, loop); results.Add((BundlePaths.Resolve(bundle, "samples/loop/x") is null, "a link loop is refused, not followed forever")); } catch (Exception) { }
            }
            else Console.WriteLine("  [SKIP] symbolic link cases: links cannot be created here");

            // ---- a real attack: a manifest whose audio clip is a file OUTSIDE the project ----------------------------
            string good = Path.Combine(root, "good.nota");
            string json;
            using (var src = new NotaEngine())
            {
                src.SetBpm(110);
                int t = src.AddInstrumentTrack(); src.SetTrackName(t, "Keys"); src.AddMidiClip(t, 0, 4);
                int v = src.AddAudioTrack(); src.SetTrackName(v, "Vox"); src.AddAudioClip(v, wav, 0.0);
                var win = new FakeProjectVersions(src, new ProjectHistory("0.0.0-test")) { ProjectPath = good };
                win.SaveVersionAsync(null).GetAwaiter().GetResult();
                json = svc.ExportManifest(good, ProjectId, "v1");
            }
            string victim = Path.Combine(root, "private-recording.wav");
            WavWriter.WriteSine(victim, seconds: 0.5, freq: 880, sampleRate: 44100);
            foreach (var (label, evilPath) in new[] { ("a ../ path", "../private-recording.wav"), ("an absolute path", victim.Replace('\\', '/')) })
            {
                var node = JsonNode.Parse(json)!.AsObject();
                var vox = node["root"]!["lists"]!["tracks"]!.AsArray().First(t => t!["props"]!["name"]!.GetValue<string>() == "Vox")!;
                vox["lists"]!["audioClips"]![0]!["props"]!["sample"] = evilPath;
                string evil = node.ToJsonString();
                using var e = new NotaEngine();
                int keep = e.AddInstrumentTrack(); e.SetTrackName(keep, "Mine");
                var before = Snapshot(e);
                var r = svc.Apply(e, evil, good);
                string[] bundleFiles = Directory.GetFiles(good, "*", SearchOption.AllDirectories);
                results.Add((!r.Applied && r.Errors.Any(x => x.Contains("audio could not be loaded")) && Snapshot(e).SequenceEqual(before),
                    $"a manifest that points an audio clip at a file outside the project ({label}) is refused and rolled back; the DAW's own project is untouched"));
                results.Add((bundleFiles.All(f => !File.ReadAllBytes(f).AsSpan().SequenceEqual(File.ReadAllBytes(victim))), $"...and the outside file ({label}) was not copied into the project"));
            }

            // ---- export never hashes a file that is only reachable through a link out of the bundle -----------------------
            if (linked)
            {
                string sneaky = Path.Combine(root, "sneaky.nota");
                CopyDir(good, sneaky);
                string sample = Directory.GetFiles(Path.Combine(sneaky, "samples")).First();
                File.Delete(sample);
                File.CreateSymbolicLink(sample, victim);
                var doc = ProjectService.Load(sneaky);
                var m = ProjectManifest.Export(doc, ProjectId, null, sneaky);
                results.Add((m["root"]!["blobs"] is not JsonObject b || !b.ContainsKey("samples/" + Path.GetFileName(sample)), "export does not hash a sample that is really a link to a file outside the project"));
                var check = svc.Check(json.Replace(Path.GetFileName(Directory.GetFiles(Path.Combine(good, "samples")).First()), Path.GetFileName(sample)), sneaky);
                results.Add((!check.Ok, "check treats such a link as a missing asset, never as a verified one"));
            }

            // ---- an asset the DAW cannot read (permissions): the check says so, it does not crash ---------------------------
            if (!OperatingSystem.IsWindows() && Environment.UserName != "root")
            {
                string locked = Path.Combine(root, "locked.nota");
                CopyDir(good, locked);
                string lockedFile = Directory.GetFiles(Path.Combine(locked, "samples")).First();
                File.SetUnixFileMode(lockedFile, UnixFileMode.None);
                try
                {
                    ManifestCheck c;
                    try { c = svc.Check(json, locked); }
                    catch (Exception ex) { c = new ManifestCheck(true, Array.Empty<string>(), Array.Empty<string>(), 0, Array.Empty<string>(), Array.Empty<string>()); results.Add((false, "an unreadable asset must not crash the check: " + ex.GetType().Name)); }
                    results.Add((!c.Ok && c.Errors.Any(x => x.Contains("could not be checked")), "an asset the DAW cannot read is reported as unverifiable, never a crash and never accepted"));
                }
                finally { File.SetUnixFileMode(lockedFile, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            }

            // ---- a hostile manifest in many shapes: never a crash ------------------------------------------------------------
            var shapes = new[]
            {
                "", " ", "null", "[]", "42", "\"text\"", "{", "{}", "{\"schema\":null}", "{\"schema\":\"agentdaw.manifest/1\"}", "{\"schema\":\"agentdaw.manifest/1\",\"root\":null}",
                "{\"schema\":\"agentdaw.manifest/1\",\"root\":{\"id\":1,\"type\":\"project\"}}", "{\"schema\":\"agentdaw.manifest/1\",\"root\":{\"lists\":{\"tracks\":[null,1,\"x\",[]]}}}",
                "{\"schema\":\"agentdaw.manifest/1\",\"root\":{\"props\":{\"tracks\":5}, \"lists\":{\"tracks\":[{\"props\":{\"type\":\"x\",\"volume\":\"loud\",\"midiClips\":[1]}}]}}}",
                "{\"schema\":\"agentdaw.manifest/1\",\"root\":{\"props\":{\"formatVersion\":\"new\"}}}", "{\"schema\":\"agentdaw.manifest/1\",\"root\":{\"props\":{\"formatVersion\":99999999999999}}}",
                new string('[', 100_000), "{\"a\":" + string.Concat(Enumerable.Repeat("{\"a\":", 5000)) + "1" + new string('}', 5001) + "}",
            };
            bool shapesOk = true; string shapeFail = "";
            foreach (var s in shapes)
            {
                try { var c = svc.Check(s, null); if (c.Ok) { shapesOk = false; shapeFail = $"accepted {s[..Math.Min(40, s.Length)]}"; } }
                catch (Exception ex) { shapesOk = false; shapeFail = $"threw {ex.GetType().Name} on {s[..Math.Min(40, s.Length)]}"; }
            }
            results.Add((shapesOk, $"{shapes.Length} hostile or malformed manifests are all refused without a crash {shapeFail}"));
            var huge = new string(' ', CollaborationService.MaxManifestBytes + 1);
            results.Add((!svc.Check(huge, null).Ok && !svc.Check(huge, null).Errors[0].Contains("Exception"), "a manifest over the size limit is refused unparsed"));

            // ---- fuzz: 3000 random corruptions of a real manifest -----------------------------------------------------------------
            var rng = new Random(20261119);
            int accepted = 0, rejected = 0, applied = 0, rolledBack = 0, crashed = 0; string crash = "";
            for (int i = 0; i < 3000; i++)
            {
                var doc = JsonNode.Parse(json)!;
                for (int k = 0, n = 1 + rng.Next(3); k < n; k++) Corrupt(doc, rng);
                string mutated = doc.ToJsonString();
                try
                {
                    var c = svc.Check(mutated, i % 3 == 0 ? good : null);
                    if (!c.Ok) { rejected++; continue; }
                    accepted++;
                    if (i % 3 != 0) continue;                        // only apply when the bundle is given: that is the supported path
                    using var e = new NotaEngine();
                    int mine = e.AddInstrumentTrack(); e.SetTrackName(mine, "Mine"); e.AddMidiClip(mine, 0, 4);
                    var before = Snapshot(e);
                    var r = svc.Apply(e, mutated, good);
                    if (r.Applied) applied++;
                    else { rolledBack++; if (!Snapshot(e).SequenceEqual(before)) { crashed++; crash = "a refused apply changed the project"; } }
                }
                catch (Exception ex) { crashed++; crash = $"{ex.GetType().Name}: {ex.Message}"; if (crashed > 3) break; }
            }
            results.Add((crashed == 0, $"3000 random corruptions of a real manifest: no crash and no half-applied project {crash} (rejected {rejected}, accepted {accepted}, applied {applied}, rolled back {rolledBack})"));
            results.Add((rejected > 500 && accepted > 100, "the fuzz exercised both outcomes: many corruptions refused, many harmless ones accepted"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        foreach (var r in results) yield return r;
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>One random corruption somewhere in the document: remove, retype, null, truncate, duplicate or inflate a value.</summary>
    private static void Corrupt(JsonNode root, Random rng)
    {
        var nodes = new List<(JsonNode? parent, string? key, int idx, JsonNode node)>();
        void Walk(JsonNode? parent, string? key, int idx, JsonNode node)
        {
            nodes.Add((parent, key, idx, node));
            if (node is JsonObject o) foreach (var kv in o.ToList()) if (kv.Value is not null) Walk(node, kv.Key, -1, kv.Value);
            if (node is JsonArray a) for (int i = 0; i < a.Count; i++) if (a[i] is not null) Walk(node, null, i, a[i]!);
        }
        Walk(null, null, -1, root);
        var (parent, k, idx, n) = nodes[rng.Next(nodes.Count)];
        if (parent is null) return;
        JsonNode? replacement = rng.Next(9) switch
        {
            0 => null,
            1 => JsonValue.Create(rng.Next(2) == 0 ? -1 : int.MaxValue),
            2 => JsonValue.Create("x" + new string('y', rng.Next(300))),
            3 => new JsonArray(),
            4 => new JsonObject(),
            5 => JsonValue.Create(double.MaxValue),
            6 => JsonValue.Create(rng.Next(2) == 0),
            7 => JsonValue.Create(-1e300),
            _ => n.DeepClone(),                                   // duplicate (a repeated id is a good corruption)
        };
        if (rng.Next(6) == 0 && parent is JsonObject po && k is not null) { po.Remove(k); return; }                  // delete the key
        if (rng.Next(6) == 0 && parent is JsonArray pa && idx >= 0) { pa.RemoveAt(idx); return; }                    // delete the element
        if (parent is JsonObject obj && k is not null) obj[k] = replacement;
        else if (parent is JsonArray arr && idx >= 0 && idx < arr.Count) arr[idx] = replacement;
    }
}
