// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 Egor Khindikaynen (Nota). See LICENSES/ for license terms.
//
// A read-only view of an engine (AGENT DAW Shared Studio, P2). Fail closed: a call goes through only if its name matches
// an explicit read pattern or it is one of the three viewer transport controls; everything else, including every setter
// and every member added to IAudioEngine later, throws. The allow list is deliberately NOT a plain prefix rule, because
// prefixes hide mutators (TryDeleteClip, CancelFreeze).

using System;
using System.Reflection;
using System.Text.RegularExpressions;
using Nota.Application;

namespace Nota.Infrastructure.Studios;

public sealed class StudioReadOnlyException(string member)
    : InvalidOperationException($"Shared Studio is read-only: {member} would change the shared master. Make the change in My Studio and submit it as a proposal.")
{
    public string Member { get; } = member;
}

public class ReadOnlyEngineProxy : DispatchProxy
{
    private IAudioEngine _inner = default!;

    // Reads: property getters and Get*/TryGet*/Is*/Has* followed by a capital letter. (TryDeleteClip, CancelFreeze: not reads.)
    private static readonly Regex ReadName = new(@"^(get_[A-Za-z0-9_]+|(Get|TryGet|Is|Has)[A-Z][A-Za-z0-9]*)$", RegexOptions.Compiled);
    /// <summary>What a viewer may do to the shared project: listen, and move the playhead. Nothing that edits it.</summary>
    public static readonly string[] ViewerControls = { "Play", "StopTransport", "Seek" };

    public static bool IsAllowed(string member) => ReadName.IsMatch(member) || Array.IndexOf(ViewerControls, member) >= 0 || member == "Dispose";

    public static IAudioEngine Wrap(IAudioEngine inner)
    {
        var proxy = Create<IAudioEngine, ReadOnlyEngineProxy>();
        ((ReadOnlyEngineProxy)(object)proxy)._inner = inner;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method is null) return null;
        if (!IsAllowed(method.Name)) throw new StudioReadOnlyException(method.Name);
        if (method.Name == "Dispose") return null; // the studio manager owns the engine's lifetime
        try { return method.Invoke(_inner, args); }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}
