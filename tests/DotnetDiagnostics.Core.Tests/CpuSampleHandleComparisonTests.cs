using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class CpuSampleHandleComparisonTests
{
    [Fact]
    public void CompatiblePair_ReturnsObjectiveMetricsQualityAndBoundedMovers()
    {
        var baseline = Artifact(
            123,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("Shared", 30, 30),
            ("Removed", 20, 20),
            ("Other", 50, 50));
        var current = Artifact(
            123,
            DateTimeOffset.UnixEpoch.AddSeconds(10),
            TimeSpan.FromSeconds(8),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("Shared", 50, 50),
            ("Added", 10, 10),
            ("Other", 40, 40));

        var result = CpuSampleHandleComparison.Compare(
            Handle("baseline", baseline),
            baseline,
            Handle("current", current),
            current,
            topN: 10);

        result.IsError.Should().BeFalse();
        var comparison = result.Data!;
        comparison.Schema.Should().Be(CpuSampleHandleComparisonResult.SchemaV1);
        comparison.GetType().GetProperty("Verdict").Should().BeNull();
        comparison.Compatibility.Status.Should().Be("qualified");
        comparison.Baseline.Quality.Should().NotBeNull();
        comparison.Current.Quality.Should().NotBeNull();
        comparison.Baseline.TotalSamples.Should().Be(100);
        comparison.Current.DurationSeconds.Should().Be(8);
        comparison.Notes.Should().Contain(note => note.Contains("Workload equivalence", StringComparison.Ordinal));
        comparison.Notes.Should().Contain(note => note.Contains("durations differ", StringComparison.Ordinal));

        var shared = comparison.TopMovers.Single(row => row.DisplayName == "Shared");
        shared.BaselineExclusiveSamples.Should().Be(30);
        shared.CurrentExclusiveSamples.Should().Be(50);
        shared.ExclusiveShare.BaselineDenominator.Should().Be(100);
        shared.ExclusiveShare.CurrentDenominator.Should().Be(100);
        shared.ExclusiveShare.DeltaAbs.Should().Be(20);
        shared.ExclusiveShare.DeltaUnit.Should().Be("percentage-points");
        shared.ExclusiveShare.BetterDirection.Should().Be("neutral");
        shared.ExclusiveShare.Outcome.Should().Be("up");

        var added = comparison.TopMovers.Single(row => row.DisplayName == "Added");
        added.BaselineExclusiveSamples.Should().BeNull();
        added.ExclusiveShare.DeltaAbs.Should().BeNull();
        added.ExclusiveShare.Outcome.Should().Be("unavailable");
        added.ExclusiveShare.UnavailableReason.Should().Be("MissingBaselineObservation");
        var removed = comparison.TopMovers.Single(row => row.DisplayName == "Removed");
        removed.CurrentExclusiveSamples.Should().BeNull();
        removed.ExclusiveShare.UnavailableReason.Should().Be("MissingCurrentObservation");
    }

    [Fact]
    public void SameHandle_AaComparisonIsFlatWithoutPerformanceVerdict()
    {
        var artifact = Artifact(
            123,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.EventPipeSampleProfiler,
            ("Shared", 100, 100));
        var handle = Handle("same", artifact);

        var comparison = CpuSampleHandleComparison.Compare(handle, artifact, handle, artifact).Data!;

        comparison.Compatibility.Status.Should().Be("qualified");
        comparison.TopMovers.Should().ContainSingle()
            .Which.ExclusiveShare.Outcome.Should().Be("flat");
        comparison.TopMovers.Single().ExclusiveShare.DeltaAbs.Should().Be(0);
    }

    [Fact]
    public void BackendMismatch_RetainsObservationsButSuppressesDeltas()
    {
        var baseline = Artifact(
            123,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("Shared", 20, 20));
        var current = Artifact(
            123,
            DateTimeOffset.UnixEpoch.AddSeconds(5),
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.WindowsEtwOnCpu,
            ("Shared", 40, 40));

        var comparison = CpuSampleHandleComparison.Compare(
            Handle("baseline", baseline),
            baseline,
            Handle("current", current),
            current).Data!;

        comparison.Compatibility.Status.Should().Be("incompatible");
        comparison.Compatibility.Reasons.Should().ContainSingle(reason => reason.Code == "CpuBackendMismatch");
        var mover = comparison.TopMovers.Should().ContainSingle().Which;
        mover.ExclusiveShare.BaselineValue.Should().Be(100);
        mover.ExclusiveShare.CurrentValue.Should().Be(100);
        mover.ExclusiveShare.DeltaAbs.Should().BeNull();
        mover.ExclusiveShare.Outcome.Should().Be("unavailable");
        mover.ExclusiveShare.UnavailableReason.Should().Be("CpuBackendMismatch");
        comparison.Metrics.Single(metric => metric.Name == "samplePopulation")
            .Delta.UnavailableReason.Should().Be("CpuBackendMismatch");
        comparison.Metrics.Single(metric => metric.Name == "duration")
            .Delta.DeltaAbs.Should().Be(0);
    }

    [Fact]
    public void ReversedChronologyAndDifferentProcessAreExplicitErrors()
    {
        var earlier = Artifact(
            123,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("Shared", 10, 10));
        var later = Artifact(
            123,
            DateTimeOffset.UnixEpoch.AddSeconds(10),
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("Shared", 10, 10));

        var reversed = CpuSampleHandleComparison.Compare(
            Handle("later", later),
            later,
            Handle("earlier", earlier),
            earlier);
        reversed.Error.Should().NotBeNull();
        reversed.Error!.Kind.Should().Be("InvalidArgument");
        reversed.Error.Message.Should().Contain("earlier capture");

        var otherProcess = later with { ProcessId = 456 };
        var crossProcess = CpuSampleHandleComparison.Compare(
            Handle("baseline", earlier),
            earlier,
            Handle("current", otherProcess),
            otherProcess);
        crossProcess.Error.Should().NotBeNull();
        crossProcess.Error!.Message.Should().Contain("same process");

        var offlineHandle = Handle("offline", earlier) with { Origin = HandleOrigin.Imported };
        var offline = CpuSampleHandleComparison.Compare(
            offlineHandle,
            earlier,
            Handle("current", later),
            later);
        offline.Error.Should().NotBeNull();
        offline.Error!.Message.Should().Contain("live CPU handles");
    }

    [Fact]
    public void ZeroBaseline_PreservesAbsoluteDeltaAndMarksRelativeDeltaUnavailable()
    {
        var baseline = Artifact(
            123,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("Shared", 0, 10),
            ("Other", 100, 100));
        var current = Artifact(
            123,
            DateTimeOffset.UnixEpoch.AddSeconds(5),
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("Shared", 10, 10),
            ("Other", 90, 90));

        var comparison = CpuSampleHandleComparison.Compare(
            Handle("baseline", baseline),
            baseline,
            Handle("current", current),
            current).Data!;

        var shared = comparison.TopMovers.Single(row => row.DisplayName == "Shared");
        shared.ExclusiveShare.BaselineValue.Should().Be(0);
        shared.ExclusiveShare.CurrentValue.Should().Be(10);
        shared.ExclusiveShare.DeltaAbs.Should().Be(10);
        shared.ExclusiveShare.RelativeDelta.Should().BeNull();
        shared.ExclusiveShare.RelativeDeltaUnavailableReason.Should().Be("ZeroBaseline");
    }

    [Fact]
    public void UnresolvedSymbols_AreReportedAsQualityAndPopulationEvidence()
    {
        var baseline = Artifact(
            123,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("[unknown]", 20, 20),
            ("Shared", 80, 80));
        var current = Artifact(
            123,
            DateTimeOffset.UnixEpoch.AddSeconds(5),
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("0x1234", 40, 40),
            ("Shared", 60, 60));

        var comparison = CpuSampleHandleComparison.Compare(
            Handle("baseline", baseline),
            baseline,
            Handle("current", current),
            current,
            topN: 10).Data!;

        comparison.Baseline.UnresolvedExclusiveSamples.Should().Be(20);
        comparison.Baseline.UnresolvedExclusivePercent.Should().Be(20);
        comparison.Current.UnresolvedExclusiveSamples.Should().Be(40);
        comparison.Current.UnresolvedExclusivePercent.Should().Be(40);
        comparison.TopMovers.Where(row => row.Unresolved).Should().HaveCount(2);
        var unresolvedMetric = comparison.Metrics.Single(metric => metric.Name == "unresolvedExclusivePercent");
        unresolvedMetric.Delta.DeltaAbs.Should().Be(20);
        unresolvedMetric.Delta.BaselineDenominator.Should().Be(100);
        unresolvedMetric.Delta.CurrentDenominator.Should().Be(100);
    }

    [Fact]
    public void TopMovers_AreHardCapped()
    {
        var rows = Enumerable.Range(1, 60)
            .Select(index => ($"Method{index:D2}", (long)index, (long)index))
            .ToArray();
        var baseline = Artifact(
            123,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            rows);
        var current = Artifact(
            123,
            DateTimeOffset.UnixEpoch.AddSeconds(5),
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            rows);

        var comparison = CpuSampleHandleComparison.Compare(
            Handle("baseline", baseline),
            baseline,
            Handle("current", current),
            current,
            CpuSampleHandleComparison.MaxTopMovers).Data!;

        comparison.TopMovers.Should().HaveCount(CpuSampleHandleComparison.MaxTopMovers);
        CpuSampleHandleComparison.Compare(
            Handle("baseline", baseline),
            baseline,
            Handle("current", current),
            current,
            CpuSampleHandleComparison.MaxTopMovers + 1)
            .Error.Should().NotBeNull();
    }

    [Fact]
    public void MethodShares_UseRawCountsAndDenominatorsBeforeFinalRounding()
    {
        var baseline = Artifact(
            123,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("Shared", 50_049, 50_049),
            ("Other", 949_951, 949_951));
        var current = Artifact(
            123,
            DateTimeOffset.UnixEpoch.AddSeconds(5),
            TimeSpan.FromSeconds(5),
            CpuSampleEvidence.LinuxPerfOnCpu,
            ("Shared", 50_050, 50_050),
            ("Other", 949_950, 949_950));

        var comparison = CpuSampleHandleComparison.Compare(
            Handle("baseline", baseline),
            baseline,
            Handle("current", current),
            current).Data!;

        var shared = comparison.TopMovers.Single(row => row.DisplayName == "Shared");
        shared.ExclusiveShare.BaselineValue.Should().Be(5.0049);
        shared.ExclusiveShare.CurrentValue.Should().Be(5.005);
        shared.ExclusiveShare.DeltaAbs.Should().Be(0.0001);
        shared.ExclusiveShare.Outcome.Should().Be("up");
    }

    private static DiagnosticHandle Handle(string id, CpuSampleTraceArtifact artifact) =>
        new(id, DateTimeOffset.UtcNow.AddMinutes(10), artifact.ProcessId, "cpu-sample")
        {
            Origin = HandleOrigin.Live,
            ProducingTool = "collect_sample",
        };

    private static CpuSampleTraceArtifact Artifact(
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        CpuSampleEvidence evidence,
        params (string Method, long Exclusive, long Inclusive)[] rows)
    {
        var total = rows.Sum(static row => row.Exclusive);
        var root = new CallTreeNode(
            new SampledFrame("[root]", "<root>"),
            total,
            0,
            rows.Select(static row => new CallTreeNode(
                new SampledFrame("App.dll", row.Method),
                row.Inclusive,
                row.Exclusive,
                [])).ToArray());
        return new CpuSampleTraceArtifact(processId, startedAt, duration, total, root)
        {
            Evidence = evidence,
        };
    }
}
