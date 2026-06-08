using DotnetDiagnosticsMcp.Core.Comparison;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class SnapshotDifferTests
{
    private static MetricValue Metric(string name, double value, BetterDirection dir = BetterDirection.Lower, MetricRole role = MetricRole.Primary, string? unit = "count")
        => new(new MetricDefinition(name, role, dir, MetricAggregation.Point, MetricNormalization.None, unit), value);

    private static ComparableSnapshot MetricSnap(string label, string kind, params MetricValue[] metrics)
        => new(ComparableSnapshot.SchemaV1, kind, label, DateTimeOffset.UnixEpoch, 1, metrics, Array.Empty<ComparableRow>());

    private static ComparableSnapshot KeySnap(string label, params (string id, double value)[] rows)
        => new(
            ComparableSnapshot.SchemaV1, "cpu-sample", label, DateTimeOffset.UnixEpoch, 1,
            Array.Empty<MetricValue>(),
            rows.Select(r => new ComparableRow(
                new ComparableKey("cpu-sample", r.id),
                r.id,
                new[] { Metric("exclusivePercent", r.value) })).ToArray());

    // ---- Guard / validation -----------------------------------------------------------------

    [Fact]
    public void FewerThanTwo_IsIncomparable()
    {
        var diff = SnapshotDiffer.Compare(new[] { MetricSnap("only", "gc-datas", Metric("m", 1)) });
        diff.Verdict.Should().Be("incomparable");
    }

    [Fact]
    public void MixedKinds_IsIncomparable()
    {
        var diff = SnapshotDiffer.Compare(new[]
        {
            MetricSnap("a", "gc-datas", Metric("m", 1)),
            MetricSnap("b", "counters", Metric("m", 1)),
        });
        diff.Verdict.Should().Be("incomparable");
    }

    // ---- Metric verdicts (N=2) --------------------------------------------------------------

    [Fact]
    public void LowerBetter_Decrease_IsImprovement()
    {
        var diff = SnapshotDiffer.Compare(new[]
        {
            MetricSnap("base", "gc-datas", Metric("tcp", 10)),
            MetricSnap("after", "gc-datas", Metric("tcp", 5)),
        });
        diff.Verdict.Should().Be("improvement");
        diff.MetricSeries.Single().Direction.Should().Be("improved");
        diff.MetricSeries.Single().DeltaAbs.Should().Be(-5);
    }

    [Fact]
    public void LowerBetter_Increase_IsRegression()
    {
        var diff = SnapshotDiffer.Compare(new[]
        {
            MetricSnap("base", "gc-datas", Metric("tcp", 5)),
            MetricSnap("after", "gc-datas", Metric("tcp", 10)),
        });
        diff.Verdict.Should().Be("regression");
    }

    [Fact]
    public void OnePrimaryUp_OneDown_IsMixed()
    {
        var diff = SnapshotDiffer.Compare(new[]
        {
            MetricSnap("base", "gc-datas", Metric("a", 10), Metric("b", 5)),
            MetricSnap("after", "gc-datas", Metric("a", 5), Metric("b", 10)),
        });
        diff.Verdict.Should().Be("mixed");
    }

    [Fact]
    public void NoMeaningfulChange_IsNoChange()
    {
        var diff = SnapshotDiffer.Compare(new[]
        {
            MetricSnap("base", "gc-datas", Metric("a", 10)),
            MetricSnap("after", "gc-datas", Metric("a", 10)),
        });
        diff.Verdict.Should().Be("no_change");
    }

    [Fact]
    public void MetricAbsentInEndpoint_IsNotApplicable_WithNote()
    {
        var diff = SnapshotDiffer.Compare(new[]
        {
            MetricSnap("base", "gc-datas", Metric("a", 10)),
            MetricSnap("after", "gc-datas", Metric("b", 10)),
        });
        diff.MetricSeries.Should().OnlyContain(s => s.Direction == "n/a");
        diff.Notes.Should().Contain(n => n.Contains("absent"));
        diff.Verdict.Should().Be("no_overlap");
    }

    // ---- Trend classification (N>=3) --------------------------------------------------------

    [Theory]
    [InlineData(new[] { 1.0, 2.0, 3.0 }, MetricTrend.MonotonicUp)]
    [InlineData(new[] { 10.0, 2.0, 1.0, 1.0 }, MetricTrend.Converged)]
    [InlineData(new[] { 5.0, 1.0, 5.0, 1.0 }, MetricTrend.Oscillating)]
    [InlineData(new[] { 3.0, 3.0, 3.0 }, MetricTrend.Flat)]
    [InlineData(new[] { 9.0, 6.0, 3.0 }, MetricTrend.MonotonicDown)]
    public void Trend_IsClassified(double[] values, MetricTrend expected)
    {
        var snaps = values.Select((v, i) => MetricSnap($"t{i}", "gc-datas", Metric("m", v))).ToArray();
        var diff = SnapshotDiffer.Compare(snaps);
        diff.MetricSeries.Single().Trend.Should().Be(expected);
    }

    [Fact]
    public void Pairwise_HasHeadlineBaselineEachAndAdjacent()
    {
        var diff = SnapshotDiffer.Compare(new[]
        {
            MetricSnap("t0", "gc-datas", Metric("m", 10)),
            MetricSnap("t1", "gc-datas", Metric("m", 5)),
            MetricSnap("t2", "gc-datas", Metric("m", 2)),
        });

        diff.Pairwise!.Headline.Relation.Should().Be("first→last");
        diff.Pairwise.Headline.Verdict.Should().Be("improvement");
        diff.Pairwise.BaselineEach.Should().HaveCount(2);
        diff.Pairwise.Adjacent.Should().HaveCount(2);
        diff.Pairwise.Adjacent.Should().OnlyContain(p => p.Verdict == "improvement");
    }

    // ---- Dispersion -------------------------------------------------------------------------

    [Fact]
    public void Dispersion_UniformReplicas_IsUniform()
    {
        var snaps = Enumerable.Range(0, 4).Select(i => MetricSnap($"pod{i}", "gc-datas", Metric("m", 10))).ToArray();
        var diff = SnapshotDiffer.Compare(snaps, JourneyMode.Dispersion);

        diff.Verdict.Should().Be("uniform");
        diff.Pairwise.Should().BeNull();
        var disp = diff.MetricSeries.Single().Dispersion!;
        disp.OutlierIndex.Should().Be(-1);
        disp.StdDev.Should().Be(0);
    }

    [Fact]
    public void Dispersion_OneOutlierReplica_IsDispersed_WithOutlierIndex()
    {
        var diff = SnapshotDiffer.Compare(new[]
        {
            MetricSnap("pod0", "gc-datas", Metric("m", 10)),
            MetricSnap("pod1", "gc-datas", Metric("m", 10)),
            MetricSnap("pod2", "gc-datas", Metric("m", 10)),
            MetricSnap("pod3", "gc-datas", Metric("m", 50)),
        }, JourneyMode.Dispersion);

        diff.Verdict.Should().Be("dispersed");
        diff.MetricSeries.Single().Dispersion!.OutlierIndex.Should().Be(3);
    }

    // ---- Key-set kinds ----------------------------------------------------------------------

    [Fact]
    public void KeySet_SharedKeyImproves_IsImprovement()
    {
        var diff = SnapshotDiffer.Compare(new[]
        {
            KeySnap("base", ("A", 10), ("B", 5)),
            KeySnap("after", ("A", 5), ("B", 5)),
        });

        diff.Verdict.Should().Be("improvement");
        diff.KeyMatrix.Should().HaveCount(2);
    }

    [Fact]
    public void KeySet_AddedKey_LowerBetter_IsRegression()
    {
        var diff = SnapshotDiffer.Compare(new[]
        {
            KeySnap("base", ("A", 10)),
            KeySnap("after", ("A", 10), ("C", 7)),
        });

        diff.Verdict.Should().Be("regression");
    }

    [Fact]
    public void KeySet_NoSharedKeys_IsNoOverlap()
    {
        var diff = SnapshotDiffer.Compare(new[]
        {
            KeySnap("base", ("A", 10)),
            KeySnap("after", ("B", 10)),
        });

        diff.Verdict.Should().Be("no_overlap");
    }

    [Fact]
    public void KeySet_TruncatesToTopN_WithNote()
    {
        var baseRows = Enumerable.Range(0, 30).Select(i => ($"K{i}", 100.0 - i)).ToArray();
        var afterRows = Enumerable.Range(0, 30).Select(i => ($"K{i}", 200.0 - i)).ToArray();

        var diff = SnapshotDiffer.Compare(new[] { KeySnap("base", baseRows), KeySnap("after", afterRows) }, topN: 5);

        diff.KeyMatrix.Should().HaveCount(5);
        diff.Notes.Should().Contain(n => n.Contains("truncated"));
    }

    [Fact]
    public void KeySet_Dispersion_UniformRowsAcrossReplicas_IsUniform()
    {
        var diff = SnapshotDiffer.Compare(new[]
        {
            KeySnap("pod0", ("A", 10), ("B", 5)),
            KeySnap("pod1", ("A", 10), ("B", 5)),
            KeySnap("pod2", ("A", 10), ("B", 5)),
        }, JourneyMode.Dispersion);

        diff.Verdict.Should().Be("uniform");
    }

    [Fact]
    public void KeySet_Dispersion_OutlierRowAcrossReplicas_IsDispersed()
    {
        var diff = SnapshotDiffer.Compare(new[]
        {
            KeySnap("pod0", ("A", 10)),
            KeySnap("pod1", ("A", 10)),
            KeySnap("pod2", ("A", 50)),
        }, JourneyMode.Dispersion);

        diff.Verdict.Should().Be("dispersed");
    }

    [Fact]
    public void KeySet_DuplicateKeyInCapture_IsNoted()
    {
        var snapshot = new ComparableSnapshot(
            ComparableSnapshot.SchemaV1, "cpu-sample", "base", DateTimeOffset.UnixEpoch, 1,
            Array.Empty<MetricValue>(),
            new[]
            {
                new ComparableRow(new ComparableKey("cpu-sample", "A"), "A", new[] { Metric("p", 10) }),
                new ComparableRow(new ComparableKey("cpu-sample", "A"), "A", new[] { Metric("p", 99) }),
            });
        var after = KeySnap("after", ("A", 5));

        var diff = SnapshotDiffer.Compare(new[] { snapshot, after });

        diff.Notes.Should().Contain(n => n.Contains("Duplicate key"));
        // First occurrence kept: 10 -> 5 is an improvement, not 99 -> 5.
        diff.KeyMatrix.Single().Values[0].Should().Be(10);
    }

    [Fact]
    public void DifferentProcessIds_AreNoted()
    {
        var a = new ComparableSnapshot(ComparableSnapshot.SchemaV1, "gc-datas", "a", DateTimeOffset.UnixEpoch, 100, new[] { Metric("m", 1) }, Array.Empty<ComparableRow>());
        var b = new ComparableSnapshot(ComparableSnapshot.SchemaV1, "gc-datas", "b", DateTimeOffset.UnixEpoch, 200, new[] { Metric("m", 1) }, Array.Empty<ComparableRow>());

        var diff = SnapshotDiffer.Compare(new[] { a, b });
        diff.Notes.Should().Contain(n => n.Contains("different process ids"));
    }
}
