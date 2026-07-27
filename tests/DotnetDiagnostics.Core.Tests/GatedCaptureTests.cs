using System.Collections.Concurrent;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.GatedCapture;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.UseCases;
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

    [Fact]
    public async Task WatchAndCapture_ThrowsWhenMetricSessionFailsWithoutSamples()
    {
        var collector = new ThresholdGatedCaptureCollector(new ThrowingSampler(), NeverExits);
        var predicate = new TriggerPredicate(GatedCaptureMetric.Cpu, TriggerOperator.GreaterThan, 85);

        var act = async () => await collector.WatchAndCaptureAsync(
            1234, predicate, GatedCaptureKind.CpuSample,
            window: TimeSpan.FromSeconds(30),
            maxCaptures: 1,
            sampleInterval: TimeSpan.FromMilliseconds(5),
            captureCallback: (_, _) => Task.FromResult(new GatedCaptureOutcome("nope")));

        var ex = await act.Should().ThrowAsync<GatedCaptureSamplerException>();
        ex.Which.ProcessId.Should().Be(1234);
        ex.Which.Metric.Should().Be(GatedCaptureMetric.Cpu);
    }

    [Theory]
    [InlineData("cpu-sample")]
    [InlineData("thread-snapshot")]
    public async Task GatedUseCase_StampsCollectEventsAsProducingTool(string captureKind)
    {
        var store = new MemoryDiagnosticHandleStore();

        var result = await GatedCaptureUseCases.WatchAndCapture(
            new ImmediateCaptureCollector(),
            new FixedProcessContextResolver(),
            store,
            new FixedCpuSampler(),
            new FixedThreadInspector(),
            dumpInspector: null!,
            dumper: null!,
            triggerWhen: "cpu>1",
            captureKind,
            windowSeconds: 1,
            maxCaptures: 1,
            sampleIntervalSeconds: 1,
            processId: 1234);

        result.Error.Should().BeNull();
        var handleId = result.Data!.Captures.Should().ContainSingle().Which.Handle;
        handleId.Should().NotBeNullOrWhiteSpace();
        store.TryGetWithKind(handleId!)!.Value.Handle.ProducingTool.Should().Be("collect_events");
    }

    private static Task NeverExits(int processId, CancellationToken cancellationToken)
        => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    private sealed class ImmediateCaptureCollector : IThresholdGatedCaptureCollector
    {
        public async Task<GatedCaptureResult> WatchAndCaptureAsync(
            int processId,
            TriggerPredicate predicate,
            GatedCaptureKind captureKind,
            TimeSpan window,
            int maxCaptures,
            TimeSpan sampleInterval,
            Func<GatedCaptureTrigger, CancellationToken, Task<GatedCaptureOutcome>> captureCallback,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UnixEpoch;
            var outcome = await captureCallback(
                new GatedCaptureTrigger(processId, 2, now, 0),
                cancellationToken);
            return new GatedCaptureResult(
                processId,
                "cpu",
                "cpu-usage",
                predicate.ToString(),
                GatedCaptureKinds.Token(captureKind),
                now,
                TimeSpan.Zero,
                window,
                maxCaptures,
                1,
                2,
                2,
                2,
                true,
                false,
                false,
                [
                    new GatedCaptureRecord(
                        0,
                        2,
                        now,
                        GatedCaptureKinds.Token(captureKind),
                        outcome.Summary,
                        outcome.Handle,
                        outcome.HandleExpiresAt,
                        outcome.ArtifactPath,
                        outcome.Error),
                ],
                []);
        }
    }

    private sealed class FixedProcessContextResolver : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(
            int? requestedProcessId,
            CancellationToken cancellationToken)
            => Task.FromResult(new ProcessContextResolution(
                new ProcessContext(
                    requestedProcessId ?? 1234,
                    RuntimeFlavor.CoreClr,
                    CanSampleCpu: true,
                    CanCollectGcDump: true,
                    AutoResolved: false,
                    RuntimeVersion: "10.0.0",
                    BindingSource: "explicit"),
                null));
    }

    private sealed class FixedCpuSampler : ICpuSampler
    {
        public Task<CpuSampleResult> SampleAsync(
            int processId,
            TimeSpan duration,
            int topN = 25,
            SourceResolutionOptions? sourceResolution = null,
            MethodInstantiationResolutionOptions? methodInstantiationResolution = null,
            NativeAotSymbolResolutionOptions? nativeAotSymbols = null,
            bool exportTrace = false,
            CancellationToken cancellationToken = default)
        {
            var startedAt = DateTimeOffset.UnixEpoch;
            var root = new CallTreeNode(new SampledFrame(string.Empty, "<root>"), 0, 0, []);
            var artifact = new CpuSampleTraceArtifact(processId, startedAt, duration, 0, root);
            return Task.FromResult(new CpuSampleResult(
                new CpuSample(processId, startedAt, duration, 0, []),
                artifact));
        }
    }

    private sealed class FixedThreadInspector : IThreadSnapshotInspector
    {
        public Task<ThreadSnapshotArtifact> InspectLiveAsync(
            int processId,
            ThreadSnapshotOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ThreadSnapshotArtifact(
                ThreadSnapshotOrigin.Live,
                processId,
                DateTimeOffset.UnixEpoch,
                TimeSpan.Zero,
                ".NET",
                "10.0.0",
                [],
                []));

        public Task<ThreadSnapshotArtifact> InspectDumpAsync(
            string dumpFilePath,
            ThreadSnapshotOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>Fails immediately, as if the EventPipe session could not be started.</summary>
    private sealed class ThrowingSampler : IGatedMetricSampler
    {
        public Task SampleAsync(
            int processId,
            GatedCaptureMetric metric,
            TimeSpan interval,
            Action<double> onSample,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("diagnostic socket unavailable");
    }

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
