using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDbgMcp.Core.Counters;

/// <summary>
/// Default <see cref="ICounterCollector"/> backed by an EventPipe session subscribed to
/// EventCounter providers (defaults to <c>System.Runtime</c> and <c>Microsoft.AspNetCore.Hosting</c>).
/// </summary>
public sealed class EventPipeCounterCollector : ICounterCollector
{
    private static readonly IReadOnlyList<string> DefaultProviders = new[]
    {
        "System.Runtime",
        "Microsoft.AspNetCore.Hosting",
        "Microsoft-AspNetCore-Server-Kestrel",
    };

    private readonly ILogger<EventPipeCounterCollector> _logger;

    public EventPipeCounterCollector(ILogger<EventPipeCounterCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeCounterCollector>.Instance;
    }

    public async Task<CounterSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        IReadOnlyList<string>? providers = null,
        int intervalSeconds = 1,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        if (intervalSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds), "Interval must be >= 1 second.");
        }

        var providerNames = providers is { Count: > 0 } ? providers : DefaultProviders;
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EventCounterIntervalSec"] = intervalSeconds.ToString(CultureInfo.InvariantCulture),
        };

        var eventPipeProviders = providerNames
            .Select(name => new EventPipeProvider(name, EventLevel.Verbose, (long)EventKeywords.All, arguments))
            .ToList();

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionAsync(eventPipeProviders, requestRundown: false, circularBufferMB: 128, cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var latest = new ConcurrentDictionary<string, CounterValue>(StringComparer.Ordinal);

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Dynamic.All += traceEvent =>
                {
                    if (!string.Equals(traceEvent.EventName, "EventCounters", StringComparison.Ordinal))
                    {
                        return;
                    }

                    var payload = ExtractPayload(traceEvent);
                    if (payload is null)
                    {
                        return;
                    }

                    var key = $"{traceEvent.ProviderName}/{payload.Name}";
                    latest[key] = payload with { Provider = traceEvent.ProviderName };
                };

                source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EventPipe source ended for pid {Pid}.", processId);
            }
        }, cancellationToken);

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // best-effort stop
            }

            try
            {
                await processingTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // already logged
            }

            session.Dispose();
        }

        var counters = latest.Values
            .OrderBy(c => c.Provider, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        return new CounterSnapshot(processId, startedAt, duration, counters);
    }

    private static CounterValue? ExtractPayload(TraceEvent traceEvent)
    {
        if (traceEvent.PayloadValue(0) is not IDictionary<string, object> outer)
        {
            return null;
        }

        if (!outer.TryGetValue("Payload", out var inner) || inner is not IDictionary<string, object> data)
        {
            return null;
        }

        var name = AsString(data, "Name");
        var display = AsString(data, "DisplayName");
        var unit = data.TryGetValue("DisplayUnits", out var u) ? u as string : null;

        double value;
        CounterKind kind;
        if (data.TryGetValue("Mean", out var meanObj))
        {
            value = ToDouble(meanObj);
            kind = CounterKind.Mean;
        }
        else if (data.TryGetValue("Increment", out var incObj))
        {
            value = ToDouble(incObj);
            kind = CounterKind.Sum;
        }
        else
        {
            return null;
        }

        return new CounterValue(
            Provider: traceEvent.ProviderName,
            Name: name,
            DisplayName: string.IsNullOrEmpty(display) ? name : display,
            Value: value,
            Unit: string.IsNullOrEmpty(unit) ? null : unit,
            Kind: kind);
    }

    private static string AsString(IDictionary<string, object> data, string key)
        => data.TryGetValue(key, out var v) && v is string s ? s : string.Empty;

    private static double ToDouble(object value) => value switch
    {
        double d => d,
        float f => f,
        long l => l,
        int i => i,
        _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
    };
}
