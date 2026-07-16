using DotnetDiagnostics.Core.ProcessDiscovery;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>
/// Host-neutral process-resolution helpers shared by every diagnostic use case that targets a
/// live .NET process (issue #285, prerequisite for the non-MCP CLI front-end in #283). Each use
/// case accepts an optional <c>processId</c>; when the caller omits it the resolver auto-selects
/// the lone visible candidate. On ambiguity / nothing visible the resolver outcome is translated
/// into a structured <see cref="DiagnosticResult{T}"/> so the front-end (LLM or CLI) never has to
/// interpret a thrown exception. Successful resolutions carry the capability digest on
/// <see cref="DiagnosticResult{T}.ResolvedProcess"/> so the obligatory inspect_process opener can
/// be skipped entirely.
/// </summary>
public static class ProcessResolutionHelpers
{
    /// <summary>
    /// Outcome of resolving an optional process id: either a successfully resolved
    /// (<see cref="ProcessId"/>, <see cref="Context"/>) pair, or a populated
    /// <see cref="Failure"/> envelope the caller should return verbatim.
    /// </summary>
    public readonly record struct ResolvedContext<T>(
        int ProcessId,
        ProcessContext? Context,
        DiagnosticResult<T>? Failure);

    /// <summary>
    /// Resolves <paramref name="processId"/> through <paramref name="resolver"/>, mapping any
    /// resolution error into a structured failure envelope.
    /// </summary>
    public static async Task<ResolvedContext<T>> ResolveContextAsync<T>(
        IProcessContextResolver resolver,
        int? processId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        var resolution = await resolver.ResolveAsync(processId, cancellationToken).ConfigureAwait(false);
        if (resolution.Error is null)
        {
            var ctx = resolution.Context!;
            return new ResolvedContext<T>(ctx.ProcessId, ctx, Failure: null);
        }

        var failure = BuildResolutionFailure<T>(resolution);
        return new ResolvedContext<T>(ProcessId: 0, Context: null, Failure: failure);
    }

    /// <summary>
    /// Stamps the resolved capability digest onto a successful result so callers can skip the
    /// obligatory bootstrap opener. No-op when <paramref name="context"/> is null.
    /// </summary>
    public static DiagnosticResult<T> WithContext<T>(
        DiagnosticResult<T> result,
        ProcessContext? context)
        => context is null ? result : result with { ResolvedProcess = context };

    private static DiagnosticResult<T> BuildResolutionFailure<T>(ProcessContextResolution resolution)
    {
        var error = resolution.Error!;
        return error.Kind switch
        {
            "NoDotnetProcessFound" => DiagnosticResult.Fail<T>(
                "No .NET process is visible to the diagnostic IPC on this host.",
                error,
                new NextActionHint(
                    "inspect_process",
                    "Confirm the target is running and shares your PID namespace + UID (containers/K8s).",
                    new Dictionary<string, object?> { ["view"] = "list" })),

            "AmbiguousDotnetProcess" => DiagnosticResult.Fail<T>(
                $"{resolution.Candidates?.Count ?? 0} .NET processes visible — pass processId explicitly.",
                error,
                new NextActionHint(
                    "inspect_process",
                    "Inspect the candidate list inline below and re-issue the call with the chosen processId.",
                    new Dictionary<string, object?> { ["view"] = "list" })),

            "EndpointUnavailable" => DiagnosticResult.Fail<T>(
                error.Message,
                error,
                new NextActionHint(
                    "inspect_process",
                    "Re-list processes — the target may have exited or the sidecar UID may not match.",
                    new Dictionary<string, object?> { ["view"] = "list" })),

            _ => DiagnosticResult.Fail<T>(error.Message, error),
        };
    }
}
