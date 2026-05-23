using System.Collections.Immutable;

namespace DotnetDiagnosticsMcp.Server.Security;

/// <summary>
/// Identity stamped on <see cref="Microsoft.AspNetCore.Http.HttpContext"/> after a
/// successful bearer-token authentication. Carries only the human-readable token
/// <see cref="Name"/> (safe to log) and the granted <see cref="Scopes"/> — never the
/// presented bearer value.
/// </summary>
/// <remarks>
/// Foundational type for RFC 0001 (per-tool authorization scopes). B5.2 will consume
/// this via <c>[RequireScope]</c>; this PR (B5.1) only ensures the principal is
/// available so downstream filters have something to call <see cref="HasScope"/> on.
/// The root <see cref="RootScope"/> wildcard is honoured here so consumers never need
/// to special-case it.
/// </remarks>
public sealed class BearerPrincipal
{
    /// <summary>The wildcard scope: a principal granted this single scope is treated as
    /// holding every scope. Used by the legacy <c>MCP_BEARER_TOKEN</c> back-compat path
    /// (per RFC §2.13 / §7.1) and by the ephemeral loopback fallback.</summary>
    public const string RootScope = "root";

    /// <summary>Synthetic token name attached to the legacy <c>MCP_BEARER_TOKEN</c> path
    /// and to the loopback ephemeral fallback. Documented so audit logs are
    /// self-explanatory.</summary>
    public const string LegacyRootName = "legacy-root";

    public BearerPrincipal(string name, ImmutableHashSet<string> scopes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(scopes);
        if (scopes.Count == 0)
        {
            throw new ArgumentException("Bearer principal must carry at least one scope.", nameof(scopes));
        }

        Name = name;
        Scopes = scopes;
    }

    public string Name { get; }

    public ImmutableHashSet<string> Scopes { get; }

    /// <summary>Returns <c>true</c> when the principal holds <paramref name="scope"/> or
    /// the <see cref="RootScope"/> wildcard. B5.2 callers should always go through this
    /// method rather than poking <see cref="Scopes"/> directly so the wildcard semantic
    /// stays in one place.</summary>
    public bool HasScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return Scopes.Contains(RootScope) || Scopes.Contains(scope);
    }
}
