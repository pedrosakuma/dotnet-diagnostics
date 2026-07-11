using DotnetDiagnostics.Core.Activities;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class EventPipeActivityCollectorTests
{
    [Fact]
    public void BuildProviderArguments_UsesRequestedSourceFiltersWhenPresent()
    {
        var arguments = EventPipeActivityCollector.BuildProviderArguments(["Orders", "Payments.Api"]);

        arguments.Should().ContainSingle().Which.Value.Should().Be(
            "[AS]Orders/Stop:-TraceId;SpanId;ParentSpanId;StartTimeTicks=StartTimeUtc.Ticks;DurationTicks=Duration.Ticks;ActivitySourceName=Source.Name;Tags=TagObjects.*Enumerate\n" +
            "[AS]Payments.Api/Stop:-TraceId;SpanId;ParentSpanId;StartTimeTicks=StartTimeUtc.Ticks;DurationTicks=Duration.Ticks;ActivitySourceName=Source.Name;Tags=TagObjects.*Enumerate");
    }


    [Fact]
    public void BuildProviderArguments_FallsBackToWildcardForWildcardPatterns()
    {
        var arguments = EventPipeActivityCollector.BuildProviderArguments(["Orders.*", "Payments.Api"]);

        arguments.Should().ContainSingle().Which.Value.Should().Be(
            "[AS]*/Stop:-TraceId;SpanId;ParentSpanId;StartTimeTicks=StartTimeUtc.Ticks;DurationTicks=Duration.Ticks;ActivitySourceName=Source.Name;Tags=TagObjects.*Enumerate");
    }

    [Fact]
    public void BuildProviderArguments_FallsBackToWildcardForProviderGrammarCharacters()
    {
        var arguments = EventPipeActivityCollector.BuildProviderArguments(["Namespace.Outer+Inner"]);

        arguments.Should().ContainSingle().Which.Value.Should().Be(
            "[AS]*/Stop:-TraceId;SpanId;ParentSpanId;StartTimeTicks=StartTimeUtc.Ticks;DurationTicks=Duration.Ticks;ActivitySourceName=Source.Name;Tags=TagObjects.*Enumerate");
    }

    [Fact]
    public void BuildProviderArguments_FallsBackToWildcardWhenNoFilterIsSupplied()
    {
        var arguments = EventPipeActivityCollector.BuildProviderArguments(null);

        arguments.Should().ContainSingle().Which.Value.Should().Be(
            "[AS]*/Stop:-TraceId;SpanId;ParentSpanId;StartTimeTicks=StartTimeUtc.Ticks;DurationTicks=Duration.Ticks;ActivitySourceName=Source.Name;Tags=TagObjects.*Enumerate");
    }
}
