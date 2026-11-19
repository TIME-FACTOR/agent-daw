// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Applying a manifest is all or nothing (AGENT DAW P0-5). Fault injection: an engine wrapper that throws on the Nth call
// to a chosen method, and a manifest whose audio cannot be decoded. Alone: `--atomic-apply`.

using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Nota.Application;
using Nota.Infrastructure;

namespace Nota.SmokeTest;

/// <summary>Forwards every call to the real engine, but throws once on the Nth call of a named method.</summary>
public class FaultyEngineProxy : DispatchProxy
{
    private IAudioEngine _inner = default!;
    private string _method = "";
    private int _failOn, _seen;
    public int Injected { get; private set; }

    public static (IAudioEngine Proxy, FaultyEngineProxy Control) Wrap(IAudioEngine inner, string method, int failOnCall)
    {
        var p = Create<IAudioEngine, FaultyEngineProxy>();
        var c = (FaultyEngineProxy)(object)p;
        c._inner = inner; c._method = method; c._failOn = failOnCall;
        return (p, c);
    }

    protected override object? Invoke(MethodInfo? m, object?[]? args)
    {
        if (m is null) return null;
        if (m.Name == _method && ++_seen == _failOn) { Injected++; throw new IOException($"injected fault in {m.Name} (call {_seen})"); }
        try { return m.Invoke(_inner, args); }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }
}

internal static class AtomicApplyTests
{
    private const string ProjectId = "44444444444444444444444444444444";

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

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (condition()) return true; Thread.Sleep(5); }
        return condition();
    }

    private static int Backups() => Directory.GetDirectories(Path.GetTempPath(), "nota-apply-backup-*").Length;

    public static IEnumerable<(bool ok, string label)> Run()
    {
        var results = new List<(bool, string)>();
        string root = Path.Combine(Path.GetTempPath(), "nota-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string wav = Path.Combine(root, "sine.wav");
        WavWriter.WriteSine(wav, seconds: 0.5, freq: 330, sampleRate: 44100);
        var svc = new CollaborationService();
        try
        {
            // The incoming master: 3 instrument tracks with a clip each, plus an audio track with a sample.
            string bundle = Path.Combine(root, "incoming.nota");
            string json;
            using (var src = new NotaEngine())
            {
                src.SetBpm(100);
                for (int t = 0; t < 3; t++) { int id = src.AddInstrumentTrack(); src.SetTrackName(id, $"New {t}"); src.AddMidiClip(id, 0, 4); }
                int vox = src.AddAudioTrack(); src.SetTrackName(vox, "New Vox"); src.AddAudioClip(vox, wav, 0.0);
                var win = new FakeProjectVersions(src, new ProjectHistory("0.0.0-test")) { ProjectPath = bundle };
                win.SaveVersionAsync(null).GetAwaiter().GetResult();
                json = svc.ExportManifest(bundle, ProjectId, "v2");
            }

            bool tempoReal = false;
            NotaEngine Prior(out List<string> before)
            {
                var e = new NotaEngine();
                // Tempo changes are commands the audio thread applies, so the engine must be running for 90 to really be 90.
                try { e.Start(); e.SetBpm(90); tempoReal = WaitFor(() => Math.Abs(e.Bpm - 90) < 1e-6); } catch (NotaEngineException) { tempoReal = false; }
                int a = e.AddInstrumentTrack(); e.SetTrackName(a, "Keep A"); e.AddMidiClip(a, 0, 8); e.SetClipName(a, 0, "keep clip");
                int b = e.AddAudioTrack(); e.SetTrackName(b, "Keep B");
                before = Snapshot(e);
                return e;
            }

            // ---- success: replaced, nothing left behind ----------------------------------------------------------
            int backupsBefore = Backups();
            using (var e = Prior(out _))
            {
                var ok = svc.Apply(e, json, bundle);
                results.Add((ok.Applied && e.TrackCount == 4 && e.GetTrackName(FirstId(e)) == "New 0" && Backups() == backupsBefore, "a good manifest replaces the project and leaves no backup behind"));
            }

            // ---- an exception halfway through loading: the previous project is restored exactly ---------------------
            using (var e = Prior(out var before))
            {
                var (proxy, ctl) = FaultyEngineProxy.Wrap(e, "AddMidiClip", failOnCall: 2);   // the second MIDI clip of the incoming project
                var r = svc.Apply(proxy, json, bundle);
                var after = Snapshot(e);
                results.Add((ctl.Injected == 1 && !r.Applied && r.Errors.Any(x => x.Contains("restored")) && after.SequenceEqual(before) && Backups() == backupsBefore,
                    $"a failure partway through leaves the previous project exactly as it was (tracks, ids, clips) and no backup file ({r.Errors.FirstOrDefault()?[..Math.Min(70, r.Errors[0].Length)]}…)"));
                if (tempoReal) results.Add((WaitFor(() => Math.Abs(e.Bpm - 90) < 1e-6), "…including the tempo (90 bpm before, 90 bpm after)"));
                else Console.WriteLine("  [SKIP] tempo restoration: tempo needs a running audio thread and no output device could be opened here");
            }

            // ---- the same failure with nothing open: back to empty ---------------------------------------------------
            using (var e = new NotaEngine())
            {
                var (proxy, ctl) = FaultyEngineProxy.Wrap(e, "AddMidiClip", failOnCall: 2);
                var r = svc.Apply(proxy, json, bundle);
                results.Add((ctl.Injected == 1 && !r.Applied && e.TrackCount == 0, "a failure when nothing was open leaves nothing open"));
            }

            // ---- audio that cannot be decoded is refused with rollback, not loaded with a hole in it --------------------
            string bad = Path.Combine(root, "bad.nota");
            CopyDir(bundle, bad);
            var node = JsonNode.Parse(json)!.AsObject();
            string samplePath = node["root"]!["blobs"]!.AsObject().First(kv => kv.Key.StartsWith("samples/")).Key;
            byte[] garbage = System.Text.Encoding.ASCII.GetBytes("this is not audio at all");
            File.WriteAllBytes(Path.Combine(bad, samplePath), garbage);
            node["root"]!["blobs"]![samplePath] = "sha256:" + Convert.ToHexString(SHA256.HashData(garbage)).ToLowerInvariant();   // the manifest agrees with the file: the check passes
            string badJson = node.ToJsonString();
            using (var e = Prior(out var before))
            {
                bool checkOk = svc.Check(badJson, bad).Ok;
                var r = svc.Apply(e, badJson, bad);
                results.Add((checkOk && !r.Applied && r.Errors.Any(x => x.Contains("audio could not be loaded")) && Snapshot(e).SequenceEqual(before) && Backups() == backupsBefore,
                    "audio that passes the hash check but cannot be decoded is refused and the previous project is restored"));
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        foreach (var r in results) yield return r;
    }

    private static int FirstId(IAudioEngine e) { e.TryGetTrackInfo(0, out var ti); return ti.Id; }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
