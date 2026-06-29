using System;
using System.Collections.Generic;
using System.Linq;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.ReplicaCounters;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

public sealed class ReplicaCounterSkewAnalyzerTests
{
    [Fact]
    public void Analyze_FlagsHeapOutlierReplica()
    {
        var readings = new[]
        {
            Reading("pod-a", cpu: 30, heap: 100, queue: 0),
            Reading("pod-b", cpu: 31, heap: 105, queue: 0),
            Reading("pod-c", cpu: 95, heap: 900, queue: 40),
        };

        var skew = ReplicaCounterSkewAnalyzer.Analyze(readings);

        skew.PodCount.Should().Be(3);
        skew.OutlierPod.Should().Be("pod-c");
        skew.OutlierScore.Should().BeGreaterThan(0);
        var heap = skew.Metrics.Single(m => m.Metric == "gc-heap-size");
        heap.MaxPod.Should().Be("pod-c");
        heap.MinPod.Should().Be("pod-a");
        heap.Spread.Should().BeApproximately(800, 0.01);
    }

    [Fact]
    public void Analyze_NoOutlier_WhenReplicasUniform()
    {
        var readings = new[]
        {
            Reading("pod-a", cpu: 50, heap: 200, queue: 2),
            Reading("pod-b", cpu: 50, heap: 200, queue: 2),
            Reading("pod-c", cpu: 50, heap: 200, queue: 2),
        };

        var skew = ReplicaCounterSkewAnalyzer.Analyze(readings);

        skew.OutlierPod.Should().BeNull();
        skew.Warnings.Should().Contain(w => w.Contains("within noise"));
    }

    [Fact]
    public void Analyze_TwoReplicas_NamesNoOutlier_ButReportsDispersion()
    {
        var readings = new[]
        {
            Reading("pod-a", cpu: 10, heap: 100, queue: 0),
            Reading("pod-b", cpu: 90, heap: 900, queue: 40),
        };

        var skew = ReplicaCounterSkewAnalyzer.Analyze(readings);

        skew.OutlierPod.Should().BeNull();
        skew.Metrics.Should().NotBeEmpty();
        skew.Warnings.Should().Contain(w => w.Contains("two replicas"));
    }

    [Fact]
    public void Analyze_SingleReplica_Warns()
    {
        var skew = ReplicaCounterSkewAnalyzer.Analyze(new[] { Reading("solo", 10, 10, 0) });

        skew.PodCount.Should().Be(1);
        skew.Warnings.Should().Contain(w => w.Contains(">=2"));
    }

    [Fact]
    public void Project_ExtractsHeadlineMetrics()
    {
        var snapshot = new CounterSnapshot(
            ProcessId: 42,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(5),
            Counters: new[]
            {
                new CounterValue("System.Runtime", "cpu-usage", "CPU", 73, CounterKind.Mean, "%"),
                new CounterValue("System.Runtime", "gc-heap-size", "Heap", 512, CounterKind.Mean, "MB"),
                new CounterValue("System.Runtime", "threadpool-queue-length", "Q", 4, CounterKind.Mean),
                new CounterValue("System.Runtime", "alloc-rate", "Alloc", 999, CounterKind.Sum),
            },
            Meters: Array.Empty<MeterInstrumentValue>(),
            Notes: Array.Empty<string>());

        var reading = ReplicaCounterSkewAnalyzer.Project("pod-x", snapshot);

        reading.ProcessId.Should().Be(42);
        reading.Values["cpu"].Should().Be(73);
        reading.Values["gc-heap-size"].Should().Be(512);
        reading.Values["threadpool-queue"].Should().Be(4);
        reading.Values.Should().NotContainKey("alloc-rate");
    }

    private static ReplicaCounterReading Reading(string pod, double cpu, double heap, double queue)
        => new(pod, 1, new Dictionary<string, double>
        {
            ["cpu"] = cpu,
            ["gc-heap-size"] = heap,
            ["threadpool-queue"] = queue,
        });
}
