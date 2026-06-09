namespace DotnetDiagnostics.Core.ProcessDiscovery;

using DotnetDiagnostics.Core;

/// <summary>
/// Resolves the target <c>processId</c> for every diagnostic tool that operates against a
/// live .NET process. When the caller omits the id and exactly one .NET process is reachable,
/// the resolver auto-selects it transparently — saving the LLM the obligatory
/// <c>list_dotnet_processes</c> + <c>get_diagnostic_capabilities</c> opening round-trips.
/// </summary>
/// <remarks>
/// Resolution outcomes are non-throwing: the tool is expected to translate
/// <see cref="ProcessContextResolution.Error"/> into a structured <see cref="DiagnosticResult{T}"/>
/// so the LLM gets a stable <see cref="DiagnosticError.Kind"/> + a <see cref="NextActionHint"/>
/// instead of a thrown exception.
/// </remarks>
public interface IProcessContextResolver
{
    /// <summary>
    /// Resolves the caller-supplied process id (or auto-resolves one when omitted) and
    /// attaches a capability digest for the chosen target.
    /// </summary>
    /// <param name="requestedProcessId">Process id the caller passed, or <c>null</c>/<c>0</c> to request auto-resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken);

    /// <summary>
    /// Session-aware overload introduced in Phase 2 of the central-orchestrator design
    /// (issue #20). When <paramref name="sessionId"/> has a registered
    /// <see cref="SessionTargetBinding"/> in <see cref="ISessionTargetBindingStore"/> AND the
    /// caller did not pass an explicit pid, the binding wins over local auto-resolution.
    /// </summary>
    /// <remarks>
    /// An explicit, positive <paramref name="requestedProcessId"/> always wins — the binding
    /// is a default, never an override. Implementations that don't care about session
    /// bindings (e.g. test stubs) can fall through to the original overload; the default
    /// implementation does exactly that, preserving backward compatibility for every
    /// existing <see cref="IProcessContextResolver"/> implementation.
    /// </remarks>
    /// <param name="sessionId">MCP session id, or <c>null</c> when the caller has no session context (e.g. <c>--stdio</c>).</param>
    /// <param name="requestedProcessId">Process id the caller passed, or <c>null</c>/<c>0</c> to request session-binding-then-auto-resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ProcessContextResolution> ResolveAsync(string? sessionId, int? requestedProcessId, CancellationToken cancellationToken)
        => ResolveAsync(requestedProcessId, cancellationToken);
}

/// <summary>Outcome of <see cref="IProcessContextResolver.ResolveAsync(int?, CancellationToken)"/>.</summary>
/// <param name="Context">Resolved context when successful, otherwise <c>null</c>.</param>
/// <param name="Error">Structured error when resolution failed, otherwise <c>null</c>.</param>
/// <param name="Candidates">Candidate list for ambiguous resolutions so the LLM can pick without a second round-trip.</param>
public sealed record ProcessContextResolution(
    ProcessContext? Context,
    DiagnosticError? Error,
    IReadOnlyList<DotnetProcess>? Candidates = null);
