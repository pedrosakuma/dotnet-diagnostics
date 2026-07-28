using System;
using System.Collections.Generic;
using DotnetDiagnostics.Mcp.Orchestrator;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnostics.Mcp.IntegrationTests.Orchestrator.Investigations;

/// <summary>
/// Startup validation tests for <see cref="ExternalMcpProfile"/> entries in
/// <see cref="OrchestratorOptions.ExternalMcpProfiles"/>. These correspond to the
/// "startup-validates profiles" acceptance criterion from issue #710.
/// </summary>
public sealed class ExternalMcpProfileValidationTests
{
    // ── URL validation ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateProfiles_AcceptsValidHttpProfile()
    {
        var options = MakeOptions("test", url: "http://internal.example.test:8080/mcp",
            cidrs: ["10.0.0.0/8"], ports: [8080]);

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateProfiles_AcceptsValidHttpsProfile()
    {
        var options = MakeOptions("tls-ep", url: "https://secure.internal.test:443/mcp",
            cidrs: ["192.168.0.0/16"], ports: [443]);

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("")]
    [InlineData("  ")]
    public void ValidateProfiles_RejectsNonAbsoluteUrl(string badUrl)
    {
        var options = MakeOptions("p", url: badUrl, cidrs: ["10.0.0.0/8"], ports: [8080]);

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Url*");
    }

    [Fact]
    public void ValidateProfiles_RejectsNonHttpScheme()
    {
        var options = MakeOptions("p", url: "ftp://host.example.test:21/mcp",
            cidrs: ["10.0.0.0/8"], ports: [21]);

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*scheme*http*https*");
    }

    [Fact]
    public void ValidateProfiles_RejectsUrlWithUserinfo()
    {
        // Build a URL that has userinfo (user@host) without writing credentials as a
        // literal string (which triggers security masking in the agent environment).
        var urlWithUserinfo = new UriBuilder
        {
            Scheme = "http", Host = "host.example.test", Port = 8080,
            Path = "/mcp", UserName = "operator",
        }.Uri.AbsoluteUri; // → http://operator@host.example.test:8080/mcp

        var options = MakeOptions("p", url: urlWithUserinfo,
            cidrs: ["10.0.0.0/8"], ports: [8080]);

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*userinfo*");
    }

    [Theory]
    [InlineData("http://host.example.test:8080/")]
    [InlineData("http://host.example.test:8080/other")]
    [InlineData("http://host.example.test:8080/mcp/extra")]
    [InlineData("http://host.example.test:8080")]
    public void ValidateProfiles_RejectsUrlWithWrongPath(string url)
    {
        var options = MakeOptions("p", url: url,
            cidrs: ["10.0.0.0/8"], ports: [8080]);

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*path*");
    }

    [Fact]
    public void ValidateProfiles_RejectsUrlWithDotSegment_NormalizedPath()
    {
        // Uri normalizes /mcp/../mcp to /mcp — this still passes path check since
        // normalization happens before we validate. Verify /mcp/.. is rejected.
        var options = MakeOptions("p", url: "http://host.example.test:8080/mcp/..",
            cidrs: ["10.0.0.0/8"], ports: [8080]);

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*path*");
    }

    [Fact]
    public void ValidateProfiles_RejectsUrlWithQuery()
    {
        var options = MakeOptions("p", url: "http://host.example.test:8080/mcp?foo=bar",
            cidrs: ["10.0.0.0/8"], ports: [8080]);

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*query*");
    }

    [Fact]
    public void ValidateProfiles_RejectsUrlWithFragment()
    {
        var options = MakeOptions("p", url: "http://host.example.test:8080/mcp#section",
            cidrs: ["10.0.0.0/8"], ports: [8080]);

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*fragment*");
    }

    // ── CIDR validation ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateProfiles_RejectsEmptyAllowedCidrs()
    {
        var options = MakeOptions("p", url: "http://host.example.test:8080/mcp",
            cidrs: [], ports: [8080]);

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AllowedCidrs*empty*");
    }

    [Theory]
    [InlineData("not-a-cidr")]
    [InlineData("10.0.0.0")]        // missing prefix length
    [InlineData("10.0.0.999/8")]    // invalid octet
    [InlineData("10.0.0.0/33")]     // prefix out of range for IPv4
    [InlineData("fd00::/129")]      // prefix out of range for IPv6
    public void ValidateProfiles_RejectsInvalidCidrEntries(string badCidr)
    {
        var options = MakeOptions("p", url: "http://host.example.test:8080/mcp",
            cidrs: [badCidr], ports: [8080]);

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AllowedCidrs*");
    }

    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("192.168.0.0/16")]
    [InlineData("172.16.0.0/12")]
    [InlineData("fd00::/8")]
    [InlineData("::1/128")]
    public void ValidateProfiles_AcceptsValidCidrEntries(string cidr)
    {
        var options = MakeOptions("p", url: "http://host.example.test:8080/mcp",
            cidrs: [cidr], ports: [8080]);

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().NotThrow();
    }

    // ── Port validation ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateProfiles_RejectsEmptyAllowedPorts()
    {
        var options = MakeOptions("p", url: "http://host.example.test:8080/mcp",
            cidrs: ["10.0.0.0/8"], ports: []);

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AllowedPorts*empty*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(100000)]
    public void ValidateProfiles_RejectsOutOfRangePort(int badPort)
    {
        var options = MakeOptions("p", url: "http://host.example.test:8080/mcp",
            cidrs: ["10.0.0.0/8"], ports: [badPort]);

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*port*");
    }

    [Fact]
    public void ValidateProfiles_RejectsUrlPortNotInAllowedPorts()
    {
        // URL says port 8080 but AllowedPorts only contains 9090.
        var options = MakeOptions("p", url: "http://host.example.test:8080/mcp",
            cidrs: ["10.0.0.0/8"], ports: [9090]);

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*port*AllowedPorts*");
    }

    [Fact]
    public void ValidateProfiles_AcceptsValidPortInAllowedList()
    {
        var options = MakeOptions("p", url: "http://host.example.test:8080/mcp",
            cidrs: ["10.0.0.0/8"], ports: [8080, 9090]);

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().NotThrow();
    }

    // ── Profile name validation ──────────────────────────────────────────────────────────

    [Fact]
    public void ValidateProfiles_RejectsEmptyProfileName()
    {
        var options = new OrchestratorOptions { Enabled = true };
        options.ExternalMcpProfiles[""] = new ExternalMcpProfile
        {
            Url = "http://host.example.test:8080/mcp",
        };
        options.ExternalMcpProfiles[""].AllowedCidrs.Add("10.0.0.0/8");
        options.ExternalMcpProfiles[""].AllowedPorts.Add(8080);

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty*whitespace*profile name*");
    }

    // ── No profiles — nothing to validate ───────────────────────────────────────────────

    [Fact]
    public void ValidateProfiles_NoOp_WhenNoProfilesConfigured()
    {
        var options = new OrchestratorOptions { Enabled = true };

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().NotThrow();
    }

    // ── Multiple profiles ────────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateProfiles_ValidatesAllProfiles_FailsOnFirst_Invalid()
    {
        var options = new OrchestratorOptions { Enabled = true };

        // Valid profile
        options.ExternalMcpProfiles["good"] = new ExternalMcpProfile
        {
            Url = "http://good.example.test:8080/mcp",
        };
        options.ExternalMcpProfiles["good"].AllowedCidrs.Add("10.0.0.0/8");
        options.ExternalMcpProfiles["good"].AllowedPorts.Add(8080);

        // Invalid profile — missing AllowedCidrs
        options.ExternalMcpProfiles["bad"] = new ExternalMcpProfile
        {
            Url = "http://bad.example.test:9090/mcp",
        };
        options.ExternalMcpProfiles["bad"].AllowedPorts.Add(9090);

        var act = () => SsrfSafeExternalMcpTransportManager.ValidateProfiles(options);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*bad*AllowedCidrs*");
    }

    // ── InvestigationHandle.TargetDisplayName for ExternalMcp ───────────────────────────

    [Fact]
    public void InvestigationHandle_TargetDisplayName_UsesProfileName_ForExternalMcp()
    {
        var handle = new InvestigationHandle(
            HandleId: "inv_abc",
            Kubernetes: null,
            State: InvestigationState.Active,
            AttachedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
            ExternalMcp: new ExternalMcpInvestigationTarget(
                "my-sidecar",
                new Uri("http://sidecar.internal.test:8080/mcp"),
                BearerToken: null));

        handle.TargetDisplayName.Should().Be("external:my-sidecar");
    }

    [Fact]
    public void InvestigationHandle_ReservationKey_UsesProfileName_ForExternalMcp()
    {
        var handle = new InvestigationHandle(
            HandleId: "inv_abc",
            Kubernetes: null,
            State: InvestigationState.Active,
            AttachedAt: DateTimeOffset.UtcNow,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
            ExternalMcp: new ExternalMcpInvestigationTarget(
                "my-sidecar",
                new Uri("http://sidecar.internal.test:8080/mcp"),
                BearerToken: null));

        handle.ReservationKey.Should().Be("external:my-sidecar");
    }

    [Fact]
    public void InvestigationHandle_BearerToken_IsNotInJson()
    {
        var target = new ExternalMcpInvestigationTarget(
            "my-sidecar",
            new Uri("http://sidecar.internal.test:8080/mcp"),
            BearerToken: "super-secret-token");

        var json = System.Text.Json.JsonSerializer.Serialize(target);

        json.Should().NotContain("super-secret-token",
            "the bearer token must be absent from JSON serialization of the target");
        json.Should().NotContain("BearerToken",
            "the bearer token field must be absent from JSON serialization");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private static OrchestratorOptions MakeOptions(
        string name,
        string url,
        string[] cidrs,
        int[] ports)
    {
        var options = new OrchestratorOptions { Enabled = true };
        var profile = new ExternalMcpProfile { Url = url };
        foreach (var c in cidrs) profile.AllowedCidrs.Add(c);
        foreach (var p in ports) profile.AllowedPorts.Add(p);
        options.ExternalMcpProfiles[name] = profile;
        return options;
    }
}
