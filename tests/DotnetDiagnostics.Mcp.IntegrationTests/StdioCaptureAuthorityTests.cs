using System.Collections.Immutable;
using DotnetDiagnostics.Mcp.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class StdioCaptureAuthorityTests
{
    [Fact]
    public void DefaultLocalRoot_DoesNotGrantLiteralCaptureAuthority()
    {
        var accessor = StdioRootPrincipalAccessor.FromConfiguration(Config());
        accessor.Should().BeSameAs(StdioRootPrincipalAccessor.Instance);
        accessor.Current!.HasScope("investigation-export").Should().BeTrue();
        accessor.Current.HasExplicitScope("module-bytes-read").Should().BeFalse();
    }

    [Fact]
    public void OptIn_GrantsOnlyCaptureBytesAndSelectedSensitivity()
    {
        var accessor = StdioRootPrincipalAccessor.FromConfiguration(Config(
            ("Stdio:CaptureBytes", "true"),
            ("Stdio:CaptureModifiers:0", "sensitive-heap-read")));
        accessor.Current!.HasExplicitScope("module-bytes-read").Should().BeTrue();
        accessor.Current.HasExplicitScope("sensitive-heap-read").Should().BeTrue();
        accessor.Current.HasExplicitScope("sensitive-parameter-read").Should().BeFalse();
        accessor.Current.HasExplicitScope("eventsource-any").Should().BeFalse();
        StdioRootPrincipalAccessor.IsCurrent(accessor).Should().BeTrue();
    }

    [Theory]
    [InlineData("eventsource-any")]
    [InlineData("sensitive-parameter-read")]
    public void ExplicitSensitivity_IsAvailableOnlyThroughHostConstruction(string modifier)
    {
        var accessor = StdioRootPrincipalAccessor.FromConfiguration(Config(
            ("Stdio:CaptureBytes", "true"), ("Stdio:CaptureModifiers:0", modifier)));
        accessor.Current!.HasExplicitScope(modifier).Should().BeTrue();
    }

    [Theory]
    [InlineData("false", "eventsource-any")]
    [InlineData("true", "root")]
    [InlineData("true", "delete-artifact")]
    public void InvalidConfiguration_FailsClosed(string enabled, string modifier)
    {
        var create = () => StdioRootPrincipalAccessor.FromConfiguration(Config(
            ("Stdio:CaptureBytes", enabled), ("Stdio:CaptureModifiers:0", modifier)));
        create.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RemoteDisplayName_DoesNotMakeAnAccessorLocalOrGrantCaptureScope()
    {
        var accessor = new RemoteAccessor(new("stdio-root", ImmutableHashSet.Create("root"), "remote-owner"));
        StdioRootPrincipalAccessor.IsCurrent(accessor).Should().BeFalse();
        accessor.Current.HasExplicitScope("module-bytes-read").Should().BeFalse();
    }

    private static IConfiguration Config(params (string Key, string Value)[] entries)
        => new ConfigurationBuilder().AddInMemoryCollection(
            entries.Select(item => new KeyValuePair<string, string?>(item.Key, item.Value))).Build();

    private sealed record RemoteAccessor(BearerPrincipal Current) : IPrincipalAccessor;
}
