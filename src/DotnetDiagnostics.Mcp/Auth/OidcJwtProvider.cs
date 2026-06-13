using System.Collections.Immutable;
using System.Security.Claims;
using DotnetDiagnostics.Mcp.Security;

namespace DotnetDiagnostics.Mcp.Auth;

/// <summary>
/// A single trusted OIDC issuer the HTTP transport will accept JWTs from. The server can
/// trust more than one issuer at once (e.g. a cloud managed-identity tenant plus an
/// in-cluster Kubernetes <c>TokenRequest</c> issuer) — see <see cref="OidcJwtAuthOptions"/>.
/// </summary>
internal sealed class OidcJwtProvider
{
    public OidcJwtProvider(
        string schemeName,
        string issuer,
        string audience,
        Uri metadataAddress,
        ImmutableArray<string> scopeClaimNames,
        ImmutableArray<string> grantedScopes,
        ImmutableArray<RequiredClaimRule> requiredClaims)
    {
        SchemeName = schemeName;
        Issuer = issuer;
        Audience = audience;
        MetadataAddress = metadataAddress;
        ScopeClaimNames = scopeClaimNames;
        GrantedScopes = grantedScopes;
        RequiredClaims = requiredClaims;
    }

    public string SchemeName { get; }

    public string Issuer { get; }

    public string Audience { get; }

    public Uri MetadataAddress { get; }

    public ImmutableArray<string> ScopeClaimNames { get; }

    /// <summary>
    /// Scopes the operator grants to any token that passes this provider's issuer, audience,
    /// and required-claim checks, unioned with whatever the token's scope claim carries. Lets
    /// trusted identities whose tokens do not self-describe MCP scopes (e.g. a Kubernetes
    /// projected ServiceAccount token) still be authorized, with the identity pinned via
    /// <see cref="RequiredClaims"/>.
    /// </summary>
    public ImmutableArray<string> GrantedScopes { get; }

    public ImmutableArray<RequiredClaimRule> RequiredClaims { get; }

    public bool TryCreatePrincipal(
        ClaimsPrincipal principal,
        out BearerPrincipal? bearerPrincipal,
        out string? failureMessage)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (!ValidateRequiredClaims(principal, out failureMessage))
        {
            bearerPrincipal = null;
            return false;
        }

        var scopes = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var grantedScope in GrantedScopes)
        {
            scopes.Add(grantedScope);
        }

        foreach (var claimType in ScopeClaimNames)
        {
            foreach (var claim in principal.FindAll(claimType))
            {
                foreach (var scope in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    scopes.Add(scope);
                }
            }
        }

        if (scopes.Count == 0)
        {
            bearerPrincipal = null;
            failureMessage = ScopeClaimNames.Length == 1
                ? $"JWT is missing scope claim '{ScopeClaimNames[0]}' and no scopes are granted for this issuer."
                : $"JWT is missing any configured scope claim ({string.Join(", ", ScopeClaimNames)}) and no scopes are granted for this issuer.";
            return false;
        }

        bearerPrincipal = new BearerPrincipal(ResolvePrincipalName(principal), scopes.ToImmutable());
        failureMessage = null;
        return true;
    }

    private bool ValidateRequiredClaims(ClaimsPrincipal principal, out string? failureMessage)
    {
        foreach (var rule in RequiredClaims)
        {
            var values = principal.FindAll(rule.ClaimType)
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToArray();

            if (values.Length == 0)
            {
                failureMessage = $"JWT is missing required claim '{rule.ClaimType}'.";
                return false;
            }

            if (rule.AllowedValues.Count == 0)
            {
                continue;
            }

            if (values.Any(rule.AllowedValues.Contains))
            {
                continue;
            }

            failureMessage =
                $"JWT claim '{rule.ClaimType}' did not match any configured allowed value.";
            return false;
        }

        failureMessage = null;
        return true;
    }

    private static string ResolvePrincipalName(ClaimsPrincipal principal)
    {
        foreach (var claimType in new[] { "preferred_username", "client_id", "azp", "appid", "sub" })
        {
            var value = string.IsNullOrWhiteSpace(principal.FindFirst(claimType)?.Value)
                ? null
                : principal.FindFirst(claimType)!.Value.Trim();
            if (value is not null)
            {
                return value;
            }
        }

        return "oidc-jwt";
    }

    internal sealed record RequiredClaimRule(string ClaimType, ImmutableHashSet<string> AllowedValues);
}
