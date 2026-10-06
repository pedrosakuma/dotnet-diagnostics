using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Counters;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class DurableCaptureUseCasesTests
{
    [Fact]
    public async Task ComposedSessionNamedCapturesKeepSeparateRoutesAndTypedResults()
    {
        var service = Service();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var envelopes = new List<DiagnosticResult<CounterSnapshot>>();
        var snapshots = new List<CounterSnapshot>();
        var original = DiagnosticResult.Ok(Snapshot, "original envelope");

        var result = await service.CaptureAsync("session", "batch", Owner, async token =>
        {
            await using var session = new ComposedDiagnosticSession(42);
            using var rawSubscription = session.Attach<DiagnosticSessionCaptureResult<CounterSnapshot>>((item, _) =>
            {
                snapshots.Add(item.Result);
                return ValueTask.CompletedTask;
            });
            using var envelopeSubscription = session.Attach<DiagnosticSessionCaptureResult<DiagnosticResult<CounterSnapshot>>>((item, _) =>
            {
                envelopes.Add(item.Result);
                return ValueTask.CompletedTask;
            });
            session.AddCapture("counters", "raw", async _ =>
            {
                var sink = CaptureRecordingContext.Current!;
                firstStarted.SetResult();
                await secondStarted.Task;
                Assert.Same(sink, CaptureRecordingContext.Current);
                sink.TryAppend(new("raw", At, null, null, []));
                sink.ReportSourceLoss("EventPipe", 2);
                return Snapshot;
            });
            session.AddDiagnosticCapture("counters", "envelope", async _ =>
            {
                secondStarted.SetResult();
                await firstStarted.Task;
                var sink = CaptureRecordingContext.Current!;
                sink.TryAppend(new("envelope", At, null, null, []));
                sink.ReportSourceLoss("EventPipe", 3);
                return original;
            });
            await session.StartAsync(token);
            var completion = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(DiagnosticSessionStatus.Completed, completion.Status);
            return DiagnosticResult.Ok(completion, "session finished");
        });

        Assert.False(result.IsError, result.Error?.Message);
        Assert.Same(original, Assert.Single(envelopes));
        Assert.Single(snapshots).Should().BeEquivalentTo(Snapshot);
        Assert.Equal(CaptureState.Sealed, result.Capture!.State);
        Assert.Equal(5, result.Capture.Quality.SourceRejected);
        var parent = result.Capture.Artifacts.Single(a => a.Name == "session");
        var composition = (await service.OpenAsync(result.Capture.CaptureId, parent.ArtifactId, Owner)).Composition!;
        Assert.Equal(2, composition.Children.Count);
        foreach (var child in composition.Children)
        {
            Assert.True(child.SnapshotAvailable);
            Assert.Equal(child.Name == "raw" ? 2 : 3, child.SourceRejected);
            var page = await service.QueryRecordsAsync(result.Capture.CaptureId, new(child.ArtifactId), Owner);
            Assert.Equal(child.Name, Assert.Single(page.Records).Record.Category);
            var opened = await service.OpenAsync(result.Capture.CaptureId, child.ArtifactId, Owner);
            _handles.TryGet<CounterSnapshot>(opened.Handle.Id).Should().BeEquivalentTo(Snapshot);
        }
        Assert.Null(CaptureRecordingContext.Current);
    }

    [Fact]
    public async Task ComposedSessionDeliveryDropsDoNotDropProducerEvidenceOrTerminalSnapshot()
    {
        var service = Service();
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await service.CaptureAsync("session", "batch", Owner, async token =>
        {
            await using var session = new ComposedDiagnosticSession(42, eventCapacity: 1);
            using var subscription = session.Attach<DiagnosticSessionObservation<int>>(async (_, _) =>
            {
                handlerEntered.TrySetResult();
                await releaseHandler.Task;
            });
            session.AddStreamingCapture<int, CounterSnapshot>("counters", "stream", async (publish, _) =>
            {
                var sink = CaptureRecordingContext.Current!;
                sink.TryAppend(new("stream", At, null, "0", []));
                publish(0);
                await handlerEntered.Task;
                for (var i = 1; i < 21; i++)
                {
                    sink.TryAppend(new("stream", At, null, null, []));
                    publish(i);
                }
                sink.ReportSourceLoss("EventPipe", 0);
                producerFinished.SetResult();
                return Snapshot;
            });
            await session.StartAsync(token);
            try
            {
                await producerFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                releaseHandler.TrySetResult();
            }
            var completion = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(completion.DroppedObservations > 0);
            return DiagnosticResult.Ok(completion, "session finished");
        });

        Assert.False(result.IsError, result.Error?.Message);
        Assert.Equal(21, result.Capture!.Quality.Offered);
        Assert.Equal(21, result.Capture.Quality.Persisted);
        Assert.Equal(0, result.Capture.Quality.SourceRejected);
        var child = result.Capture.Artifacts.Single(a => a.Name == "stream");
        var opened = await service.OpenAsync(result.Capture.CaptureId, child.ArtifactId, Owner);
        _handles.TryGet<CounterSnapshot>(opened.Handle.Id).Should().BeEquivalentTo(Snapshot);
    }

    [Fact]
    public async Task ComposedSessionStructuredFailurePreservesEnvelopeAndInterruptsCapture()
    {
        var service = Service();
        var original = DiagnosticResult.Fail<CounterSnapshot>("partial",
            new("TargetExited", "original failure")) with { Data = Snapshot, Cancelled = true };
        DiagnosticResult<CounterSnapshot>? delivered = null;
        var result = await service.CaptureAsync("session", "batch", Owner, async token =>
        {
            await using var session = new ComposedDiagnosticSession(42);
            using var subscription = session.Attach<DiagnosticSessionCaptureResult<DiagnosticResult<CounterSnapshot>>>((item, _) =>
            {
                delivered = item.Result;
                return ValueTask.CompletedTask;
            });
            session.AddDiagnosticCapture("counters", "partial", _ => Task.FromResult(original));
            await session.StartAsync(token);
            return DiagnosticResult.Ok(await session.Completion, "session drained");
        });

        Assert.Same(original, delivered);
        Assert.Equal("CaptureChildIncomplete", result.Error!.Kind);
        Assert.Equal(CaptureState.Interrupted, result.Capture!.State);
        var recovered = await service.RecoverAsync(result.Capture.CaptureId, Owner);
        var parent = recovered.Artifacts.Single(a => a.Name == "session");
        var composition = (await service.OpenAsync(recovered.CaptureId, parent.ArtifactId, Owner)).Composition!;
        var child = Assert.Single(composition.Children);
        Assert.Equal(original.Error, child.Error);
        Assert.True(child.Cancelled);
        Assert.True(child.SnapshotAvailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComposedSessionSourceFailureOrCancellationIsRecordedInItsChild(bool cancel)
    {
        var service = Service();
        var result = await service.CaptureAsync("session", "batch", Owner, async token =>
        {
            await using var session = new ComposedDiagnosticSession(42);
            using var subscription = session.Attach<DiagnosticSessionCaptureResult<CounterSnapshot>>((_, _) =>
                ValueTask.CompletedTask);
            session.AddCapture<CounterSnapshot>("counters", "interrupted", async cancellationToken =>
            {
                await Task.Yield();
                if (!cancel) throw new InvalidOperationException("producer failed");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Snapshot;
            });
            await session.StartAsync(token);
            var completion = cancel
                ? await session.StopAsync()
                : await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(cancel ? DiagnosticSessionStatus.Stopped : DiagnosticSessionStatus.Failed, completion.Status);
            return DiagnosticResult.Ok(completion, "session ended");
        });

        Assert.Equal("CaptureChildIncomplete", result.Error!.Kind);
        Assert.Equal(CaptureState.Interrupted, result.Capture!.State);
        var recovered = await service.RecoverAsync(result.Capture.CaptureId, Owner);
        var parent = recovered.Artifacts.Single(a => a.Name == "session");
        var composition = (await service.OpenAsync(recovered.CaptureId, parent.ArtifactId, Owner)).Composition!;
        var child = Assert.Single(composition.Children);
        Assert.Equal(cancel, child.Cancelled);
        Assert.Equal(cancel ? null : "ChildCollectionFailed", child.Error?.Kind);
        Assert.False(child.SnapshotAvailable);
        Assert.Null(CaptureRecordingContext.Current);
    }
}
