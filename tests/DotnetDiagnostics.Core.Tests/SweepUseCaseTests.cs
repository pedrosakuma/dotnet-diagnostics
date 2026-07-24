using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.UseCases;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class SweepUseCaseTests
{
    [Fact]
    public void FormatFailureText_RemainsHostPathNeutral()
    {
        SweepUseCase.FormatFailureText(2).Should()
            .Be(" 2 collector(s) failed.")
            .And.NotContain("data.", "Core is shared by hosts with different JSON envelopes");
    }

    [Fact]
    public void ResolveTriageCounterSnapshot_UsesFullHandleArtifactForTrendEvidence()
    {
        var first = new[]
        {
            new CounterValue("System.Runtime", "loh-size", "loh-size", 10_000_000, CounterKind.Mean, "B"),
        };
        var last = new[]
        {
            new CounterValue("System.Runtime", "loh-size", "loh-size", 25_000_000, CounterKind.Mean, "B"),
        };
        var full = new CounterSnapshot(42, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(6), last, [], [])
        {
            FirstCounters = first,
        };
        var handles = new MemoryDiagnosticHandleStore();
        var handle = handles.Register(42, CollectionHandleKinds.Counters, full, TimeSpan.FromMinutes(1));
        var inline = full with { Counters = [], FirstCounters = null };
        var result = DiagnosticResult.OkWithHandle(
            inline,
            "summary",
            handle.Id,
            handle.ExpiresAt);

        var resolved = SweepUseCase.ResolveTriageCounterSnapshot(result, handles);

        resolved.Should().BeSameAs(full);
        resolved!.FirstCounters.Should().BeSameAs(first);
        resolved.Counters.Should().BeSameAs(last);
    }
}
