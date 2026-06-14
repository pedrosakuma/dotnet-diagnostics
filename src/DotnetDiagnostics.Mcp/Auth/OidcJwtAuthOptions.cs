using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using DotnetDiagnostics.Mcp.Security;
using Microsoft.Extensions.Configuration;

namespace DotnetDiagnostics.Mcp.Auth;

/// <summary>
/// Inbound OIDC/JWT validation configuration. Holds one <see cref="OidcJwtProvider"/> per
/// trusted issuer so a single sidecar can accept tokens minted by more than one identity
/// platform at once — e.g. a cloud managed/workload identity tenant <em>and</em> an
/// in-cluster Kubernetes projected ServiceAccount token issuer. The static opaque bearer
/// path is unaffected and continues to coexist (handled in <see cref="BearerTokenMiddleware"/>).
/// </summary>
/// <remarks>
/// Configuration sources (any subset; combined into the provider list in this order):
/// <list type="bullet">
/// <item>The legacy single-issuer variables <c>MCP_OIDC_ISSUER</c>, <c>MCP_OIDC_AUDIENCE</c>,
/// <c>MCP_OIDC_SCOPE_CLAIM</c>, <c>MCP_OIDC_REQUIRED_CLAIMS_JSON</c> define the first provider.</item>
/// <item><c>MCP_OIDC_PROVIDERS_JSON</c> — a JSON array of additional providers, each
/// <c>{ "issuer", "audience", "scopeClaim"?, "requiredClaims"? }</c>.</item>
/// </list>
/// </remarks>
internal sealed class OidcJwtAuthOptions
{
    public const string DefaultSchemeName = OidcJwtAuthExtensions.JwtScheme;

    public static readonly OidcJwtAuthOptions Disabled = new(ImmutableArray<OidcJwtProvider>.Empty);

    private OidcJwtAuthOptions(ImmutableArray<OidcJwtProvider> providers)
    {
        Providers = providers;
    }

    public ImmutableArray<OidcJwtProvider> Providers { get; }

    public bool IsEnabled => !Providers.IsDefaultOrEmpty;

    // Back-compat single-provider accessors. They surface the first (default) provider so the
    // historical single-issuer call sites and tests keep working; multi-issuer call sites
    // iterate Providers directly.
    public Uri? MetadataAddress => IsEnabled ? Providers[0].MetadataAddress : null;

    public ImmutableArray<string> ScopeClaimNames =>
        IsEnabled ? Providers[0].ScopeClaimNames : ImmutableArray<string>.Empty;

    public ImmutableArray<string> GrantedScopes =>
        IsEnabled ? Providers[0].GrantedScopes : ImmutableArray<string>.Empty;

    public ImmutableArray<OidcJwtProvider.RequiredClaimRule> RequiredClaims =>
        IsEnabled ? Providers[0].RequiredClaims : ImmutableArray<OidcJwtProvider.RequiredClaimRule>.Empty;

    public bool TryCreatePrincipal(
        ClaimsPrincipal principal,
        out BearerPrincipal? bearerPrincipal,
        out string? failureMessage)
    {
        if (!IsEnabled)
        {
            bearerPrincipal = null;
            failureMessage = "OIDC/JWT auth is not configured.";
            return false;
        }

        return Providers[0].TryCreatePrincipal(principal, out bearerPrincipal, out failureMessage);
    }

    public static OidcJwtAuthOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var providers = ImmutableArray.CreateBuilder<OidcJwtProvider>();

        var defaultProvider = ParseDefaultProvider(configuration);
        if (defaultProvider is not null)
        {
            providers.Add(defaultProvider);
        }

        var providersJson = TrimToNull(configuration["MCP_OIDC_PROVIDERS_JSON"]);
        if (providersJson is not null)
        {
            ParseAdditionalProviders(providersJson, providers);
        }

        if (providers.Count == 0)
        {
            return Disabled;
        }

        return new OidcJwtAuthOptions(providers.ToImmutable());
    }

    private static OidcJwtProvider? ParseDefaultProvider(IConfiguration configuration)
    {
        var issuer = TrimToNull(configuration["MCP_OIDC_ISSUER"]);
        var audience = TrimToNull(configuration["MCP_OIDC_AUDIENCE"]);
        var scopeClaimName = TrimToNull(configuration["MCP_OIDC_SCOPE_CLAIM"]);
        var grantedScopes = TrimToNull(configuration["MCP_OIDC_GRANTED_SCOPES"]);
        var requiredClaimsJson = TrimToNull(configuration["MCP_OIDC_REQUIRED_CLAIMS_JSON"]);

        if (issuer is null &&
            audience is null &&
            scopeClaimName is null &&
            grantedScopes is null &&
            requiredClaimsJson is null)
        {
            return null;
        }

        if (issuer is null || audience is null)
        {
            throw new InvalidOperationException(
                "OIDC/JWT auth requires both MCP_OIDC_ISSUER and MCP_OIDC_AUDIENCE. " +
                "Set both values together or leave both unset to keep legacy bearer behavior.");
        }

        return BuildProvider(
            DefaultSchemeName,
            issuer,
            audience,
            scopeClaimName,
            ParseScopeList(grantedScopes),
            requiredClaimsJson is null
                ? ImmutableArray<OidcJwtProvider.RequiredClaimRule>.Empty
                : ParseRequiredClaims(requiredClaimsJson));
    }

    private static void ParseAdditionalProviders(
        string json,
        ImmutableArray<OidcJwtProvider>.Builder providers)
    {
        using var document = ParseJsonDocument(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "MCP_OIDC_PROVIDERS_JSON must be a JSON array of { issuer, audience, scopeClaim?, requiredClaims? } objects.");
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "MCP_OIDC_PROVIDERS_JSON entries must be JSON objects.");
            }

            var issuer = TrimToNull(GetOptionalString(element, "issuer"));
            var audience = TrimToNull(GetOptionalString(element, "audience"));
            if (issuer is null || audience is null)
            {
                throw new InvalidOperationException(
                    "Each MCP_OIDC_PROVIDERS_JSON entry requires non-empty 'issuer' and 'audience'.");
            }

            var scopeClaimName = TrimToNull(GetOptionalString(element, "scopeClaim"));
            var grantedScopes = ParseGrantedScopes(element);

            var requiredClaims = ImmutableArray<OidcJwtProvider.RequiredClaimRule>.Empty;
            if (element.TryGetProperty("requiredClaims", out var requiredClaimsElement) &&
                requiredClaimsElement.ValueKind != JsonValueKind.Null)
            {
                requiredClaims = ParseRequiredClaims(requiredClaimsElement);
            }

            var schemeName = providers.Count == 0
                ? DefaultSchemeName
                : $"{DefaultSchemeName}-{providers.Count}";

            providers.Add(BuildProvider(schemeName, issuer, audience, scopeClaimName, grantedScopes, requiredClaims));
        }
    }

    private static ImmutableArray<string> ParseGrantedScopes(JsonElement element)
    {
        if (!element.TryGetProperty("grantedScopes", out var grantedScopesElement))
        {
            return ImmutableArray<string>.Empty;
        }

        switch (grantedScopesElement.ValueKind)
        {
            case JsonValueKind.Null:
                return ImmutableArray<string>.Empty;
            case JsonValueKind.String:
                return ParseScopeList(grantedScopesElement.GetString());
            case JsonValueKind.Array:
                var scopes = ImmutableArray.CreateBuilder<string>();
                foreach (var item in grantedScopesElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidOperationException(
                            "MCP_OIDC_PROVIDERS_JSON 'grantedScopes' array must contain only strings.");
                    }

                    var scope = TrimToNull(item.GetString());
                    if (scope is not null)
                    {
                        scopes.Add(scope);
                    }
                }

                return scopes.ToImmutable();
            default:
                throw new InvalidOperationException(
                    "MCP_OIDC_PROVIDERS_JSON 'grantedScopes' must be a string or an array of strings.");
        }
    }

    private static readonly char[] ScopeSeparators = { ' ', ',', '\t', '\n', '\r' };

    private static ImmutableArray<string> ParseScopeList(string? value)
    {
        if (value is null)
        {
            return ImmutableArray<string>.Empty;
        }

        var scopes = value.Split(
            ScopeSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ImmutableArray.Create(scopes);
    }

    private static OidcJwtProvider BuildProvider(
        string schemeName,
        string issuer,
        string audience,
        string? scopeClaimName,
        ImmutableArray<string> grantedScopes,
        ImmutableArray<OidcJwtProvider.RequiredClaimRule> requiredClaims)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                $"OIDC issuer must be an absolute URI. Value '{issuer}' is invalid.");
        }

        var scopeClaimNames = scopeClaimName is null
            ? ImmutableArray.Create("scp", "scope")
            : ImmutableArray.Create(scopeClaimName);

        return new OidcJwtProvider(
            schemeName,
            issuer,
            audience,
            BuildMetadataAddress(issuer),
            scopeClaimNames,
            grantedScopes,
            requiredClaims);
    }

    private static ImmutableArray<OidcJwtProvider.RequiredClaimRule> ParseRequiredClaims(string json)
    {
        using var document = ParseJsonDocument(json);
        return ParseRequiredClaims(document.RootElement);
    }

    private static ImmutableArray<OidcJwtProvider.RequiredClaimRule> ParseRequiredClaims(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Required-claims config must be a JSON object mapping claim names to a string, null, or an array of strings.");
        }

        var rules = ImmutableArray.CreateBuilder<OidcJwtProvider.RequiredClaimRule>();
        foreach (var property in root.EnumerateObject())
        {
            var claimType = TrimToNull(property.Name);
            if (claimType is null)
            {
                throw new InvalidOperationException(
                    "Required-claims config contains an empty claim name.");
            }

            var allowedValues = property.Value.ValueKind switch
            {
                JsonValueKind.Null => ImmutableHashSet.Create<string>(StringComparer.Ordinal),
                JsonValueKind.String => ImmutableHashSet.Create(StringComparer.Ordinal, GetRequiredString(claimType, property.Value)),
                JsonValueKind.Array => ParseAllowedValues(claimType, property.Value),
                _ => throw new InvalidOperationException(
                    $"Required-claims config claim '{claimType}' must map to null, a string, or an array of strings."),
            };

            rules.Add(new OidcJwtProvider.RequiredClaimRule(claimType, allowedValues));
        }

        return rules.ToImmutable();
    }

    private static ImmutableHashSet<string> ParseAllowedValues(string claimType, JsonElement element)
    {
        var values = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    $"Required-claims config claim '{claimType}' array must contain only strings.");
            }

            values.Add(GetRequiredString(claimType, item));
        }

        return values.ToImmutable();
    }

    private static JsonDocument ParseJsonDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"OIDC configuration contains invalid JSON: {ex.Message}", ex);
        }
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new InvalidOperationException(
                $"MCP_OIDC_PROVIDERS_JSON property '{propertyName}' must be a string."),
        };
    }

    private static string GetRequiredString(string claimType, JsonElement element)
    {
        var value = TrimToNull(element.GetString());
        if (value is null)
        {
            throw new InvalidOperationException(
                $"Required-claims config claim '{claimType}' contains an empty string.");
        }

        return value;
    }

    private static Uri BuildMetadataAddress(string issuer)
        => new(issuer.TrimEnd('/') + "/.well-known/openid-configuration", UriKind.Absolute);

    private static string? TrimToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
