using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Security;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>
/// Host-neutral validation of a caller-supplied <c>symbolPath</c> against the configured
/// <see cref="SymbolServerAllowlist"/>. Extracted from the MCP Server in #288 so the standalone CLI
/// and the MCP tools share one source of truth. The transport-specific bypass decision (does the
/// caller hold the RFC 0001 <c>symbols-remote</c> scope?) is hoisted into a precomputed
/// <c>principalAllowsSymbolsRemote</c> flag, and the legacy deprecation telemetry is routed through
/// the <see cref="ISymbolServerDeprecationSink"/> seam.
/// </summary>
public static class SymbolPathValidation
{
    /// <summary>
    /// Returns a denial <see cref="DiagnosticResult{T}"/> when <paramref name="symbolPath"/>
    /// references a remote symbol server host that is not on <paramref name="allowlist"/>, or
    /// <see langword="null"/> when the path is acceptable. When the caller holds the
    /// <c>symbols-remote</c> scope (<paramref name="principalAllowsSymbolsRemote"/> = true) the
    /// allowlist is bypassed entirely.
    /// </summary>
    public static DiagnosticResult<T>? Validate<T>(
        SymbolServerAllowlist allowlist,
        string? symbolPath,
        bool principalAllowsSymbolsRemote,
        ISymbolServerDeprecationSink? deprecation = null)
    {
        if (principalAllowsSymbolsRemote)
        {
            return null;
        }
        var validation = allowlist.Validate(symbolPath);
        if (validation.IsAllowed)
        {
            // RFC 0001 §7.3: only emit when a remote host was actually accepted (not for
            // null / empty / pure local paths). Defers to SymbolServerAllowlist's own
            // tokenizer so a local cache directory whose name contains "http://" is not
            // a false positive.
            if (deprecation is not null && SymbolServerAllowlist.ContainsRemoteUrl(symbolPath))
            {
                deprecation.NotifySymbolServerAllowlistBypass();
            }
            return null;
        }
        return DiagnosticResult.Fail<T>(
            $"symbolPath references remote symbol server host '{validation.DeniedHost}' which is not on the allowlist.",
            new DiagnosticError(
                "SymbolServerNotAllowed",
                "Remote symbol servers are denied by default. Add the host to `Diagnostics:SymbolServerAllowlist` (env: `Diagnostics__SymbolServerAllowlist__0=<host>`), grant the caller the `symbols-remote` scope, or drop the `srv*http(s)://…` segment and rely on the local symbol cache. Tracked by issue #165.",
                validation.DeniedSegment));
    }
}
