using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.EventSources;

/// <summary>
/// Broad EventPipe catalog collector. It records only event metadata (provider, name, level and
/// timestamp), never payload values, so it can safely sweep multiple providers without the targeted
/// event_source collector's allowlist/redaction gates.
/// </summary>
public sealed class EventPipeEventCatalogCollector : IEventCatalogCollector
{
    public static readonly IReadOnlyList<string> DefaultProviders = new[]
    {
        "Microsoft-Windows-DotNETRuntime",
        "System.Runtime",
        "Microsoft-Diagnostics-DiagnosticSource",
        "Microsoft-Extensions-Logging",
        "System.Threading.Tasks.TplEventSource",
    };

    private readonly ILogger<EventPipeEventCatalogCollector> _logger;

    public EventPipeEventCatalogCollector(ILogger<EventPipeEventCatalogCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeEventCatalogCollector>.Instance;
    }

    public async Task<EventCatalogSnapshot> CaptureAsync(
        int processId,
        TimeSpan duration,
        IReadOnlyList<string>? providers = null,
        int maxEvents = 200,
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

        var enabledProviders = NormalizeProviders(providers);
        var eventPipeProviders = enabledProviders
            .Select(p => new EventPipeProvider(p, EventLevel.Informational, keywords: -1))
            .ToArray();

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(eventPipeProviders, requestRundown: false, circularBufferMB: 64, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var counts = new ConcurrentDictionary<(string Provider, string EventName, string Level), long>();
        var sample = new ConcurrentQueue<CatalogEventOccurrence>();
        long total = 0;
        long sampled = 0;

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Dynamic.All += traceEvent =>
                {
                    var provider = traceEvent.ProviderName ?? string.Empty;
                    var eventName = string.IsNullOrWhiteSpace(traceEvent.EventName)
                        ? ((int)traceEvent.ID).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : traceEvent.EventName;
                    var level = traceEvent.Level.ToString();

                    Interlocked.Increment(ref total);
                    counts.AddOrUpdate((provider, eventName, level), 1, static (_, current) => current + 1);

                    // Metadata-only bounded sample. Do not read PayloadNames or PayloadByName here:
                    // arbitrary EventSource payload values may contain PII/auth context.
                    if (Interlocked.Increment(ref sampled) <= maxEvents)
                    {
                        sample.Enqueue(new CatalogEventOccurrence(
                            new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime(), TimeSpan.Zero),
                            provider,
                            eventName,
                            level));
                    }
                };

                source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EventPipe catalog source ended for pid {Pid}.", processId);
            }
        }, cancellationToken);

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { await session.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            try { await processingTask.ConfigureAwait(false); } catch (Exception) { }
            session.Dispose();
        }

        var catalog = counts
            .Select(kvp => new EventCatalogEntry(kvp.Key.Provider, kvp.Key.EventName, kvp.Key.Level, kvp.Value))
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Provider, StringComparer.Ordinal)
            .ThenBy(e => e.EventName, StringComparer.Ordinal)
            .ThenBy(e => e.Level, StringComparer.Ordinal)
            .ToList();

        return new EventCatalogSnapshot(
            processId,
            startedAt,
            duration,
            enabledProviders,
            Volatile.Read(ref total),
            catalog.Count,
            catalog,
            maxEvents,
            sample.ToList());
    }

    private static IReadOnlyList<string> NormalizeProviders(IReadOnlyList<string>? providers)
    {
        if (providers is null || providers.Count == 0)
        {
            return DefaultProviders;
        }

        var normalized = providers
            .SelectMany(p => p.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return normalized.Count == 0 ? DefaultProviders : normalized;
    }
}
