using DotnetDiagnostics.Mcp.Hosting;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

/// <summary>Tests for the H9/B1 non-loopback bind detection used by the bearer-auth
/// bind guard. Covers the formerly-missed port-only ASP.NET Core env keys
/// (gpt-5.5 review of B5.1 surfaced this as Critical).</summary>
public sealed class BindingInspectorTests
{
    private static IConfiguration ConfigFrom(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IConfiguration EmptyConfig() => new ConfigurationBuilder().Build();

    [Fact]
    public void EmptyConfig_NoAppUrls_IsLoopback()
    {
        BindingInspector.HasNonLoopbackBinding(Array.Empty<string>(), EmptyConfig()).Should().BeFalse();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("localhost")]
    public void LoopbackUrls_AreLoopback(string host)
    {
        var cfg = ConfigFrom(new() { ["urls"] = $"http://{host}:5000" });
        BindingInspector.HasNonLoopbackBinding(Array.Empty<string>(), cfg).Should().BeFalse();
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("example.com")]
    public void NonLoopbackUrls_AreNonLoopback(string host)
    {
        var cfg = ConfigFrom(new() { ["urls"] = $"http://{host}:5000" });
        BindingInspector.HasNonLoopbackBinding(Array.Empty<string>(), cfg).Should().BeTrue();
    }

    [Theory]
    [InlineData("HTTP_PORTS")]
    [InlineData("HTTPS_PORTS")]
    [InlineData("ASPNETCORE_HTTP_PORTS")]
    [InlineData("ASPNETCORE_HTTPS_PORTS")]
    [InlineData("DOTNET_HTTP_PORTS")]
    [InlineData("DOTNET_HTTPS_PORTS")]
    public void PortOnlyKeys_AreAlwaysNonLoopback(string key)
    {
        // gpt-5.5 review of B5.1 surfaced this as Critical: Kestrel binds these to
        // wildcard interfaces, so any non-empty value implies a network-exposed
        // listener. Without this branch the bind guard would silently allow the
        // ephemeral-token fallback to run on a wildcard-bound HTTP listener.
        var cfg = ConfigFrom(new() { [key] = "5000" });
        BindingInspector.HasNonLoopbackBinding(Array.Empty<string>(), cfg).Should().BeTrue();
    }

    [Fact]
    public void KestrelEndpoint_NonLoopback_Detected()
    {
        var cfg = ConfigFrom(new()
        {
            ["Kestrel:Endpoints:Http:Url"] = "http://0.0.0.0:5000",
        });
        BindingInspector.HasNonLoopbackBinding(Array.Empty<string>(), cfg).Should().BeTrue();
    }

    [Fact]
    public void AppUrls_NonLoopback_Detected()
    {
        BindingInspector.HasNonLoopbackBinding(new[] { "http://0.0.0.0:5000" }, EmptyConfig()).Should().BeTrue();
    }

    [Fact]
    public void MixedLoopbackAndNonLoopback_DetectedAsNonLoopback()
    {
        var cfg = ConfigFrom(new() { ["urls"] = "http://127.0.0.1:5000;http://0.0.0.0:5001" });
        BindingInspector.HasNonLoopbackBinding(Array.Empty<string>(), cfg).Should().BeTrue();
    }

    [Fact]
    public void DirectHttps_NonLoopback_IsNotCleartext()
    {
        var cfg = ConfigFrom(new() { ["urls"] = "https://0.0.0.0:5000" });

        BindingInspector.HasNonLoopbackBinding(Array.Empty<string>(), cfg).Should().BeTrue();
        BindingInspector.HasNonLoopbackHttpBinding(Array.Empty<string>(), cfg).Should().BeFalse();
    }

    [Fact]
    public void LoopbackHttp_IsNotCleartextNonLoopback()
    {
        var cfg = ConfigFrom(new() { ["urls"] = "http://127.0.0.1:5000" });

        BindingInspector.HasNonLoopbackHttpBinding(Array.Empty<string>(), cfg).Should().BeFalse();
    }

    [Theory]
    [InlineData("HTTP_PORTS", true)]
    [InlineData("ASPNETCORE_HTTP_PORTS", true)]
    [InlineData("HTTPS_PORTS", false)]
    [InlineData("ASPNETCORE_HTTPS_PORTS", false)]
    public void PortOnlyKeys_DistinguishCleartext(string key, bool expectedCleartext)
    {
        var cfg = ConfigFrom(new() { [key] = "5000" });

        BindingInspector.HasNonLoopbackHttpBinding(Array.Empty<string>(), cfg)
            .Should().Be(expectedCleartext);
    }

    [Fact]
    public void ExplicitHttpsUrl_IgnoresInheritedHttpPorts()
    {
        var cfg = ConfigFrom(new()
        {
            ["ASPNETCORE_URLS"] = "https://0.0.0.0:18887",
            ["ASPNETCORE_HTTP_PORTS"] = "8080",
        });

        BindingInspector.HasNonLoopbackBinding(Array.Empty<string>(), cfg).Should().BeTrue();
        BindingInspector.HasNonLoopbackHttpBinding(Array.Empty<string>(), cfg).Should().BeFalse();
    }

    [Fact]
    public void KestrelHttpsEndpoint_TakesPrecedenceOverLowerPriorityHttpSettings()
    {
        var cfg = ConfigFrom(new()
        {
            ["Kestrel:Endpoints:Https:Url"] = "https://0.0.0.0:18887",
            ["ASPNETCORE_URLS"] = "http://0.0.0.0:5000",
            ["ASPNETCORE_HTTP_PORTS"] = "8080",
        });

        BindingInspector.HasNonLoopbackBinding(Array.Empty<string>(), cfg).Should().BeTrue();
        BindingInspector.HasNonLoopbackHttpBinding(Array.Empty<string>(), cfg).Should().BeFalse();
    }

    [Fact]
    public void AppHttpsUrl_IgnoresInheritedHttpPorts()
    {
        var cfg = ConfigFrom(new() { ["HTTP_PORTS"] = "8080" });
        var appUrls = new[] { "https://0.0.0.0:18887" };

        BindingInspector.HasNonLoopbackBinding(appUrls, cfg).Should().BeTrue();
        BindingInspector.HasNonLoopbackHttpBinding(appUrls, cfg).Should().BeFalse();
    }

    [Fact]
    public void HttpPortsFallback_IsCleartextWithoutHigherPriorityConfiguration()
    {
        var cfg = ConfigFrom(new() { ["ASPNETCORE_HTTP_PORTS"] = "8080" });

        BindingInspector.HasNonLoopbackBinding(Array.Empty<string>(), cfg).Should().BeTrue();
        BindingInspector.HasNonLoopbackHttpBinding(Array.Empty<string>(), cfg).Should().BeTrue();
    }

    [Theory]
    [InlineData("http://*:5000", true)]
    [InlineData("http://+:5000", true)]
    [InlineData("https://*:5000", false)]
    [InlineData("https://+:5000", false)]
    public void KestrelWildcardHosts_PreserveTransportScheme(
        string url,
        bool expectedCleartext)
    {
        var cfg = ConfigFrom(new() { ["urls"] = url });

        BindingInspector.HasNonLoopbackBinding(Array.Empty<string>(), cfg).Should().BeTrue();
        BindingInspector.HasNonLoopbackHttpBinding(Array.Empty<string>(), cfg)
            .Should().Be(expectedCleartext);
    }

    [Theory]
    [InlineData("urls")]
    [InlineData("ASPNETCORE_URLS")]
    [InlineData("DOTNET_URLS")]
    public void ExplicitUrlConfigurationSources_DetectWildcardHttp(string key)
    {
        var cfg = ConfigFrom(new()
        {
            [key] = "http://*:5000",
            ["HTTPS_PORTS"] = "8443",
        });

        BindingInspector.HasNonLoopbackBinding(Array.Empty<string>(), cfg).Should().BeTrue();
        BindingInspector.HasNonLoopbackHttpBinding(Array.Empty<string>(), cfg).Should().BeTrue();
    }

    [Fact]
    public void KestrelWildcardHttpsEndpoint_OverridesLowerPriorityHttpPorts()
    {
        var cfg = ConfigFrom(new()
        {
            ["Kestrel:Endpoints:Https:Url"] = "https://+:18887",
            ["ASPNETCORE_HTTP_PORTS"] = "8080",
        });

        BindingInspector.HasNonLoopbackBinding(Array.Empty<string>(), cfg).Should().BeTrue();
        BindingInspector.HasNonLoopbackHttpBinding(Array.Empty<string>(), cfg).Should().BeFalse();
    }

    [Fact]
    public void AppWildcardHttpUrl_OverridesLowerPriorityHttpsPorts()
    {
        var cfg = ConfigFrom(new() { ["HTTPS_PORTS"] = "8443" });
        var appUrls = new[] { "http://+:18887" };

        BindingInspector.HasNonLoopbackBinding(appUrls, cfg).Should().BeTrue();
        BindingInspector.HasNonLoopbackHttpBinding(appUrls, cfg).Should().BeTrue();
    }
}
