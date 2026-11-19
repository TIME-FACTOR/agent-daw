// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// ICollaborationPort over ProjectManifest / ProjectService (AGENT DAW P0-7).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nota.Application;

namespace Nota.Infrastructure;

public sealed class CollaborationService : ICollaborationPort
{
    public const string Api = "agentdaw.local/1";
    /// <summary>A manifest larger than this is refused unparsed (a project of tens of thousands of clips is far smaller).</summary>
    public const int MaxManifestBytes = 64 * 1024 * 1024;

    public CollabInfo Info() => new(Api, ProjectManifest.Schema, ProjectService.CurrentFormatVersion,
        new[] { "manifest.export", "manifest.check", "manifest.apply", "stable-ids", "audio.tap.http" });

    public string ExportManifest(string bundleDir, string projectId, string? version)
    {
        var doc = ProjectService.Load(bundleDir);
        return ProjectManifest.Export(doc, projectId, version, bundleDir).ToJsonString();
    }

    public ManifestCheck Check(string manifestJson, string? bundleDir)
    {
        var (import, errors) = TryImport(manifestJson);
        if (import is null) return new ManifestCheck(false, errors, Array.Empty<string>(), 0, Array.Empty<string>(), Array.Empty<string>());
        var (missing, corrupt) = bundleDir is null ? (new List<string>(), new List<string>()) : VerifyBlobs(manifestJson, bundleDir);
        return new ManifestCheck(missing.Count == 0 && corrupt.Count == 0, Array.Empty<string>(), import.Warnings.ToArray(),
            import.Document.Tracks.Count, missing.ToArray(), corrupt.ToArray());
    }

    /// <summary>Warnings that mean audio the manifest names could not be loaded. Applying with such a loss would leave a
    /// project with holes in it, so it is refused and rolled back. (A plugin that is not installed is deliberately NOT
    /// here: that is expected, and the contribution is heard through its frozen audio.)</summary>
    private static bool LosesAudio(string warning) => warning.StartsWith("Couldn't reload", StringComparison.Ordinal)
                                                    || warning.StartsWith("A referenced sample was missing", StringComparison.Ordinal);

    /// <summary>All or nothing: either the manifest's project is now loaded, or the engine holds exactly what it held before.
    /// The previous project is saved to a temporary bundle first and put back on any exception or lost audio.</summary>
    public ManifestApplyResult Apply(IAudioEngine engine, string manifestJson, string bundleDir)
    {
        var check = Check(manifestJson, bundleDir);
        if (!check.Ok)
        {
            var why = check.Errors.Concat(check.MissingBlobs.Select(m => $"missing asset {m}")).Concat(check.CorruptBlobs.Select(c => $"corrupt asset {c}")).ToArray();
            return new ManifestApplyResult(false, why, check.Warnings, 0);
        }
        var import = TryImport(manifestJson).Import!; // parsed once already; cannot fail now

        string? backup = null;
        try
        {
            if (engine.TrackCount > 0)
            {
                backup = Path.Combine(Path.GetTempPath(), "nota-apply-backup-" + Guid.NewGuid().ToString("N"));
                var snapshot = ProjectService.Capture(engine, new TransportState(engine.Bpm, 1.0, false, false), new List<string>());
                ProjectService.Save(snapshot, backup, engine);
            }
            string[] applyWarnings;
            try { applyWarnings = ProjectService.Apply(import.Document, engine, bundleDir).ToArray(); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return RolledBack(engine, backup, $"applying failed ({e.GetType().Name}: {e.Message})", import.Warnings);
            }
            var lost = applyWarnings.Where(LosesAudio).ToArray();
            if (lost.Length > 0) return RolledBack(engine, backup, "audio could not be loaded: " + string.Join("; ", lost), import.Warnings.Concat(applyWarnings));
            return new ManifestApplyResult(true, Array.Empty<string>(), import.Warnings.Concat(applyWarnings).ToArray(), import.Document.Tracks.Count);
        }
        finally
        {
            if (backup is not null) try { Directory.Delete(backup, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static ManifestApplyResult RolledBack(IAudioEngine engine, string? backup, string reason, IEnumerable<string> warnings)
    {
        string note = "the previous project was restored";
        try
        {
            if (backup is not null) ProjectService.Apply(ProjectService.Load(backup), engine, backup);
            else engine.Reset();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            note = $"the previous project could NOT be restored ({e.GetType().Name}: {e.Message}); reopen it from disk";
        }
        return new ManifestApplyResult(false, new[] { $"{reason}; {note}" }, warnings.ToArray(), 0);
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static (ProjectManifest.ImportResult? Import, string[] Errors) TryImport(string manifestJson)
    {
        if (manifestJson is null || manifestJson.Length > MaxManifestBytes) return (null, new[] { "manifest is empty or larger than the limit" });
        try
        {
            var node = JsonNode.Parse(manifestJson) as JsonObject;
            if (node is null) return (null, new[] { "manifest is not a JSON object" });
            return (ProjectManifest.Import(node), Array.Empty<string>());
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or NotSupportedException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return (null, new[] { e.Message });
        }
    }

    /// <summary>Checks every `root.blobs` entry against the files in the bundle. Paths that leave the bundle count as missing.</summary>
    private static (List<string> Missing, List<string> Corrupt) VerifyBlobs(string manifestJson, string bundleDir)
    {
        var missing = new List<string>(); var corrupt = new List<string>();
        var blobs = (JsonNode.Parse(manifestJson) as JsonObject)?["root"]?["blobs"] as JsonObject;
        if (blobs is null) return (missing, corrupt);
        string full = Path.GetFullPath(bundleDir);
        foreach (var kv in blobs)
        {
            string file = Path.GetFullPath(Path.Combine(full, kv.Key));
            if (!file.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file)) { missing.Add(kv.Key); continue; }
            using var fs = File.OpenRead(file);
            string actual = "sha256:" + System.Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            if (!string.Equals(actual, kv.Value?.GetValue<string>(), StringComparison.Ordinal)) corrupt.Add(kv.Key);
        }
        return (missing, corrupt);
    }
}
