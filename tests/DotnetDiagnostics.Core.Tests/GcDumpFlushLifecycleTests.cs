using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Dump;
using FluentAssertions;
using System.IO.Pipes;

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

        var timedOut = await GcDumpHeapSnapshotCollector.RunFlushAsync(
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
            CancellationToken.None);

        timedOut.Should().BeTrue();
        stream.ReadActiveAtDispose.Should().BeFalse();
        Console.WriteLine($"Native transport: {Environment.OSVersion}; pending PipeStream read quiesced before physical close.");
    }

    [Fact(Timeout = 15_000)]
    public async Task NonCooperativeRead_HasBoundedCleanupAndNoTrace()
    {
        using var stream = new GatedReadStream(ignoreCancellation: true);
        var parserExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var root = Path.Combine(Path.GetTempPath(), $"gcdump-940-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var collector = new GcDumpHeapSnapshotCollector(new ArtifactRoot(root), (_, ct) =>
                GcDumpHeapSnapshotCollector.RunFlushAsync(
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
                    ct));

            var snapshot = await collector.CollectAsync(42, new GcDumpOptions(ExportTrace: true))
                .WaitAsync(TimeSpan.FromSeconds(5));
            snapshot.GcDumpStatus!.TimedOut.Should().BeTrue();
            snapshot.GcDumpStatus.TraceExportCompleted.Should().BeFalse();
            snapshot.TracePath.Should().BeNull();
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
            var collector = new GcDumpHeapSnapshotCollector(new ArtifactRoot(root), (_, ct) =>
                GcDumpHeapSnapshotCollector.RunFlushAsync(
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
                    ct));
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
        var timedOut = await GcDumpHeapSnapshotCollector.RunFlushAsync(
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
            CancellationToken.None);

        timedOut.Should().BeTrue();
        stream.ReadActiveAtDispose.Should().BeFalse();
    }

    [Theory(Timeout = 15_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalStop_RechecksBudgetAfterDrain(bool expiresDuringStop)
    {
        using var stream = new GatedReadStream();
        var remaining = TimeSpan.FromSeconds(5);
        var timedOut = await GcDumpHeapSnapshotCollector.RunFlushAsync(
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
            CancellationToken.None);

        timedOut.Should().Be(expiresDuringStop);
        stream.ReadActiveAtDispose.Should().BeFalse();
    }

    [Theory(Timeout = 15_000)]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task ParserFailure_IsNeverReclassifiedByForcedClose(bool afterOwnedRead, int failureKind)
    {
        using var stream = new GatedReadStream();
        Exception failure = failureKind switch
        {
            0 => new ObjectDisposedException("unrelated parser state"),
            1 => new FormatException("invalid event payload"),
            _ => new OperationCanceledException(new CancellationToken(true)),
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
            CancellationToken.None);

        var result = await act.Should().ThrowAsync<Exception>();
        result.Which.Should().BeSameAs(failure);
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
            CancellationToken.None);

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
            cancellation.Token);
        await stream.ReadEntered.Task;
        cancellation.Cancel();

        Func<Task> observe = () => act;
        await observe.Should().ThrowAsync<OperationCanceledException>();
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
            CancellationToken.None);

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
            CancellationToken.None);

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
            var collector = new GcDumpHeapSnapshotCollector(new ArtifactRoot(root), (_, ct) =>
                GcDumpHeapSnapshotCollector.RunFlushAsync(
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
                    ct));

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

    private sealed class GatedReadStream(Stream? inner = null, bool ignoreCancellation = false) : Stream
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
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
