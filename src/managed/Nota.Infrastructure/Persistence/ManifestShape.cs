// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// The shape of an agentdaw.manifest/1 document, checked before the DAW trusts any of it (AGENT DAW hardening). The collaboration
// service validates manifests against the protocol's JSON Schema, but a manifest can reach the DAW from anywhere, so the DAW
// checks the parts it relies on itself: every node has a valid unique id and a type, containers have the right JSON types,
// asset hashes are sha256, and the tree is neither absurdly deep nor absurdly large. Iterative on purpose: hostile nesting must
// not be able to overflow the stack.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Nota.Infrastructure;

public static class ManifestShape
{
    public const int MaxDepth = 64;
    public const int MaxNodes = 250_000;

    /// <summary>The first problem found, or null if the manifest is well formed.</summary>
    public static string? FirstProblem(JsonNode? manifest)
    {
        if (manifest is not JsonObject m) return "the manifest is not a JSON object";
        if (m["schema"] is not JsonValue sv || !sv.TryGetValue<string>(out var schema) || schema != ProjectManifest.Schema) return "the manifest's schema is not agentdaw.manifest/1";
        if (m["projectId"] is not JsonValue pv || !pv.TryGetValue<string>(out var pid) || !ObjectIds.IsValid(pid)) return "the manifest has no valid projectId";
        if (m["root"] is not JsonObject root) return "the manifest has no root object";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<(JsonObject Node, int Depth)>();
        stack.Push((root, 1));
        while (stack.Count > 0)
        {
            var (n, depth) = stack.Pop();
            if (depth > MaxDepth) return $"the project is nested more than {MaxDepth} levels deep";
            if (n["id"] is not JsonValue iv || !iv.TryGetValue<string>(out var id) || !ObjectIds.IsValid(id)) return "an object has no valid id";
            if (!seen.Add(id)) return $"the id {id} is used twice";
            if (seen.Count > MaxNodes) return $"the project has more than {MaxNodes} objects";
            if (n["type"] is not JsonValue tv || !tv.TryGetValue<string>(out var type) || type.Length == 0 || type.Length > 64) return $"object {id} has no valid type";
            if (n["props"] is { } props && props is not JsonObject) return $"object {id}: props is not an object";
            if (n["blobs"] is { } blobs)
            {
                if (blobs is not JsonObject bo) return $"object {id}: blobs is not an object";
                foreach (var kv in bo)
                    if (kv.Value is not JsonValue hv || !hv.TryGetValue<string>(out var h) || h.Length != 71 || !h.StartsWith("sha256:", StringComparison.Ordinal) || !h[7..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                        return $"object {id}: blob {kv.Key} is not a sha256 hash";
            }
            if (n["lists"] is { } lists)
            {
                if (lists is not JsonObject lo) return $"object {id}: lists is not an object";
                foreach (var kv in lo)
                {
                    if (kv.Value is not JsonArray arr) return $"object {id}: list {kv.Key} is not an array";
                    foreach (var child in arr)
                    {
                        if (child is not JsonObject co) return $"object {id}: list {kv.Key} holds something that is not an object";
                        stack.Push((co, depth + 1));
                    }
                }
            }
        }
        return null;
    }
}
