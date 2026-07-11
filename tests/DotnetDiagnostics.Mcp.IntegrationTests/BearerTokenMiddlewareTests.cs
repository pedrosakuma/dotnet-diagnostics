using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Mcp.Auth;
using DotnetDiagnostics.Mcp.Hosting;
using DotnetDiagnostics.Mcp.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

/// <summary>Tests for the scoped <see cref="BearerTokenMiddleware"/>. Covers happy
/// path principal stamping, the structured 401 envelope, the missing-header path,
/// and an end-to-end loop through a <see cref="WebApplicationFactory{TEntryPoint}"/>
/// configured with <c>Auth:BearerTokens</c>. Env-mutating cases live in this
/// collection so the WebApplicationFactory and registry tests don't race over
/// <c>MCP_BEARER_TOKEN</c>.</summary>
[Collection(nameof(EnvSerial))]
public sealed class BearerTokenMiddlewareTests
{
    private static BearerTokenRegistry RegistryWith(params (string Name, string Token, string[] Scopes)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < entries.Length; i++)
        {
            dict[$"Auth:BearerTokens:{i}:Name"] = entries[i].Name;
            dict[$"Auth:BearerTokens:{i}:Token"] = entries[i].Token;
            for (var j = 0; j < entries[i].Scopes.Length; j++)
            {
                dict[$"Auth:BearerTokens:{i}:Scopes:{j}"] = entries[i].Scopes[j];
            }
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        using var env = EnvScope.Clear("MCP_BEARER_TOKEN");
        return BearerTokenRegistry.Build(config, NullLogger.Instance, allowEphemeralFallback: true);
    }

    private static async Task<HttpContext> RunAsync(
        IPrincipalResolver resolver,
        string? authorization,
        ILogger<BearerTokenMiddleware>? logger = null,
        string path = "/mcp",
        OidcJwtAuthOptions? oidcOptions = null,
        IServiceProvider? requestServices = null)
    {
        var nextCalled = false;
        var middleware = new BearerTokenMiddleware(
            ctx => { nextCalled = true; return Task.CompletedTask; },
            resolver,
            oidcOptions ?? OidcJwtAuthOptions.Disabled,
            logger ?? NullLogger<BearerTokenMiddleware>.Instance,
            new OrchestratorObservabilityOptions());

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        ctx.RequestServices = requestServices ?? new ServiceCollection().BuildServiceProvider();
        if (authorization is not null)
        {
            ctx.Request.Headers.Authorization = authorization;
        }

        await middleware.InvokeAsync(ctx);

        ctx.Items["__nextCalled"] = nextCalled;
        ctx.Response.Body.Position = 0;
        return ctx;
    }

    private static OidcJwtAuthOptions OidcOptionsWithProviders(params (string Issuer, string Audience)[] providers)
    {
        var settings = new Dictionary<string, string?>();
        if (providers.Length > 0)
        {
            settings["MCP_OIDC_ISSUER"] = providers[0].Issuer;
            settings["MCP_OIDC_AUDIENCE"] = providers[0].Audience;
        }

        if (providers.Length > 1)
        {
            var extras = providers
                .Skip(1)
                .Select(provider => new Dictionary<string, string>
                {
                    ["issuer"] = provider.Issuer,
                    ["audience"] = provider.Audience,
                });
            settings["MCP_OIDC_PROVIDERS_JSON"] = JsonSerializer.Serialize(extras);
        }

        return OidcJwtAuthOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    private static string CreateUnsignedJwt(string issuer, params string[] audiences)
    {
        object audienceValue = audiences.Length == 1 ? audiences[0] : audiences;
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["iss"] = issuer,
            ["aud"] = audienceValue,
        });

        return string.Concat(
            Base64UrlEncode("{\"alg\":\"none\",\"typ\":\"JWT\"}"),
            ".",
            Base64UrlEncode(payload),
            ".signature");
    }

    private static string Base64UrlEncode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    [Fact]
    public async Task ValidToken_StampsPrincipal_AndCallsNext()
    {
        var registry = RegistryWith(("ops-viewer", "tok-aaa", new[] { "read-counters" }));

        var ctx = await RunAsync(registry, "Bearer tok-aaa");

        ((bool)ctx.Items["__nextCalled"]!).Should().BeTrue();
        var principal = ctx.GetBearerPrincipal();
        principal.Should().NotBeNull();
        principal!.Name.Should().Be("ops-viewer");
        principal.HasScope("read-counters").Should().BeTrue();
    }

    [Fact]
    public async Task MissingHeader_Returns401_WithEnvelope_AndDoesNotCallNext()
    {
        var registry = RegistryWith(("x", "tok-x", new[] { "read-counters" }));

        var ctx = await RunAsync(registry, authorization: null);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        ctx.Response.Headers.WWWAuthenticate.ToString().Should().Be("Bearer");
        ctx.Response.ContentType.Should().Be("application/json");
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("error").GetProperty("kind").GetString().Should().Be("unauthenticated");
        doc.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Be("invalid bearer token");
        ((bool)ctx.Items["__nextCalled"]!).Should().BeFalse();
        ctx.GetBearerPrincipal().Should().BeNull();
    }

    [Fact]
    public async Task BadToken_Returns401_AndDoesNotLeakTokenValue()
    {
        var registry = RegistryWith(("x", "real-tok", new[] { "read-counters" }));
        var capture = new ListLoggerProvider();
        using var lf = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Information));
        var logger = lf.CreateLogger<BearerTokenMiddleware>();

        const string forged = "BAD-secret-shouldnt-be-logged";
        var ctx = await RunAsync(registry, $"Bearer {forged}", logger);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        ((bool)ctx.Items["__nextCalled"]!).Should().BeFalse();

        capture.Records.Should().NotBeEmpty();
        foreach (var record in capture.Records)
        {
            record.Message.Should().NotContain(forged, "the presented bearer must never appear in any log line");
            record.Message.Should().NotContain("real-tok", "registered token values must never appear in logs");
        }
    }

    [Fact]
    public async Task MalformedScheme_Returns401()
    {
        var registry = RegistryWith(("x", "tok", new[] { "read-counters" }));

        var ctx = await RunAsync(registry, "Basic tok");

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task HealthPath_BypassesAuth()
    {
        var registry = RegistryWith(("x", "tok", new[] { "read-counters" }));

        var ctx = await RunAsync(registry, authorization: null, path: "/health");

        ((bool)ctx.Items["__nextCalled"]!).Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task AllowPath_LogsTokenName_AtInformation()
    {
        var registry = RegistryWith(("ops-viewer", "tok-aaa", new[] { "read-counters" }));
        var capture = new ListLoggerProvider();
        using var lf = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Information));

        await RunAsync(registry, "Bearer tok-aaa", lf.CreateLogger<BearerTokenMiddleware>());

        capture.Records.Should().ContainSingle(r =>
            r.Level == LogLevel.Information && r.Message.Contains("ops-viewer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DenyPath_LogsRemoteIp_AndMissingHeaderFlag_AtWarning()
    {
        var registry = RegistryWith(("x", "tok", new[] { "read-counters" }));
        var capture = new ListLoggerProvider();
        using var lf = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Information));

        await RunAsync(registry, authorization: null, lf.CreateLogger<BearerTokenMiddleware>());

        var warning = capture.Records.Single(r => r.Level == LogLevel.Warning);
        warning.Message.Should().Contain("missingHeader=true");
        warning.Message.Should().Contain("remoteIp=");
    }

    // ---------------------------------------------------------------------
    // End-to-end through WebApplicationFactory — exercises Program.cs wiring.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task EndToEnd_ScopedToken_GoesThroughMiddleware()
    {
        // Each WebApplicationFactory captures Program-scope env vars at construction;
        // serialize against other env-touching tests via [Collection(EnvSerial)].
        using var env = EnvScope.Clear("MCP_BEARER_TOKEN");

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Auth:BearerTokens:0:Name", "scoped-token");
                b.UseSetting("Auth:BearerTokens:0:Token", "scoped-secret-aaa");
                b.UseSetting("Auth:BearerTokens:0:Scopes:0", "read-counters");
            });

        using var client = factory.CreateClient();

        // No header → 401 with structured envelope
        var unauth = await client.GetAsync("/mcp");
        unauth.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var unauthBody = await unauth.Content.ReadAsStringAsync();
        unauthBody.Should().Contain("\"kind\":\"unauthenticated\"");

        // Bad token → 401
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        var bad = await client.GetAsync("/mcp");
        bad.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Good token → not 401 (actual MCP handshake on GET returns whatever the SDK
        // returns; we only care that auth let it through, hence !=401).
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "scoped-secret-aaa");
        var ok = await client.GetAsync("/mcp");
        ok.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EndToEnd_LegacyEnvVar_StillWorks()
    {
        using var env = EnvScope.Set("MCP_BEARER_TOKEN", "legacy-secret-xyz");

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "legacy-secret-xyz");
        var resp = await client.GetAsync("/mcp");
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        var bad = await client.GetAsync("/mcp");
        bad.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task JwtMultiIssuer_Routes_Directly_To_Matching_Scheme()
    {
        var oidcOptions = OidcOptionsWithProviders(
            ("https://entra.example.test", "api://dotnet-diagnostics-mcp"),
            ("https://kubernetes.default.svc.cluster.local", "dotnet-diagnostics-mcp"));
        var authService = new CountingAuthenticationService(new Dictionary<string, AuthenticateResult>(StringComparer.Ordinal)
        {
            [OidcJwtAuthOptions.DefaultSchemeName] = AuthenticateResult.Fail("should not be called"),
            [$"{OidcJwtAuthOptions.DefaultSchemeName}-1"] = AuthenticateResult.Success(
                new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "k8s") }, "Bearer")),
                    $"{OidcJwtAuthOptions.DefaultSchemeName}-1")),
        });
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authService)
            .BuildServiceProvider();

        var authorization = string.Concat(
            "Bearer ",
            CreateUnsignedJwt("https://kubernetes.default.svc.cluster.local", "dotnet-diagnostics-mcp"));
        var ctx = await RunAsync(
            RegistryWith(),
            authorization,
            oidcOptions: oidcOptions,
            requestServices: services);

        ((bool)ctx.Items["__nextCalled"]!).Should().BeTrue();
        authService.InvokedSchemes.Should().Equal($"{OidcJwtAuthOptions.DefaultSchemeName}-1");
        ctx.GetBearerPrincipal()!.Name.Should().Be("k8s");
    }

    [Fact]
    public void LegacyPrincipal_HasRootName_AndRootScope()
    {
        using var env = EnvScope.Set("MCP_BEARER_TOKEN", "legacy-secret");
        var registry = BearerTokenRegistry.Build(
            new ConfigurationBuilder().Build(), NullLogger.Instance, true);

        var p = registry.TryResolve("legacy-secret")!;
        p.Name.Should().Be(BearerPrincipal.LegacyRootName);
        p.Scopes.Should().Equal(new[] { BearerPrincipal.RootScope });
    }

    private sealed class CountingAuthenticationService(IReadOnlyDictionary<string, AuthenticateResult> results)
        : IAuthenticationService
    {
        public List<string> InvokedSchemes { get; } = new();

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            scheme.Should().NotBeNull();
            InvokedSchemes.Add(scheme!);
            var result = results[scheme!];
            if (result.Succeeded && result.Principal is not null)
            {
                context.SetBearerPrincipal(new BearerPrincipal(result.Principal.Identity?.Name ?? scheme!, ImmutableHashSet.Create("read-counters")));
            }

            return Task.FromResult(result);
        }

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => throw new NotSupportedException();

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => throw new NotSupportedException();

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
            => throw new NotSupportedException();

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
            => throw new NotSupportedException();
    }
}
