using DotnetDiagnostics.BenchmarkDotNet;
using FluentAssertions;

namespace DotnetDiagnostics.BenchmarkDotNet.Tests;

public class InProcessDiagnosticCollectorTests
{
    private static readonly string[] ExpectedKinds =
    {
        "counters", "exceptions", "gc", "datas", "catalog",
        "activities", "logs", "jit", "threadpool", "contention", "db",
    };

    [Fact]
    public void SupportedKinds_MatchExpectedSet()
        => InProcessDiagnosticCollector.SupportedKinds.Should().BeEquivalentTo(ExpectedKinds);

    [Fact]
    public void SupportedKinds_ExcludesEventSource()
        => InProcessDiagnosticCollector.IsSupported("event_source").Should().BeFalse();

    [Theory]
    [InlineData("gc", true)]
    [InlineData("contention", true)]
    [InlineData("event_source", false)]
    [InlineData("nonsense", false)]
    public void IsSupported_ReflectsTheSet(string kind, bool expected)
        => InProcessDiagnosticCollector.IsSupported(kind).Should().Be(expected);

    [Fact]
    public void Unsupported_ProducesErrorCapture()
    {
        var capture = KindCapture.Unsupported("event_source");

        capture.Kind.Should().Be("event_source");
        capture.IsError.Should().BeTrue();
        capture.Headline.Should().Contain("event_source");
        capture.Json.Should().Contain("unsupported");
    }
}
