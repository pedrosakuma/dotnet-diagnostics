using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Analysis;
using Microsoft.Diagnostics.Tracing.Etlx;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TraceLog = Microsoft.Diagnostics.Tracing.Etlx.TraceLog;

namespace DotnetDiagnostics.Core.Gc;

/// <summary>
/// Default <see cref="IGcDatasCollector"/>. Collects GC events to a temporary <c>.nettrace</c> at
/// <c>Microsoft-Windows-DotNETRuntime</c> keyword 0x1 (GC) / <see cref="EventLevel.Informational"/>,
/// then post-processes with <see cref="TraceLog"/> + the loaded-runtime analysis pipeline to read
/// the DATAS <c>GCDynamicEvent</c> payloads (which are not exposed on a raw EventPipe stream).
/// </summary>
public sealed class EventPipeGcDatasCollector : IGcDatasCollector
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const long GcKeyword = 0x1;

    private readonly ILogger<EventPipeGcDatasCollector> _logger;

    public EventPipeGcDatasCollector(ILogger<EventPipeGcDatasCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeGcDatasCollector>.Instance;
    }

    public async Task<GcDatasSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        int maxEvents = 1000,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        if (maxEvents < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents), "maxEvents must be >= 1.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var tracePath = Path.Combine(Path.GetTempPath(), $"datas-{processId}-{Guid.NewGuid():N}.nettrace");
        string? etlxPath = null;

        try
        {
            await CollectTraceAsync(processId, tracePath, duration, cancellationToken).ConfigureAwait(false);
            etlxPath = TraceLog.CreateFromEventPipeDataFile(tracePath);
            return Parse(processId, etlxPath, startedAt, duration, maxEvents);
        }
        finally
        {
            TryDelete(tracePath);
            if (etlxPath is not null)
            {
                TryDelete(etlxPath);
            }
        }
    }

    private static async Task CollectTraceAsync(int pid, string outputPath, TimeSpan duration, CancellationToken ct)
    {
        var providers = new[]
        {
            new EventPipeProvider(RuntimeProvider, EventLevel.Informational, GcKeyword),
        };

        var client = new DiagnosticsClient(pid);
        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(providers, requestRundown: false, circularBufferMB: 256, TimeSpan.FromSeconds(30), ct)
            .ConfigureAwait(false);

        var copyTask = Task.Run(async () =>
        {
            await using var output = File.Create(outputPath);
            await session.EventStream.CopyToAsync(output, ct).ConfigureAwait(false);
        }, ct);

        try
        {
            await Task.Delay(duration, ct).ConfigureAwait(false);
        }
        finally
        {
            try { await session.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            try { await copyTask.ConfigureAwait(false); } catch (Exception) { }
            session.Dispose();
        }
    }

    private GcDatasSnapshot Parse(int pid, string etlxPath, DateTimeOffset startedAt, TimeSpan duration, int maxEvents)
    {
        var samples = new List<DatasSampleEvent>();
        var tuning = new List<DatasTuningEvent>();
        var fullGc = new List<DatasFullGcTuningEvent>();
        var malformed = 0;
        var unsupportedVersion = 0;
        var extraBytes = 0;

        try
        {
            using var traceLog = new TraceLog(etlxPath);
            var source = traceLog.Events.GetSource();
            source.NeedLoadedDotNetRuntimes();
            source.Process();

            foreach (var process in source.Processes())
            {
                if (process.ProcessID != pid)
                {
                    continue;
                }

                var runtime = process.LoadedDotNetRuntime();
                if (runtime is null)
                {
                    continue;
                }

                foreach (var gc in runtime.GC.GCs)
                {
                    var dynamicEvents = gc.DynamicEvents;
                    if (dynamicEvents is null)
                    {
                        continue;
                    }

                    foreach (var dyn in dynamicEvents)
                    {
                        var payload = dyn.Payload;
                        if (payload is null)
                        {
                            continue;
                        }

                        var ts = new DateTimeOffset(dyn.TimeStamp.ToUniversalTime(), TimeSpan.Zero);
                        switch (dyn.Name)
                        {
                            case DatasPayloadParser.SampleEventName:
                                Accumulate(
                                    DatasPayloadParser.TryParseSample(payload, ts, out var s, out var sExtra),
                                    s, samples, maxEvents, sExtra,
                                    ref malformed, ref unsupportedVersion, ref extraBytes);
                                break;
                            case DatasPayloadParser.TuningEventName:
                                Accumulate(
                                    DatasPayloadParser.TryParseTuning(payload, ts, out var t, out var tExtra),
                                    t, tuning, maxEvents, tExtra,
                                    ref malformed, ref unsupportedVersion, ref extraBytes);
                                break;
                            case DatasPayloadParser.FullGcTuningEventName:
                                Accumulate(
                                    DatasPayloadParser.TryParseFullGcTuning(payload, ts, out var f, out var fExtra),
                                    f, fullGc, maxEvents, fExtra,
                                    ref malformed, ref unsupportedVersion, ref extraBytes);
                                break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse DATAS trace for pid {Pid}.", pid);
        }

        return new GcDatasSnapshot(
            pid,
            startedAt,
            duration,
            samples,
            tuning,
            fullGc,
            new DatasParseStats(malformed, unsupportedVersion, extraBytes));
    }

    private static void Accumulate<T>(
        DatasParseOutcome outcome,
        T? parsed,
        List<T> sink,
        int maxEvents,
        bool extra,
        ref int malformed,
        ref int unsupportedVersion,
        ref int extraBytes)
        where T : class
    {
        switch (outcome)
        {
            case DatasParseOutcome.Decoded:
                if (parsed is not null && sink.Count < maxEvents)
                {
                    sink.Add(parsed);
                }

                if (extra)
                {
                    extraBytes++;
                }

                break;
            case DatasParseOutcome.Malformed:
                malformed++;
                break;
            case DatasParseOutcome.UnsupportedVersion:
                unsupportedVersion++;
                break;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete temp file {Path}.", path);
        }
    }
}
