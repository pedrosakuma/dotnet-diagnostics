using System.Collections.Concurrent;
using DotnetDiagnostics.Core.GatedCapture;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class GatedCaptureTests
{
    [Theory]
    [InlineData("cpu>85", GatedCaptureMetric.Cpu, TriggerOperator.GreaterThan, 85)]
    [InlineData("cpu >= 85", GatedCaptureMetric.Cpu, TriggerOperator.GreaterOrEqual, 85)]
    [InlineData("gcHeapMb>=1500", GatedCaptureMetric.GcHeapMb, TriggerOperator.GreaterOrEqual, 1500)]
    [InlineData("rssMb<1024", GatedCaptureMetric.RssMb, TriggerOperator.LessThan, 1024)]
    [InlineData("threadCount <= 10", GatedCaptureMetric.ThreadCount, TriggerOperator.LessOrEqual, 10)]
    [InlineData("activeTimerCount>1000", GatedCaptureMetric.ActiveTimerCount, TriggerOperator.GreaterThan, 1000)]
    public void TryParse_AcceptsValidPredicates(string text, GatedCaptureMetric metric, TriggerOperator op, double threshold)
    {
        TriggerPredicate.TryParse(text, out var predicate, out var error).Should().BeTrue();
        error.Should().BeNull();
        predicate!.Metric.Should().Be(metric);
        predicate.Operator.Should().Be(op);
        predicate.Threshold.Should().Be(threshold);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cpu")]
    [InlineData("cpu=85")]
    [InlineData("bogus>10")]
    [InlineData("cpu>notanumber")]
    public void TryParse_RejectsInvalidPredicates(string? text)
    {
        TriggerPredicate.TryParse(text, out var predicate, out var error).Should().BeFalse();
        predicate.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TryParse_RoundTripsThroughToString()
    {
        TriggerPredicate.TryParse("gcHeapMb>=1500", out var predicate, out _).Should().BeTrue();
        predicate!.ToString().Should().Be("gcHeapMb>=1500");

        TriggerPredicate.TryParse(predicate.ToString(), out var reparsed, out _).Should().BeTrue();
        reparsed.Should().Be(predicate);
    }

    [Theory]
    [InlineData(TriggerOperator.GreaterThan, 85, 90, true)]
    [InlineData(TriggerOperator.GreaterThan, 85, 85, false)]
    [InlineData(TriggerOperator.GreaterOrEqual, 85, 85, true)]
    [InlineData(TriggerOperator.LessThan, 10, 5, true)]
    [InlineData(TriggerOperator.LessOrEqual, 10, 10, true)]
    [InlineData(TriggerOperator.LessThan, 10, 10, false)]
    public void Evaluate_AppliesOperator(TriggerOperator op, double threshold, double value, bool expected)
    {
        var predicate = new TriggerPredicate(GatedCaptureMetric.Cpu, op, threshold);
        predicate.Evaluate(value).Should().Be(expected);
    }

    [Fact]
    public void IsUpperBound_DistinguishesDirection()
    {
        new TriggerPredicate(GatedCaptureMetric.Cpu, TriggerOperator.GreaterThan, 1).IsUpperBound.Should().BeTrue();
        new TriggerPredicate(GatedCaptureMetric.Cpu, TriggerOperator.GreaterOrEqual, 1).IsUpperBound.Should().BeTrue();
        new TriggerPredicate(GatedCaptureMetric.Cpu, TriggerOperator.LessThan, 1).IsUpperBound.Should().BeFalse();
        new TriggerPredicate(GatedCaptureMetric.Cpu, TriggerOperator.LessOrEqual, 1).IsUpperBound.Should().BeFalse();
    }

    [Theory]
    [InlineData("cpu-sample", GatedCaptureKind.CpuSample)]
    [InlineData("cpu", GatedCaptureKind.CpuSample)]
    [InlineData("dump", GatedCaptureKind.Dump)]
    [InlineData("heap", GatedCaptureKind.Heap)]
    [InlineData("heap-snapshot", GatedCaptureKind.Heap)]
    [InlineData("thread-snapshot", GatedCaptureKind.ThreadSnapshot)]
    [InlineData("threads", GatedCaptureKind.ThreadSnapshot)]
    public void GatedCaptureKinds_TryParse_AcceptsAliases(string token, GatedCaptureKind expected)
    {
        GatedCaptureKinds.TryParse(token, out var kind).Should().BeTrue();
        kind.Should().Be(expected);
    }

    [Fact]
    public async Task WatchAndCapture_FiresCaptureWhenPredicateTrips()
    {
        // Sampler emits 50, 60, 95 → predicate cpu>85 trips on the third sample.
        var sampler = new ScriptedSampler(50, 60, 95);
        var collector = new ThresholdGatedCaptureCollector(sampler, NeverExits);
        var predicate = new TriggerPredicate(GatedCaptureMetric.Cpu, TriggerOperator.GreaterThan, 85);

        var captured = new ConcurrentBag<double>();
        var result = await collector.WatchAndCaptureAsync(
            processId: 1234,
            predicate,
            GatedCaptureKind.CpuSample,
            window: TimeSpan.FromSeconds(30),
            maxCaptures: 1,
            sampleInterval: TimeSpan.FromMilliseconds(5),
            captureCallback: (trigger, _) =>
            {
                captured.Add(trigger.ObservedValue);
                return Task.FromResult(new GatedCaptureOutcome("captured", Handle: "h-1"));
            });

        result.Tripped.Should().BeTrue();
        result.Captures.Should().HaveCount(1);
        result.Captures[0].Handle.Should().Be("h-1");
        result.PeakObservedValue.Should().Be(95);
        captured.Should().ContainSingle().Which.Should().Be(95);
        result.ProcessExited.Should().BeFalse();
    }

    [Fact]
    public async Task WatchAndCapture_StopsAtMaxCaptures()
    {
        var sampler = new ScriptedSampler(90, 91, 92, 93, 94);
        var collector = new ThresholdGatedCaptureCollector(sampler, NeverExits);
        var predicate = new TriggerPredicate(GatedCaptureMetric.Cpu, TriggerOperator.GreaterThan, 50);

        var fired = 0;
        var result = await collector.WatchAndCaptureAsync(
            1234, predicate, GatedCaptureKind.CpuSample,
            window: TimeSpan.FromSeconds(30),
            maxCaptures: 2,
            sampleInterval: TimeSpan.FromMilliseconds(5),
            captureCallback: (_, _) =>
            {
                Interlocked.Increment(ref fired);
                return Task.FromResult(new GatedCaptureOutcome("captured", Handle: "h"));
            });

        result.Captures.Should().HaveCount(2);
        fired.Should().Be(2);
    }

    [Fact]
    public async Task WatchAndCapture_ReturnsWhenWindowExpiresWithoutTripping()
    {
        var sampler = new ScriptedSampler(10, 20, 30);
        var collector = new ThresholdGatedCaptureCollector(sampler, NeverExits);
        var predicate = new TriggerPredicate(GatedCaptureMetric.Cpu, TriggerOperator.GreaterThan, 85);

        var result = await collector.WatchAndCaptureAsync(
            1234, predicate, GatedCaptureKind.CpuSample,
            window: TimeSpan.FromMilliseconds(200),
            maxCaptures: 1,
            sampleInterval: TimeSpan.FromMilliseconds(5),
            captureCallback: (_, _) => Task.FromResult(new GatedCaptureOutcome("nope")));

        result.Tripped.Should().BeFalse();
        result.Captures.Should().BeEmpty();
        result.WindowExpired.Should().BeTrue();
        result.SamplesObserved.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task WatchAndCapture_ReturnsWhenProcessExits()
    {
        var sampler = new ScriptedSampler(10, 20);
        var exited = new TaskCompletionSource();
        var collector = new ThresholdGatedCaptureCollector(
            sampler,
            async (_, ct) =>
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
                exited.TrySetResult();
            });
        var predicate = new TriggerPredicate(GatedCaptureMetric.Cpu, TriggerOperator.GreaterThan, 85);

        var result = await collector.WatchAndCaptureAsync(
            1234, predicate, GatedCaptureKind.Dump,
            window: TimeSpan.FromSeconds(30),
            maxCaptures: 1,
            sampleInterval: TimeSpan.FromMilliseconds(5),
            captureCallback: (_, _) => Task.FromResult(new GatedCaptureOutcome("nope")));

        result.ProcessExited.Should().BeTrue();
        result.Tripped.Should().BeFalse();
    }

    private static Task NeverExits(int processId, CancellationToken cancellationToken)
        => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    /// <summary>Emits a fixed script of values at the requested interval, then idles until cancelled.</summary>
    private sealed class ScriptedSampler : IGatedMetricSampler
    {
        private readonly double[] _values;

        public ScriptedSampler(params double[] values) => _values = values;

        public async Task SampleAsync(
            int processId,
            GatedCaptureMetric metric,
            TimeSpan interval,
            Action<double> onSample,
            CancellationToken cancellationToken)
        {
            foreach (var value in _values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onSample(value);
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
    }
}
