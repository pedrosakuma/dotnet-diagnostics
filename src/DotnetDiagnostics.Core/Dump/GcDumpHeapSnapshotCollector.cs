using System.Diagnostics;
using System.Diagnostics.Tracing;
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
/// runtime to induce a blocking gen-2 GC and stream the entire managed object graph as
/// <c>GCBulkNode</c> / <c>GCBulkType</c> events. The node sizes and type names are aggregated into
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

    public GcDumpHeapSnapshotCollector(ILogger<GcDumpHeapSnapshotCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<GcDumpHeapSnapshotCollector>.Instance;
    }

    public async Task<HeapSnapshotArtifact> CollectAsync(
        int processId,
        GcDumpOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new GcDumpOptions();
        var timeout = opts.Timeout ?? DefaultTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be positive.");
        }

        var snapshotTopN = Math.Max(opts.TopTypes, opts.SnapshotTopTypes);
        var capturedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var aggregator = await CollectGraphAsync(processId, timeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        var warnings = new List<string>
        {
            "GC handles, static fields, delegate targets, finalizable types and segment layout are unavailable over EventPipe; capture with source=dump or source=live for those views.",
        };
        if (aggregator.NodeCount == 0)
        {
            warnings.Add("No managed objects were reported. The GC heap dump may have timed out before the runtime started streaming the object graph.");
        }

        var (byBytes, byInstances) = aggregator.Project(snapshotTopN);

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
        };
    }

    private async Task<GcDumpTypeAggregator> CollectGraphAsync(int processId, TimeSpan timeout, CancellationToken ct)
    {
        var client = new DiagnosticsClient(processId);

        // Flush the type table so the GCBulkType name stream is fully populated before the dump.
        await FlushTypeTableAsync(client, ct).ConfigureAwait(false);

        var providers = new[]
        {
            new EventPipeProvider(RuntimeProvider, EventLevel.Verbose, GcHeapSnapshotKeyword),
        };
        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(providers, requestRundown: false, circularBufferMB: 256, ct)
            .ConfigureAwait(false);

        try
        {
            var aggregator = new GcDumpTypeAggregator();
            var gcNum = -1;
            var dumpComplete = false;
            var dataSeen = false;

            var processing = Task.Run(() =>
            {
                using var source = new EventPipeEventSource(session.EventStream);

                source.Clr.GCStart += data =>
                {
                    dataSeen = true;
                    if (gcNum < 0 && data.Depth == 2 && data.Type != GCType.BackgroundGC)
                    {
                        gcNum = data.Count;
                    }
                };
                source.Clr.GCStop += data =>
                {
                    if (data.Count == gcNum)
                    {
                        dumpComplete = true;
                        source.StopProcessing();
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
                    dataSeen = true;
                    for (var i = 0; i < data.Count; i++)
                    {
                        var v = data.Values(i);
                        aggregator.AddNode(v.TypeID, v.Size);
                    }
                };

                source.Process();
            }, CancellationToken.None);

            var deadline = Stopwatch.StartNew();
            while (!processing.Wait(100, CancellationToken.None))
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }
                if (!dataSeen && deadline.Elapsed.TotalSeconds > 5)
                {
                    _logger.LogWarning("No EventPipe heap data within 5s for PID {Pid}; assuming no managed heap.", processId);
                    break;
                }
                if (dumpComplete || deadline.Elapsed > timeout)
                {
                    break;
                }
            }

            try
            {
                await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Stopping gcdump EventPipe session for PID {Pid} threw.", processId);
            }

            try
            {
                await processing.WaitAsync(timeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Processing did not drain in time; return whatever was aggregated.
            }

            return aggregator;
        }
        finally
        {
            session.Dispose();
        }
    }

    private static async Task FlushTypeTableAsync(DiagnosticsClient client, CancellationToken ct)
    {
        var providers = new[]
        {
            new EventPipeProvider(SampleProfilerProvider, EventLevel.Informational),
        };
        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(providers, requestRundown: false, circularBufferMB: 1, ct)
            .ConfigureAwait(false);
        try
        {
            await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            session.Dispose();
        }
    }
}
