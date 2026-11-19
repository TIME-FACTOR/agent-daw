// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// MCP tools — the DAW's local collaboration contract, agentdaw.local/1 (AGENT DAW P0-7). A host
// program (Agent One) uses these to read a project out as a manifest, check a manifest (and which
// asset files it still needs), and load a manifest into the open project. They carry no policy:
// who may change what, votes and merges all happen outside the DAW. The tool names and parameters
// are a versioned contract (see the protocol package's contract/local-api.1.json); change them only
// with a new version.

using System.ComponentModel;
using ModelContextProtocol.Server;
using Nota.Application;

namespace Nota.Mcp.Tools;

[McpServerToolType]
public sealed class CollabTools(IAudioEngine engine, IEngineDispatch dispatch, IArrangementRefresh refresh,
                                IProjectVersionsAccess project, ICollaborationPort collab)
    : EngineTools(engine, dispatch, refresh)
{
    [McpServerTool(Name = "collab_info"), Description(
        "The collaboration contract this DAW speaks: api version, manifest schema, project file format and the capabilities "
        + "it implements, plus whether a project is open, where it is saved and whether it has unsaved edits. Call this first.")]
    public async Task<CollabInfoResult> Info()
    {
        var (path, dirty) = await Read(() => (project.ProjectPath, project.HasUnsavedChanges));
        var i = collab.Info();
        return new CollabInfoResult(i.Api, i.ManifestSchema, i.ProjectFormat, i.Capabilities, path, dirty);
    }

    public sealed record CollabInfoResult(string Api, string ManifestSchema, int ProjectFormat, string[] Capabilities,
        [property: Description("The open project's folder, or null while it has never been saved")] string? ProjectPath,
        [property: Description("The project has edits that are not saved; an export would not include them")] bool UnsavedChanges);

    [McpServerTool(Name = "collab_export_manifest"), Description(
        "Export the SAVED open project as an agentdaw.manifest/1 JSON document (tracks and clips keyed by stable ids, with a "
        + "sha256 table of its audio and plugin-state files). Fails, with a reason, if the project was never saved or has unsaved "
        + "edits: save first. projectId is the collaboration project's id (32 lowercase hex chars) and is not interpreted. "
        + "version is an opaque label copied into the manifest.")]
    public async Task<string> ExportManifest(string projectId, string? version = null)
    {
        var (path, dirty) = await Read(() => (project.ProjectPath, project.HasUnsavedChanges));
        if (path is null) throw new InvalidOperationException("The project has never been saved; save it first.");
        if (dirty) throw new InvalidOperationException("The project has unsaved edits that an export would not include; save it first.");
        return collab.ExportManifest(path, projectId, version);
    }

    [McpServerTool(Name = "collab_check_manifest"), Description(
        "Validate a manifest without changing anything: schema and format problems, warnings (such as a reference to a track that "
        + "is gone), the track count, and, when bundlePath is given, which of its asset files are missing from that .nota folder or "
        + "fail their sha256 (so the caller knows exactly what to fetch).")]
    public Task<ManifestCheck> CheckManifest(string manifestJson, string? bundlePath = null)
        => Task.FromResult(collab.Check(manifestJson, bundlePath));

    [McpServerTool(Name = "collab_apply_manifest"), Description(
        "Replace the OPEN project with the one in a manifest, reading its assets from bundlePath (a .nota folder that already holds "
        + "every file the manifest lists). Refuses, changing nothing, unless the manifest checks out with no missing or corrupt assets. "
        + "Requires confirmReplace = true. Also refuses while the open project has unsaved edits unless discardUnsaved = true, since "
        + "applying drops them. Returns what happened, warnings included.")]
    public async Task<ManifestApplyResult> ApplyManifest(string manifestJson, string bundlePath, bool confirmReplace = false, bool discardUnsaved = false)
    {
        if (!confirmReplace) return new ManifestApplyResult(false, new[] { "confirmReplace must be true: applying replaces the open project" }, Array.Empty<string>(), 0);
        if (await Read(() => project.HasUnsavedChanges) && !discardUnsaved)
            return new ManifestApplyResult(false, new[] { "the open project has unsaved edits; save them or pass discardUnsaved = true" }, Array.Empty<string>(), 0);
        if (!Directory.Exists(bundlePath)) return new ManifestApplyResult(false, new[] { $"bundlePath does not exist: {bundlePath}" }, Array.Empty<string>(), 0);
        return await Mutate(() => collab.Apply(E, manifestJson, bundlePath));
    }
}
