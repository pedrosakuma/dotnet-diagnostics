using DotnetDiagnostics.BenchmarkDotNet;
using FluentAssertions;

namespace DotnetDiagnostics.BenchmarkDotNet.Tests;

public class DiagnosticKindAttributeTests
{
    [Fact]
    public void ParsesSingleKind_WithDefaultDuration()
    {
        var attr = new DiagnosticKindAttribute("gc");

        attr.Kinds.Should().Be("gc");
        attr.DurationSeconds.Should().Be(5);
        attr.KindList.Should().ContainSingle().Which.Should().Be("gc");
    }

    [Fact]
    public void ParsesMultipleKinds_TrimmingAndDroppingEmpties()
    {
        var attr = new DiagnosticKindAttribute(" gc , contention ,, threadpool ", durationSeconds: 8);

        attr.DurationSeconds.Should().Be(8);
        attr.KindList.Should().Equal("gc", "contention", "threadpool");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Throws_OnNullOrWhitespaceKinds(string? kinds)
    {
        var act = () => new DiagnosticKindAttribute(kinds!);

        act.Should().Throw<ArgumentException>();
    }
}
