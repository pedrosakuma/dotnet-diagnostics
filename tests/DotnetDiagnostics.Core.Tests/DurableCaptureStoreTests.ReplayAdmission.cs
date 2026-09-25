using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class DurableCaptureStoreTests
{
    [Fact]
    public async Task SequentialReplay_WaitsForSmallQueue_WithoutRetryOrQueueLoss()
    {
        await using var writer = await Store(new CaptureStoreOptions
        {
            QueueRecords = 1, QueueBytes = 256, BatchRecords = 1
        }).CreateAsync(new("sequential replay"), Owner);
        var artifact = writer.AddArtifact("test", "test");
        for (var i = 0; i < 100; i++)
            Assert.True(await writer.AppendAsync(artifact, new(NumericValue: i)));
        writer.SetSourceRejected(0);
        var info = await writer.CompleteAsync();
        var metrics = writer.GetMetrics();
        Assert.True(info.Quality.IsComplete);
        Assert.Equal(100, info.Quality.Offered);
        Assert.Equal(100, info.Quality.Accepted);
        Assert.Equal(100, info.Quality.Persisted);
        Assert.Equal(0, info.Quality.QueueRejected);
        Assert.Equal(0, metrics.WaitingAppends);
        Assert.Equal(0, metrics.WaitingAppendBytes);
        Assert.Equal(0, metrics.QueueBytes);
        AssertConservation(info.Quality);
        using var reader = await Store().OpenAsync(info.CaptureId, Owner);
        Assert.Equal(Enumerable.Range(0, 100).Select(static i => (double?)i),
            reader.Query(new(artifact, PageSize: 1000)).Records.Select(static r => r.Record.NumericValue));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayWaiters_HaveIndependentCountAndByteBounds_AndKeepFifo(bool byteBound)
    {
        var options = new CaptureStoreOptions
        {
            QueueRecords = 1, QueueBytes = 256, MaxBatchAge = TimeSpan.FromSeconds(1),
            MaxPendingAppends = byteBound ? 8 : 3, MaxPendingAppendBytes = byteBound ? 384 : 4096
        };
        await using var writer = await Store(options).CreateAsync(new("bounded replay waiters"), Owner);
        var artifact = writer.AddArtifact("test", "test");
        Assert.True(writer.TryAppend(artifact, new(NumericValue: 0)));
        var pending = Enumerable.Range(1, 3).Select(i =>
            writer.AppendAsync(artifact, new(NumericValue: i)).AsTask()).ToArray();
        Assert.All(pending, static task => Assert.False(task.IsCompleted));
        Assert.Equal(3, writer.GetMetrics().WaitingAppends);
        Assert.Equal(384, writer.GetMetrics().WaitingAppendBytes);
        Assert.False(await writer.AppendAsync(artifact, new(NumericValue: 4)));
        Assert.Equal(3, writer.GetMetrics().WaitingAppends);
        Assert.All(await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10)), static accepted => Assert.True(accepted));
        var info = await writer.CompleteAsync();
        Assert.Equal(5, info.Quality.Offered);
        Assert.Equal(4, info.Quality.Accepted);
        Assert.Equal(4, info.Quality.Persisted);
        Assert.Equal(1, info.Quality.QueueRejected);
        Assert.Equal(0, writer.GetMetrics().WaitingAppendBytes);
        AssertConservation(info.Quality);
        using var reader = await Store().OpenAsync(info.CaptureId, Owner);
        Assert.Equal(Enumerable.Range(0, 4).Select(static i => (double?)i),
            reader.Query(new(artifact)).Records.Select(static r => r.Record.NumericValue));
    }

    [Fact]
    public async Task ReplayCancellation_RejectsEachPendingOfferOnce_AndKeepsOtherWaitersUsable()
    {
        await using var writer = await Store(new CaptureStoreOptions
        {
            QueueRecords = 1, MaxBatchAge = TimeSpan.FromSeconds(1)
        }).CreateAsync(new("cancel replay"), Owner);
        var artifact = writer.AddArtifact("test", "test");
        Assert.True(writer.TryAppend(artifact, new(NumericValue: 0)));
        using var cancellation = new CancellationTokenSource();
        var canceled = writer.AppendAsync(artifact, new(NumericValue: 1), cancellation.Token).AsTask();
        var survivor = writer.AppendAsync(artifact, new(NumericValue: 2)).AsTask();
        Assert.Equal(2, writer.GetMetrics().WaitingAppends);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal(1, writer.GetMetrics().WaitingAppends);
        Assert.Equal(1, writer.GetMetrics().Quality.QueueRejected);
        Assert.True(await survivor.WaitAsync(TimeSpan.FromSeconds(10)));
        var info = await writer.CompleteAsync();
        Assert.Equal(3, info.Quality.Offered);
        Assert.Equal(2, info.Quality.Persisted);
        Assert.Equal(1, info.Quality.QueueRejected);
        Assert.Equal(0, info.Quality.Pending);
        Assert.Equal(0, writer.GetMetrics().WaitingAppends);
        AssertConservation(info.Quality);
    }

    [Fact]
    public async Task PrecancelledReplay_IsNotCountedAsAnOffer()
    {
        await using var writer = await Store().CreateAsync(new("pre-canceled"), Owner);
        var artifact = writer.AddArtifact("test", "test");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await writer.AppendAsync(artifact, new(), cancellation.Token));
        Assert.Equal(0, writer.GetMetrics().Quality.Offered);
        await writer.CompleteAsync();
    }

    [Fact]
    public async Task WaitingReplay_RechecksLifetimeCapAfterOlderOfferAdmission()
    {
        await using var writer = await Store(new CaptureStoreOptions
        {
            QueueRecords = 1, MaxLogicalBytes = 256, MaxBatchAge = TimeSpan.FromSeconds(1)
        }).CreateAsync(new("waiting lifetime cap"), Owner);
        var artifact = writer.AddArtifact("test", "test");
        Assert.True(writer.TryAppend(artifact, new()));
        var first = writer.AppendAsync(artifact, new()).AsTask();
        var second = writer.AppendAsync(artifact, new()).AsTask();
        Assert.Equal(2, writer.GetMetrics().WaitingAppends);
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(await second.WaitAsync(TimeSpan.FromSeconds(10)));
        var info = await writer.CompleteAsync();
        Assert.Equal(3, info.Quality.Offered);
        Assert.Equal(2, info.Quality.Accepted);
        Assert.Equal(2, info.Quality.Persisted);
        Assert.Equal(1, info.Quality.StorageRejected);
        Assert.Equal(0, info.Quality.QueueRejected);
        Assert.Equal(256, writer.GetMetrics().LogicalBytes);
        Assert.Equal(0, writer.GetMetrics().WaitingAppends);
        AssertConservation(info.Quality);
    }

    [Fact]
    public async Task ConcurrentCancellationAndShutdown_ClearMultipleReplayWaitersWithoutDeadlock()
    {
        var writer = await Store(new CaptureStoreOptions { QueueRecords = 1 })
            .CreateAsync(new("racing replay close"), Owner);
        var artifact = writer.AddArtifact("test", "test");
        Assert.True(writer.TryAppend(artifact, new()));
        using var cancellation = new CancellationTokenSource();
        var pending = Enumerable.Range(0, 32).Select(i =>
            writer.AppendAsync(artifact, new(NumericValue: i), cancellation.Token).AsTask()).ToArray();
        var cancel = Task.Run(cancellation.Cancel);
        var close = writer.CompleteAsync();
        var outcomes = await Task.WhenAll(pending.Select(ObserveAsync)).WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.WaitAsync(TimeSpan.FromSeconds(10));
        var info = await close.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(33, info.Quality.Offered);
        Assert.Equal(1 + outcomes.Count(static accepted => accepted), info.Quality.Accepted);
        Assert.Equal(info.Quality.Accepted, info.Quality.Persisted);
        Assert.Equal(outcomes.Count(static accepted => !accepted), info.Quality.QueueRejected);
        Assert.Equal(0, writer.GetMetrics().WaitingAppends);
        Assert.Equal(0, writer.GetMetrics().WaitingAppendBytes);
        Assert.Equal(0, info.Quality.Pending);
        AssertConservation(info.Quality);
        await writer.DisposeAsync();

        static async Task<bool> ObserveAsync(Task<bool> task)
        {
            try { return await task; }
            catch (OperationCanceledException) { return false; }
            catch (CaptureStoreException ex) when (ex.Code == CaptureErrorCode.Closed) { return false; }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_WakesReplayWaitersBeforeWaitingForProducerQuiescence(bool dispose)
    {
        var writer = await Store(new CaptureStoreOptions
        {
            QueueRecords = 1, MaxBatchAge = TimeSpan.FromSeconds(1)
        }).CreateAsync(new("close replay"), Owner);
        var artifact = writer.AddArtifact("test", "test");
        Assert.True(writer.TryAppend(artifact, new(NumericValue: 0)));
        var pending = writer.AppendAsync(artifact, new(NumericValue: 1)).AsTask();
        Assert.Equal(1, writer.GetMetrics().WaitingAppends);
        Task stop = dispose ? writer.DisposeAsync().AsTask() : writer.CompleteAsync();
        Assert.Equal(CaptureErrorCode.Closed,
            (await Assert.ThrowsAsync<CaptureStoreException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)))).Code);
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        var metrics = writer.GetMetrics();
        Assert.Equal(2, metrics.Quality.Offered);
        Assert.Equal(1, metrics.Quality.QueueRejected);
        Assert.Equal(1, metrics.Quality.Persisted);
        Assert.Equal(0, metrics.Quality.Pending);
        Assert.Equal(0, metrics.WaitingAppends);
        Assert.Equal(0, metrics.WaitingAppendBytes);
        AssertConservation(metrics.Quality);
        await writer.DisposeAsync();
    }

    [Fact]
    public async Task ReplayHardBounds_RejectImmediatelyAndCountOnce_WithoutWaitingForImpossibleCapacity()
    {
        await using var writer = await Store(new CaptureStoreOptions
        {
            QueueRecords = 1, QueueBytes = 256, MaxLogicalBytes = 256, BatchRecords = 1
        }).CreateAsync(new("replay hard bounds"), Owner);
        var artifact = writer.AddArtifact("test", "test");
        Assert.False(await writer.AppendAsync(artifact, new(NumericValue: double.NaN)));
        Assert.False(await writer.AppendAsync(artifact, new(Name: new string('x', 100))));
        Assert.True(await writer.AppendAsync(artifact, new()));
        Assert.True(await writer.AppendAsync(artifact, new()));
        Assert.False(await writer.AppendAsync(artifact, new()));
        var info = await writer.CompleteAsync();
        Assert.Equal(5, info.Quality.Offered);
        Assert.Equal(1, info.Quality.RecordRejected);
        Assert.Equal(2, info.Quality.StorageRejected);
        Assert.Equal(0, info.Quality.QueueRejected);
        Assert.Equal(2, info.Quality.Persisted);
        Assert.Equal(256, writer.GetMetrics().LogicalBytes);
        AssertConservation(info.Quality);
    }

    [Fact]
    public async Task ReplayRecordLargerThanEntireQueue_RejectsRatherThanWaitingForever()
    {
        await using var writer = await Store(new CaptureStoreOptions { QueueBytes = 256 })
            .CreateAsync(new("impossible queue fit"), Owner);
        var artifact = writer.AddArtifact("test", "test");
        Assert.False(await writer.AppendAsync(artifact, new(Name: new string('x', 100))));
        var quality = (await writer.CompleteAsync()).Quality;
        Assert.Equal(1, quality.Offered);
        Assert.Equal(1, quality.QueueRejected);
        Assert.Equal(0, quality.Accepted);
        Assert.Equal(0, quality.Pending);
        AssertConservation(quality);
    }

    [Fact]
    public async Task SqliteFull_WakesReplayWaiterWithoutRequiringCompleteFirst()
    {
        var writer = await Store(new CaptureStoreOptions
        {
            QueueRecords = 1, MaxDatabaseBytes = 96 * 1024, MaxBatchAge = TimeSpan.FromSeconds(1)
        }).CreateAsync(new("replay write failure"), Owner);
        var artifact = writer.AddArtifact("test", "test");
        Assert.True(writer.TryAppend(artifact, new(Name: new string('x', 20000))));
        var waiting = writer.AppendAsync(artifact, new()).AsTask();
        Assert.Equal(1, writer.GetMetrics().WaitingAppends);
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(await writer.AppendAsync(artifact, new()));
        Assert.Equal(CaptureErrorCode.CapacityExceeded,
            (await Assert.ThrowsAsync<CaptureStoreException>(() => writer.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10)))).Code);
        var metrics = writer.GetMetrics();
        Assert.Equal(3, metrics.Quality.Offered);
        Assert.Equal(3, metrics.Quality.StorageRejected);
        Assert.Equal(0, metrics.Quality.Persisted);
        Assert.Equal(0, metrics.Quality.Pending);
        Assert.Equal(0, metrics.WaitingAppends);
        Assert.Equal(0, metrics.WaitingAppendBytes);
        AssertConservation(metrics.Quality);
        await Assert.ThrowsAsync<CaptureStoreException>(async () => await writer.DisposeAsync());
    }

    [Fact]
    public async Task WaitingReplay_OwnsItsBoundedFieldCopy()
    {
        await using var writer = await Store(new CaptureStoreOptions
        {
            QueueRecords = 1, MaxBatchAge = TimeSpan.FromSeconds(1)
        }).CreateAsync(new("owned replay"), Owner);
        var artifact = writer.AddArtifact("test", "test");
        Assert.True(writer.TryAppend(artifact, new()));
        var fields = new[] { new CaptureField("message", CaptureFieldKind.Text, StringValue: "original") };
        var waiting = writer.AppendAsync(artifact, new(Fields: fields)).AsTask();
        Assert.False(waiting.IsCompleted);
        fields[0] = new("mutated", CaptureFieldKind.Null);
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        var info = await writer.CompleteAsync();
        using var reader = await Store().OpenAsync(info.CaptureId, Owner);
        Assert.Equal("original", reader.Query(new(artifact)).Records[1].Record.Fields![0].StringValue);
    }
}
