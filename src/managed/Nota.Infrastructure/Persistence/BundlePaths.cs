// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// Where a file named INSIDE a project may live (AGENT DAW safety). A project file, and now a manifest that arrives from another
// producer, names its audio and plugin-state files by relative path. Those names are untrusted: without a check, "../../Users/x/
// private.wav" or "/etc/anything" would make the DAW read files outside the project (and a later save would copy them into it,
// ready to be shared). Resolve returns the file's path only if it stays inside the bundle after ".." segments and symbolic links
// are resolved; otherwise null.

using System;
using System.IO;

namespace Nota.Infrastructure;

public static class BundlePaths
{
    /// <summary>The name used for a path that was refused. It does not exist, so every loader falls through to its ordinary
    /// "couldn't reload this file" handling instead of reading something it must not.</summary>
    public const string Blocked = ".agent-daw-refused-path";

    public static string? Resolve(string bundleDir, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Length > 1024 || relative.Contains('\0')) return null;
        string rel = relative.Replace('\\', '/');                    // both separators separate on every platform in a project file
        if (rel.StartsWith('/') || rel.Contains(':')) return null;   // rooted, a drive letter, or a Windows alternate data stream
        foreach (var segment in rel.Split('/'))
            if (segment == "..") return null;
        string root = RealPath(Path.GetFullPath(bundleDir));
        string full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        string real = RealPath(full);
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return real.StartsWith(prefix, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>Like <see cref="Resolve"/> but never null: a refused name becomes a path that does not exist.</summary>
    public static string ResolveOrBlocked(string bundleDir, string relative)
        => Resolve(bundleDir, relative) ?? Path.Combine(bundleDir, Blocked);

    /// <summary>The path with every existing component's symbolic link followed, so a link inside the bundle cannot lead out of it.</summary>
    public static string RealPath(string full) => RealPath(full, new int[1]);

    // `hops` is shared across the whole resolution, including the resolution of each link's own target, so a loop ends.
    private static string RealPath(string full, int[] hops)
    {
        string root = Path.GetPathRoot(full) ?? "";
        string current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.Exists && info.LinkTarget is not null)
            {
                if (++hops[0] > 40) return Path.Combine(current, Blocked);        // a link loop
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                // A target's own path can pass through other links (on macOS even /var and /tmp are links): resolve it too.
                if (target is not null) next = RealPath(Path.GetFullPath(target.FullName), hops);
            }
            current = next;
        }
        return current;
    }
}
