using System.Diagnostics.Tracing;
using System.Globalization;
using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Internal;
using DotnetDiagnostics.Core.Security;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.Exceptions;

/// <summary>
/// Default <see cref="IExceptionCollector"/> backed by an EventPipe session subscribed to the
/// runtime Exception keyword (0x8000) on <c>Microsoft-Windows-DotNETRuntime</c>.
/// </summary>
public sealed class EventPipeExceptionCollector : IExceptionCollector
{
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const long ExceptionKeyword = 0x8000;

    private readonly ILogger<EventPipeExceptionCollector> _logger;

    public EventPipeExceptionCollector(ILogger<EventPipeExceptionCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeExceptionCollector>.Instance;
    }

    public async Task<ExceptionSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        int maxRecent = 100,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        if (maxRecent < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRecent), "maxRecent must be >= 1.");
        }

        var providers = new[]
        {
            new EventPipeProvider(RuntimeProvider, EventLevel.Warning, ExceptionKeyword),
        };

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(providers, requestRundown: false, circularBufferMB: 64, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        // EventPipeEventSource invokes these callbacks on the single source.Process() thread, so
        // plain collections are sufficient and avoid unnecessary synchronization on the hot path.
        var recent = new List<ManagedExceptionEvent>(Math.Min(maxRecent, 128));
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        var recording = CaptureRecordingContext.Current;
        var redactor = recording is null ? null : new SensitiveDataRedactor();

        var processingTask = Task.Run(() =>
        {
            long? sourceLoss = null;
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Clr.ExceptionStart += traceEvent =>
                {
                    total++;
                    RecordException(traceEvent.TimeStamp, traceEvent.ThreadID,
                        traceEvent.ExceptionType, traceEvent.ExceptionMessage, traceEvent.ExceptionHRESULT,
                        recent, counts, maxRecent, recording, redactor);
                };

                source.Process();
                sourceLoss = source.EventsLost;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EventPipe exception source ended for pid {Pid}.", processId);
            }
            finally
            {
                EventPipeCollectionRunner.ReportSourceLoss(recording, sourceLoss);
            }
        }, cancellationToken);

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await EventPipeSessionShutdown.StopAndDrainAsync(
                session,
                processingTask,
                ex => _logger.LogDebug(ex, "Stopping EventPipe exception session for pid {Pid} failed.", processId))
                .ConfigureAwait(false);
        }

        var byType = counts
            .Select(kvp => new ExceptionCount(kvp.Key, kvp.Value))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.ExceptionType, StringComparer.Ordinal)
            .ToList();

        return new ExceptionSnapshot(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            TotalExceptions: total,
            ByType: byType,
            Recent: recent) { RecentCap = maxRecent };
    }

    internal static void RecordException(DateTime timestamp, int threadId, string? type, string? message, int hresult,
        List<ManagedExceptionEvent> recent, Dictionary<string, int> counts, int maxRecent,
        ICaptureObservationSink? recording, SensitiveDataRedactor? redactor)
    {
        var key = type ?? "(unknown)";
        counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;
        if (recording is not null)
            RuntimeObservationProjection.Exception(recording, new DateTimeOffset(timestamp.ToUniversalTime()),
                threadId, type, message, hresult, redactor!);
        if (recent.Count < maxRecent)
            recent.Add(new ManagedExceptionEvent(new DateTimeOffset(timestamp.ToUniversalTime()), key,
                message ?? string.Empty, "0x" + hresult.ToString("X", CultureInfo.InvariantCulture), threadId));
    }
}
