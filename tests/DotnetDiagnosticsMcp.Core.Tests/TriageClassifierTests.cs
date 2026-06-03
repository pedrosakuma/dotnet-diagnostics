using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.Triage;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// Unit coverage for <see cref="TriageClassifier"/>. Guards that the triage verdict surface is
/// comprehensive across the dimensions humans reach for triage about — CPU, memory, GC, contention,
/// thread-pool starvation and I/O — so a "my app is slow" prompt is never silently mis-classified
/// as healthy (#280 discoverability follow-up).
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
        result.Severity.Should().Be(TriageSeverity.Critical);
    }

    [Fact]
    public void Classify_HighGcTime_ReturnsGcPressure()
    {
        var result = TriageClassifier.Classify(SnapshotOf(("time-in-gc", 20)));

        result.Verdict.Should().Be(TriageClassifier.GcPressure);
    }

    [Fact]
    public void Classify_HighContention_ReturnsLockContention()
    {
        var result = TriageClassifier.Classify(SnapshotOf(("monitor-lock-contention-count", 25)));

        result.Verdict.Should().Be(TriageClassifier.LockContention);
        result.Severity.Should().Be(TriageSeverity.Degraded);
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
    }

    [Fact]
    public void Classify_VeryHighAllocationRate_ReturnsCriticalMemoryPressure()
    {
        var result = TriageClassifier.Classify(SnapshotOf(("alloc-rate", 150_000_000))); // 150 MB/s

        result.Verdict.Should().Be(TriageClassifier.MemoryPressure);
        result.Severity.Should().Be(TriageSeverity.Critical);
    }

    [Fact]
    public void Classify_FrequentGen2Collections_ReturnsMemoryPressure()
    {
        var result = TriageClassifier.Classify(SnapshotOf(("gen-2-gc-count", 5)));

        result.Verdict.Should().Be(TriageClassifier.MemoryPressure);
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
    }

    [Fact]
    public void Classify_ThreadPoolQueueBuildup_ReturnsThreadPoolStarvation()
    {
        var result = TriageClassifier.Classify(SnapshotOf(
            ("cpu-usage", 80),
            ("threadpool-queue-length", 120)));

        result.Verdict.Should().Be(TriageClassifier.CpuBound);
        result.SecondaryVerdicts.Should().Contain(TriageClassifier.ThreadPoolStarvation);
    }

    [Fact]
    public void Classify_LowCpuWithQueueBuildup_ReturnsIoBound()
    {
        var result = TriageClassifier.Classify(SnapshotOf(
            ("cpu-usage", 10),
            ("threadpool-queue-length", 15)));

        result.Verdict.Should().Be(TriageClassifier.IoBound);
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
        result.Severity.Should().Be(TriageSeverity.Healthy);
    }
}
