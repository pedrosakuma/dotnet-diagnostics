using System.Security.Claims;
using DotnetDiagnostics.Mcp.Auth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class OidcJwtAuthOptionsTests
{
    [Fact]
    public void FromConfiguration_WithNoOidcKeys_ReturnsDisabled()
    {
        var options = OidcJwtAuthOptions.FromConfiguration(new ConfigurationBuilder().Build());

        options.IsEnabled.Should().BeFalse();
        options.ScopeClaimNames.Should().BeEmpty();
    }

    [Fact]
    public void FromConfiguration_ParsesCustomScopeClaim_AndRequiredClaims()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MCP_OIDC_ISSUER"] = "https://issuer.example.test/tenant",
            ["MCP_OIDC_AUDIENCE"] = "dotnet-diagnostics-mcp",
            ["MCP_OIDC_SCOPE_CLAIM"] = "roles",
            ["MCP_OIDC_REQUIRED_CLAIMS_JSON"] = "{\"azp\":\"diag-client\",\"tenant\":null}",
        }).Build();

        var options = OidcJwtAuthOptions.FromConfiguration(configuration);

        options.IsEnabled.Should().BeTrue();
        options.MetadataAddress!.AbsoluteUri.Should().Be("https://issuer.example.test/tenant/.well-known/openid-configuration");
        options.ScopeClaimNames.Should().Equal("roles");
        options.RequiredClaims.Should().HaveCount(2);
    }

    [Fact]
    public void TryCreatePrincipal_Merges_Default_Scope_Claims_And_Required_Claims()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MCP_OIDC_ISSUER"] = "https://issuer.example.test",
            ["MCP_OIDC_AUDIENCE"] = "dotnet-diagnostics-mcp",
            ["MCP_OIDC_REQUIRED_CLAIMS_JSON"] = "{\"azp\":\"diag-client\"}",
        }).Build();
        var options = OidcJwtAuthOptions.FromConfiguration(configuration);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("scp", "read-counters eventpipe"),
            new Claim("scope", "heap-read"),
            new Claim("azp", "diag-client"),
            new Claim("preferred_username", "entra-client"),
        }));

        var ok = options.TryCreatePrincipal(principal, out var bearerPrincipal, out var failureMessage);

        ok.Should().BeTrue();
        failureMessage.Should().BeNull();
        bearerPrincipal.Should().NotBeNull();
        bearerPrincipal!.Name.Should().Be("entra-client");
        bearerPrincipal.Scopes.Should().BeEquivalentTo(new[] { "read-counters", "eventpipe", "heap-read" });
    }

    [Fact]
    public void FromConfiguration_ParsesMultipleProviders_FromProvidersJson()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MCP_OIDC_ISSUER"] = "https://entra.example.test/tenant/v2.0",
            ["MCP_OIDC_AUDIENCE"] = "api://dotnet-diagnostics-mcp",
            ["MCP_OIDC_PROVIDERS_JSON"] =
                "[{\"issuer\":\"https://kubernetes.default.svc.cluster.local\"," +
                "\"audience\":\"dotnet-diagnostics-mcp\"," +
                "\"scopeClaim\":\"scope\"," +
                "\"requiredClaims\":{\"sub\":\"system:serviceaccount:diag:investigator\"}}]",
        }).Build();

        var options = OidcJwtAuthOptions.FromConfiguration(configuration);

        options.IsEnabled.Should().BeTrue();
        options.Providers.Should().HaveCount(2);

        var entra = options.Providers[0];
        entra.SchemeName.Should().Be(OidcJwtAuthOptions.DefaultSchemeName);
        entra.Issuer.Should().Be("https://entra.example.test/tenant/v2.0");
        entra.Audience.Should().Be("api://dotnet-diagnostics-mcp");

        var k8s = options.Providers[1];
        k8s.SchemeName.Should().Be($"{OidcJwtAuthOptions.DefaultSchemeName}-1");
        k8s.Issuer.Should().Be("https://kubernetes.default.svc.cluster.local");
        k8s.MetadataAddress.AbsoluteUri.Should()
            .Be("https://kubernetes.default.svc.cluster.local/.well-known/openid-configuration");
        k8s.ScopeClaimNames.Should().Equal("scope");
        k8s.RequiredClaims.Should().ContainSingle().Which.ClaimType.Should().Be("sub");
    }

    [Fact]
    public void FromConfiguration_ProvidersJsonOnly_DefaultsFirstSchemeName()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MCP_OIDC_PROVIDERS_JSON"] =
                "[{\"issuer\":\"https://issuer.example.test\",\"audience\":\"dotnet-diagnostics-mcp\"}]",
        }).Build();

        var options = OidcJwtAuthOptions.FromConfiguration(configuration);

        options.IsEnabled.Should().BeTrue();
        options.Providers.Should().ContainSingle()
            .Which.SchemeName.Should().Be(OidcJwtAuthOptions.DefaultSchemeName);
    }

    [Fact]
    public void FromConfiguration_ProvidersJsonMissingAudience_Throws()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MCP_OIDC_PROVIDERS_JSON"] = "[{\"issuer\":\"https://issuer.example.test\"}]",
        }).Build();

        var act = () => OidcJwtAuthOptions.FromConfiguration(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*issuer*audience*");
    }

    [Fact]
    public void FromConfiguration_ParsesGrantedScopes_FromProvidersJsonAndEnv()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MCP_OIDC_ISSUER"] = "https://issuer.example.test",
            ["MCP_OIDC_AUDIENCE"] = "dotnet-diagnostics-mcp",
            ["MCP_OIDC_GRANTED_SCOPES"] = "read-counters eventpipe",
            ["MCP_OIDC_PROVIDERS_JSON"] =
                "[{\"issuer\":\"https://kubernetes.default.svc.cluster.local\"," +
                "\"audience\":\"dotnet-diagnostics-mcp\"," +
                "\"grantedScopes\":[\"read-counters\"]}]",
        }).Build();

        var options = OidcJwtAuthOptions.FromConfiguration(configuration);

        options.Providers.Should().HaveCount(2);
        options.Providers[0].GrantedScopes.Should().Equal("read-counters", "eventpipe");
        options.Providers[1].GrantedScopes.Should().Equal("read-counters");
    }
}
