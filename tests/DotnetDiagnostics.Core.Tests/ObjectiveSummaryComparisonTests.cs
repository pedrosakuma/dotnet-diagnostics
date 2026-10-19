using System.Text.Json;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Memory;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class ObjectiveSummaryComparisonTests
{
    [Theory]
    [InlineData("request-throughput", 10, 20, "requests/s", "up", 10)]
    [InlineData("request-latency-p95", 20, 10, "ms", "down", -0.01)]
    [InlineData("threadpool-thread-count", 0, 10, null, "up", 10)]
    [InlineData("unregistered-measurement", 10, 10, "widgets", "flat", 0)]
    [InlineData("unregistered-measurement", 10, 0, null, "down", -10)]
    public void Metrics_ReportSignedDeltasWithoutBenefitJudgments(
        string name, double baseline, double current, string? unit, string outcome, double delta)
    {
        var diff = new SummaryComparer().Compare(
            Summary(name, baseline, unit), Summary(name, current, unit));

        diff.Verdict.Should().Be("not_assessed");
        var metric = diff.KeyMetricDeltas.Should().ContainSingle().Which;
        metric.BaselineValue.Should().Be(baseline);
        metric.CurrentValue.Should().Be(current);
        metric.BaselineUnit.Should().Be(unit);
        metric.CurrentUnit.Should().Be(unit);
        metric.BetterDirection.Should().Be("neutral");
        metric.Outcome.Should().Be(outcome);
        metric.DeltaAbs.Should().BeApproximately(delta, 1e-12);
        metric.DeltaUnit.Should().Be(unit == "ms" ? "s" : unit);
        diff.Notes.Should().Contain(note => note.Contains("Workload equivalence", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, "s", 900, "ms", -0.1, "s", "down")]
    [InlineData(600, "requests/min", 20, "requests/s", 10, "requests/s", "up")]
    [InlineData(600, "requests/min", 1200, "requests/min", 10, "requests/s", "up")]
    [InlineData(100, "ms", 200, "ms", 0.1, "s", "up")]
    public void Conversions_KeepRawValuesAndExplicitCommonDeltaUnit(
        double baseline, string baselineUnit, double current, string currentUnit,
        double delta, string deltaUnit, string outcome)
    {
        var before = Summary("custom", baseline, baselineUnit);
        var after = Summary("custom", current, currentUnit);
        var diff = new SummaryComparer().Compare(before, after);

        var metric = diff.KeyMetricDeltas.Single();
        metric.DeltaAbs.Should().BeApproximately(delta, 1e-12);
        metric.DeltaUnit.Should().Be(deltaUnit);
        metric.Outcome.Should().Be(outcome);
        metric.BaselineValue.Should().Be(baseline);
        metric.CurrentValue.Should().Be(current);
        metric.BaselineUnit.Should().Be(baselineUnit);
        metric.CurrentUnit.Should().Be(currentUnit);
        before.Findings.KeyMetrics!["custom"].Should().Be(baseline);
        after.Findings.KeyMetrics!["custom"].Should().Be(current);
    }

    [Theory]
    [InlineData(null, "s")]
    [InlineData("ms", null)]
    [InlineData("bytes/s", "requests/s")]
    [InlineData("Mb", "MB")]
    [InlineData("Mb/s", "MB/s")]
    [InlineData("Widgets", "widgets")]
    [InlineData("Ms", "ms")]
    public void IncompatibleUnits_RetainRowsWithoutInventingDeltas(string? beforeUnit, string? afterUnit)
    {
        var diff = new SummaryComparer().Compare(
            Summary("custom", 1, beforeUnit), Summary("custom", 2, afterUnit));
        diff.Verdict.Should().Be("incomparable");
        var metric = diff.KeyMetricDeltas.Single();
        metric.Outcome.Should().Be("incomparable");
        metric.BetterDirection.Should().Be("unknown");
        metric.DeltaAbs.Should().BeNull();
        metric.DeltaUnit.Should().BeNull();
        diff.Notes.Should().Contain(note => note.Contains("incompatible units", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteInput_IsRejectedExplicitly(double value)
    {
        var compare = new SummaryComparer();
        var invalid = Summary("custom", value, "widgets");
        var valid = Summary("custom", 1, "widgets");
        compare.Invoking(c => c.Compare(invalid, valid)).Should().Throw<ArgumentException>()
            .WithParameterName("baseline").WithMessage("*must be finite*");
        compare.Invoking(c => c.Compare(valid, invalid)).Should().Throw<ArgumentException>()
            .WithParameterName("current").WithMessage("*must be finite*");
    }

    [Fact]
    public void OverflowingDifference_IsUnavailableNotZero()
    {
        var diff = new SummaryComparer().Compare(
            Summary("custom", -double.MaxValue, "widgets"),
            Summary("custom", double.MaxValue, "widgets"));
        diff.Verdict.Should().Be("incomparable");
        diff.KeyMetricDeltas.Single().DeltaAbs.Should().BeNull();
        diff.Notes.Should().Contain(note => note.Contains("nonfinite", StringComparison.Ordinal));
    }

    [Fact]
    public void OverflowingHotspotDifference_RemainsSerializableAndExplicitlyUnavailable()
    {
        var diff = new SummaryComparer().Compare(
            WithCpu(Summary(), CpuSampleEvidenceKind.OsOnCpuSamples, Hotspot("same", -double.MaxValue)),
            WithCpu(Summary(), CpuSampleEvidenceKind.OsOnCpuSamples, Hotspot("same", double.MaxValue)));
        diff.Verdict.Should().Be("incomparable");
        diff.ChangedHotspots.Single().InclusiveDeltaPoints.Should().BeNull();
        diff.Notes.Should().Contain(note => note.Contains("percentage-point difference is nonfinite", StringComparison.Ordinal));
        JsonSerializer.Serialize(diff).Should().Contain("\"InclusiveDeltaPoints\":null");
    }

    [Theory]
    [InlineData(10, 15, 5, 1)]
    [InlineData(15, 10, -5, 1)]
    [InlineData(10, 11, 1, 0)]
    [InlineData(10, 12, 2, 1)]
    [InlineData(10, 10, 0, 0)]
    public void HotspotSelection_DoesNotJudgePerformanceOrAssumeAbsenceIsZero(
        double beforeShare, double afterShare, double change, int changedRows)
    {
        var before = WithCpu(Summary(), CpuSampleEvidenceKind.OsOnCpuSamples,
            Hotspot("shared", beforeShare), Hotspot("removed", 20));
        var after = WithCpu(Summary(), CpuSampleEvidenceKind.OsOnCpuSamples,
            Hotspot("shared", afterShare), Hotspot("new", 30));
        var diff = new SummaryComparer().Compare(before, after);

        diff.Verdict.Should().Be("not_assessed");
        var added = diff.NewHotspots.Should().ContainSingle().Which;
        added.BaselineInclusivePercent.Should().BeNull();
        added.CurrentInclusivePercent.Should().Be(30);
        added.InclusiveDeltaPoints.Should().BeNull();
        added.CurrentSelfSamples.Should().Be(new SelfSampleBreakdown(10, 0));
        var removed = diff.RemovedHotspots.Should().ContainSingle().Which;
        removed.BaselineInclusivePercent.Should().Be(20);
        removed.CurrentInclusivePercent.Should().BeNull();
        removed.InclusiveDeltaPoints.Should().BeNull();
        removed.BaselineSelfSamples.Should().Be(new SelfSampleBreakdown(10, 0));
        diff.ChangedHotspots.Should().HaveCount(changedRows);
        if (changedRows > 0)
            diff.ChangedHotspots.Single().InclusiveDeltaPoints.Should().Be(change);
    }

    [Theory]
    [InlineData(CpuSampleEvidenceKind.OsOnCpuSamples, CpuSampleEvidenceKind.OsOnCpuSamples, "not_assessed")]
    [InlineData(CpuSampleEvidenceKind.StackFrequencyWithHeuristicWaits, CpuSampleEvidenceKind.StackFrequencyWithHeuristicWaits, "inconclusive")]
    [InlineData(CpuSampleEvidenceKind.LegacyUnknown, CpuSampleEvidenceKind.LegacyUnknown, "incomparable")]
    [InlineData(CpuSampleEvidenceKind.OsOnCpuSamples, CpuSampleEvidenceKind.StackFrequencyWithHeuristicWaits, "incomparable")]
    public void CpuSemantics_RetainConservativeStructuralStates(
        CpuSampleEvidenceKind baseline, CpuSampleEvidenceKind current, string verdict)
    {
        var diff = new SummaryComparer().Compare(
            WithCpu(Summary(), baseline, Hotspot("shared", 10)),
            WithCpu(Summary(), current, Hotspot("shared", 20)));
        diff.Verdict.Should().Be(verdict);
        diff.ChangedHotspots.Single().InclusiveDeltaPoints.Should().Be(10);
    }

    [Fact]
    public void EventPipe_WithComparableMetrics_PreservesStackFrequencyLimitation()
    {
        var diff = new SummaryComparer().Compare(
            WithCpu(Summary("latency", 1, "s"), CpuSampleEvidenceKind.StackFrequencyWithHeuristicWaits, Hotspot("shared", 10)),
            WithCpu(Summary("latency", 2, "s"), CpuSampleEvidenceKind.StackFrequencyWithHeuristicWaits, Hotspot("shared", 20)));
        diff.Verdict.Should().Be("not_assessed");
        diff.KeyMetricDeltas.Single().Outcome.Should().Be("up");
        diff.Notes.Should().Contain(note => note.Contains("not measured on-CPU time", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptySummaries_AreInconclusiveRatherThanHealthy()
    {
        new SummaryComparer().Compare(Summary(), Summary()).Verdict.Should().Be("inconclusive");
    }

    [Fact]
    public void MissingMetric_DoesNotBecomeZeroOrHideBehindAnUnchangedRow()
    {
        var before = Summary("shared", 10, "widgets") with
        {
            Findings = Summary("shared", 10, "widgets").Findings with
            {
                KeyMetrics = new Dictionary<string, double> { ["shared"] = 10, ["only-before"] = 2 },
            },
        };
        var diff = new SummaryComparer().Compare(before, Summary("shared", 10, "widgets"));
        diff.Verdict.Should().Be("incomparable");
        var absent = diff.KeyMetricDeltas.Single(row => row.Name == "only-before");
        absent.BaselineValue.Should().Be(2);
        absent.CurrentValue.Should().BeNull();
        absent.Outcome.Should().Be("incomparable");
        absent.DeltaAbs.Should().BeNull();
        diff.KeyMetricDeltas.Single(row => row.Name == "shared").DeltaAbs.Should().Be(0);
        diff.Notes.Should().Contain(note => note.Contains("absent from one summary", StringComparison.Ordinal));
    }

    [Fact]
    public void OppositeMetricDirections_AreNotAMixedPerformanceVerdict()
    {
        var before = Summary() with
        {
            Findings = Summary().Findings with
            {
                KeyMetrics = new Dictionary<string, double> { ["latency"] = 10, ["throughput"] = 10 },
            },
        };
        var after = before with
        {
            Findings = before.Findings with
            {
                KeyMetrics = new Dictionary<string, double> { ["latency"] = 5, ["throughput"] = 20 },
            },
        };
        var diff = new SummaryComparer().Compare(before, after);
        diff.Verdict.Should().Be("not_assessed");
        diff.KeyMetricDeltas.Should().Contain(row => row.Name == "latency" && row.Outcome == "down" && row.DeltaAbs == -5);
        diff.KeyMetricDeltas.Should().Contain(row => row.Name == "throughput" && row.Outcome == "up" && row.DeltaAbs == 10);
    }

    [Fact]
    public void LegacySavedSummaryAndPublicConstructors_RemainReadable()
    {
        var original = Summary("throughput", 10);
        var saved = JsonSerializer.Serialize(original, InvestigationSummaryJsonContext.Default.InvestigationSummary);
        var restored = JsonSerializer.Deserialize(saved, InvestigationSummaryJsonContext.Default.InvestigationSummary)!;
        restored.Findings.CpuEvidenceKind.Should().BeNull();
        var diff = new SummaryComparer().Compare(restored, Summary("throughput", 20));
        diff.Verdict.Should().Be("not_assessed");
        diff.KeyMetricDeltas.Single().DeltaUnit.Should().BeNull();
        new KeyMetricDelta("old", 1, 2, "higher", "improved").DeltaAbs.Should().BeNull();
        var roundTrip = JsonSerializer.Deserialize<SummaryDiff>(JsonSerializer.Serialize(diff))!;
        roundTrip.KeyMetricDeltas.Single().DeltaAbs.Should().Be(10);
    }

    private static HotspotSummary Hotspot(string name, double share) =>
        new(new SymbolRef("App.dll", name), 10, 10, share, share)
        {
            SelfSamples = new SelfSampleBreakdown(10, 0),
        };

    private static InvestigationSummary WithCpu(
        InvestigationSummary summary, CpuSampleEvidenceKind kind, params HotspotSummary[] hotspots) =>
        summary with { Findings = summary.Findings with { CpuEvidenceKind = kind, TopHotspots = hotspots } };

    private static InvestigationSummary Summary(string? name = null, double value = 0, string? unit = null) =>
        new(InvestigationSummary.SchemaV1, "test", DateTimeOffset.UnixEpoch, 1,
            new InvestigationProvenance(),
            new InvestigationFindings(0, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), [],
                name is null ? null : new Dictionary<string, double> { [name] = value })
            {
                KeyMetricUnits = name is null ? null : new Dictionary<string, string?> { [name] = unit },
            });
}
