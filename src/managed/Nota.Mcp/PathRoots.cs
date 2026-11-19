// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Optional confinement of folders an MCP caller names (AGENT DAW P0-8). When NOTA_MCP_ROOTS lists one or more folders
// (separated by the platform path separator), a path a tool is given must lie inside one of them, after ".." segments
// and symbolic links are resolved. When it is not set nothing changes. Used by the collaboration tools; the older tools
// are not retrofitted.

namespace Nota.Mcp;

public static class PathRoots
{
    public const string Variable = "NOTA_MCP_ROOTS";

    public static IReadOnlyList<string> Configured()
        => (Environment.GetEnvironmentVariable(Variable) ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => RealPath(Path.GetFullPath(r))).ToArray();

    /// <summary>Null when <paramref name="path"/> is allowed, otherwise the reason it is not.</summary>
    public static string? Refusal(string path)
    {
        var roots = Configured();
        if (roots.Count == 0) return null;
        string real;
        try { real = RealPath(Path.GetFullPath(path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        { return $"the path is not valid: {e.Message}"; }
        foreach (var r in roots)
            if (real.Equals(r, StringComparison.Ordinal) || real.StartsWith(r.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        return $"the path is outside the folders this DAW allows ({Variable})";
    }

    /// <summary>The path with every existing component's symbolic link followed, so a link inside an allowed folder cannot lead out of it.</summary>
    private static string RealPath(string full)
    {
        string root = Path.GetPathRoot(full) ?? "";
        string current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.Exists && info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                next = target is null ? next : Path.GetFullPath(target.FullName);
            }
            current = next;
        }
        return current;
    }
}
