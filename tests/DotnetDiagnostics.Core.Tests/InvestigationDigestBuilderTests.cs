using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Unit coverage for the host-neutral <see cref="InvestigationDigestBuilder"/> (issue #827) — the
/// cross-collector CPU + allocation correlation factored out of the MCP <c>collect_batch</c> tool
/// (issue #825) so the standalone CLI's <c>session</c> REPL and the BenchmarkDotNet diagnoser can
/// reuse the same ranking/gating logic instead of re-implementing it.
/// </summary>
public class InvestigationDigestBuilderTests
{
    [Fact]
    public void Build_BothArtifactsSupplied_PopulatesBothHalves()
    {
        var digest = InvestigationDigestBuilder.Build(Trace(), Allocation());

        digest.Should().NotBeNull();
        digest!.TopCpuSelfTime.Should().NotBeNullOrEmpty();
        digest.TopCpuWaitCategories.Should().NotBeNull();
        digest.HotPathLeaf.Should().NotBeNull();
        digest.HotPathDepth.Should().NotBeNull();
        digest.TopAllocationTypes.Should().NotBeNullOrEmpty();
        digest.TopAllocationCallsites.Should().NotBeNull();
    }

    [Fact]
    public void Build_CpuOnly_LeavesAllocationFieldsNull()
    {
        var digest = InvestigationDigestBuilder.Build(Trace(), allocationSummary: null);

        digest.Should().NotBeNull();
        digest!.TopCpuSelfTime.Should().NotBeNullOrEmpty();
        digest.TopAllocationTypes.Should().BeNull();
        digest.TopAllocationCallsites.Should().BeNull();
    }

    [Fact]
    public void Build_AllocationOnly_LeavesCpuFieldsNull()
    {
        var digest = InvestigationDigestBuilder.Build(cpuTrace: null, Allocation());

        digest.Should().NotBeNull();
        digest!.TopCpuSelfTime.Should().BeNull();
        digest.TopCpuWaitCategories.Should().BeNull();
        digest.HotPathLeaf.Should().BeNull();
        digest.HotPathDepth.Should().BeNull();
        digest.TopAllocationTypes.Should().NotBeNullOrEmpty();
        digest.TopAllocationCallsites.Should().NotBeNull();
    }

    [Fact]
    public void Build_NeitherArtifactSupplied_ReturnsNull()
    {
        var digest = InvestigationDigestBuilder.Build(cpuTrace: null, allocationSummary: null);

        digest.Should().BeNull();
    }

    [Fact]
    public void Build_RespectsTopNCap()
    {
        var digest = InvestigationDigestBuilder.Build(Trace(), Allocation(), topN: 1);

        digest.Should().NotBeNull();
        digest!.TopCpuSelfTime!.Count.Should().Be(1);
        digest.TopAllocationTypes!.Count.Should().Be(1);
        digest.TopAllocationCallsites!.Count.Should().Be(1);
    }

    private static CpuSampleTraceArtifact Trace()
    {
        var leafA = new CallTreeNode(new SampledFrame("App.dll", "LeafA"), 40, 40, Array.Empty<CallTreeNode>());
        var leafB = new CallTreeNode(new SampledFrame("App.dll", "LeafB"), 60, 60, Array.Empty<CallTreeNode>());
        var root = new CallTreeNode(new SampledFrame("App.dll", "Root"), 100, 0, new[] { leafA, leafB });
        return new CpuSampleTraceArtifact(123, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), 100, root);
    }

    private static AllocationSample Allocation() => new(
        123,
        DateTimeOffset.UtcNow,
        TimeSpan.FromSeconds(5),
        TotalEvents: 10,
        TotalBytes: 4096,
        TopByBytes:
        [
            new AllocatedType("MyApp.Widget", 3000, 6, HeapKind.Small),
            new AllocatedType("MyApp.Gadget", 1096, 4, HeapKind.Small),
        ],
        TopByCount:
        [
            new AllocatedType("MyApp.Widget", 3000, 6, HeapKind.Small),
            new AllocatedType("MyApp.Gadget", 1096, 4, HeapKind.Small),
        ])
    {
        TopBySite =
        [
            new AllocationSite(new SampledFrame("MyApp.dll", "MyApp.Worker.Allocate"), 3000, 6, HeapKind.Small),
            new AllocationSite(new SampledFrame("MyApp.dll", "MyApp.Worker.AllocateMore"), 1096, 4, HeapKind.Small),
        ],
    };
}
