// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Two studios in one process (AGENT DAW P2, spike S1). Alone: `--studios`. Switch times printed here are MEASURED on the
// machine running the test, with no hosted plugins: a starting point, not a promise for plugin-heavy projects.

using System.Reflection;
using Nota.Application;
using Nota.Infrastructure;
using Nota.Infrastructure.Studios;

namespace Nota.SmokeTest;

internal static class StudioTests
{
    private const string ProjectId = "33333333333333333333333333333333";

    public static IEnumerable<(bool ok, string label)> Run()
    {
        var results = new List<(bool, string)>();
        string root = Path.Combine(Path.GetTempPath(), "nota-studios-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var collab = new CollaborationService();
        try
        {
            // ---- the read-only view is exhaustive and fails closed --------------------------------------------
            var members = typeof(IAudioEngine).GetMethods().Concat(typeof(IAudioEngine).GetInterfaces().SelectMany(i => i.GetMethods())).ToList();
            using var probe = new NotaEngine();
            var ro = ReadOnlyEngineProxy.Wrap(probe);
            int denied = 0, allowed = 0; var leaks = new List<string>();
            foreach (var m in members)
            {
                bool shouldAllow = ReadOnlyEngineProxy.IsAllowed(m.Name);
                if (shouldAllow) { allowed++; continue; }
                var args = m.GetParameters().Select(p => DefaultFor(p.ParameterType)).ToArray();
                try { m.Invoke(ro, args); leaks.Add(m.Name); }
                catch (TargetInvocationException e) when (e.InnerException is StudioReadOnlyException) { denied++; }
                catch (Exception e) { leaks.Add($"{m.Name} ({e.GetType().Name})"); }
            }
            results.Add((leaks.Count == 0 && denied > 600 && allowed > 40, $"the read-only view denies all {denied} non-read members of the engine interface and allows {allowed} reads/viewer controls (leaks: {string.Join(",", leaks.Take(5))})"));
            string[] mustDeny = { "TryDeleteClip", "CancelFreeze", "Undo", "Redo", "Reset", "SetBpm", "AddInstrumentTrack", "Start", "Stop", "SetAudioOutputDevice", "PlayWithCountIn", "SetTrackName", "DeleteClip", "SplitClip", "set_Anything" };
            results.Add((mustDeny.All(n => !ReadOnlyEngineProxy.IsAllowed(n)) && new[] { "Play", "StopTransport", "Seek", "get_PositionBeats", "TryGetTrackInfo", "GetClipName", "IsTrackFrozen" }.All(ReadOnlyEngineProxy.IsAllowed),
                "known mutators are denied (including look-alikes TryDeleteClip and CancelFreeze); reads and the three viewer controls are allowed"));

            // ---- a saved master to load into Shared Studio ---------------------------------------------------
            string bundle = Path.Combine(root, "master.nota");
            string json;
            using (var src = new NotaEngine())
            {
                src.SetBpm(120);
                for (int t = 0; t < 3; t++) { int id = src.AddInstrumentTrack(); src.SetTrackName(id, $"Master {t}"); src.AddMidiClip(id, 0, 4); }
                var win = new FakeProjectVersions(src, new ProjectHistory("0.0.0-test")) { ProjectPath = bundle };
                win.SaveVersionAsync(null).GetAwaiter().GetResult();
                json = collab.ExportManifest(bundle, ProjectId, "v1");
            }

            using var studios = new StudioManager(() => new NotaEngine(), collab);
            bool started0 = true;
            try { studios.SwitchTo(Studio.Shared); studios.SwitchTo(Studio.My); } catch (NotaEngineException) { started0 = false; }
            var apply = studios.ApplySharedMaster(json, bundle);
            results.Add((apply.Applied && studios.Shared.TrackCount == 3 && studios.My.TrackCount == 0, "the master loads into Shared Studio and leaves My Studio untouched"));

            // ---- viewer controls work, edits throw ------------------------------------------------------------
            bool threw = false; try { studios.Shared.SetBpm(90); } catch (StudioReadOnlyException) { threw = true; }
            bool threw2 = false; try { studios.Shared.AddInstrumentTrack(); } catch (StudioReadOnlyException) { threw2 = true; }
            if (started0) studios.SwitchTo(Studio.Shared);   // transport commands run on the audio thread, so the studio must be the one playing
            studios.Shared.Seek(8);
            bool seeked = started0 ? WaitFor(() => Math.Abs(studios.Shared.PositionBeats - 8) < 1e-6) : true;   // transport commands reach the audio thread on its next block
            results.Add((threw && threw2 && seeked && studios.Shared.TrackCount == 3 && studios.Shared.TryGetTrackInfo(0, out _) && studios.Shared.GetTrackName(studios.Shared.TrackCount > 0 ? FirstId(studios.Shared) : 0).StartsWith("Master"),
                "Shared Studio can be read and its playhead moved; an edit throws and changes nothing"));

            // ---- My Studio is private and survives switching ----------------------------------------------------
            int mine = studios.My.AddInstrumentTrack(); studios.My.SetTrackName(mine, "My idea"); studios.My.AddMidiClip(mine, 0, 8);
            string startError = "";
            bool started = true;
            try { studios.SwitchTo(Studio.Shared); studios.SwitchTo(Studio.My); }
            catch (NotaEngineException e) { started = false; startError = e.Message; }
            results.Add((studios.My.TrackCount == 1 && studios.Shared.TrackCount == 3 && studios.My.GetTrackName(mine) == "My idea" && studios.My.Undo() && studios.My.TrackCount < 2,
                "switching studios keeps My Studio's unsaved edits and undo history, and never changes Shared Studio"));
            studios.My.Redo();

            // ---- only one studio owns the audio device -----------------------------------------------------------
            if (started)
            {
                studios.SwitchTo(Studio.My);
                bool myOwns = studios.My.IsRunning && !studios.Shared.IsRunning;
                studios.SwitchTo(Studio.Shared);
                bool sharedOwns = studios.Shared.IsRunning && !studios.My.IsRunning;
                results.Add((myOwns && sharedOwns && studios.Active == Studio.Shared, "exactly one studio owns the audio device at a time, and switching hands it over"));
            }
            else Console.WriteLine($"  [SKIP] audio ownership: no output device could be opened here ({startError})");

            // ---- a new master while someone is listening waits for a safe point -------------------------------
            if (started0)
            {
                studios.SwitchTo(Studio.Shared);
                studios.Shared.Seek(0); studios.Shared.Play();
                bool playing = WaitFor(() => studios.Shared.IsPlaying);
                var edited = JsonNode_AddTrack(json, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeee01", "Kick");
                var q = studios.ApplySharedMaster(edited, bundle);
                bool queued = playing && q.Queued && !q.Applied && studios.Shared.TrackCount == 3 && studios.HasPendingSharedUpdate;
                studios.Shared.StopTransport();
                WaitFor(() => !studios.Shared.IsPlaying);
                var later = studios.TryApplyPending();
                results.Add((queued && later is { Applied: true } && studios.Shared.TrackCount == 4 && !studios.HasPendingSharedUpdate,
                    "an update that arrives while Shared Studio is playing is queued, and applied once it stops"));
            }
            else
            {
                Console.WriteLine("  [SKIP] queued master update: needs a running audio thread to play, and no output device could be opened here");
                studios.ApplySharedMaster(JsonNode_AddTrack(json, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeee01", "Kick"), bundle);
            }

            // ---- a bad master is refused, the shared project is left as it was ---------------------------------------
            var bad = studios.ApplySharedMaster("{ not json", bundle);
            results.Add((!bad.Applied && !bad.Queued && bad.Errors.Length > 0 && studios.Shared.TrackCount == 4, "a malformed master is refused and Shared Studio keeps what it had"));

            // ---- measured switch time (no plugins) -----------------------------------------------------------------------
            var times = new List<double>();
            if (started)
                for (int i = 0; i < 20; i++)
                {
                    double ms = studios.SwitchTo(i % 2 == 0 ? Studio.My : Studio.Shared).TotalMilliseconds;
                    times.Add(ms);
                    if (ms > 300 && studios.LastSwitch is { } sw) Console.WriteLine($"  SLOW-SWITCH #{i}: {ms:F0} ms = pending {sw.ApplyPending.TotalMilliseconds:F0} + stop-old {sw.StopOld.TotalMilliseconds:F0} + start-new {sw.StartNew.TotalMilliseconds:F0}");
                }
            else
                for (int i = 0; i < 20; i++) times.Add(studios.SwitchTo(i % 2 == 0 ? Studio.My : Studio.Shared, startAudio: false).TotalMilliseconds);
            if (studios.LastSwitch is { } last) Console.WriteLine($"  last switch breakdown: pending {last.ApplyPending.TotalMilliseconds:F1} ms, stop old studio's audio {last.StopOld.TotalMilliseconds:F1} ms, start new studio's audio {last.StartNew.TotalMilliseconds:F1} ms");
            times.Sort();
            Console.WriteLine($"  SWITCH-TIME (measured here, no hosted plugins, {(started ? "audio device started" : "no device")}): median {times[times.Count / 2]:F1} ms, p95 {times[(int)(times.Count * 0.95)]:F1} ms, max {times[^1]:F1} ms over {times.Count} switches");
            results.Add((times[(int)(times.Count * 0.95)] < 500, $"switching is quick without plugins (p95 {times[(int)(times.Count * 0.95)]:F1} ms, bound 500 ms)"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        foreach (var r in results) yield return r;
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (condition()) return true; Thread.Sleep(5); }
        return condition();
    }

    private static int FirstId(IAudioEngine e) { e.TryGetTrackInfo(0, out var ti); return ti.Id; }

    private static object? DefaultFor(Type t)
    {
        if (t.IsByRef) t = t.GetElementType()!;
        if (t == typeof(string)) return "";
        if (t.IsArray) return Array.CreateInstance(t.GetElementType()!, 0);
        if (t.IsValueType) return Activator.CreateInstance(t);
        return null;
    }

    private static string JsonNode_AddTrack(string manifestJson, string id, string name)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(manifestJson)!.AsObject();
        node["root"]!["lists"]!["tracks"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse(
            $"{{\"id\":\"{id}\",\"type\":\"track\",\"props\":{{\"type\":1,\"name\":\"{name}\",\"volume\":1,\"pan\":0}}}}"));
        return node.ToJsonString();
    }
}
