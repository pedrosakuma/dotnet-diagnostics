namespace DotnetDiagnosticsMcp.Core.ProcessDiscovery;

/// <summary>
/// Per-MCP-session storage of "which target this session is bound to". Used by
/// <see cref="IProcessContextResolver"/>'s session-aware overload to resolve a target without
/// the caller having to repeat <c>processId</c> on every tool call.
/// </summary>
/// <remarks>
/// <para>
/// Phase 2 of the central-orchestrator design (issue #20) introduces this store as the seam
/// Phase 3 will write into when <c>attach_to_pod</c> ships. In Phase 2 nothing populates it
/// in production, so resolver behaviour is identical to the pre-orchestrator world — see
/// <c>docs/central-orchestrator-design.md</c> §8. Use <c>SetBinding</c> (not <c>Set</c>) to
/// avoid clashing with the reserved <c>Set</c> property keyword in some CLR languages.
/// </para>
/// <para>
/// Implementations MUST be thread-safe: MCP sessions can issue concurrent tool calls and
/// the resolver lookup happens on every call.
/// </para>
/// </remarks>
public interface ISessionTargetBindingStore
{
    /// <summary>
    /// Returns the binding for <paramref name="sessionId"/>, or <c>null</c> when no binding
    /// is registered (or it has expired and the implementation has evicted it).
    /// </summary>
    /// <param name="sessionId">MCP session id. May be <c>null</c>/empty; implementations MUST return <c>null</c> in that case.</param>
    SessionTargetBinding? TryGet(string? sessionId);

    /// <summary>
    /// Registers (or replaces) the binding for <paramref name="sessionId"/>.
    /// </summary>
    /// <param name="sessionId">MCP session id. MUST be non-null and non-empty.</param>
    /// <param name="binding">Binding to register.</param>
    void SetBinding(string sessionId, SessionTargetBinding binding);

    /// <summary>
    /// Removes the binding for <paramref name="sessionId"/> if one exists. Returns <c>true</c>
    /// when a binding was actually removed.
    /// </summary>
    bool Remove(string? sessionId);
}
