// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// The local collaboration contract agentdaw.local/1 (AGENT DAW P0-7): the MCP tools match the frozen
// contract file, and each tool does what it says and refuses what it must. Alone: `--collab-tools`.

using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Nota.Application;
using Nota.Infrastructure;
using Nota.Mcp.Tools;

namespace Nota.SmokeTest;

internal static class CollabToolsTests
{
    private const string ProjectId = "22222222222222222222222222222222";

    public static IEnumerable<(bool ok, string label)> Run()
    {
        var results = new List<(bool, string)>();
        string root = Path.Combine(Path.GetTempPath(), "nota-collab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string wav = Path.Combine(root, "sine.wav");
        WavWriter.WriteSine(wav, seconds: 0.5, freq: 330, sampleRate: 44100);
        try
        {
            // ---- the frozen contract ----------------------------------------------------------------
            var contract = JsonNode.Parse(File.ReadAllText(FixturePath("local-api.1.json")))!.AsObject();
            var declared = contract["tools"]!.AsArray().Select(t => (Name: t!["name"]!.GetValue<string>(),
                Params: t["params"]!.AsArray().Select(p => $"{p!["name"]!.GetValue<string>()}:{p["type"]!.GetValue<string>()}:{(p["required"]!.GetValue<bool>() ? "req" : "opt")}").ToArray())).ToList();
            string Ty(Type t) => t == typeof(string) ? "string" : t == typeof(bool) ? "boolean" : t.Name;
            var actual = typeof(CollabTools).GetMethods().Select(m => (m, a: m.GetCustomAttribute<McpServerToolAttribute>())).Where(x => x.a is not null)
                .Select(x => (Name: x.a!.Name!, Params: x.m.GetParameters().Select(p => $"{p.Name}:{Ty(p.ParameterType)}:{(p.HasDefaultValue ? "opt" : "req")}").ToArray())).ToList();
            results.Add((declared.Select(d => d.Name).SequenceEqual(actual.Select(a => a.Name))
                         && declared.Zip(actual).All(z => z.First.Params.SequenceEqual(z.Second.Params)),
                "the MCP tools match contract/local-api.1.json exactly (names, parameters, optionality)"));
            var svc = new CollaborationService();
            results.Add((contract["api"]!.GetValue<string>() == svc.Info().Api && contract["manifestSchema"]!.GetValue<string>() == svc.Info().ManifestSchema
                         && contract["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()).SequenceEqual(svc.Info().Capabilities),
                "api version, manifest schema and capabilities match the contract"));

            // ---- a saved project ------------------------------------------------------------------------
            string bundle = Path.Combine(root, "song.nota");
            IProjectHistory history = new ProjectHistory("0.0.0-test");
            using var e = new NotaEngine();
            e.SetBpm(120);
            int drums = e.AddInstrumentTrack(); e.SetTrackName(drums, "Drums");
            int vox = e.AddAudioTrack(); e.SetTrackName(vox, "Vox");
            int mc = e.AddMidiClip(drums, 0, 4); e.SetClipName(drums, mc, "Beat");
            e.SetClipNotes(drums, mc, new[] { new NotaNote(36, 0, 1, 1f) });
            e.AddAudioClipEx(vox, wav, startBeat: 0, sourceOffsetFrames: 0, lengthFrames: 0, gain: 1f);
            var window = new FakeProjectVersions(e, history);
            var tools = new CollabTools(e, new SyncDispatch(), new NoRefresh(), window, svc);

            // info
            var info = tools.Info().Result;
            results.Add((info.Api == "agentdaw.local/1" && info.ProjectPath is null && info.ProjectFormat == ProjectService.CurrentFormatVersion, "collab_info reports the contract and no project path before a save"));

            // export refuses until saved and clean
            static string? Err(Func<Task> f) { try { f().GetAwaiter().GetResult(); return null; } catch (Exception ex) { return ex.Message; } }
            results.Add((Err(() => tools.ExportManifest(ProjectId)) is { } m1 && m1.Contains("never been saved"), "export refuses a project that was never saved"));
            window.ProjectPath = bundle;
            window.SaveVersionAsync(null).GetAwaiter().GetResult();
            window.HasUnsavedChanges = true;
            results.Add((Err(() => tools.ExportManifest(ProjectId)) is { } m2 && m2.Contains("unsaved"), "export refuses while there are unsaved edits"));
            window.HasUnsavedChanges = false;
            string json = tools.ExportManifest(ProjectId, "v7").Result;
            var manifest = JsonNode.Parse(json)!.AsObject();
            results.Add((manifest["schema"]!.GetValue<string>() == "agentdaw.manifest/1" && manifest["version"]!.GetValue<string>() == "v7"
                         && manifest["root"]!["lists"]!["tracks"]!.AsArray().Count == 2 && manifest["root"]!["blobs"]!.AsObject().Count >= 1,
                "export returns a manifest with 2 tracks, the version label and an asset table"));

            // check
            var ok = tools.CheckManifest(json, bundle).Result;
            results.Add((ok.Ok && ok.Tracks == 2 && ok.MissingBlobs.Length == 0 && ok.CorruptBlobs.Length == 0, "check accepts the export and verifies its assets against the bundle"));
            var junk = tools.CheckManifest("{ not json", null).Result;
            var wrong = tools.CheckManifest("{\"schema\":\"nope\"}", null).Result;
            var newer = (JsonObject)manifest.DeepClone(); newer["root"]!["props"]!["formatVersion"] = ProjectService.CurrentFormatVersion + 5;
            var newerCheck = tools.CheckManifest(newer.ToJsonString(), null).Result;
            results.Add((!junk.Ok && junk.Errors.Length > 0 && !wrong.Ok && !newerCheck.Ok && newerCheck.Errors[0].Contains("format"),
                "check reports garbage, a wrong schema and a too-new project format as errors without throwing"));
            string sample = manifest["root"]!["blobs"]!.AsObject().First(kv => kv.Key.StartsWith("samples/")).Key;
            string samplePath = Path.Combine(bundle, sample);
            byte[] original = File.ReadAllBytes(samplePath);
            File.WriteAllBytes(samplePath, original.Concat(new byte[] { 1 }).ToArray());
            var corrupt = tools.CheckManifest(json, bundle).Result;
            File.Delete(samplePath);
            var missing = tools.CheckManifest(json, bundle).Result;
            File.WriteAllBytes(samplePath, original);
            results.Add((!corrupt.Ok && corrupt.CorruptBlobs.Contains(sample) && !missing.Ok && missing.MissingBlobs.Contains(sample), "check names corrupt and missing asset files"));

            // apply: refusals change nothing
            int tracksBefore = e.TrackCount;
            var edited = (JsonObject)manifest.DeepClone();
            edited["root"]!["lists"]!["tracks"]!.AsArray().Add(JsonNode.Parse(
                "{\"id\":\"eeeeeeeeeeeeeeeeeeeeeeeeeeeeee01\",\"type\":\"track\",\"props\":{\"type\":1,\"name\":\"Kick\",\"volume\":1,\"pan\":0}}"));
            string editedJson = edited.ToJsonString();
            var noConfirm = tools.ApplyManifest(editedJson, bundle).Result;
            window.HasUnsavedChanges = true;
            var dirty = tools.ApplyManifest(editedJson, bundle, confirmReplace: true).Result;
            window.HasUnsavedChanges = false;
            var noDir = tools.ApplyManifest(editedJson, Path.Combine(root, "nope.nota"), confirmReplace: true).Result;
            File.WriteAllBytes(samplePath, original.Concat(new byte[] { 2 }).ToArray());
            var badAsset = tools.ApplyManifest(editedJson, bundle, confirmReplace: true).Result;
            File.WriteAllBytes(samplePath, original);
            results.Add((!noConfirm.Applied && !dirty.Applied && !noDir.Applied && !badAsset.Applied && badAsset.Errors.Any(x => x.Contains("corrupt asset")) && e.TrackCount == tracksBefore,
                "apply refuses without confirmation, over unsaved edits, for a missing folder and for a corrupt asset, and changes nothing"));

            // apply: success, including discardUnsaved
            window.HasUnsavedChanges = true;
            var applied = tools.ApplyManifest(editedJson, bundle, confirmReplace: true, discardUnsaved: true).Result;
            var names = new List<string>(); var ids = new Dictionary<string, string>();
            for (int i = 0; e.TryGetTrackInfo(i, out var ti); i++) { string n = e.GetTrackName(ti.Id); names.Add(n); ids[n] = e.GetTrackUid(ti.Id); }
            results.Add((applied.Applied && applied.Tracks == 3 && names.SequenceEqual(new[] { "Drums", "Vox", "Kick" }) && ids["Kick"] == "eeeeeeeeeeeeeeeeeeeeeeeeeeeeee01",
                $"apply loads the edited manifest: 3 tracks, new track present with the id the manifest gave it ({applied.Warnings.Length} warnings)"));

            // apply into a different, fresh engine reproduces the project (the other producer's machine)
            using var other = new NotaEngine();
            var otherTools = new CollabTools(other, new SyncDispatch(), new NoRefresh(), new FakeProjectVersions(other, history), svc);
            var onOther = otherTools.ApplyManifest(json, bundle, confirmReplace: true).Result;
            var otherIds = new List<string>(); var origIds = new List<string>();
            for (int i = 0; other.TryGetTrackInfo(i, out var ti); i++) otherIds.Add(other.GetTrackUid(ti.Id));
            results.Add((onOther.Applied && otherIds.Count == 2 && otherIds[0] == manifest["root"]!["lists"]!["tracks"]![0]!["id"]!.GetValue<string>(),
                "a different engine applying the same manifest ends up with the same track ids"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        foreach (var r in results) yield return r;
    }

    private static string FixturePath(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "tests", "Nota.SmokeTest", "Fixtures", name);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(name);
    }
}
