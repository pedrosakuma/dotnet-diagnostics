using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Dump;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class HeapGrowthDiffTests
{
    [Fact]
    public void Build_RanksGrowersByByteDelta_NotPercent()
    {
        // Leaks dragging the most absolute bytes must outrank a small-but-high-% mover.
        var baseline = HeapSnapshot(
            ("Leaky.BigCache", 1_000_000, 10),
            ("Tiny.Noise", 100, 1));
        var current = HeapSnapshot(
            ("Leaky.BigCache", 5_000_000, 50),
            ("Tiny.Noise", 400, 4));

        var growth = HeapGrowthDiff.Build(baseline, "b", current, "c", "bytes", minDeltaPct: 5, topN: 25);

        growth.Verdict.Should().Be("leak_suspected");
        growth.TotalGrowers.Should().Be(2);
        growth.Growers.Should().HaveCount(2);
        growth.Growers[0].TypeFullName.Should().Be("Leaky.BigCache");
        growth.Growers[0].BytesDelta.Should().Be(4_000_000);
        growth.Growers[0].InstancesDelta.Should().Be(40);
        growth.Growers[0].IsNew.Should().BeFalse();
        growth.Growers[1].TypeFullName.Should().Be("Tiny.Noise");
    }

    [Fact]
    public void Build_OnlyPositiveGrowthSurfaces_ShrinkingTypesDropped()
    {
        var baseline = HeapSnapshot(("Stable.Type", 1_000, 10), ("Shrinking.Type", 1_000, 10));
        var current = HeapSnapshot(("Stable.Type", 1_000, 10), ("Shrinking.Type", 400, 4));

        var growth = HeapGrowthDiff.Build(baseline, "b", current, "c", "bytes", minDeltaPct: 5, topN: 25);

        growth.Growers.Should().BeEmpty();
        growth.Verdict.Should().Be("stable");
    }

    [Fact]
    public void Build_NewTypeIsFlaggedAndCountedAsGrowth()
    {
        var baseline = HeapSnapshot(("Existing.Type", 1_000, 10));
        var current = HeapSnapshot(("Existing.Type", 1_000, 10), ("Brand.New", 2_048, 8));

        var growth = HeapGrowthDiff.Build(baseline, "b", current, "c", "bytes", minDeltaPct: 5, topN: 25);

        var newRow = growth.Growers.Should().ContainSingle(g => g.TypeFullName == "Brand.New").Subject;
        newRow.IsNew.Should().BeTrue();
        newRow.BaselineBytes.Should().Be(0);
        newRow.BytesDelta.Should().Be(2_048);
        newRow.BytesDeltaPercent.Should().Be(100);
    }

    [Fact]
    public void Build_RankByInstances_OrdersByInstanceDelta()
    {
        var baseline = HeapSnapshot(("ManyInstances", 1_000, 10), ("BigBytes", 10_000, 11));
        var current = HeapSnapshot(("ManyInstances", 2_000, 1_010), ("BigBytes", 90_000, 20));

        var growth = HeapGrowthDiff.Build(baseline, "b", current, "c", "instances", minDeltaPct: 5, topN: 25);

        growth.RankBy.Should().Be("instances");
        growth.Growers[0].TypeFullName.Should().Be("ManyInstances");
        growth.Growers[0].InstancesDelta.Should().Be(1_000);
    }

    [Fact]
    public void Build_MinDeltaPct_FiltersBelowThreshold()
    {
        var baseline = HeapSnapshot(("Barely.Grows", 1_000, 10));
        var current = HeapSnapshot(("Barely.Grows", 1_020, 10)); // +2%

        var growth = HeapGrowthDiff.Build(baseline, "b", current, "c", "bytes", minDeltaPct: 5, topN: 25);

        growth.Growers.Should().BeEmpty();
    }

    [Fact]
    public void Build_AttachesRetentionPathsFromCurrentSnapshotToMatchingGrower()
    {
        var baseline = HeapSnapshot(("Leaky.Cache", 1_000, 10));
        var path = new RetentionPath(
            "Leaky.Cache",
            0xDEAD,
            new[] { new RetentionFrame("Root.Holder", 0xBEEF) { RootKind = "StaticVar" } },
            Truncated: false);
        var current = HeapSnapshot(retentionPaths: new[] { path }, ("Leaky.Cache", 5_000, 50));

        var growth = HeapGrowthDiff.Build(baseline, "b", current, "c", "bytes", minDeltaPct: 5, topN: 25);

        var grower = growth.Growers.Should().ContainSingle().Subject;
        grower.RetentionPaths.Should().ContainSingle();
        grower.RetentionPaths![0].TargetTypeFullName.Should().Be("Leaky.Cache");
    }

    [Fact]
    public void Build_NoRetentionPaths_EmitsRecaptureNote()
    {
        var baseline = HeapSnapshot(("Leaky.Cache", 1_000, 10));
        var current = HeapSnapshot(("Leaky.Cache", 5_000, 50));

        var growth = HeapGrowthDiff.Build(baseline, "b", current, "c", "bytes", minDeltaPct: 5, topN: 25);

        growth.Notes.Should().Contain(n => n.Contains("includeRetentionPaths=true", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_DifferentProcessIds_EmitsCrossProcessNote()
    {
        var baseline = HeapSnapshot(pid: 100, ("X", 1_000, 10));
        var current = HeapSnapshot(pid: 200, ("X", 5_000, 50));

        var growth = HeapGrowthDiff.Build(baseline, "b", current, "c", "bytes", minDeltaPct: 0, topN: 25);

        growth.Notes.Should().Contain(n => n.Contains("different runs/processes", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_TopN_TruncatesRankedRowsButKeepsTotalGrowerCount()
    {
        var baseline = HeapSnapshot(("A", 10, 1), ("B", 10, 1), ("C", 10, 1));
        var current = HeapSnapshot(("A", 3_000, 1), ("B", 2_000, 1), ("C", 1_000, 1));

        var growth = HeapGrowthDiff.Build(baseline, "b", current, "c", "bytes", minDeltaPct: 5, topN: 2);

        growth.TotalGrowers.Should().Be(3);
        growth.Growers.Should().HaveCount(2);
        growth.Growers.Select(g => g.TypeFullName).Should().Equal("A", "B");
    }

    [Fact]
    public void Build_TotalHeapGrowthBytes_ReflectsHeapSummaryDelta()
    {
        var baseline = HeapSnapshot(heapTotalBytes: 1_000_000, ("X", 1_000, 10));
        var current = HeapSnapshot(heapTotalBytes: 4_000_000, ("X", 5_000, 50));

        var growth = HeapGrowthDiff.Build(baseline, "b", current, "c", "bytes", minDeltaPct: 5, topN: 25);

        growth.BaselineHeapBytes.Should().Be(1_000_000);
        growth.CurrentHeapBytes.Should().Be(4_000_000);
        growth.TotalHeapGrowthBytes.Should().Be(3_000_000);
    }

    private static HeapSnapshotArtifact HeapSnapshot(params (string typeName, long bytes, long instances)[] rows)
        => HeapSnapshot(pid: 123, heapTotalBytes: 1024, retentionPaths: null, rows);

    private static HeapSnapshotArtifact HeapSnapshot(int pid, params (string typeName, long bytes, long instances)[] rows)
        => HeapSnapshot(pid, heapTotalBytes: 1024, retentionPaths: null, rows);

    private static HeapSnapshotArtifact HeapSnapshot(long heapTotalBytes, params (string typeName, long bytes, long instances)[] rows)
        => HeapSnapshot(pid: 123, heapTotalBytes, retentionPaths: null, rows);

    private static HeapSnapshotArtifact HeapSnapshot(IReadOnlyList<RetentionPath> retentionPaths, params (string typeName, long bytes, long instances)[] rows)
        => HeapSnapshot(pid: 123, heapTotalBytes: 1024, retentionPaths, rows);

    private static HeapSnapshotArtifact HeapSnapshot(
        int pid,
        long heapTotalBytes,
        IReadOnlyList<RetentionPath>? retentionPaths,
        params (string typeName, long bytes, long instances)[] rows)
    {
        var stats = rows.Select(row =>
            new TypeStat(
                TypeFullName: row.typeName,
                ModuleName: null,
                InstanceCount: row.instances,
                TotalBytes: row.bytes,
                TotalBytesPercent: 0,
                Identity: new TypeIdentity(row.typeName))).ToArray();

        return new HeapSnapshotArtifact(
            Origin: HeapSnapshotOrigin.Live,
            ProcessId: pid,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(10),
            Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "x64", IsServerGC: false, HeapCount: 1),
            Heap: new DumpHeapSummary(heapTotalBytes, 0, 0, heapTotalBytes, 0, 0, heapTotalBytes),
            TopTypesByBytes: stats,
            TopTypesByInstances: stats)
        {
            RetentionPaths = retentionPaths,
        };
    }
}
