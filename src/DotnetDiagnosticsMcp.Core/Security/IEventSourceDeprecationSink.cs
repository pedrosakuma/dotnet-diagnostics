namespace DotnetDiagnosticsMcp.Core.Security;

/// <summary>
/// Host-neutral seam for the once-per-process deprecation telemetry emitted when a legacy
/// deployment-wide gate (rather than an RFC 0001 modifier scope) is the mechanism that unlocks a
/// sensitive EventSource capture. Extracted in #288 so the Core <c>collect</c> orchestration owns
/// no transport/logging assumptions: the MCP Server adapts its
/// <c>LegacyDiagnosticsFlagDeprecation</c> singleton onto this interface, while the standalone CLI
/// (a synthetic-root caller that never trips the legacy branch) simply passes <c>null</c>.
/// </summary>
/// <remarks>
/// Implementations must be idempotent per process (emit each warning at most once) and must never
/// log or echo bearer values. The two notification points mirror the legacy gates that the
/// <c>collect_event_source</c> path can still fall back to: the <c>EventSourceAllowlist</c> policy
/// and the <c>Diagnostics:AllowSensitiveHeapValues</c> flag.
/// </remarks>
public interface IEventSourceDeprecationSink
{
    /// <summary>
    /// Records that <c>collect_event_source</c> accepted a provider name via the curated allowlist
    /// (default or configured) for a caller that did NOT hold the <c>eventsource-any</c> scope.
    /// </summary>
    void NotifyEventSourceAllowlistBypass();

    /// <summary>
    /// Records that the <c>Diagnostics:AllowSensitiveHeapValues</c> flag was the path that unlocked
    /// a non-allowlisted EventSource capture for a caller that did NOT hold the
    /// <c>eventsource-any</c> scope.
    /// </summary>
    void NotifySensitiveHeapValuesFlagBypass();
}
