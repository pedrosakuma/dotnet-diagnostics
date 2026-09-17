using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Internal;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.UseCases;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class GcActivitiesCaptureUseCaseTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow;
    private static readonly DateTimeOffset Lifetime = ProcessLifetime.TryReadStart(Environment.ProcessId)!.Value;

    [Fact]
    public async Task StartsBothBeforeEitherCompletes_ResolvesOnce_StoresAcquiredArtifactsAndActualIntersection()
    {
        var enteredGc = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredActivity = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new Resolver();
        var gcValue = Gc();
        var activityValue = Activities();
        var store = new MemoryDiagnosticHandleStore();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = GcActivitiesCaptureUseCase.CollectAsync(
            new GcCollector(async ct => { enteredGc.SetResult(); await release.Task.WaitAsync(ct); return gcValue; }),
            new ActivitiesCollector(async ct => { enteredActivity.SetResult(); await release.Task.WaitAsync(ct); return activityValue; }),
            resolver, store, new(), cancellationToken: stop.Token);
        try
        {
            await Task.WhenAll(enteredGc.Task, enteredActivity.Task).WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally { release.TrySetResult(); await task.WaitAsync(TimeSpan.FromSeconds(6)); }
        var result = (await task).Data!;
        resolver.Calls.Should().Be(1);
        result.Status.Should().Be("captured");
        result.IntersectionStart.Should().Be(Start.AddSeconds(1));
        result.IntersectionEnd.Should().Be(Start.AddSeconds(10));
        result.StartupSkewMs.Should().Be(1000);
        result.Overlay.Should().NotBeNull();
        store.TryGet<GcSummary>(result.Gc.Handle!.Id).Should().BeSameAs(gcValue);
        store.TryGet<ActivityCapture>(result.Activities.Handle!.Id).Should().BeSameAs(activityValue);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SynchronousFailureStillObservesBothSidesAndPreservesSuccessfulArtifact(bool failGc, bool failActivity)
    {
        var activityCalls = 0;
        var store = new MemoryDiagnosticHandleStore();
        var result = await GcActivitiesCaptureUseCase.CollectAsync(
            new GcCollector(_ => failGc ? throw new InvalidOperationException("GC failed before startup") : Task.FromResult(Gc())),
            new ActivitiesCollector(_ => { activityCalls++; return failActivity ? throw new InvalidOperationException("activities failed") : Task.FromResult(Activities()); }),
            new Resolver(), store, new());
        activityCalls.Should().Be(1);
        result.Data!.Status.Should().Be(failGc && failActivity ? "failed" : "partial");
        result.Data.Overlay.Should().BeNull();
        result.Data.OverlayUnavailableReason.Should().NotBeNullOrEmpty();
        if (failGc) result.Data.Gc.UnavailableReason.Should().Contain("GC failed");
        else result.Data.Gc.Handle.Should().NotBeNull();
        if (failActivity) result.Data.Activities.UnavailableReason.Should().Contain("activities failed");
        else result.Data.Activities.Handle.Should().NotBeNull();
    }

    [Fact]
    public async Task CancellationAwaitsBothCollectorCleanups()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var cleanups = 0;
        using var cancel = new CancellationTokenSource();
        async Task Wait(CancellationToken ct)
        {
            if (Interlocked.Increment(ref count) == 2) entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { Interlocked.Increment(ref cleanups); }
        }
        var store = new MemoryDiagnosticHandleStore();
        var task = GcActivitiesCaptureUseCase.CollectAsync(
            new GcCollector(async ct => { await Wait(ct); return Gc(); }),
            new ActivitiesCollector(async ct => { await Wait(ct); return Activities(); }),
            new Resolver(), store, new(), cancellationToken: cancel.Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
        finally { cancel.Cancel(); }
        var result = await task.WaitAsync(TimeSpan.FromSeconds(2));
        cleanups.Should().Be(2);
        result.Cancelled.Should().BeTrue();
        result.Data!.Status.Should().Be("failed");
        result.Data.Gc.UnavailableReason.Should().Contain("cancelled");
        result.Data.Activities.UnavailableReason.Should().Contain("cancelled");
    }

    [Theory]
    [InlineData("nonoverlap")]
    [InlineData("unknown-lifetime")]
    [InlineData("mismatched-lifetime")]
    [InlineData("mismatched-pid")]
    [InlineData("legacy-gc")]
    [InlineData("legacy-activity")]
    [InlineData("activity-loss")]
    [InlineData("early-exit")]
    public async Task UnreliableEvidenceNeverProducesPlausibleCompleteOverlay(string scenario)
    {
        var gc = Gc();
        var activities = Activities();
        if (scenario == "nonoverlap") activities = activities with { StartedAt = Start.AddSeconds(20) };
        if (scenario == "unknown-lifetime") activities = activities with { ProcessStartedAt = null };
        if (scenario == "mismatched-lifetime") activities = activities with { ProcessStartedAt = Lifetime.AddSeconds(-1) };
        if (scenario == "mismatched-pid") activities = activities with { ProcessId = 1 };
        if (scenario == "legacy-gc") gc = gc with { Suspension = null };
        if (scenario == "legacy-activity") activities = activities with { Observation = null };
        if (scenario == "activity-loss") activities = activities with { Observation = new(TimeSpan.FromSeconds(10), "normal-stop", 3) };
        if (scenario == "early-exit") activities = activities with { Observation = new(TimeSpan.FromSeconds(10), "early-exit", 0) };
        var store = new MemoryDiagnosticHandleStore();
        var result = await GcActivitiesCaptureUseCase.CollectAsync(new GcCollector(_ => Task.FromResult(gc)),
            new ActivitiesCollector(_ => Task.FromResult(activities)), new Resolver(), store, new());
        result.Data!.Status.Should().Be("partial");
        result.Data.Overlay.Should().BeNull();
        result.Data.OverlayUnavailableReason.Should().NotBeNullOrEmpty();
        if (scenario == "nonoverlap") result.Data.IntersectionStart.Should().BeNull();
    }

    private static GcSummary Gc() => new(Environment.ProcessId, Start, TimeSpan.FromSeconds(10), 0,
        TimeSpan.Zero, TimeSpan.Zero, [], [], Suspension: new("no-detected-loss", Start, Start.AddSeconds(10),
            Lifetime, "normal-stop", TimeSpan.Zero, TimeSpan.Zero, 0, 0, [], new Dictionary<string, long>()));

    [Fact]
    public async Task CapacityEvictionIsNotReportedAsAnAvailablePair()
    {
        var store = new MemoryDiagnosticHandleStore(maxEntries: 1);
        var result = await GcActivitiesCaptureUseCase.CollectAsync(
            new GcCollector(_ => Task.FromResult(Gc())), new ActivitiesCollector(_ => Task.FromResult(Activities())),
            new Resolver(), store, new());
        result.Data!.Status.Should().Be("partial");
        result.Data.Overlay.Should().BeNull();
        result.Data.OverlayUnavailableReason.Should().Contain("unknown or expired");
        store.TryGet<ActivityCapture>(result.Data.Activities.Handle!.Id).Should().NotBeNull();
    }

    [Fact]
    public async Task ResolvedLifetimeMismatchCannotLeaveQueryableCompatibleLookingPair()
    {
        var gc = Gc();
        gc = gc with { Suspension = gc.Suspension! with { ProcessStartedAt = Lifetime.AddMinutes(-1) } };
        var activities = Activities() with { ProcessStartedAt = Lifetime.AddMinutes(-1) };
        var store = new MemoryDiagnosticHandleStore();
        var result = await GcActivitiesCaptureUseCase.CollectAsync(
            new GcCollector(_ => Task.FromResult(gc)), new ActivitiesCollector(_ => Task.FromResult(activities)),
            new Resolver(), store, new());
        result.Data!.Status.Should().Be("partial");
        result.Data.Gc.Handle.Should().BeNull();
        result.Data.Activities.Handle.Should().BeNull();
        result.Data.Overlay.Should().BeNull();
        result.Data.OverlayUnavailableReason.Should().Contain("resolved target lifetime");
    }

    [Fact]
    public async Task SecondRegistrationFailureKeepsFirstArtifactAndDisclosesMissingHandle()
    {
        var store = new FailingActivityStore();
        var gc = Gc();
        var result = await GcActivitiesCaptureUseCase.CollectAsync(
            new GcCollector(_ => Task.FromResult(gc)), new ActivitiesCollector(_ => Task.FromResult(Activities())),
            new Resolver(), store, new());
        result.Data!.Status.Should().Be("partial");
        result.Data.Gc.Handle!.Origin.Should().Be(HandleOrigin.Live);
        store.TryGet<GcSummary>(result.Data.Gc.Handle.Id).Should().BeSameAs(gc);
        result.Data.Activities.Handle.Should().BeNull();
        result.Data.Activities.Capture.Should().NotBeNull();
        result.Data.Activities.UnavailableReason.Should().Contain("registration failed");
        result.Data.Overlay.Should().BeNull();
        store.InvalidateForProcess(Environment.ProcessId).Should().Be(0, "completed artifacts survive target exit");
    }

    private sealed class FailingActivityStore : IDiagnosticHandleStore
    {
        private readonly MemoryDiagnosticHandleStore _inner = new();
        public DiagnosticHandle Register(int processId, string kind, object artifact, TimeSpan ttl,
            bool evictWhenProcessExits = true, HandleOrigin? origin = null)
            => kind == DotnetDiagnostics.Core.Collection.CollectionHandleKinds.Activities
                ? throw new InvalidOperationException("registration failed")
                : _inner.Register(processId, kind, artifact, ttl, evictWhenProcessExits, origin);
        public T? TryGet<T>(string handle) where T : class => _inner.TryGet<T>(handle);
        public HandleLookup? TryGetWithKind(string handle) => _inner.TryGetWithKind(handle);
        public bool Invalidate(string handle) => _inner.Invalidate(handle);
        public int InvalidateForProcess(int processId) => _inner.InvalidateForProcess(processId);
    }

    private static ActivityCapture Activities() => new(Environment.ProcessId, ["test"], Start.AddSeconds(1),
        TimeSpan.FromSeconds(10), 0, 0, [], [], [], new(null, 200, 0, 0, 0, 0, 0), Lifetime)
        { Observation = new(TimeSpan.FromSeconds(10), "normal-stop", 0) };

    private sealed class Resolver : IProcessContextResolver
    {
        public int Calls { get; private set; }
        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ProcessContextResolution(new(Environment.ProcessId, RuntimeFlavor.CoreClr, true, true, true), null));
        }
    }

    private sealed class GcCollector(Func<CancellationToken, Task<GcSummary>> collect) : IGcCollector
    {
        public Task<GcSummary> CollectAsync(int processId, TimeSpan duration, int maxEvents = 200, CancellationToken cancellationToken = default)
            => collect(cancellationToken);
    }

    private sealed class ActivitiesCollector(Func<CancellationToken, Task<ActivityCapture>> collect) : IActivityCollector
    {
        public Task<ActivityCapture> CollectAsync(int processId, TimeSpan duration, IReadOnlyList<string>? sources = null,
            int maxActivities = 200, CancellationToken cancellationToken = default) => collect(cancellationToken);
    }
}
