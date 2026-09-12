using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Evidence;
using DotnetDiagnostics.Core.Memory;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class SampleDifferTests
{
    [Fact]
    public void HeapDiff_UpwardDeltaAtThreshold_FlagsRegression()
    {
        var baseline = HeapSnapshot(("System.Byte[]", 100, 1));
        var current = HeapSnapshot(("System.Byte[]", 105, 1));

        var diff = ComparablePairwiseSampleDiff.Compare(baseline, "b", current, "c", minDeltaPct: 5, topN: 10);

        diff.Verdict.Should().Be("regression");
        diff.Changed.Should().ContainSingle();
        diff.Changed[0].Direction.Should().Be("up");
        diff.Changed[0].DeltaPct.Should().Be(5);
    }

    [Fact]
    public void HeapDiff_DownwardDeltaAtThreshold_FlagsImprovement()
    {
        var baseline = HeapSnapshot(("System.Byte[]", 100, 1));
        var current = HeapSnapshot(("System.Byte[]", 95, 1));

        var diff = ComparablePairwiseSampleDiff.Compare(baseline, "b", current, "c", minDeltaPct: 5, topN: 10);

        diff.Verdict.Should().Be("improvement");
        diff.Changed.Should().ContainSingle();
        diff.Changed[0].Direction.Should().Be("down");
        diff.Changed[0].DeltaPct.Should().Be(-5);
    }

    [Fact]
    public void HeapDiff_RegressionAndImprovementSignals_FlagsMixed()
    {
        var baseline = HeapSnapshot(
            ("System.Byte[]", 100, 1),
            ("System.String", 100, 1));
        var current = HeapSnapshot(
            ("System.Byte[]", 130, 1),
            ("System.String", 70, 1));

        var diff = ComparablePairwiseSampleDiff.Compare(baseline, "b", current, "c", minDeltaPct: 5, topN: 10);

        diff.Verdict.Should().Be("mixed");
        diff.Changed.Should().HaveCount(2);
    }

    [Fact]
    public void HeapDiff_NoOverlap_ForcesNoChange()
    {
        var baseline = HeapSnapshot(("System.Byte[]", 100, 1));
        var current = HeapSnapshot(("System.String", 500, 3));

        var diff = ComparablePairwiseSampleDiff.Compare(baseline, "b", current, "c", minDeltaPct: 5, topN: 10);

        diff.Verdict.Should().Be("no_change");
        diff.Notes.Should().Contain(note => note.Contains("No overlapping symbols/types", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HeapSnapshotOrigin.Live)]
    [InlineData(HeapSnapshotOrigin.Dump)]
    public void HeapDiff_ClrMdProducerShape_AllowsAddedRowsWithoutQuality(HeapSnapshotOrigin origin)
    {
        var baseline = HeapSnapshot(("Existing.Type", 100, 1)) with { Origin = origin };
        var current = HeapSnapshot(("Existing.Type", 110, 1), ("Brand.New", 500, 3)) with
        {
            Origin = origin,
        };

        var diff = ComparablePairwiseSampleDiff.Compare(baseline, "b", current, "c", minDeltaPct: 0, topN: 10);

        diff.Verdict.Should().Be("regression");
        diff.Added.Should().ContainSingle(row => row.Key.TypeFullName == "Brand.New");
        diff.BaselineQuality.Should().BeNull();
        diff.CurrentQuality.Should().BeNull();
    }

    [Fact]
    public void HeapDiff_LegacyGcDump_OmitsUnmatchedRowsAndIsInconclusive()
    {
        var baseline = HeapSnapshot(("Existing.Type", 100, 1)) with
        {
            Origin = HeapSnapshotOrigin.GcDump,
        };
        var current = HeapSnapshot(("Existing.Type", 110, 1), ("PossiblyOmitted.Type", 500, 3)) with
        {
            Origin = HeapSnapshotOrigin.GcDump,
            Quality = CompleteGcDumpQuality,
        };

        var diff = ComparablePairwiseSampleDiff.Compare(baseline, "b", current, "c", minDeltaPct: 0, topN: 10);

        diff.Verdict.Should().Be("inconclusive");
        diff.Added.Should().BeEmpty();
        diff.Changed.Should().ContainSingle();
        diff.BaselineQuality.Should().BeEquivalentTo(EvidenceQuality.LegacyUnknown);
    }

    [Fact]
    public void CpuDiff_UsesMethodIdentityForOverlapEvenWhenDisplayDiffers()
    {
        var identity = new DotnetDiagnostics.Core.Memory.MethodIdentity(
            MethodName: "DoWork",
            GenericArity: 0,
            ModuleName: "MyApp.dll",
            ModuleVersionId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            MetadataToken: 0x06000042,
            TypeFullName: "MyApp.Worker");

        var baseline = CpuArtifact(new SymbolRef("MyApp.dll", "MyApp.Worker.DoWork"), identity, exclusive: 10);
        var current = CpuArtifact(new SymbolRef("DifferentDisplay.dll", "Completely.Different.Name"), identity, exclusive: 20);

        var diff = ComparablePairwiseSampleDiff.Compare(baseline, "b", current, "c", minDeltaPct: 5, topN: 10);

        diff.Verdict.Should().Be("regression");
        diff.Notes.Should().BeNull();
        diff.Changed.Should().ContainSingle();
    }

    [Theory]
    [InlineData(100, "no_change")]
    [InlineData(200, "regression")]
    [InlineData(50, "improvement")]
    public void CpuDiff_UsesSampleShareRatherThanAbsoluteWork(long currentExclusive, string verdict)
    {
        var symbol = new SymbolRef("CoreClrSample.dll", "GenericFixture.Echo(!!0)");
        var identity = new MethodIdentity(
            MethodName: "Echo",
            GenericArity: 1,
            ModuleName: symbol.Module,
            ModuleVersionId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            MetadataToken: 0x06000042,
            TypeFullName: "GenericFixture");
        var baseline = CpuArtifact(symbol, identity, exclusive: 10);
        var current = CpuArtifact(symbol, identity, currentExclusive, totalSamples: 1000);

        var diff = ComparablePairwiseSampleDiff.Compare(baseline, "b", current, "c", minDeltaPct: 1, topN: 25);

        diff.Verdict.Should().Be(verdict);
        diff.Added.Should().BeEmpty();
        diff.Removed.Should().BeEmpty();
        if (verdict == "no_change")
        {
            diff.Changed.Should().BeEmpty("ten times as many samples at the same share is not a CPU regression");
        }
        else
        {
            var row = diff.Changed.Should().ContainSingle().Subject;
            row.Key.Identity.Should().Be(identity);
            row.Baseline!.ExclusivePercent.Should().Be(10);
            row.Current!.ExclusivePercent.Should().Be(currentExclusive / 10.0);
            row.Direction.Should().Be(verdict == "regression" ? "up" : "down");
        }
    }

    private static CpuSampleTraceArtifact CpuArtifact(SymbolRef symbol, MethodIdentity identity, long exclusive, long totalSamples = 100)
        => new(
            123,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(5),
            totalSamples,
            new CallTreeNode(
                new SampledFrame(string.Empty, "<root>"),
                totalSamples,
                0,
                [new CallTreeNode(new SampledFrame(symbol.Module, symbol.MethodFullName), exclusive, exclusive, Array.Empty<CallTreeNode>())]),
            MethodIdentities: new Dictionary<SymbolRef, MethodIdentity>
            {
                [symbol] = identity,
            });

    private static HeapSnapshotArtifact HeapSnapshot(params (string typeName, long bytes, long instances)[] rows)
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
            ProcessId: 123,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(10),
            Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "x64", IsServerGC: false, HeapCount: 1),
            Heap: new DumpHeapSummary(1024, 0, 0, 1024, 0, 0, 1024),
            TopTypesByBytes: stats,
            TopTypesByInstances: stats);
    }

    private static EvidenceQuality CompleteGcDumpQuality { get; } = new(
        EvidenceQuality.SchemaV1,
        [],
        new EvidenceConclusionPolicy(
            EvidenceConclusionSupport.Supported,
            EvidenceConclusionSupport.Supported,
            EvidenceConclusionSupport.Supported));
}
