using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Internal;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class DurableCaptureUseCasesTests
{
    [Fact]
    public async Task RealSweepCoordinatorRunsEveryCollectorDespiteChildCapacityExhaustion()
    {
        var service = Service(new() { MaxArtifacts = 2 });
        var collectors = new CapacityCollectors();
        var result = await service.CaptureAsync("bounded sweep", "sweep", Owner, token =>
            SweepUseCase.RunSweep(collectors, collectors, collectors, collectors, collectors,
                new CapacityResolver(), _handles, cancellationToken: token));
        Assert.Equal(5, collectors.Calls);
        Assert.Equal("CapturePersistenceFailed", result.Error!.Kind);
        Assert.NotNull(result.Data!.Counters);
        Assert.NotNull(result.Data.Gc);
        Assert.NotNull(result.Data.Exceptions);
        Assert.NotNull(result.Data.ThreadPool);
        Assert.NotNull(result.Data.Resource);
        Assert.Empty(result.Data.Failures);
        Assert.Equal(CaptureState.Interrupted, result.Capture!.State);
        Assert.Equal(2, result.Capture.Artifacts.Count);
        Assert.NotNull(service.LookupBinding(result.Data.Handles["counters"]!)!.Artifact);
        foreach (var kind in new[] { "gc", "exceptions", "threadpool" })
        {
            var handle = result.Data.Handles[kind]!;
            Assert.NotNull(_handles.TryGetWithKind(handle));
            Assert.Null(service.LookupBinding(handle)!.Artifact);
            await Assert.ThrowsAsync<CaptureStoreException>(() => service.AuthorizeHandleAsync(handle, Owner));
        }
    }

    [Fact]
    public async Task RealCorrelatedCoordinatorRetainsBothSideResultsAndFailClosesRejectedDelayedHandle()
    {
        var service = Service(new() { MaxArtifacts = 2 });
        var collectors = new CapacityCollectors();
        var result = await service.CaptureAsync("bounded pair", "gc-activities", Owner, token =>
            GcActivitiesCaptureUseCase.CollectAsync(collectors, collectors, new CapacityResolver(), _handles,
                new(), cancellationToken: token));
        Assert.Equal(2, collectors.Calls);
        Assert.Equal("CapturePersistenceFailed", result.Error!.Kind);
        Assert.NotNull(result.Data!.Gc.Capture);
        Assert.NotNull(result.Data.Activities.Capture);
        Assert.NotNull(result.Data.Gc.Handle);
        Assert.NotNull(result.Data.Activities.Handle);
        Assert.NotNull(service.LookupBinding(result.Data.Gc.Handle!.Id)!.Artifact);
        var rejected = result.Data.Activities.Handle!.Id;
        Assert.NotNull(_handles.TryGetWithKind(rejected));
        Assert.Null(service.LookupBinding(rejected)!.Artifact);
        await Assert.ThrowsAsync<CaptureStoreException>(() => service.AuthorizeHandleAsync(rejected, Owner));
        Assert.Equal(CaptureState.Interrupted, result.Capture!.State);
        Assert.Equal(2, result.Capture.Artifacts.Count);
    }

    private sealed class CapacityResolver : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessContextResolution(
                new(Environment.ProcessId, RuntimeFlavor.CoreClr, true, true, true), null));
    }

    private sealed class CapacityCollectors : ICounterCollector, IGcCollector, IExceptionCollector,
        IThreadPoolCollector, IActivityCollector, IProcessResourcesCollector
    {
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
        private readonly DateTimeOffset _lifetime = ProcessLifetime.TryReadStart(Environment.ProcessId)!.Value;
        private int _calls;
        internal int Calls => _calls;
        private Task<T> Complete<T>(T value)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(value);
        }

        Task<CounterSnapshot> ICounterCollector.CollectAsync(int processId, TimeSpan duration,
            IReadOnlyList<string>? providers, IReadOnlyList<string>? meters, int intervalSeconds,
            int maxInstrumentTimeSeries, CancellationToken cancellationToken)
            => Complete(Snapshot with { ProcessId = processId });

        Task<GcSummary> IGcCollector.CollectAsync(int processId, TimeSpan duration, int maxEvents, CancellationToken cancellationToken)
            => Complete(new GcSummary(processId, _start, duration, 0, TimeSpan.Zero, TimeSpan.Zero, [], [],
                Suspension: new("no-detected-loss", _start, _start + duration, _lifetime, "normal-stop",
                    TimeSpan.Zero, TimeSpan.Zero, 0, 0, [], new Dictionary<string, long>())));

        Task<ActivityCapture> IActivityCollector.CollectAsync(int processId, TimeSpan duration,
            IReadOnlyList<string>? sources, int maxActivities, CancellationToken cancellationToken)
            => Complete(new ActivityCapture(processId, ["test"], _start, duration, 0, 0, [], [], [],
                new(null, 200, 0, 0, 0, 0, 0), _lifetime)
                { Observation = new(duration, "normal-stop", 0) });

        Task<ExceptionSnapshot> IExceptionCollector.CollectAsync(int processId, TimeSpan duration,
            int maxRecent, CancellationToken cancellationToken)
            => Complete(((ExceptionSnapshot)CaptureArtifactCodecTests.Snapshots()
                .Single(row => (string)row[0] == "exception-snapshot")[1]) with { ProcessId = processId });

        Task<ThreadPoolEventSnapshot> IThreadPoolCollector.CollectAsync(int processId, TimeSpan duration,
            CancellationToken cancellationToken)
            => Complete(new ThreadPoolEventSnapshot(processId, _start, duration, [], [], [], [], null, 0, 0, []));

        Task<ProcessResources> IProcessResourcesCollector.CollectAsync(int processId, int durationSeconds,
            int sampleEverySeconds, CancellationToken cancellationToken)
            => Complete(new ProcessResources(processId, _start, 1, null, null, null, null, [], null));
    }
}
