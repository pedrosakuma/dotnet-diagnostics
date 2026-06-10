using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Tools.Dispatch;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// #204 — sanity coverage for the shared discriminator-validation helper. The
/// helper is consumed by sub-issues #205–#212 to validate <c>kind=</c> / <c>view=</c> /
/// <c>source=</c> values without throwing.
/// </summary>
public sealed class DiscriminatorDispatchTests
{
    private static readonly string[] Allowed = { "summary", "detail", "raw" };

    [Fact]
    public void TryValidate_KnownValue_PassesThrough()
    {
        var ok = DiscriminatorDispatch.TryValidate<string>(
            "detail", Allowed, "view", out var canonical, out var failure);

        ok.Should().BeTrue();
        canonical.Should().Be("detail");
        failure.Should().BeNull();
    }

    [Fact]
    public void TryValidate_UnknownValue_ReturnsStructuredEnvelope()
    {
        var ok = DiscriminatorDispatch.TryValidate<string>(
            "verbose", Allowed, "view", out _, out var failure);

        ok.Should().BeFalse();
        failure.Should().NotBeNull();
        failure!.Error.Should().NotBeNull();
        failure.Error!.Kind.Should().Be("InvalidArgument");
        failure.Error.Message.Should().Contain("verbose").And.Contain("summary, detail, raw");
        failure.Error.Detail.Should().Be("view");
    }

    [Fact]
    public void TryValidate_NullOrWhitespace_ReturnsRequiredFailure()
    {
        var ok = DiscriminatorDispatch.TryValidate<string>(
            "  ", Allowed, "view", out _, out var failure);

        ok.Should().BeFalse();
        failure.Should().NotBeNull();
        failure!.Error!.Kind.Should().Be("InvalidArgument");
        failure.Error.Message.Should().Contain("required");
    }

    [Fact]
    public void TryValidate_IsCaseSensitive_RejectsDifferentCase()
    {
        // Documents the deliberate ordinal-match policy: 'SUMMARY' is NOT the same as 'summary'.
        var ok = DiscriminatorDispatch.TryValidate<string>(
            "SUMMARY", Allowed, "view", out _, out var failure);

        ok.Should().BeFalse();
        failure!.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public void TryValidate_TrimmedValue_StillMatches()
    {
        var ok = DiscriminatorDispatch.TryValidate<string>(
            "  raw  ", Allowed, "view", out var canonical, out var failure);

        ok.Should().BeTrue();
        canonical.Should().Be("raw");
        failure.Should().BeNull();
    }
}
