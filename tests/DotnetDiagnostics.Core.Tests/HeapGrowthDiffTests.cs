using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Drilldown;
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
    public void Build_SameNamedTypesAcrossModules_UsesIdentityAndSkipsAmbiguousNameOnlyPaths()
    {
        const string typeName = "Shared.Model";
        var identityA = new TypeIdentity(typeName)
        {
            ModuleName = "ModuleA.dll",
            ModuleVersionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            MetadataToken = 0x02000001,
        };
        var identityB = new TypeIdentity(typeName)
        {
            ModuleName = "ModuleB.dll",
            ModuleVersionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            MetadataToken = 0x02000001,
        };
        var baseline = HeapSnapshotWithIdentity(
            retentionPaths: null,
            (identityA, 1_000, 10),
            (identityB, 2_000, 20));
        var exactPath = new RetentionPath(
            typeName,
            0xA001,
            [new RetentionFrame("Root.A", 0xA000) { RootKind = "StaticVar" }],
            Truncated: false)
        {
            TargetIdentity = identityA,
        };
        var moduleOnlyPath = new RetentionPath(
            typeName,
            0xB001,
            [new RetentionFrame("Root.B", 0xB000) { RootKind = "StaticVar" }],
            Truncated: false)
        {
            TargetIdentity = new TypeIdentity(typeName) { ModuleName = identityB.ModuleName },
        };
        var ambiguousPath = new RetentionPath(
            typeName,
            0xFFFF,
            [new RetentionFrame("Root.Unknown", 0xFF00) { RootKind = "StaticVar" }],
            Truncated: false);
        var current = HeapSnapshotWithIdentity(
            [exactPath, moduleOnlyPath, ambiguousPath],
            (identityA, 5_000, 50),
            (identityB, 6_000, 60));

        var growth = HeapGrowthDiff.Build(baseline, "b", current, "c", "bytes", minDeltaPct: 5, topN: 25);

        var growerA = growth.Growers.Single(g => g.Identity!.ModuleVersionId == identityA.ModuleVersionId);
        growerA.RetentionPaths.Should().ContainSingle()
            .Which.TargetObjectAddress.Should().Be(0xA001);
        growerA.TotalRetentionPaths.Should().Be(1);

        var growerB = growth.Growers.Single(g => g.Identity!.ModuleVersionId == identityB.ModuleVersionId);
        growerB.RetentionPaths.Should().ContainSingle()
            .Which.TargetObjectAddress.Should().Be(0xB001);
        growerB.TotalRetentionPaths.Should().Be(1);
        growth.Notes.Should().ContainSingle(note =>
            note.Contains("Skipped 1 retention path(s)", StringComparison.Ordinal) &&
            note.Contains(typeName, StringComparison.Ordinal) &&
            note.Contains("no weaker correlation", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_SameNamedModulePathOnlyTypes_RemainSeparate()
    {
        const string typeName = "Shared.ModuleOnly";
        var identityA = new TypeIdentity(typeName)
        {
            ModuleName = "Shared.dll",
            ModulePath = "/app/a/Shared.dll",
        };
        var identityB = new TypeIdentity(typeName)
        {
            ModuleName = "Shared.dll",
            ModulePath = "/app/b/Shared.dll",
        };
        var baseline = HeapSnapshotWithIdentity(
            retentionPaths: null,
            (identityA, 1_000, 10),
            (identityB, 2_000, 20));
        var current = HeapSnapshotWithIdentity(
            [
                new RetentionPath(
                    typeName,
                    0xA001,
                    [new RetentionFrame("Root.A", 0xA000) { RootKind = "StaticVar" }],
                    Truncated: false)
                {
                    TargetIdentity = identityA,
                },
                new RetentionPath(
                    typeName,
                    0xB001,
                    [new RetentionFrame("Root.B", 0xB000) { RootKind = "StaticVar" }],
                    Truncated: false)
                {
                    TargetIdentity = identityB,
                },
            ],
            (identityA, 5_000, 50),
            (identityB, 6_000, 60));

        var growth = HeapGrowthDiff.Build(baseline, "b", current, "c", "bytes", minDeltaPct: 5, topN: 25);

        growth.Growers.Should().HaveCount(2);
        growth.Growers.Single(g => g.Identity!.ModulePath == identityA.ModulePath)
            .RetentionPaths.Should().ContainSingle().Which.TargetObjectAddress.Should().Be(0xA001);
        growth.Growers.Single(g => g.Identity!.ModulePath == identityB.ModulePath)
            .RetentionPaths.Should().ContainSingle().Which.TargetObjectAddress.Should().Be(0xB001);

        var comparable = new HeapSnapshotComparableProjector().Project(current, "current");
        comparable.Rows.Should().HaveCount(2);
        comparable.Rows
            .Select(row => row.Key.ExactId ?? row.Key.StableId)
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Build_AsymmetricMvidMetadata_MatchesByUniqueSharedModulePathAndToken()
    {
        const string typeName = "Shared.Asymmetric";
        var baselineIdentity = new TypeIdentity(typeName)
        {
            ModuleName = "Shared.dll",
            ModulePath = "/app/Shared.dll",
            MetadataToken = 0x02000001,
        };
        var currentIdentity = baselineIdentity with
        {
            ModuleVersionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        };
        var baseline = HeapSnapshotWithIdentity(null, (baselineIdentity, 1_000, 10));
        var current = HeapSnapshotWithIdentity(null, (currentIdentity, 5_000, 50));

        var growth = HeapGrowthDiff.Build(baseline, "b", current, "c", "bytes", minDeltaPct: 0, topN: 25);

        var row = growth.Growers.Should().ContainSingle().Subject;
        row.IsNew.Should().BeFalse();
        row.BaselineBytes.Should().Be(1_000);
        row.BytesDelta.Should().Be(4_000);
    }

    [Fact]
    public void Build_DeduplicatesRankingLists_ButSumsDistinctLoadedModuleCopies()
    {
        const string typeName = "Shared.Copy";
        var stableIdentity = new TypeIdentity(typeName)
        {
            ModuleName = "Shared.dll",
            ModulePath = "/app/Shared.dll",
            ModuleVersionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            MetadataToken = 0x02000001,
        };
        var baselineCopyA = Stat(stableIdentity, 100, 10, moduleImageBase: 0x1000);
        var baselineCopyB = Stat(stableIdentity, 200, 20, moduleImageBase: 0x2000);
        var currentCopyA = Stat(stableIdentity, 150, 15, moduleImageBase: 0x3000);
        var currentCopyB = Stat(stableIdentity, 250, 25, moduleImageBase: 0x4000);
        var baseline = HeapSnapshotWithLists([baselineCopyA, baselineCopyB], [baselineCopyB, baselineCopyA]);
        var current = HeapSnapshotWithLists([currentCopyA, currentCopyB], [currentCopyB, currentCopyA]);

        var growth = HeapGrowthDiff.Build(baseline, "b", current, "c", "bytes", minDeltaPct: 0, topN: 25);

        var row = growth.Growers.Should().ContainSingle().Subject;
        row.BaselineBytes.Should().Be(300);
        row.CurrentBytes.Should().Be(400);
        row.BytesDelta.Should().Be(100);
        row.BaselineInstances.Should().Be(30);
        row.CurrentInstances.Should().Be(40);
    }

    [Fact]
    public void ComparableProjections_SumLoadedCopiesAndDeduplicateRankingLists()
    {
        var identity = new TypeIdentity("Shared.ProjectedCopy")
        {
            ModuleName = "Shared.dll",
            ModulePath = "/first/Shared.dll",
            ModuleVersionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            MetadataToken = 0x02000001,
        };
        var copyA = Stat(identity, 100, 10, moduleImageBase: 0x1000);
        var copyB = Stat(identity with { ModulePath = "/second/Shared.dll" }, 200, 20, moduleImageBase: 0x2000);
        var snapshot = HeapSnapshotWithLists([copyA, copyB], [copyB, copyA]);

        var typed = HeapSnapshotComparableProjector.ProjectTyped(snapshot);
        typed.Should().ContainSingle();
        typed.Values.Single().Should().Be(new HeapDiffMetric(300, 30));

        var generic = new HeapSnapshotComparableProjector().Project(snapshot, "sample");
        generic.Rows.Should().ContainSingle();
        generic.Rows[0].Metrics.Single(metric => metric.Definition.Name == "totalBytes").Value.Should().Be(300);
        generic.Rows[0].Metrics.Single(metric => metric.Definition.Name == "instanceCount").Value.Should().Be(30);
    }

    [Fact]
    public void PairwiseDiff_MatchesStrongIdentityAcrossDifferentCopyPaths()
    {
        var mvid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var baselineIdentity = new TypeIdentity("Shared.PairwiseCopy")
        {
            ModuleName = "Shared.dll",
            ModulePath = "/baseline/Shared.dll",
            ModuleVersionId = mvid,
            MetadataToken = 0x02000001,
        };
        var currentIdentity = baselineIdentity with { ModulePath = "/current/Shared.dll" };
        var baseline = HeapSnapshotWithLists(
            [Stat(baselineIdentity, 100, 10, 0x1000), Stat(baselineIdentity, 200, 20, 0x2000)],
            [Stat(baselineIdentity, 200, 20, 0x2000), Stat(baselineIdentity, 100, 10, 0x1000)]);
        var current = HeapSnapshotWithLists(
            [Stat(currentIdentity, 150, 15, 0x3000), Stat(currentIdentity, 250, 25, 0x4000)],
            [Stat(currentIdentity, 250, 25, 0x4000), Stat(currentIdentity, 150, 15, 0x3000)]);

        var diff = ComparablePairwiseSampleDiff.Compare(baseline, "b", current, "c", minDeltaPct: 0, topN: 10);

        var changed = diff.Changed.Should().ContainSingle().Subject;
        changed.Baseline!.TotalBytes.Should().Be(300);
        changed.Current!.TotalBytes.Should().Be(400);
        diff.Added.Should().BeEmpty();
        diff.Removed.Should().BeEmpty();
    }

    [Fact]
    public void PairwiseDiff_DoesNotAssignOneNameOnlyBaselineToAmbiguousCurrentModules()
    {
        const string typeName = "Shared.AmbiguousPairwise";
        var baseline = HeapSnapshotWithIdentity(
            null,
            (new TypeIdentity(typeName), 100, 1));
        var current = HeapSnapshotWithIdentity(
            null,
            (new TypeIdentity(typeName) { ModuleName = "A.dll", ModulePath = "/app/A.dll" }, 200, 2),
            (new TypeIdentity(typeName) { ModuleName = "B.dll", ModulePath = "/app/B.dll" }, 300, 3));

        var diff = ComparablePairwiseSampleDiff.Compare(
            baseline,
            "b",
            current,
            "c",
            minDeltaPct: 0,
            topN: 10);

        diff.Changed.Should().BeEmpty();
        diff.Added.Should().HaveCount(2);
        diff.Removed.Should().ContainSingle();
    }

    [Fact]
    public void RetentionTargets_DeduplicateCanonicalLoadedCopiesBeforeBudget()
    {
        var shared = new TypeIdentity("Shared.RetentionCopy")
        {
            ModuleName = "Shared.dll",
            ModuleVersionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            MetadataToken = 0x02000001,
        };
        var other = new TypeIdentity("Other.RetentionTarget")
        {
            ModuleName = "Other.dll",
            ModuleVersionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            MetadataToken = 0x02000001,
        };
        var ranked = new[]
        {
            Stat(shared with { ModulePath = "/first/Shared.dll" }, 500, 5, 0x1000),
            Stat(shared with { ModulePath = "/second/Shared.dll" }, 400, 4, 0x2000),
            Stat(other, 300, 3, 0x3000),
        };

        var targets = ClrMdRetentionAnalyzer.SelectTargets(ranked, targetCount: 2);

        targets.Select(static target => target.TypeFullName)
            .Should().Equal("Shared.RetentionCopy", "Other.RetentionTarget");
    }

    [Fact]
    public void Build_UniqueNameOnlyTargetIdentity_AttachesRetentionPath()
    {
        const string typeName = "Dynamic.Unique";
        var identity = new TypeIdentity(typeName)
        {
            ModuleName = "DynamicAssembly",
        };
        var path = new RetentionPath(
            typeName,
            0xD001,
            [new RetentionFrame("Root.Dynamic", 0xD000) { RootKind = "StrongHandle" }],
            Truncated: false)
        {
            TargetIdentity = new TypeIdentity(typeName),
        };
        var baseline = HeapSnapshotWithIdentity(null, (identity, 100, 1));
        var current = HeapSnapshotWithIdentity([path], (identity, 500, 5));

        var growth = HeapGrowthDiff.Build(baseline, "b", current, "c", "bytes", minDeltaPct: 0, topN: 25);

        growth.Growers.Should().ContainSingle()
            .Which.RetentionPaths.Should().ContainSingle()
            .Which.TargetObjectAddress.Should().Be(0xD001);
    }

    [Fact]
    public void Build_OnUnix_ModulePathFallbackIsCaseSensitive()
    {
        if (OperatingSystem.IsWindows()) return;

        const string typeName = "Shared.CaseSensitive";
        var upper = new TypeIdentity(typeName)
        {
            ModuleName = "Shared.dll",
            ModulePath = "/app/Shared.dll",
        };
        var lower = new TypeIdentity(typeName)
        {
            ModuleName = "shared.dll",
            ModulePath = "/app/shared.dll",
        };
        var path = new RetentionPath(
            typeName,
            0xC001,
            [new RetentionFrame("Root.Lower", 0xC000) { RootKind = "StaticVar" }],
            Truncated: false)
        {
            TargetIdentity = new TypeIdentity(typeName)
            {
                ModulePath = lower.ModulePath,
                ModuleName = lower.ModuleName,
            },
        };
        var baseline = HeapSnapshotWithIdentity(null, (upper, 100, 1), (lower, 100, 1));
        var current = HeapSnapshotWithIdentity([path], (upper, 200, 2), (lower, 300, 3));

        var growth = HeapGrowthDiff.Build(baseline, "b", current, "c", "bytes", minDeltaPct: 0, topN: 25);

        growth.Growers.Single(row => row.Identity!.ModulePath == lower.ModulePath)
            .RetentionPaths.Should().ContainSingle().Which.TargetObjectAddress.Should().Be(0xC001);
        growth.Growers.Single(row => row.Identity!.ModulePath == upper.ModulePath)
            .RetentionPaths.Should().BeNullOrEmpty();
    }

    [Fact]
    public void ClrMdRetentionMatching_OnUnix_UsesCaseSensitiveModulePaths()
    {
        if (OperatingSystem.IsWindows()) return;

        var target = new TypeIdentity("Shared.CaseSensitive")
        {
            ModulePath = "/app/Shared.dll",
            ModuleName = "Shared.dll",
        };
        var caseDistinctObserved = new TypeIdentity(target.TypeFullName)
        {
            ModulePath = "/app/shared.dll",
            ModuleName = "shared.dll",
        };

        ClrMdRetentionAnalyzer.MatchesTarget(target, caseDistinctObserved, sameNameCount: 2)
            .Should().BeFalse();
        ClrMdRetentionAnalyzer.MatchesTarget(target, target, sameNameCount: 2)
            .Should().BeTrue();
    }

    [Fact]
    public void TypeIdentity_PublicSchema_DoesNotExposeRuntimeModuleImageBase()
        => typeof(TypeIdentity).GetProperty("ModuleImageBase").Should().BeNull();

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

    private static HeapSnapshotArtifact HeapSnapshotWithIdentity(
        IReadOnlyList<RetentionPath>? retentionPaths,
        params (TypeIdentity identity, long bytes, long instances)[] rows)
    {
        var stats = rows.Select(row =>
            new TypeStat(
                TypeFullName: row.identity.TypeFullName,
                ModuleName: row.identity.ModuleName,
                InstanceCount: row.instances,
                TotalBytes: row.bytes,
                TotalBytesPercent: 0,
                Identity: row.identity)).ToArray();

        return new HeapSnapshotArtifact(
            Origin: HeapSnapshotOrigin.Live,
            ProcessId: 123,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(10),
            Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "x64", IsServerGC: false, HeapCount: 1),
            Heap: new DumpHeapSummary(stats.Sum(stat => stat.TotalBytes), 0, 0, 0, 0, 0, 0),
            TopTypesByBytes: stats,
            TopTypesByInstances: stats)
        {
            RetentionPaths = retentionPaths,
        };
    }

    private static TypeStat Stat(TypeIdentity identity, long bytes, long instances, ulong? moduleImageBase = null)
        => new(
            identity.TypeFullName,
            identity.ModuleName,
            instances,
            bytes,
            0,
            identity)
        {
            ModuleImageBase = moduleImageBase,
        };

    private static HeapSnapshotArtifact HeapSnapshotWithLists(
        IReadOnlyList<TypeStat> byBytes,
        IReadOnlyList<TypeStat> byInstances)
        => new(
            HeapSnapshotOrigin.Live,
            123,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(10),
            new DumpRuntimeInfo("CoreCLR", "10.0.0", "x64", IsServerGC: false, HeapCount: 1),
            new DumpHeapSummary(byBytes.Sum(stat => stat.TotalBytes), 0, 0, 0, 0, 0, 0),
            byBytes,
            byInstances);
}
