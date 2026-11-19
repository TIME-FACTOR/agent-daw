// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// The DAW's generic, local collaboration surface (AGENT DAW P0-7, contract agentdaw.local/1).
// It moves whole project snapshots in and out of the DAW as manifests (agentdaw.manifest/1) and
// checks them. It knows nothing about who is collaborating, votes, permissions or networks: those
// live outside the DAW and talk to it over the MCP tools built on this port.

namespace Nota.Application;

public sealed record CollabInfo(string Api, string ManifestSchema, int ProjectFormat, string[] Capabilities);

/// <summary>Outcome of checking a manifest. <see cref="Ok"/> is false for anything that stops it being applied;
/// missing or corrupt blobs are listed separately so an asset service knows exactly what to fetch.</summary>
public sealed record ManifestCheck(bool Ok, string[] Errors, string[] Warnings, int Tracks, string[] MissingBlobs, string[] CorruptBlobs);

public sealed record ManifestApplyResult(bool Applied, string[] Errors, string[] Warnings, int Tracks);

public interface ICollaborationPort
{
    CollabInfo Info();

    /// <summary>The saved project at <paramref name="bundleDir"/> as manifest JSON (asset hashes read from the bundle).</summary>
    string ExportManifest(string bundleDir, string projectId, string? version);

    /// <summary>Parses and validates a manifest; with <paramref name="bundleDir"/>, also verifies its blobs against the bundle.</summary>
    ManifestCheck Check(string manifestJson, string? bundleDir);

    /// <summary>Replaces the engine's project with the manifest's. Refuses (Applied = false, nothing changed) unless
    /// <see cref="Check"/> passes with no missing or corrupt blobs in <paramref name="bundleDir"/>.</summary>
    ManifestApplyResult Apply(IAudioEngine engine, string manifestJson, string bundleDir);
}
