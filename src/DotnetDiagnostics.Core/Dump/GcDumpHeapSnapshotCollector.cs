using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Net.Sockets;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.Dump;

/// <summary>
/// Default <see cref="IGcDumpHeapSnapshotCollector"/>. Opens an EventPipe session on
/// <c>Microsoft-Windows-DotNETRuntime</c> with the <c>GCHeapSnapshot</c> keyword, which forces the
/// runtime to induce a blocking gen-2 GC and stream heap-dump events. This collector consumes
/// <c>GCBulkNode</c> / <c>GCBulkType</c> records only; it does not retain object edges or roots.
/// The observed node sizes and type names are aggregated into
/// the same <see cref="HeapSnapshotArtifact"/> the ClrMD inspectors produce, so the existing
/// <c>query_snapshot</c> heap views work unchanged — without ptrace, ClrMD attach, or a dump file.
/// <para>
/// The induced-GC trigger and termination handshake (flush the type table, watch for the gen-2
/// GCStart/GCStop pair) are ported from dotnet/diagnostics' MIT-licensed
/// <c>EventPipeDotNetHeapDumper</c>; the aggregation is reimplemented here so we don't take a
/// dependency on the gcdump-private MemoryGraph types (absent from TraceEvent 3.2.2).
/// </para>
/// </summary>
public sealed class GcDumpHeapSnapshotCollector : IGcDumpHeapSnapshotCollector
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const string SampleProfilerProvider = "Microsoft-DotNETCore-SampleProfiler";

    // GCHeapSnapshot = GC | Type | GCHeapDump | GCHeapAndTypeNames keywords (0x1980001).
    private const long GcHeapSnapshotKeyword = 0x1980001;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<GcDumpHeapSnapshotCollector> _logger;
    private readonly IArtifactRootProvider? _artifactRoot;
    private readonly Func<TimeSpan, CancellationToken, Task<bool>>? _flush;

    public GcDumpHeapSnapshotCollector(
        ILogger<GcDumpHeapSnapshotCollector>? logger = null,
        IArtifactRootProvider? artifactRoot = null)
    {
        _logger = logger ?? NullLogger<GcDumpHeapSnapshotCollector>.Instance;
        _artifactRoot = artifactRoot;
    }

    internal GcDumpHeapSnapshotCollector(
        IArtifactRootProvider artifactRoot,
        Func<TimeSpan, CancellationToken, Task<bool>> flush)
        : this(artifactRoot: artifactRoot)
    {
        _flush = flush;
    }

    public async Task<HeapSnapshotArtifact> CollectAsync(
        int processId,
        GcDumpOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new GcDumpOptions();

        // Backstop guard (issue #471). Requesting the GCHeapSnapshot EventPipe keyword crashes
        // NativeAOT .NET 10 targets — the runtime segfaults and the process exits mid-handshake
        // (reproduced on SDK 10.0.201). The capability gate already withholds gcdump on AOT, but a
        // direct caller (e.g. the CLI session REPL) must never reach the session-start code path and
        // kill the target, so we refuse here before touching the diagnostic socket.
        if (opts.Runtime == RuntimeFlavor.NativeAot)
        {
            throw new NotSupportedException(
                "gcdump is not supported on NativeAOT: requesting the GCHeapSnapshot EventPipe keyword " +
                "crashes .NET 10 NativeAOT targets (the runtime segfaults). Use collect_process_dump instead.");
        }

        var timeout = opts.Timeout ?? DefaultTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be positive.");
        }

        var snapshotTopN = Math.Max(opts.TopTypes, opts.SnapshotTopTypes);
        var capturedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        string? exportFullPath = null;
        string? exportRelative = null;
        if (opts.ExportTrace)
        {
            if (_artifactRoot is null)
            {
                throw new InvalidOperationException("Trace export requires an artifact root; none was configured.");
            }
            var directory = SafeArtifactPath.ResolveDirectory(_artifactRoot.Root, "traces", defaultRelative: "traces");
            var stamp = capturedAt.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
            exportFullPath = Path.Combine(directory, $"gcdump_pid{processId.ToString(CultureInfo.InvariantCulture)}_{stamp}.nettrace");
            exportRelative = Path.GetRelativePath(_artifactRoot.Root, exportFullPath).Replace(Path.DirectorySeparatorChar, '/');
        }

        var collection = await CollectGraphAsync(processId, timeout, exportFullPath, cancellationToken).ConfigureAwait(false);
        var aggregator = collection.Aggregator;
        sw.Stop();
        var traceAvailable = exportFullPath is not null
            && collection.StreamCompleted
            && File.Exists(exportFullPath);
        if (traceAvailable)
        {
            SafeArtifactPath.SetRestrictiveFilePermissions(exportFullPath!);
        }

        var warnings = new List<string>
        {
            "GC handles, static fields, delegate targets, finalizable types and segment layout are unavailable over EventPipe; capture with source=dump or source=live for those views.",
        };
        if (aggregator.NodeCount == 0)
        {
            warnings.Add("No managed objects were reported. The GC heap dump may have timed out before the runtime started streaming the object graph.");
        }
        if (collection.TimedOut)
        {
            warnings.Add($"GC heap collection reached its {timeout.TotalSeconds:0.#}s timeout; the snapshot may be incomplete.");
        }
        if (opts.ExportTrace && !traceAvailable)
        {
            warnings.Add("GC trace export was not completed before collection ended; no trace artifact was published.");
        }
        if (collection.ReaderFailed)
        {
            warnings.Add("The EventPipe heap reader failed after partial per-type evidence may have been retained.");
        }
        else if (!collection.StreamCompleted)
        {
            warnings.Add("The EventPipe heap stream did not complete cleanly; retained per-type evidence may be partial.");
        }
        if (!collection.GcStopObserved)
        {
            warnings.Add("The induced blocking GC stop was not observed; this does not establish a startup cause or an empty heap.");
        }
        if (aggregator.MissingTypeNameCount > 0)
        {
            warnings.Add($"{aggregator.MissingTypeNameCount} observed type id(s) had no streamed name and are shown as hexadecimal ids.");
        }

        var (byBytes, byInstances) = aggregator.Project(snapshotTopN);
        var projectedTypeCount = aggregator.CountProjectedTypes(snapshotTopN);
        if (projectedTypeCount < aggregator.TypeCount)
        {
            warnings.Add($"{aggregator.TypeCount - projectedTypeCount} lower-ranked observed type(s) were omitted by the snapshot top-N projection; aggregation itself was not capped.");
        }
        var status = new GcDumpCaptureStatus(
            collection.GcStopObserved,
            collection.StreamCompleted,
            collection.TimedOut,
            collection.ReaderFailed,
            opts.ExportTrace,
            traceAvailable);
        var quality = GcDumpEvidence.BuildQuality(
            aggregator.NodeCount,
            aggregator.TypeCount,
            aggregator.MissingTypeNameCount,
            projectedTypeCount,
            status);

        return new HeapSnapshotArtifact(
            Origin: HeapSnapshotOrigin.GcDump,
            ProcessId: processId,
            CapturedAt: capturedAt,
            WalkDuration: sw.Elapsed,
            Runtime: new DumpRuntimeInfo(
                Name: "CoreCLR",
                Version: string.Empty,
                Architecture: System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                IsServerGC: false,
                HeapCount: 0),
            Heap: new DumpHeapSummary(aggregator.TotalBytes, 0, 0, 0, 0, 0, aggregator.TotalBytes),
            TopTypesByBytes: byBytes,
            TopTypesByInstances: byInstances)
        {
            Warnings = warnings,
            TracePath = traceAvailable ? exportRelative : null,
            Quality = quality,
            GcDumpStatus = status,
        };
    }

    private async Task<GcDumpCollection> CollectGraphAsync(int processId, TimeSpan timeout, string? exportFullPath, CancellationToken ct)
    {
        var collectionTimer = Stopwatch.StartNew();
        var client = new DiagnosticsClient(processId);

        // Flush the type table so the GCBulkType name stream is fully populated before the dump.
        var flushBudget = Min(Remaining(collectionTimer, timeout), TimeSpan.FromSeconds(5));
        if (flushBudget <= TimeSpan.Zero)
        {
            return new GcDumpCollection(new GcDumpTypeAggregator(), TimedOut: true);
        }
        try
        {
            if (await (_flush is null
                ? FlushTypeTableAsync(client, flushBudget, ct)
                : _flush(flushBudget, ct)).ConfigureAwait(false))
            {
                return new GcDumpCollection(new GcDumpTypeAggregator(), TimedOut: true);
            }
        }
        catch (TimeoutException)
        {
            return new GcDumpCollection(new GcDumpTypeAggregator(), TimedOut: true);
        }

        var providers = new[]
        {
            new EventPipeProvider(RuntimeProvider, EventLevel.Verbose, GcHeapSnapshotKeyword),
        };
        var startBudget = Remaining(collectionTimer, timeout);
        if (startBudget <= TimeSpan.Zero)
        {
            return new GcDumpCollection(new GcDumpTypeAggregator(), TimedOut: true);
        }
        EventPipeSession session;
        try
        {
            session = await client
                .StartEventPipeSessionWithTimeoutAsync(providers, requestRundown: false, circularBufferMB: 256, startBudget, ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new GcDumpCollection(new GcDumpTypeAggregator(), TimedOut: true);
        }

        var aggregator = new GcDumpTypeAggregator();
        var gcNum = -1;
        var dataSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dumpComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processing = Task.Run(() =>
        {
            Stream eventStream = session.EventStream;
            FileStream? exportFile = null;
            try
            {
                if (exportFullPath is not null)
                {
                    // Tee the raw EventPipe byte stream to disk while the aggregator consumes it, so the
                    // persisted .nettrace is byte-identical to what dotnet-gcdump would write.
                    exportFile = SafeArtifactPath.CreateRestrictedFile(exportFullPath);
                    eventStream = new TeeReadStream(eventStream, exportFile);
                }

                using var source = new EventPipeEventSource(eventStream);

                source.Clr.GCStart += data =>
                {
                    dataSeen.TrySetResult();
                    if (gcNum < 0 && data.Depth == 2 && data.Type != GCType.BackgroundGC)
                    {
                        gcNum = data.Count;
                    }
                };
                source.Clr.GCStop += data =>
                {
                    if (data.Count == gcNum)
                    {
                        // Mark completion only — do NOT StopProcessing here. GCStop proves the induced
                        // GC finished, but tail GCBulkNode/TypeBulkType events may still be buffered.
                        // The control path stops the session, letting Process() drain to EOF naturally.
                        dumpComplete.TrySetResult();
                    }
                };
                source.Clr.TypeBulkType += data =>
                {
                    for (var i = 0; i < data.Count; i++)
                    {
                        var v = data.Values(i);
                        aggregator.RegisterType(v.TypeID, v.TypeName);
                    }
                };
                source.Clr.GCBulkNode += data =>
                {
                    dataSeen.TrySetResult();
                    for (var i = 0; i < data.Count; i++)
                    {
                        var v = data.Values(i);
                        aggregator.AddNode(v.TypeID, v.Size);
                    }
                };

                source.Process();
            }
            finally
            {
                // Always release the export handle, even when the parser throws, so a failed/cancelled
                // export does not leak the fd or leave the restricted file locked.
                exportFile?.Dispose();
            }
        }, CancellationToken.None);

        var cancelled = false;
        var timedOut = false;
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, ct);
        var remaining = Remaining(collectionTimer, timeout);
        var noDataTask = remaining > TimeSpan.FromSeconds(5)
            ? Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)
            : Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
        var timeoutTask = Task.Delay(remaining, CancellationToken.None);

        var initialCompletion = await Task.WhenAny(
            processing,
            dataSeen.Task,
            dumpComplete.Task,
            noDataTask,
            timeoutTask,
            cancellationTask).ConfigureAwait(false);

        if (initialCompletion == cancellationTask)
        {
            cancelled = true;
        }
        else if (initialCompletion == timeoutTask)
        {
            timedOut = true;
        }
        else if (initialCompletion == noDataTask && !dataSeen.Task.IsCompleted)
        {
            _logger.LogWarning(
                "No EventPipe heap data was observed within 5s for PID {Pid}; ending the capture without inferring an empty heap or a startup cause.",
                processId);
        }
        else if (initialCompletion == dataSeen.Task || (initialCompletion == noDataTask && dataSeen.Task.IsCompleted))
        {
            var terminalCompletion = await Task.WhenAny(
                processing,
                dumpComplete.Task,
                timeoutTask,
                cancellationTask).ConfigureAwait(false);
            cancelled = terminalCompletion == cancellationTask;
            timedOut = terminalCompletion == timeoutTask;
        }

        try
        {
            await EventPipeSessionShutdown.StopAndDrainAsync(
                session,
                processing,
                ex => _logger.LogDebug(ex, "Stopping gcdump EventPipe session for PID {Pid} threw.", processId),
                Remaining(collectionTimer, timeout),
                propagateProcessingErrors: false).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }

        ct.ThrowIfCancellationRequested();
        return new GcDumpCollection(
            aggregator,
            timedOut || collectionTimer.Elapsed >= timeout,
            GcStopObserved: dumpComplete.Task.IsCompletedSuccessfully,
            StreamCompleted: processing.IsCompletedSuccessfully,
            ReaderFailed: processing.IsFaulted);
    }

    private async Task<bool> FlushTypeTableAsync(DiagnosticsClient client, TimeSpan timeout, CancellationToken ct)
    {
        var flushTimer = Stopwatch.StartNew();
        var providers = new[]
        {
            new EventPipeProvider(SampleProfilerProvider, EventLevel.Informational),
        };
        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(providers, requestRundown: false, circularBufferMB: 1, timeout, ct)
            .ConfigureAwait(false);
        return await RunFlushAsync(
            session.EventStream,
            static (stream, onEvent) =>
            {
                using var source = new EventPipeEventSource(stream);
                source.Dynamic.All += _ => onEvent();
                source.Process();
            },
            session.StopAsync,
            session.Dispose,
            () => Remaining(flushTimer, timeout),
            ex => _logger.LogDebug(ex, "Stopping gcdump type-table flush session threw."),
            ct).ConfigureAwait(false);
    }

    internal static async Task<bool> RunFlushAsync(
        Stream stream,
        Action<Stream, Action> process,
        Func<CancellationToken, Task> stopAsync,
        Action dispose,
        Func<TimeSpan> remainingBudget,
        Action<Exception> onError,
        CancellationToken ct)
    {
        using var readCancellation = new CancellationTokenSource();
        var readToken = readCancellation.Token;
        using var readStream = new GcDumpFlushReadStream(stream, readToken);
        var firstEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processing = Task.Run(() =>
        {
            try
            {
                process(readStream, () => firstEvent.TrySetResult());
            }
            catch (OperationCanceledException ex) when (
                ex.CancellationToken == readToken && readToken.IsCancellationRequested)
            {
                // Only cancellation of this flush's own read is expected. Parser errors (including
                // ObjectDisposedException) and unrelated cancellation still propagate.
            }
        }, CancellationToken.None);

        var remaining = remainingBudget();
        var timedOut = remaining <= TimeSpan.Zero;
        if (remaining > TimeSpan.Zero)
        {
            using var timeoutCancellation = new CancellationTokenSource();
            var timeoutTask = Task.Delay(remaining, timeoutCancellation.Token);
            try
            {
                var completion = await Task.WhenAny(firstEvent.Task, processing, timeoutTask)
                    .WaitAsync(ct).ConfigureAwait(false);
                timedOut = completion == timeoutTask;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Still stop and quiesce the owned reader before honoring user cancellation.
            }
            finally
            {
                await timeoutCancellation.CancelAsync().ConfigureAwait(false);
            }
        }

        // The runtime can abort the stream at the deadline before Task.Delay wins the race.
        // Classify by the monotonic budget as well as by the winning task.
        timedOut |= remainingBudget() <= TimeSpan.Zero;

        EventPipeForcedCloseReason? forcedClose = null;
        try
        {
            await EventPipeSessionShutdown.StopAndDrainAsync(
                stopAsync,
                dispose,
                processing,
                onError,
                remainingBudget(),
                propagateProcessingErrors: true,
                beforeForcedClose: async reason =>
                {
                    forcedClose = reason;
                    if (processing.IsCompleted)
                    {
                        return;
                    }
                    async Task CancelAndQuiesceAsync()
                    {
                        await readCancellation.CancelAsync().ConfigureAwait(false);
                        // Observe completion, not its error: the normal drain propagates parser failures.
                        await Task.WhenAny(processing).ConfigureAwait(false);
                    }
                    var quiescence = CancelAndQuiesceAsync();
                    try
                    {
                        await quiescence.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
                    }
                    finally
                    {
                        _ = quiescence.ContinueWith(
                            static task => _ = task.Exception,
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                }).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }
        catch (Exception ex) when (IsExpectedFlushTermination(
            ex, firstEvent.Task.IsCompletedSuccessfully, timedOut || remainingBudget() <= TimeSpan.Zero))
        {
            // A deliberately stopped auxiliary session can end TraceEvent with either of these
            // verified platform-specific truncated-stream shapes. Keep every other parser failure.
            onError(ex);
        }
        ct.ThrowIfCancellationRequested();
        if (forcedClose == EventPipeForcedCloseReason.StopFailed && remainingBudget() > TimeSpan.Zero)
        {
            throw new IOException("The gcdump type-table flush could not stop its EventPipe session cleanly.");
        }
        return timedOut || remainingBudget() <= TimeSpan.Zero || forcedClose is not null;
    }

    internal static bool IsExpectedFlushTermination(Exception exception, bool firstEventObserved, bool timedOut)
        => (firstEventObserved || timedOut)
            && (exception is FormatException { Message: "Read past end of stream." }
            || exception is IOException
            {
                InnerException: SocketException
                {
                    SocketErrorCode: SocketError.ConnectionAborted or SocketError.OperationAborted,
                },
            });

    private static TimeSpan Remaining(Stopwatch timer, TimeSpan timeout)
        => timeout > timer.Elapsed ? timeout - timer.Elapsed : TimeSpan.Zero;

    private static TimeSpan Min(TimeSpan left, TimeSpan right)
        => left <= right ? left : right;

    private sealed record GcDumpCollection(
        GcDumpTypeAggregator Aggregator,
        bool TimedOut,
        bool GcStopObserved = false,
        bool StreamCompleted = false,
        bool ReaderFailed = false);
}

/// <summary>
/// Read-only stream wrapper that mirrors every byte read from the inner stream into a sink
/// stream. Used to persist a raw <c>.nettrace</c> while the aggregator parses the same EventPipe
/// stream in-flight, so export adds no second collection pass. Only the read path is teed; writes are
/// unsupported.
/// </summary>
internal sealed class TeeReadStream : Stream
{
    private readonly Stream _inner;
    private readonly Stream _sink;

    public TeeReadStream(Stream inner, Stream sink)
    {
        _inner = inner;
        _sink = sink;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
        {
            _sink.Write(buffer, offset, read);
        }
        return read;
    }

    public override void Flush() => _sink.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
