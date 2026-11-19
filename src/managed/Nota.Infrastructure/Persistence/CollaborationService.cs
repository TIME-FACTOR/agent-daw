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
        new[] { "manifest.export", "manifest.check", "manifest.apply", "stable-ids" });

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

    public ManifestApplyResult Apply(IAudioEngine engine, string manifestJson, string bundleDir)
    {
        var check = Check(manifestJson, bundleDir);
        if (!check.Ok)
        {
            var why = check.Errors.Concat(check.MissingBlobs.Select(m => $"missing asset {m}")).Concat(check.CorruptBlobs.Select(c => $"corrupt asset {c}")).ToArray();
            return new ManifestApplyResult(false, why, check.Warnings, 0);
        }
        var import = TryImport(manifestJson).Import!; // parsed once already; cannot fail now
        var warnings = import.Warnings.Concat(ProjectService.Apply(import.Document, engine, bundleDir)).ToArray();
        return new ManifestApplyResult(true, Array.Empty<string>(), warnings, import.Document.Tracks.Count);
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
