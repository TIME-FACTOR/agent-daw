// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Project <-> collaboration manifest (AGENT DAW P0-4). The manifest is the generic tree of objects
// described by the public protocol (agentdaw.manifest/1, Apache-2.0, authored separately). This codec
// is the only place that knows how Nota's project file maps onto it. It decides nothing about merging:
// diffing, change sets and merges happen outside the DAW on manifests.
//
// Mapping:
//   * root node      = the project (id supplied by the caller); props = every top-level field but `tracks`.
//   * track nodes    = list `tracks`; props = the track's fields but its clips; lists `midiClips`,
//                      `audioClips`, `sessionSlots` hold clip nodes.
//   * ids            = the saved stable ids, or the same content-derived ids Apply would assign.
//   * track references that the file stores as document INDICES (group parent, record input, MIDI source,
//     sidechain sources, CV link targets) become {"$track": "<id>"}, so adding, removing or reordering
//     tracks cannot silently repoint them. Import turns them back into indices.
//   * blobs          = root `blobs`: every bundle file the project names (samples/…, plugin-states/…) -> sha256
//     of its bytes, so an asset service can fetch exactly what is missing. Plugin state and audio stay opaque.
//
// Anything not listed above round-trips untouched as a prop value (devices, automation, modulation…): in v1
// those merge as whole values (see protocol README, "Not in v1").

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Nota.Infrastructure;

public static class ProjectManifest
{
    public const string Schema = "agentdaw.manifest/1";
    private const string Ref = "$track";

    private static readonly string[] ClipLists = { "midiClips", "audioClips", "sessionSlots" };
    private static readonly string[] ClipTypes = { "midiClip", "audioClip", "sessionSlot" };
    private static readonly char[] ClipKinds = { 'm', 'a', 's' };

    /// <summary>Builds the manifest for <paramref name="doc"/>. <paramref name="bundleDir"/> (optional) is where the
    /// bundle's files are, for the blob table. <paramref name="projectId"/> must be 32 lowercase hex chars.</summary>
    public static JsonObject Export(ProjectDocument doc, string projectId, string? version = null, string? bundleDir = null)
    {
        if (!ObjectIds.IsValid(projectId)) throw new ArgumentException("projectId must be 32 lowercase hex chars", nameof(projectId));
        var json = JsonNode.Parse(ProjectService.SerializeManifest(doc))!.AsObject();
        var tracks = json["tracks"] as JsonArray ?? new JsonArray();
        var ids = new List<string>();
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i]!.AsObject();
            ids.Add(t["id"]?.GetValue<string>() is { } s && ObjectIds.IsValid(s)
                ? s : ObjectIds.LegacyTrackId(i, t["type"]?.GetValue<int>() ?? 0, t["name"]?.GetValue<string>()));
        }
        if (ids.Distinct().Count() != ids.Count) throw new InvalidDataException("Two tracks share an id; save and reopen the project to repair it.");

        var trackNodes = new JsonArray();
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i]!.AsObject();
            string tid = ids[i];
            var node = new JsonObject { ["id"] = tid, ["type"] = "track" };
            var lists = new JsonObject();
            for (int k = 0; k < ClipLists.Length; k++)
            {
                if (t[ClipLists[k]] is not JsonArray arr || arr.Count == 0) continue;
                var nodes = new JsonArray();
                for (int c = 0; c < arr.Count; c++)
                {
                    var clip = arr[c]!.AsObject();
                    string cid = clip["id"]?.GetValue<string>() is { } cs && ObjectIds.IsValid(cs) ? cs
                        : ObjectIds.LegacyClipId(tid, ClipKinds[k], k == 2 ? clip["scene"]?.GetValue<int>() ?? c : c,
                                                 k == 2 ? 0 : clip["startBeat"]?.GetValue<double>() ?? 0, clip["name"]?.GetValue<string>());
                    var cn = new JsonObject { ["id"] = cid, ["type"] = ClipTypes[k] };
                    var cprops = (JsonObject)clip.DeepClone(); cprops.Remove("id");
                    TranslateRefs(cprops, ids, toIds: true);
                    if (cprops.Count > 0) cn["props"] = cprops;
                    nodes.Add(cn);
                }
                lists[ClipLists[k]] = nodes;
            }
            var props = (JsonObject)t.DeepClone();
            props.Remove("id");
            foreach (var l in ClipLists) props.Remove(l);
            TranslateRefs(props, ids, toIds: true, isTrack: true);
            if (props.Count > 0) node["props"] = props;
            if (lists.Count > 0) node["lists"] = lists;
            trackNodes.Add(node);
        }

        var rootProps = (JsonObject)json.DeepClone();
        rootProps.Remove("tracks");
        TranslateRefs(rootProps, ids, toIds: true);
        var root = new JsonObject { ["id"] = projectId, ["type"] = "project" };
        if (rootProps.Count > 0) root["props"] = rootProps;
        if (BlobTable(json, bundleDir) is { Count: > 0 } blobs) root["blobs"] = blobs;
        if (trackNodes.Count > 0) root["lists"] = new JsonObject { ["tracks"] = trackNodes };

        var m = new JsonObject { ["schema"] = Schema, ["projectId"] = projectId };
        if (version is not null) m["version"] = version;
        m["root"] = root;
        return m;
    }

    /// <summary>Result of <see cref="Import"/>.</summary>
    public sealed record ImportResult(ProjectDocument Document, IReadOnlyList<string> Warnings);

    /// <summary>Rebuilds a project from a manifest. A reference to a track the manifest no longer has becomes
    /// "none" (index -1, or the hardware input for a record input) and is reported in <c>Warnings</c>.</summary>
    public static ImportResult Import(JsonObject manifest)
    {
        if (manifest["schema"]?.GetValue<string>() != Schema) throw new InvalidDataException("Not an agentdaw.manifest/1 document.");
        var root = manifest["root"]?.AsObject() ?? throw new InvalidDataException("Manifest has no root.");
        var warnings = new List<string>();
        var trackNodes = (root["lists"]?["tracks"] as JsonArray)?.Select(n => n!.AsObject()).ToList() ?? new();
        var ids = trackNodes.Select(n => n["id"]!.GetValue<string>()).ToList();

        var json = root["props"] is JsonObject rp ? (JsonObject)rp.DeepClone() : new JsonObject();
        TranslateRefs(json, ids, toIds: false, warnings: warnings);
        var tracks = new JsonArray();
        foreach (var n in trackNodes)
        {
            var t = n["props"] is JsonObject tp ? (JsonObject)tp.DeepClone() : new JsonObject();
            TranslateRefs(t, ids, toIds: false, isTrack: true, warnings: warnings);
            t["id"] = n["id"]!.GetValue<string>();
            for (int k = 0; k < ClipLists.Length; k++)
            {
                if (n["lists"]?[ClipLists[k]] is not JsonArray cl) continue;
                var arr = new JsonArray();
                foreach (var cn in cl)
                {
                    var c = cn!["props"] is JsonObject cp ? (JsonObject)cp.DeepClone() : new JsonObject();
                    TranslateRefs(c, ids, toIds: false, warnings: warnings);
                    c["id"] = cn["id"]!.GetValue<string>();
                    arr.Add(c);
                }
                t[ClipLists[k]] = arr;
            }
            tracks.Add(t);
        }
        json["tracks"] = tracks;

        int version = json["formatVersion"]?.GetValue<int>() ?? 1;
        if (version > ProjectService.CurrentFormatVersion)
            throw new NotSupportedException($"This manifest is project format v{version}; this build supports up to v{ProjectService.CurrentFormatVersion}.");
        json = ProjectMigrations.Migrate(json);
        return new ImportResult(ProjectService.FromJson(json), warnings);
    }

    // --- references --------------------------------------------------------------------------------

    // Doc-index fields, by (owner object, property). Anything below -1 or equal to the field's "none" is left alone.
    // Walks the whole subtree: devices live in tracks, in the master chain and (instruments) beside them.
    private static void TranslateRefs(JsonNode? node, List<string> ids, bool toIds, bool isTrack = false, List<string>? warnings = null)
    {
        switch (node)
        {
            case JsonObject o:
                if (isTrack)
                {
                    Convert(o, "groupId", ids, toIds, none: -1, offset: 0, warnings, noneIfMissing: -1);
                    Convert(o, "midiFrom", ids, toIds, none: -1, offset: 0, warnings, noneIfMissing: -1);
                    Convert(o, "recordInput", ids, toIds, none: 1, offset: 2, warnings, noneIfMissing: 0); // 0 hardware, -1 master, n>=2 = track n-2
                }
                Convert(o, "sidechainSource", ids, toIds, none: -1, offset: 0, warnings, noneIfMissing: -1);
                Convert(o, "targetTrack", ids, toIds, none: -1, offset: 0, warnings, noneIfMissing: -1);
                // NestedSidechainDto.Source is named `source` too, but so is CvLinkDto.Source (a modulator id):
                // only convert it where the object has a `host` and `chain` (a nested sidechain).
                if (o.ContainsKey("host") && o.ContainsKey("chain") && o.ContainsKey("slot"))
                    Convert(o, "source", ids, toIds, none: -1, offset: 0, warnings, noneIfMissing: -1);
                foreach (var kv in o.ToList()) TranslateRefs(kv.Value, ids, toIds, false, warnings);
                break;
            case JsonArray a:
                foreach (var e in a) TranslateRefs(e, ids, toIds, false, warnings);
                break;
        }
    }

    private static void Convert(JsonObject o, string key, List<string> ids, bool toIds, int none, int offset, List<string>? warnings, int noneIfMissing)
    {
        if (!o.TryGetPropertyValue(key, out var v) || v is null) return;
        if (toIds)
        {
            if (v is not JsonValue jv || !jv.TryGetValue<int>(out int n)) return;
            int idx = n - offset;
            if (n < (offset == 0 ? 0 : offset) || idx < 0 || idx >= ids.Count) return; // sentinel (none / hardware / master)
            o[key] = new JsonObject { [Ref] = ids[idx] };
        }
        else if (v is JsonObject r && r.TryGetPropertyValue(Ref, out var idNode))
        {
            int at = ids.IndexOf(idNode!.GetValue<string>());
            if (at < 0) { warnings?.Add($"`{key}` named a track that is no longer in the project; cleared."); o[key] = noneIfMissing; }
            else o[key] = at + offset;
        }
    }

    // --- blobs -------------------------------------------------------------------------------------

    private static JsonObject BlobTable(JsonNode json, string? bundleDir)
    {
        var table = new JsonObject();
        if (bundleDir is null) return table;
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o: foreach (var kv in o) Walk(kv.Value); break;
                case JsonArray a: foreach (var e in a) Walk(e); break;
                case JsonValue v when v.TryGetValue<string>(out var s)
                    && (s.StartsWith(BundleContent.SamplesDir + "/", StringComparison.Ordinal) || s.StartsWith(BundleContent.StatesDir + "/", StringComparison.Ordinal)):
                    paths.Add(s); break;
            }
        }
        Walk(json);
        string full = Path.GetFullPath(bundleDir);
        foreach (var rel in paths)
        {
            string file = Path.GetFullPath(Path.Combine(full, rel));
            if (!file.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file)) continue; // never read outside the bundle
            using var fs = File.OpenRead(file);
            table[rel] = "sha256:" + System.Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        return table;
    }
}
