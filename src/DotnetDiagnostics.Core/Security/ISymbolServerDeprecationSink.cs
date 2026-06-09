namespace DotnetDiagnostics.Core.Security;

/// <summary>
/// Host-neutral seam for the once-per-process deprecation telemetry emitted when a remote symbol
/// server host is accepted via the <c>Diagnostics:SymbolServerAllowlist</c> deployment-wide gate
/// (rather than an RFC 0001 <c>symbols-remote</c> modifier scope). Extracted in #288 so the Core
/// symbol-path validation owns no transport/logging assumptions: the MCP Server adapts its
/// <c>LegacyDiagnosticsFlagDeprecation</c> singleton onto this interface, while the standalone CLI
/// (a synthetic-root caller that never trips the legacy branch) simply passes <c>null</c>.
/// </summary>
/// <remarks>
/// Implementations must be idempotent per process (emit the warning at most once) and must never
/// log or echo bearer values. Mirrors the legacy gate the symbol-path validation can still fall
/// back to: the <c>SymbolServerAllowlist</c> policy.
/// </remarks>
public interface ISymbolServerDeprecationSink
{
    /// <summary>
    /// Records that symbol-path validation accepted a remote symbol server host via the curated
    /// <c>SymbolServerAllowlist</c> (default or configured) for a caller that did NOT hold the
    /// <c>symbols-remote</c> scope.
    /// </summary>
    void NotifySymbolServerAllowlistBypass();
}
