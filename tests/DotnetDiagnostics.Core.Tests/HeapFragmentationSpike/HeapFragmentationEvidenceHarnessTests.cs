using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.HeapFragmentationSpike;

public sealed class HeapFragmentationEvidenceHarnessTests
{
    [Fact]
    public void KnownShapes_AggregateByteTotalsAndProportionalStripCannotDistinguishHoleTopology()
    {
        var contiguous = HeapFragmentationEvidenceHarness.Compare(
            KnownHeapLayoutFixtures.NonFragmented().Observation,
            topK: 4);
        var fragmented = HeapFragmentationEvidenceHarness.Compare(
            KnownHeapLayoutFixtures.Fragmented().Observation,
            topK: 4);

        fragmented.CurrentAggregate.UsedBytes.Should().Be(contiguous.CurrentAggregate.UsedBytes);
        fragmented.CurrentAggregate.FreeBytes.Should().Be(contiguous.CurrentAggregate.FreeBytes);
        fragmented.CurrentAggregate.FreePercent.Should().Be(contiguous.CurrentAggregate.FreePercent);
        fragmented.CurrentAggregate.ObjectCount.Should().Be(contiguous.CurrentAggregate.ObjectCount);
        fragmented.CurrentAggregate.FreeObjectCount.Should().Be(10);
        contiguous.CurrentAggregate.FreeObjectCount.Should().Be(1);
        contiguous.Fixture.Should().Be("non-fragmented-free-tail-control");
        fragmented.Proportional.Should().BeEquivalentTo(contiguous.Proportional);
        contiguous.Proportional.Cells.Should().Be("UUUUUUUUUUUUUUUUFFFF");
    }

    [Fact]
    public void KnownShapes_LargestHoleAndBoundedRangesDiscriminateOneLargeHoleFromManySmallHoles()
    {
        var contiguous = HeapFragmentationEvidenceHarness.Compare(
            KnownHeapLayoutFixtures.NonFragmented().Observation,
            topK: 4);
        var fragmented = HeapFragmentationEvidenceHarness.Compare(
            KnownHeapLayoutFixtures.Fragmented().Observation,
            topK: 4);

        contiguous.Detailed.LargestObservedFreeBlockBytes.Should().Be(200);
        contiguous.Detailed.ObservedRangeCount.Should().Be(1);
        contiguous.Detailed.RetainedRangeCount.Should().Be(1);
        contiguous.Detailed.OmittedRangeCount.Should().Be(0);

        fragmented.Detailed.LargestObservedFreeBlockBytes.Should().Be(20);
        fragmented.Detailed.ObservedRangeCount.Should().Be(10);
        fragmented.Detailed.RetainedRangeCount.Should().Be(4);
        fragmented.Detailed.OmittedRangeCount.Should().Be(6);
        fragmented.Detailed.TopFreeRanges.Select(range => range.Start).Should().Equal(
            0x1050UL,
            0x10b4UL,
            0x1118UL,
            0x117cUL);
    }

    [Fact]
    public void TopK_IsDeterministicAndRetainedAtInsertionTime()
    {
        var retainer = new BoundedFreeRangeRetainer(capacity: 3);
        retainer.Observe(new FreeRangeProjection(0x5000, 40));
        retainer.Observe(new FreeRangeProjection(0x3000, 80));
        retainer.Observe(new FreeRangeProjection(0x4000, 80));
        retainer.Observe(new FreeRangeProjection(0x1000, 80));
        retainer.Observe(new FreeRangeProjection(0x2000, 20));
        retainer.Observe(new FreeRangeProjection(0x6000, 100));

        retainer.Snapshot().Should().Equal(
            new FreeRangeProjection(0x6000, 100),
            new FreeRangeProjection(0x1000, 80),
            new FreeRangeProjection(0x3000, 80));
        retainer.LargestObserved.Should().Be(new FreeRangeProjection(0x6000, 100));
        retainer.ObservedCount.Should().Be(6);
        retainer.PeakRetainedCount.Should().Be(3);
        retainer.StorageSlotCount.Should().Be(3);
        retainer.SelectionComparisonCount.Should().Be(10);
    }

    [Fact]
    public void HighHoleCount_DoesNotIncreaseRetainedStorageOrRangeCount()
    {
        const int topK = 8;
        var small = HeapFragmentationEvidenceHarness.Compare(
            KnownHeapLayoutFixtures.HighHoleCount(32),
            topK);
        var large = HeapFragmentationEvidenceHarness.Compare(
            KnownHeapLayoutFixtures.HighHoleCount(100_000),
            topK);

        small.Detailed.RetentionStorageSlots.Should().Be(topK);
        large.Detailed.RetentionStorageSlots.Should().Be(topK);
        small.Detailed.PeakRetainedRangeCount.Should().Be(topK);
        large.Detailed.PeakRetainedRangeCount.Should().Be(topK);
        large.Detailed.RetainedRangeCount.Should().Be(topK);
        large.Detailed.ObservedRangeCount.Should().Be(100_000);
        large.Detailed.OmittedRangeCount.Should().Be(100_000 - topK);
        large.Detailed.TopKSelectionComparisonCount.Should().Be(799_964);
        large.Detailed.TopKSelectionComparisonUpperBound.Should().Be(100_000 * topK);
        large.Detailed.TopKSelectionComparisonCount.Should()
            .BeLessThanOrEqualTo(large.Detailed.TopKSelectionComparisonUpperBound);
        large.Detailed.TopFreeRanges.Select(range => range.Length).Should().Equal(
            100_000,
            99_999,
            99_998,
            99_997,
            99_996,
            99_995,
            99_994,
            99_993);
    }

    [Fact]
    public void PartialWalk_ProjectsResidualBytesAsUnknownWithoutLivenessOrRetainedSizeClaims()
    {
        var comparison = HeapFragmentationEvidenceHarness.Compare(
            KnownHeapLayoutFixtures.Partial().Observation,
            topK: 4,
            stripWidth: 20);

        comparison.CurrentAggregate.UsedBytes.Should().Be(500);
        comparison.CurrentAggregate.FreeBytes.Should().Be(150);
        comparison.Accounting.Should().Be(new HeapLayoutAccounting(
            HeapLayoutEvidenceState.Partial,
            SegmentBytes: 1_000,
            AccountedBytes: 650,
            UnknownBytes: 350,
            State: "partial"));
        comparison.Proportional.Cells.Should().Be("UUUUUUUUUUFFF???????");
        comparison.Conclusions.SupportsObjectLiveness.Should().BeFalse();
        comparison.Conclusions.SupportsRetainedObjectSize.Should().BeFalse();

        var rendered = comparison.Render();
        rendered.Should().Contain("unknown=350");
        rendered.Should().Contain("objectLiveness=unavailable");
        rendered.Should().Contain("retainedObjectSize=unavailable");
    }

    [Fact]
    public void CurrentLiveContract_WithoutObservableCompletionSignal_IsIndeterminate()
    {
        var comparison = HeapFragmentationEvidenceHarness.Compare(
            KnownHeapLayoutFixtures.CurrentLiveContractIndeterminate(),
            topK: 4);

        comparison.Accounting.EvidenceState.Should().Be(HeapLayoutEvidenceState.Indeterminate);
        comparison.Accounting.State.Should().Be("indeterminate");
        comparison.Render().Should().Contain("accounting: state=indeterminate");
    }

    [Fact]
    public void PinningGroundTruth_IsIndistinguishableThroughCurrentObservableContract()
    {
        var concentrated = KnownHeapLayoutFixtures.PinnedConcentrated();
        var dispersed = KnownHeapLayoutFixtures.PinnedDispersed();

        concentrated.GroundTruthPinnedObjectAddresses.Should().NotEqual(
            dispersed.GroundTruthPinnedObjectAddresses);

        var concentratedProjection = HeapFragmentationEvidenceHarness.Compare(
            concentrated.Observation,
            topK: 4);
        var dispersedProjection = HeapFragmentationEvidenceHarness.Compare(
            dispersed.Observation,
            topK: 4);

        (concentratedProjection with { Fixture = "same" })
            .Should().BeEquivalentTo(dispersedProjection with { Fixture = "same" });
        concentratedProjection.Pinning.PinnedRootCount.Should().Be(4);
        concentratedProjection.Pinning.PinnedBytes.Should().BeNull();
        concentratedProjection.Pinning.Concentration.Should().BeNull();
        concentratedProjection.Pinning.Limitation.Should().Contain(
            "not correlated with segment addresses");
    }

    [Fact]
    public void RenderedComparison_ContainsAllThreeEvidenceLevelsAndBoundedCounts()
    {
        var rendered = HeapFragmentationEvidenceHarness.Compare(
            KnownHeapLayoutFixtures.Fragmented().Observation,
            topK: 4).Render();

        rendered.Should().Contain("current aggregate:");
        rendered.Should().Contain("proportional:");
        rendered.Should().Contain("detailed:");
        rendered.Should().Contain("largestFree=20");
        rendered.Should().Contain("ranges observed=10, retained=4, omitted=6");
        rendered.Should().Contain("topKComparisons=9/40");
        rendered.Split(Environment.NewLine)
            .Count(line => line.StartsWith("  free ", StringComparison.Ordinal))
            .Should().Be(4);
    }
}
