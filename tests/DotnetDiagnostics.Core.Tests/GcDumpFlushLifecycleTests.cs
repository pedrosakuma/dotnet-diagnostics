using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Dump;
using FluentAssertions;
using System.IO.Pipes;
using System.Net.Sockets;

namespace DotnetDiagnostics.Core.Tests;

public sealed class GcDumpFlushLifecycleTests
{
    [Fact(Timeout = 15_000)]
    public async Task NativePipeRead_IsCancelledAndQuiescentBeforeOwnedPhysicalClose()
    {
        var name = $"gcdump940-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(
            name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
        using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(server.WaitForConnectionAsync(connectionTimeout.Token), client.ConnectAsync(connectionTimeout.Token));
        using var stream = new GatedReadStream(client);
        var calls = 0;

        var result = await GcDumpHeapSnapshotCollector.RunFlushAsync(
            stream,
            (input, _) => input.Read(new byte[1], 0, 1).Should().Be(0),
            _ => throw new InvalidOperationException("zero-budget stop must not send IPC"),
            stream.Dispose,
            () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    stream.ReadEntered.Task.GetAwaiter().GetResult();
                }
                return TimeSpan.Zero;
            },
            _ => { },
            CancellationToken.None,
            new QuiescenceClock());

        result.TimedOut.Should().BeTrue();
        result.Quiescence.Should().Be(GcDumpFlushQuiescence.Completed);
        stream.ReadActiveAtDispose.Should().BeFalse();
        Console.WriteLine($"Native transport: {Environment.OSVersion}; pending PipeStream read quiesced before physical close.");
    }

    [Theory(Timeout = 15_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonCooperativeRead_HasBoundedCleanupAndNoTrace(bool cancel)
    {
        using var stream = new GatedReadStream(ignoreCancellation: true);
        using var cancellation = new CancellationTokenSource();
        var clock = new QuiescenceClock();
        GcDumpFlushResult? flushResult = null;
        var parserExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var root = Path.Combine(Path.GetTempPath(), $"gcdump-940-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var collector = new GcDumpHeapSnapshotCollector(new ArtifactRoot(root), async (_, ct) =>
            {
                flushResult = await GcDumpHeapSnapshotCollector.RunFlushAsync(
                    stream,
                    (input, _) =>
                    {
                        try
                        {
                            input.Read(new byte[1], 0, 1).Should().Be(0);
                        }
                        finally
                        {
                            parserExited.TrySetResult();
                        }
                    },
                    _ => throw new InvalidOperationException("zero budget"),
                    stream.Dispose,
                    () =>
                    {
                        if (Interlocked.Increment(ref calls) == 1)
                        {
                            stream.ReadEntered.Task.GetAwaiter().GetResult();
                        }
                        return TimeSpan.Zero;
                    },
                    _ => { },
                    ct,
                    clock);
                return flushResult.TimedOut;
            });

            var collection = collector.CollectAsync(42, new GcDumpOptions(ExportTrace: true), cancellation.Token);
            await clock.TimerCreated.Task;
            stream.Disposed.Should().BeFalse();
            collection.IsCompleted.Should().BeFalse();
            if (cancel)
            {
                cancellation.Cancel();
            }
            clock.Expire();
            if (cancel)
            {
                Func<Task> observe = () => collection.WaitAsync(TimeSpan.FromSeconds(5));
                (await observe.Should().ThrowAsync<OperationCanceledException>())
                    .Which.CancellationToken.Should().Be(cancellation.Token);
            }
            else
            {
                var snapshot = await collection.WaitAsync(TimeSpan.FromSeconds(5));
                flushResult!.Quiescence.Should().Be(GcDumpFlushQuiescence.BudgetExpired);
                snapshot.GcDumpStatus!.TimedOut.Should().BeTrue();
                snapshot.GcDumpStatus.TraceExportCompleted.Should().BeFalse();
                snapshot.TracePath.Should().BeNull();
            }
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Should().BeEmpty();
            await parserExited.Task.WaitAsync(TimeSpan.FromSeconds(2));
            stream.ReadActiveAtDispose.Should().BeTrue("the explicit cleanup timeout permits physical close only as a last resort");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory(Timeout = 15_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledFlush_NeverCreatesAnyTrace(bool cancel)
    {
        using var stream = new GatedReadStream();
        using var cancellation = new CancellationTokenSource();
        var root = Path.Combine(Path.GetTempPath(), $"gcdump-940-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var collector = new GcDumpHeapSnapshotCollector(new ArtifactRoot(root), async (_, ct) =>
                (await GcDumpHeapSnapshotCollector.RunFlushAsync(
                    stream,
                    (input, _) =>
                    {
                        if (cancel)
                        {
                            cancellation.Cancel();
                            input.Read(new byte[1], 0, 1).Should().Be(0);
                        }
                        else
                        {
                            throw new ObjectDisposedException("unrelated parser");
                        }
                    },
                    _ => throw new IOException("stop failed"),
                    stream.Dispose,
                    () => TimeSpan.FromSeconds(5),
                    _ => { },
                    ct,
                    new QuiescenceClock())).TimedOut);
            var act = () => collector.CollectAsync(42, new GcDumpOptions(ExportTrace: true), cancellation.Token);
            if (cancel)
            {
                await act.Should().ThrowAsync<OperationCanceledException>();
            }
            else
            {
                await act.Should().ThrowAsync<ObjectDisposedException>();
            }
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory(Timeout = 15_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyExpiredBudget_CancelsOwnedReadBeforeClose(bool firstEvent)
    {
        using var stream = new GatedReadStream();
        var calls = 0;
        var result = await GcDumpHeapSnapshotCollector.RunFlushAsync(
            stream,
            (input, onEvent) =>
            {
                if (firstEvent)
                {
                    onEvent();
                }
                input.Read(new byte[1], 0, 1).Should().Be(0);
            },
            _ => throw new InvalidOperationException("zero-budget stop must not send IPC"),
            stream.Dispose,
            () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    stream.ReadEntered.Task.GetAwaiter().GetResult();
                }
                return TimeSpan.Zero;
            },
            _ => { },
            CancellationToken.None,
            new QuiescenceClock());

        result.TimedOut.Should().BeTrue();
        result.Quiescence.Should().Be(GcDumpFlushQuiescence.Completed);
        stream.ReadActiveAtDispose.Should().BeFalse();
    }

    [Theory(Timeout = 15_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DelayedCooperativeRead_ClosesOnlyAfterCompletionOrDeadline(bool expireDeadline, bool firstEvent)
    {
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stream = new GatedReadStream(releaseCancelledRead: releaseRead.Task);
        var clock = new QuiescenceClock();
        var calls = 0;
        var flush = GcDumpHeapSnapshotCollector.RunFlushAsync(
            stream,
            (input, onEvent) =>
            {
                if (firstEvent)
                {
                    onEvent();
                }
                input.Read(new byte[1], 0, 1).Should().Be(0);
            },
            _ => throw new InvalidOperationException("zero-budget stop must not send IPC"),
            stream.Dispose,
            () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    stream.ReadEntered.Task.GetAwaiter().GetResult();
                }
                return TimeSpan.Zero;
            },
            _ => { },
            CancellationToken.None,
            clock);
        try
        {
            await stream.CancellationObserved.Task;
            await clock.TimerCreated.Task;
            stream.Disposed.Should().BeFalse("requesting cancellation is not proof that the read has exited");
            flush.IsCompleted.Should().BeFalse();

            if (expireDeadline)
            {
                clock.Expire();
            }
            else
            {
                releaseRead.SetResult();
            }

            var result = await flush;
            result.TimedOut.Should().BeTrue();
            result.Quiescence.Should().Be(expireDeadline
                ? GcDumpFlushQuiescence.BudgetExpired
                : GcDumpFlushQuiescence.Completed);
            stream.ReadActiveAtDispose.Should().Be(expireDeadline);
            stream.ReadExited.Task.IsCompleted.Should().Be(!expireDeadline);
        }
        finally
        {
            releaseRead.TrySetResult();
            await stream.ReadExited.Task;
        }
    }

    [Theory(Timeout = 15_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalStop_RechecksBudgetAfterDrain(bool expiresDuringStop)
    {
        using var stream = new GatedReadStream();
        var remaining = TimeSpan.FromSeconds(5);
        var result = await GcDumpHeapSnapshotCollector.RunFlushAsync(
            stream,
            (input, onEvent) =>
            {
                onEvent();
                input.Read(new byte[1], 0, 1).Should().Be(0);
            },
            async _ =>
            {
                await stream.ReadEntered.Task;
                if (expiresDuringStop)
                {
                    remaining = TimeSpan.Zero;
                }
                stream.Complete();
            },
            stream.Dispose,
            () => remaining,
            _ => { },
            CancellationToken.None,
            new QuiescenceClock());

        result.TimedOut.Should().Be(expiresDuringStop);
        result.Quiescence.Should().Be(GcDumpFlushQuiescence.NotRequired);
        stream.ReadActiveAtDispose.Should().BeFalse();
    }

    [Theory(Timeout = 15_000)]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    public async Task ParserFailure_IsNeverReclassifiedByForcedClose(bool afterOwnedRead, int failureKind)
    {
        using var stream = new GatedReadStream();
        Exception failure = failureKind switch
        {
            0 => new ObjectDisposedException("unrelated parser state"),
            1 => new FormatException("invalid event payload"),
            2 => new OperationCanceledException(new CancellationToken(true)),
            3 => new TimeoutException("parser timed out"),
            _ => new IOException("unrelated transport failure"),
        };
        var parserFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = TimeSpan.FromSeconds(5);
        var act = () => GcDumpHeapSnapshotCollector.RunFlushAsync(
            stream,
            (input, onEvent) =>
            {
                onEvent();
                if (afterOwnedRead)
                {
                    try
                    {
                        input.Read(new byte[1], 0, 1).Should().Be(0);
                    }
                    catch (OperationCanceledException)
                    {
                        // Deliberately fail in unrelated parser work AFTER the owned read ends.
                    }
                }
                parserFailed.TrySetResult();
                throw failure;
            },
            async _ =>
            {
                if (afterOwnedRead)
                {
                    await stream.ReadEntered.Task;
                }
                else
                {
                    await parserFailed.Task;
                }
                remaining = TimeSpan.Zero;
                throw new IOException("stop failed");
            },
            stream.Dispose,
            () => remaining,
            _ => { },
            CancellationToken.None,
            new QuiescenceClock());

        var result = await act.Should().ThrowAsync<Exception>();
        result.Which.Should().BeSameAs(failure);
    }

    [Theory(Timeout = 15_000)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task IncompleteDrain_PreservesParserTimeoutWithoutPromotingExpectedTermination(int failureKind)
    {
        using var stream = new GatedReadStream();
        Exception failure = failureKind switch
        {
            0 => new TimeoutException("parser timed out"),
            1 => new FormatException("Read past end of stream."),
            _ => new IOException("stream aborted", new SocketException((int)SocketError.OperationAborted)),
        };
        var flush = GcDumpHeapSnapshotCollector.RunFlushAsync(
            stream,
            (input, onEvent) =>
            {
                onEvent();
                try
                {
                    input.Read(new byte[1], 0, 1).Should().Be(0);
                }
                catch (OperationCanceledException)
                {
                    // The read can only end after successful stop's drain budget expires.
                    throw failure;
                }
            },
            _ => stream.ReadEntered.Task,
            stream.Dispose,
            () => TimeSpan.FromMilliseconds(50),
            _ => { },
            CancellationToken.None,
            new QuiescenceClock());

        if (failureKind == 0)
        {
            Func<Task> observe = () => flush;
            (await observe.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(failure);
        }
        else
        {
            var result = await flush;
            result.TimedOut.Should().BeTrue();
            result.Quiescence.Should().Be(GcDumpFlushQuiescence.Completed);
        }
        stream.ReadActiveAtDispose.Should().BeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async Task AlreadyFaultedOwnedRead_IsNotHiddenByLaterForcedClose()
    {
        using var stream = new GatedReadStream();
        var failure = new ObjectDisposedException("pipe closed before owned shutdown");
        stream.Fail(failure);
        var act = () => GcDumpHeapSnapshotCollector.RunFlushAsync(
            stream,
            (input, _) => input.Read(new byte[1], 0, 1).Should().Be(0),
            _ => throw new IOException("stop failed later"),
            stream.Dispose,
            () => TimeSpan.FromSeconds(5),
            _ => { },
            CancellationToken.None,
            new QuiescenceClock());

        (await act.Should().ThrowAsync<ObjectDisposedException>()).Which.Should().BeSameAs(failure);
    }

    [Fact(Timeout = 15_000)]
    public async Task CancellationDuringOwnedRead_RemainsCancellation()
    {
        using var stream = new GatedReadStream();
        using var cancellation = new CancellationTokenSource();
        var act = GcDumpHeapSnapshotCollector.RunFlushAsync(
            stream,
            (input, _) => input.Read(new byte[1], 0, 1).Should().Be(0),
            _ => throw new IOException("stop cannot complete"),
            stream.Dispose,
            () => TimeSpan.FromSeconds(5),
            _ => { },
            cancellation.Token,
            new QuiescenceClock());
        await stream.ReadEntered.Task;
        cancellation.Cancel();

        Func<Task> observe = () => act;
        (await observe.Should().ThrowAsync<OperationCanceledException>())
            .Which.CancellationToken.Should().Be(cancellation.Token);
        stream.ReadActiveAtDispose.Should().BeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async Task UnrelatedCancellation_IsNotAnOwnedReadCancellation()
    {
        using var stream = new GatedReadStream();
        var failure = new OperationCanceledException(new CancellationToken(true));
        var act = () => GcDumpHeapSnapshotCollector.RunFlushAsync(
            stream,
            (_, _) => throw failure,
            _ => Task.CompletedTask,
            stream.Dispose,
            () => TimeSpan.FromSeconds(5),
            _ => { },
            CancellationToken.None,
            new QuiescenceClock());

        (await act.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(failure);
    }

    [Fact(Timeout = 15_000)]
    public async Task NonTimeoutStopFailure_DoesNotPermitGraphCollection()
    {
        using var stream = new GatedReadStream();
        var act = () => GcDumpHeapSnapshotCollector.RunFlushAsync(
            stream,
            (input, onEvent) =>
            {
                onEvent();
                input.Read(new byte[1], 0, 1).Should().Be(0);
            },
            async _ =>
            {
                await stream.ReadEntered.Task;
                throw new IOException("control connection failed");
            },
            stream.Dispose,
            () => TimeSpan.FromSeconds(5),
            _ => { },
            CancellationToken.None,
            new QuiescenceClock());

        await act.Should().ThrowAsync<IOException>().WithMessage("*could not stop*");
        stream.ReadActiveAtDispose.Should().BeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async Task StopBudgetExpiresWhileReadIsActive_TimeoutWithoutAnyTrace()
    {
        using var stream = new GatedReadStream();
        var remaining = TimeSpan.FromSeconds(1);
        var parserExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = Path.Combine(Path.GetTempPath(), $"gcdump-940-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var collector = new GcDumpHeapSnapshotCollector(new ArtifactRoot(root), async (_, ct) =>
                (await GcDumpHeapSnapshotCollector.RunFlushAsync(
                    stream,
                    (input, onEvent) =>
                    {
                        try
                        {
                            onEvent();
                            input.Read(new byte[1], 0, 1).Should().Be(0);
                        }
                        finally
                        {
                            parserExited.TrySetResult();
                        }
                    },
                    async token =>
                    {
                        await stream.ReadEntered.Task;
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        }
                        finally
                        {
                            remaining = TimeSpan.Zero;
                        }
                    },
                    stream.Dispose,
                    () => remaining,
                    _ => { },
                    ct,
                    new QuiescenceClock())).TimedOut);

            var snapshot = await collector.CollectAsync(42, new GcDumpOptions(ExportTrace: true));

            snapshot.GcDumpStatus!.TimedOut.Should().BeTrue();
            snapshot.GcDumpStatus.TraceExportCompleted.Should().BeFalse();
            snapshot.GcDumpStatus.EventStreamCompleted.Should().BeFalse();
            snapshot.TracePath.Should().BeNull();
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Should().BeEmpty();
            await parserExited.Task.WaitAsync(TimeSpan.FromSeconds(2));
            stream.ReadActiveAtDispose.Should().BeFalse("the owned read must quiesce before physical close");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record ArtifactRoot(string RootPath) : IArtifactRootProvider
    {
        public string Root => RootPath;
    }

    // Only the cleanup deadline uses this clock. Tests hold it still until the reader has
    // quiesced, or explicitly expire it while a controlled read is still pending.
    private sealed class QuiescenceClock : TimeProvider
    {
        private Action? _expire;
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            dueTime.Should().Be(TimeSpan.FromSeconds(1));
            period.Should().Be(Timeout.InfiniteTimeSpan);
            _expire.Should().BeNull("each flush has only one cleanup deadline");
            var timer = new DeadlineTimer(callback, state);
            _expire = timer.Fire;
            TimerCreated.SetResult();
            return timer;
        }

        public void Expire() => _expire!();

        private sealed class DeadlineTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;
            public void Fire()
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    callback(state);
                }
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class GatedReadStream(
        Stream? inner = null, bool ignoreCancellation = false, Task? releaseCancelledRead = null) : Stream
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadExited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public bool ReadActiveAtDispose { get; private set; }
        public void Complete() => _read.TrySetResult(0);
        public void Fail(Exception exception) => _read.TrySetException(exception);
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _active);
            try
            {
                var pending = inner?.ReadAsync(buffer, cancellationToken).AsTask()
                    ?? _read.Task.WaitAsync(ignoreCancellation ? CancellationToken.None : cancellationToken);
                ReadEntered.TrySetResult();
                return await pending;
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                if (releaseCancelledRead is not null)
                {
                    await releaseCancelledRead;
                }
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
                ReadExited.TrySetResult();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
                ReadActiveAtDispose |= Volatile.Read(ref _active) != 0;
                inner?.Dispose();
                _read.TrySetException(new ObjectDisposedException("owned pipe", "Cannot access a closed pipe."));
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
