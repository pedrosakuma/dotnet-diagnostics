using System.Text.RegularExpressions;

namespace DotnetDiagnostics.Core.CpuSampling;

/// <summary>
/// Best-effort recognizer that folds a compiler-generated async state-machine's <c>MoveNext</c>
/// leaf frame back into its declaring async method name (issue #811, part 3). A CPU-sample leaf
/// like <c>FixTcpClientSession+&lt;WriteLoopAsync&gt;d__22.MoveNext()</c> — the actual on-CPU work
/// happening directly inside the async method's own synchronous body, between awaits — reads as
/// unfamiliar runtime plumbing to an operator scanning <c>top-methods</c>. Folded, it reads as
/// <c>FixTcpClientSession.WriteLoopAsync() [async]</c>: immediately recognizable as busy user code.
/// </summary>
/// <remarks>
/// This only renames the state machine's own <c>MoveNext</c> leaf; it does not merge separate
/// generic async/runtime plumbing frames (e.g. <c>AsyncTaskMethodBuilder.Start</c>,
/// <c>TaskAwaiter.GetResult</c>) into the business method's row — that would require call-tree-aware
/// (inclusive) attribution and is tracked as further follow-up work, not this pass. Renaming does not
/// change the aggregation key: a given async method's <c>MoveNext</c> already aggregates under its own
/// identity-derived key, so folding is purely a display-name transform applied to an already-aggregated
/// row.
/// <para>
/// Acknowledged gap: async <b>lambdas</b> and async <b>local functions</b> compile to a
/// compiler-generated state-machine type named with a bare <c>d</c> suffix (no <c>d__NN</c> arity
/// digits), e.g. <c>Program+&lt;&gt;c+&lt;&lt;Main&gt;b__0_3&gt;d.MoveNext()</c> or
/// <c>Program+&lt;&lt;Main&gt;g__LocalAsync|0_0&gt;d.MoveNext()</c> — confirmed against a live capture
/// (issue #811). These intentionally do not match <see cref="MoveNextPattern"/> and are left
/// unfolded; recognizing them safely (without misparsing the <c>g__Name|N_M</c> / <c>b__N_M</c>
/// inner naming) is tracked as further follow-up work, not this pass.
/// </para>
/// </remarks>
internal static class AsyncStateMachineFrameFolder
{
    // Matches TraceEvent's FullMethodName rendering of a compiler-generated async state machine's
    // MoveNext, e.g. "Namespace.Type+<MethodName>d__22.MoveNext()" or a nested/generic owner such as
    // "Namespace.Outer+Inner+<MethodName>d__3.MoveNext()". A generic async method's state machine
    // additionally carries its own generic arity/instantiation between "d__NN" and ".MoveNext()" —
    // e.g. "JsonTypeInfo`1+<SerializeAsync>d__15[System.__Canon].MoveNext()" (confirmed against a live
    // ASP.NET Core Kestrel/System.Text.Json capture) — so that suffix is matched but discarded.
    // Deliberately anchored to ".MoveNext()" (no arguments — MoveNext is always parameterless) to
    // avoid false positives on business methods that merely contain "d__" in an unrelated identifier.
    // Async lambdas/local functions compile to a bare "d" suffix (no "__NN" digits) and are
    // deliberately NOT matched here — see the acknowledged-gap remark above.
    private static readonly Regex MoveNextPattern = new(
        @"^(?<owner>.+)\+<(?<method>[^>]+)>d__\d+(`\d+)?(\[.*\])?\.MoveNext\(\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Attempts to fold <paramref name="method"/> from its raw compiler-generated display name into
    /// <c>Owner.Method() [async]</c>. Returns <c>false</c> (and echoes the original string) when
    /// <paramref name="method"/> does not match the recognized <c>MoveNext</c> shape.
    /// </summary>
    internal static bool TryFold(string method, out string folded)
    {
        var match = MoveNextPattern.Match(method);
        // Guard against a distinct runtime-plumbing shape confirmed against a live capture: a generic
        // async wrapper's own MoveNext (e.g. AsyncTaskMethodBuilder<T>.AsyncStateMachineBox<T>) embeds
        // the *inner* state machine's "+<Method>d__NN[...]" shape as one of ITS OWN generic type
        // arguments — e.g. "AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[VoidTaskResult,
        // HttpConnection+<ProcessRequestsAsync>d__12`1[System.__Canon]].MoveNext()". The greedy owner
        // capture above finds the innermost/rightmost "+<...>d__NN" and would misattribute the box's
        // own frame to the wrapped business method. A generic type-argument list can only appear in a
        // captured "owner" when that owner is not actually the direct declaring type, so bail out
        // whenever the owner still contains an open bracket.
        if (!match.Success || match.Groups["owner"].Value.Contains('['))
        {
            folded = method;
            return false;
        }

        var owner = Regex.Replace(match.Groups["owner"].Value, "`\\d+", string.Empty).Replace('+', '.');
        folded = $"{owner}.{match.Groups["method"].Value}() [async]";
        return true;
    }
}
