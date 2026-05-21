namespace DotnetDiagnosticsMcp.Core.ProcessDiscovery;

/// <summary>
/// A single session→target binding held in <see cref="ISessionTargetBindingStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Per the central-orchestrator design (issue #20, Phase 2), every MCP session may opt-in to
/// a "bound target" so that subsequent tool calls within the same session resolve to that
/// target without the caller having to repeat <c>processId</c> on every request. In Phase 2
/// the binding is purely an in-process abstraction populated by tests and never by a tool —
/// Phase 3 (<c>attach_to_pod</c>) is what actually writes one in production. The store and
/// the resolver overload land in Phase 2 so Phase 3's session-binding tool ships without
/// touching every single diagnostic tool signature.
/// </para>
/// <para>
/// <see cref="Source"/> is informational and surfaced on <see cref="ProcessContext.BindingSource"/>
/// when the resolver honours the binding, so the LLM can tell at-a-glance whether the
/// target it received came from a local auto-resolve, an explicit pid argument, or an
/// orchestrator-installed binding (e.g. <c>"orchestrator-attach"</c>).
/// </para>
/// </remarks>
/// <param name="ProcessId">Operating-system process id the binding points to.</param>
/// <param name="Source">Short label describing who installed the binding (e.g. <c>"local-test"</c>, <c>"orchestrator-attach"</c>).</param>
/// <param name="ExpiresAt">Optional absolute UTC instant after which the binding is treated as expired and ignored.</param>
public sealed record SessionTargetBinding(
    int ProcessId,
    string Source,
    DateTimeOffset? ExpiresAt = null);
