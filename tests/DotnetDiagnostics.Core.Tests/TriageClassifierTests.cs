using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Triage;
using FluentAssertions;
using System.Text.Json;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Unit coverage for <see cref="TriageClassifier"/>. Guards the separation between direct
/// observations, bounded hypotheses, and the deprecated verdict compatibility projection.
/// </summary>
public sealed class TriageClassifierTests
{
    private static CounterSnapshot SnapshotOf(params (string Name, double Value)[] counters)
    {
        var values = counters
            .Select(c => new CounterValue("System.Runtime", c.Name, c.Name, c.Value, CounterKind.Mean))
            .ToList();
        return new CounterSnapshot(
            ProcessId: 1234,
            StartedAt: DateTimeOffset.UnixEpoch,
            Duration: TimeSpan.FromSeconds(5),
            Counters: values,
            Meters: Array.Empty<MeterInstrumentValue>(),
            Notes: Array.Empty<string>());
    }

    [Fact]
    public void Classify_HighCpu_ReturnsCpuBound()
    {
        var result = TriageClassifier.Classify(SnapshotOf(("cpu-usage", 95)));

        result.Verdict.Should().Be(TriageClassifier.CpuBound);
        result.Assessment.Should().Be(TriageClassifier.CriticalAssessment);
        result.Severity.Should().Be(TriageSeverity.Critical);
        result.ObservedSignals.Should().ContainSingle(s => s.Name == "cpu.utilization");
        var hypothesis = result.Hypotheses.Should().ContainSingle().Subject;
        hypothesis.Name.Should().Be(TriageClassifier.CpuComputeDemandHypothesis);
        hypothesis.Confidence.Should().Be("high");
        hypothesis.SupportingEvidence.Should().ContainSingle(e =>
            e.Name == "cpu-usage" && e.Comparison == ">=" && e.Threshold == 90);
    }

    [Fact]
    public void Classify_HighGcTime_ReturnsGcPressure()
    {
        var result = TriageClassifier.Classify(SnapshotOf(("time-in-gc", 20)));

        result.Verdict.Should().Be(TriageClassifier.GcPressure);
        result.Hypotheses.Should().ContainSingle(h => h.Name == TriageClassifier.GcOverheadHypothesis);
    }

    [Fact]
    public void Classify_HighContention_ReturnsLockContention()
    {
        var result = TriageClassifier.Classify(
            SnapshotOf(("monitor-lock-contention-count", 25)),
            requestDurationP95: 0.05);

        result.Verdict.Should().Be(TriageClassifier.LockContention);
        result.Severity.Should().Be(TriageSeverity.Degraded);
        result.Hypotheses.Should().ContainSingle()
            .Which.ContradictingEvidence.Should().ContainSingle(e => e.Name == "request-duration-p95");
    }

    [Fact]
    public void Classify_HighAllocationRate_ReturnsMemoryPressure_EvenWhenGcTimeLow()
    {
        // The core gap this closes: a leak / allocation churn with moderate GC CPU time
        // must NOT be reported as healthy.
        var result = TriageClassifier.Classify(SnapshotOf(
            ("cpu-usage", 20),
            ("time-in-gc", 5),
            ("alloc-rate", 60_000_000))); // 60 MB/s

        result.Verdict.Should().Be(TriageClassifier.MemoryPressure);
        result.Severity.Should().Be(TriageSeverity.Degraded);
        result.Hypotheses.Should().ContainSingle(h => h.Name == TriageClassifier.ManagedMemoryActivityHypothesis);
    }

    [Fact]
    public void Classify_VeryHighAllocationRate_ReturnsCriticalMemoryPressure()
    {
        var result = TriageClassifier.Classify(SnapshotOf(("alloc-rate", 150_000_000))); // 150 MB/s

        result.Verdict.Should().Be(TriageClassifier.MemoryPressure);
        result.Severity.Should().Be(TriageSeverity.Critical);
        result.Hypotheses.Should().ContainSingle()
            .Which.Confidence.Should().Be("high");
        result.Hypotheses.Should().ContainSingle()
            .Which.SupportingEvidence.Should().ContainSingle(e =>
                e.Name == "alloc-rate" && e.Threshold == 100);
    }

    [Fact]
    public void Classify_FrequentGen2Collections_ReturnsMemoryPressure()
    {
        var result = TriageClassifier.Classify(SnapshotOf(("gen-2-gc-count", 5)));

        result.Verdict.Should().Be(TriageClassifier.MemoryPressure);
        result.Hypotheses.Should().ContainSingle()
            .Which.SupportingEvidence.Should().ContainSingle(e => e.Name == "gen-2-gc-count");
    }

    [Fact]
    public void Classify_GcPressureWithHighAllocation_KeepsGcPrimary_AndMemorySecondary()
    {
        var result = TriageClassifier.Classify(SnapshotOf(
            ("time-in-gc", 35),
            ("alloc-rate", 80_000_000)));

        result.Verdict.Should().Be(TriageClassifier.GcPressure);
        result.SecondaryVerdicts.Should().Contain(TriageClassifier.MemoryPressure);
        result.Severity.Should().Be(TriageSeverity.Critical);
        result.Hypotheses.Should().Contain(h => h.Name == TriageClassifier.GcOverheadHypothesis);
        result.Hypotheses.Should().Contain(h => h.Name == TriageClassifier.ManagedMemoryActivityHypothesis);
    }

    [Fact]
    public void Classify_ThreadPoolStarvationWithHighAllocation_KeepsThreadPoolPrimary()
    {
        // Regression: memory-pressure is lowest priority and must not steal the primary verdict
        // from an established signal like threadpool starvation.
        var result = TriageClassifier.Classify(SnapshotOf(
            ("cpu-usage", 40),
            ("threadpool-queue-length", 120),
            ("alloc-rate", 60_000_000)));

        result.Verdict.Should().Be(TriageClassifier.ThreadPoolStarvation);
        result.SecondaryVerdicts.Should().Contain(TriageClassifier.MemoryPressure);
        result.Hypotheses.Should().Contain(h => h.Name == TriageClassifier.ThreadPoolBacklogHypothesis);
        result.Hypotheses.Should().Contain(h => h.Name == TriageClassifier.ManagedMemoryActivityHypothesis);
    }

    [Fact]
    public void Classify_ThreadPoolQueueBuildup_ReturnsThreadPoolStarvation()
    {
        var result = TriageClassifier.Classify(SnapshotOf(
            ("cpu-usage", 80),
            ("threadpool-queue-length", 120)));

        result.Verdict.Should().Be(TriageClassifier.CpuBound);
        result.SecondaryVerdicts.Should().Contain(TriageClassifier.ThreadPoolStarvation);
        result.Hypotheses.Should().Contain(h => h.Name == TriageClassifier.CpuComputeDemandHypothesis);
        result.Hypotheses.Should().Contain(h => h.Name == TriageClassifier.ThreadPoolBacklogHypothesis);
    }

    [Fact]
    public void Classify_LowCpuWithSmallQueue_IsInconclusive_NotIoBound()
    {
        var result = TriageClassifier.Classify(SnapshotOf(
            ("cpu-usage", 10),
            ("threadpool-queue-length", 15)));

        result.Verdict.Should().Be(TriageClassifier.Inconclusive);
        result.Verdict.Should().NotBe(TriageClassifier.IoBound);
        result.Assessment.Should().Be(TriageClassifier.InconclusiveAssessment);
        result.Severity.Should().Be(TriageSeverity.Healthy);
        result.ObservedSignals.Should().ContainSingle(s => s.Name == "threadpool.queue" && s.Level == "elevated");
        result.Hypotheses.Should().BeEmpty(
            "a low-CPU snapshot with a small transient queue does not identify I/O or another cause");
    }

    [Fact]
    public void Classify_LowCpuQueueAndLatency_EmitsWaitingHypothesis_NotIoBound()
    {
        var result = TriageClassifier.Classify(
            SnapshotOf(
                ("cpu-usage", 10),
                ("threadpool-queue-length", 15)),
            requestDurationP95: 0.8);

        result.Verdict.Should().Be(TriageClassifier.Inconclusive);
        result.Verdict.Should().NotBe(TriageClassifier.IoBound);
        result.Assessment.Should().Be(TriageClassifier.DegradedAssessment);
        var hypothesis = result.Hypotheses.Should().ContainSingle().Subject;
        hypothesis.Name.Should().Be(TriageClassifier.WaitingOrBackpressureHypothesis);
        hypothesis.Confidence.Should().Be("moderate");
        hypothesis.SupportingEvidence.Should().HaveCount(3);
        hypothesis.Summary.Should().Contain("does not identify I/O");
    }

    [Fact]
    public void Classify_CriticalQueueWithLatency_EmitsStrongBacklogAndWaitingHypotheses()
    {
        var result = TriageClassifier.Classify(
            SnapshotOf(
                ("cpu-usage", 10),
                ("threadpool-queue-length", 250)),
            requestDurationP95: 2.5);

        result.Verdict.Should().Be(TriageClassifier.ThreadPoolStarvation,
            "the field remains as a deprecated compatibility projection");
        result.Assessment.Should().Be(TriageClassifier.CriticalAssessment);
        result.Severity.Should().Be(TriageSeverity.Critical);
        result.Hypotheses.Should().Contain(h =>
            h.Name == TriageClassifier.ThreadPoolBacklogHypothesis && h.Confidence == "high");
        result.Hypotheses.Should().Contain(h =>
            h.Name == TriageClassifier.WaitingOrBackpressureHypothesis && h.Confidence == "high");
        var backlog = result.Hypotheses!.Single(h => h.Name == TriageClassifier.ThreadPoolBacklogHypothesis);
        backlog.SupportingEvidence.Should().Contain(e =>
            e.Name == "threadpool-queue-length" && e.Threshold == 200);
        backlog.SupportingEvidence.Should().Contain(e =>
            e.Name == "request-duration-p95" && e.Threshold == 500);
        var waiting = result.Hypotheses.Single(h => h.Name == TriageClassifier.WaitingOrBackpressureHypothesis);
        waiting.SupportingEvidence.Should().Contain(e =>
            e.Name == "threadpool-queue-length" && e.Threshold == 50);
        waiting.SupportingEvidence.Should().Contain(e =>
            e.Name == "request-duration-p95" && e.Threshold == 2_000);
    }

    [Fact]
    public void Classify_ExceptionStorm_IsObservedButCauseIsInconclusive()
    {
        var result = TriageClassifier.Classify(SnapshotOf(("exception-count", 75)));

        result.Verdict.Should().Be(TriageClassifier.Inconclusive);
        result.Assessment.Should().Be(TriageClassifier.InconclusiveAssessment);
        result.Severity.Should().Be(TriageSeverity.Critical);
        result.ObservedSignals.Should().ContainSingle(s => s.Name == "exceptions.rate");
        result.Hypotheses.Should().BeEmpty();
    }

    [Fact]
    public void Classify_AllQuiet_ReturnsHealthy()
    {
        var result = TriageClassifier.Classify(SnapshotOf(
            ("cpu-usage", 10),
            ("time-in-gc", 2),
            ("threadpool-queue-length", 1),
            ("monitor-lock-contention-count", 0),
            ("alloc-rate", 1_000_000),
            ("gen-2-gc-count", 0)));

        result.Verdict.Should().Be(TriageClassifier.Healthy);
        result.Assessment.Should().Be(TriageClassifier.HealthyAssessment);
        result.Severity.Should().Be(TriageSeverity.Healthy);
        result.ObservedSignals.Should().BeEmpty();
        result.Hypotheses.Should().BeEmpty();
    }

    [Fact]
    public void Classify_MixedSignals_OrdersHypothesesAndBacksEachWithExplicitEvidence()
    {
        var result = TriageClassifier.Classify(
            SnapshotOf(
                ("cpu-usage", 95),
                ("time-in-gc", 35),
                ("threadpool-queue-length", 120),
                ("monitor-lock-contention-count", 25),
                ("alloc-rate", 80_000_000),
                ("gen-2-gc-count", 5)),
            requestDurationP95: 0.9);

        result.Assessment.Should().Be(TriageClassifier.CriticalAssessment);
        result.Hypotheses.Should().HaveCount(5);
        result.Hypotheses!.Select(h => h.Name).Should().Equal(
            TriageClassifier.CpuComputeDemandHypothesis,
            TriageClassifier.GcOverheadHypothesis,
            TriageClassifier.ThreadPoolBacklogHypothesis,
            TriageClassifier.SynchronizationContentionHypothesis,
            TriageClassifier.ManagedMemoryActivityHypothesis);
        result.Hypotheses.Should().OnlyContain(h =>
            h.SupportingEvidence.Count > 0
            && h.SupportingEvidence.All(e => !string.IsNullOrWhiteSpace(e.Rationale))
            && !string.IsNullOrWhiteSpace(h.NextStep));
    }

    [Fact]
    public void Classify_HighConfidenceHypotheses_DisplayTheirEscalationThresholds()
    {
        var gc = TriageClassifier.Classify(SnapshotOf(("time-in-gc", 35)));
        gc.Hypotheses.Should().ContainSingle()
            .Which.SupportingEvidence.Should().Contain(e =>
                e.Name == "time-in-gc" && e.Threshold == 30);

        var contention = TriageClassifier.Classify(
            SnapshotOf(("monitor-lock-contention-count", 75)),
            requestDurationP95: 0.8);
        var contentionHypothesis = contention.Hypotheses.Should().ContainSingle().Subject;
        contentionHypothesis.Confidence.Should().Be("high");
        contentionHypothesis.SupportingEvidence.Should().Contain(e =>
            e.Name == "monitor-lock-contention-count" && e.Threshold == 50);
        contentionHypothesis.SupportingEvidence.Should().Contain(e =>
            e.Name == "request-duration-p95" && e.Threshold == 500);

        var gen2 = TriageClassifier.Classify(SnapshotOf(("gen-2-gc-count", 12)));
        gen2.Hypotheses.Should().ContainSingle()
            .Which.SupportingEvidence.Should().Contain(e =>
                e.Name == "gen-2-gc-count" && e.Threshold == 10);
    }

    [Fact]
    public void Classify_EqualConfidence_PrioritizesCriticalSignalsAheadOfWeakerCpu()
    {
        var result = TriageClassifier.Classify(SnapshotOf(
            ("cpu-usage", 75),
            ("threadpool-queue-length", 250),
            ("monitor-lock-contention-count", 75)));

        result.Hypotheses!.Select(h => h.Name).Should().Equal(
            TriageClassifier.ThreadPoolBacklogHypothesis,
            TriageClassifier.SynchronizationContentionHypothesis,
            TriageClassifier.CpuComputeDemandHypothesis);
        result.Hypotheses.Should().OnlyContain(h => h.Confidence == "moderate");
    }

    [Fact]
    public void Classify_SerializedContract_PreservesLegacyFieldsAndAddsV2Fields()
    {
        var result = TriageClassifier.Classify(SnapshotOf(("cpu-usage", 95)));

        var json = JsonSerializer.SerializeToNode(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json!["verdict"]!.GetValue<string>().Should().Be(TriageClassifier.CpuBound);
        json["severity"].Should().NotBeNull();
        json["evidence"].Should().NotBeNull();
        json["topIndicators"].Should().NotBeNull();
        json["modelVersion"]!.GetValue<int>().Should().Be(2);
        json["assessment"]!.GetValue<string>().Should().Be(TriageClassifier.CriticalAssessment);
        json["observedSignals"]!.AsArray().Should().NotBeEmpty();
        json["hypotheses"]!.AsArray().Should().NotBeEmpty();
    }

    [Fact]
    public void TriageResult_DeserializesLegacyPayloadAsModelVersionOne()
    {
        const string legacyJson =
            """
            {
              "verdict": "healthy",
              "severity": 0,
              "evidence": {
                "cpuUsage": 5,
                "timeInGc": 0,
                "threadPoolQueueLength": 0,
                "monitorLockContentionCount": 0,
                "allocRate": 1000,
                "gen2GcCount": 0,
                "gcHeapSize": 1000000,
                "exceptionCount": 0,
                "requestDurationP95": null
              },
              "secondaryVerdicts": null,
              "topIndicators": []
            }
            """;

        var result = JsonSerializer.Deserialize<TriageResult>(
            legacyJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        result.Should().NotBeNull();
        result!.ModelVersion.Should().Be(1);
        result.Verdict.Should().Be(TriageClassifier.Healthy);
        result.ObservedSignals.Should().BeNull();
        result.Hypotheses.Should().BeNull();

        var (verdict, severity, evidence, secondaryVerdicts, topIndicators) = result;
        verdict.Should().Be(TriageClassifier.Healthy);
        severity.Should().Be(TriageSeverity.Healthy);
        evidence.CpuUsage.Should().Be(5);
        secondaryVerdicts.Should().BeNull();
        topIndicators.Should().BeEmpty();
    }
}
