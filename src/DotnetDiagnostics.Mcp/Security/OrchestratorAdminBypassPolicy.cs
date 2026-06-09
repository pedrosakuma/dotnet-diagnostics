using System.Threading;
using DotnetDiagnostics.Mcp.Orchestrator;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Mcp.Security;

/// <summary>
/// B5.3 (issue #184) — single source of truth for "may this caller bypass the
/// cross-session owner check on an investigation handle?". Scope-first: the
/// per-bearer <c>orchestrator-admin</c> modifier scope (RFC 0001 §2.7) is the
/// primary gate. The deployment-wide
/// <see cref="OrchestratorOptions.AllowCrossSessionAdmin"/> flag remains accepted
/// as a deprecated alias so existing operator deployments keep working unchanged.
/// </summary>
/// <remarks>
/// Centralising the policy keeps the bypass semantics identical across
/// <c>list_active_investigations</c>, <c>detach_from_pod</c> and the H6 proxy
/// owner check — drift between the three call sites would re-introduce the
/// cross-session privilege bug B3 closed.
/// </remarks>
internal static class OrchestratorAdminBypassPolicy
{
    /// <summary>Modifier-scope name the bearer must carry explicitly (root tokens
    /// do NOT auto-grant it — see <see cref="BearerPrincipal.HasExplicitScope"/>).</summary>
    public const string AdminScope = "orchestrator-admin";

    // 0 = not warned yet, 1 = already warned. Process-wide one-shot — the legacy
    // flag is deployment configuration, not per-request state.
    private static int s_legacyFlagWarned;

    /// <summary>Returns <c>true</c> when the caller is allowed to act across MCP
    /// sessions on orchestrator-owned handles. The first call that resolves to
    /// "allowed via the legacy flag" logs a deprecation warning exactly once
    /// per process.</summary>
    public static bool IsBypassAllowed(BearerPrincipal? principal, OrchestratorOptions options, ILogger logger)
    {
        if (principal is not null && principal.HasExplicitScope(AdminScope))
        {
            return true;
        }

        if (options.AllowCrossSessionAdmin)
        {
            WarnLegacyFlagOnce(logger);
            return true;
        }

        return false;
    }

    private static void WarnLegacyFlagOnce(ILogger logger)
    {
        if (Interlocked.CompareExchange(ref s_legacyFlagWarned, 1, 0) == 0)
        {
            logger.LogWarning(
                "OrchestratorOptions.AllowCrossSessionAdmin is deprecated. Grant the 'orchestrator-admin' scope to the operator token instead. The flag will be removed in a future release.");
        }
    }

    /// <summary>Test hook — resets the once-per-process warning latch so unit tests
    /// can assert log emissions deterministically across runs in the same AppDomain.</summary>
    internal static void ResetWarningLatchForTests() => Interlocked.Exchange(ref s_legacyFlagWarned, 0);
}
