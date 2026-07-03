using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Signals;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Tests the diagnosis-agnostic allocation signal groupings (#525): byte-weighted concentration
/// by type (<c>allocations.by-type</c>) and by call site (<c>allocations.by-site</c>). These
/// describe <i>where</i> allocated bytes concentrate, never <i>why</i>.
/// </summary>
public sealed class AllocationSignalsTests
{
    private static AllocatedType Type(string name, long bytes) =>
        new(name, bytes, EventCount: bytes / 1024, HeapKind.Small);

    private static AllocationSite Site(string method, long bytes) =>
        new(new SampledFrame("MyApp.dll", method), bytes, EventCount: bytes / 1024, HeapKind.Small);

    private static AllocationSample Sample(long totalBytes, AllocatedType[] byType, AllocationSite[] bySite) =>
        new(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalEvents: byType.Sum(t => t.EventCount),
            TotalBytes: totalBytes,
            TopByBytes: byType,
            TopByCount: byType)
        {
            TopBySite = bySite,
        };

    // ---- by-type concentration -------------------------------------------------------------

    [Fact]
    public void ByType_EmitsConcentration_WhenOneTypeDominates()
    {
        var sample = Sample(
            totalBytes: 1_000_000,
            byType: new[] { Type("MyApp.Model.BigThing", 900_000), Type("System.String", 100_000) },
            bySite: Array.Empty<AllocationSite>());

        var signals = AllocationSignals.Detect(sample, "handle-alloc");

        var byType = signals.Should().ContainSingle(s => s.Signal == "allocations.by-type").Subject;
        byType.Salience.Should().BeApproximately(0.9, 0.001);
        byType.Summary.Should().Contain("MyApp.Model.BigThing");
        byType.Buckets[0].Key.Should().Be("MyApp.Model.BigThing");
        byType.Buckets[0].Magnitude.Should().Be(900_000);
        byType.Buckets[0].Handle.Should().Be("handle-alloc");
        byType.NextAction!.SuggestedArguments!["view"].Should().Be("call-tree");
    }

    [Fact]
    public void ByType_EmitsNothing_WhenSpreadOut()
    {
        var sample = Sample(
            totalBytes: 300_000,
            byType: new[] { Type("A", 100_000), Type("B", 100_000), Type("C", 100_000) },
            bySite: Array.Empty<AllocationSite>());

        AllocationSignals.Detect(sample, "h").Should().NotContain(s => s.Signal == "allocations.by-type");
    }

    [Fact]
    public void ByType_EmitsNothing_WhenTooFewBytes()
    {
        var sample = Sample(
            totalBytes: 1024,
            byType: new[] { Type("MyApp.Tiny", 1024) },
            bySite: Array.Empty<AllocationSite>());

        AllocationSignals.Detect(sample, "h").Should().NotContain(s => s.Signal == "allocations.by-type");
    }

    [Fact]
    public void ByType_EmitsNothing_WhenDominantTypeIsUnknown()
    {
        // NativeAOT: TypeName isn't populated by the runtime, everything rolls up under "<unknown>".
        var sample = Sample(
            totalBytes: 1_000_000,
            byType: new[] { Type("<unknown>", 1_000_000) },
            bySite: Array.Empty<AllocationSite>());

        AllocationSignals.Detect(sample, "h").Should().NotContain(s => s.Signal == "allocations.by-type");
    }

    // ---- by-site concentration --------------------------------------------------------------

    [Fact]
    public void BySite_EmitsConcentration_WhenOneSiteDominates()
    {
        var sample = Sample(
            totalBytes: 1_000_000,
            byType: new[] { Type("MyApp.Model.BigThing", 1_000_000) },
            bySite: new[] { Site("MyApp.Parsing.Parse", 850_000), Site("MyApp.Other.Do", 150_000) });

        var signals = AllocationSignals.Detect(sample, "handle-alloc");

        var bySite = signals.Should().ContainSingle(s => s.Signal == "allocations.by-site").Subject;
        bySite.Salience.Should().BeApproximately(0.85, 0.001);
        bySite.Buckets[0].Key.Should().Be("MyApp.Parsing.Parse");
        bySite.Buckets[0].Magnitude.Should().Be(850_000);
        bySite.Buckets[0].Handle.Should().Be("handle-alloc");
        bySite.NextAction!.SuggestedArguments!["view"].Should().Be("call-tree");
    }

    [Fact]
    public void BySite_EmitsNothing_WhenNoSitesResolved()
    {
        var sample = Sample(
            totalBytes: 1_000_000,
            byType: new[] { Type("MyApp.Model.BigThing", 1_000_000) },
            bySite: Array.Empty<AllocationSite>());

        AllocationSignals.Detect(sample, "h").Should().NotContain(s => s.Signal == "allocations.by-site");
    }
}
