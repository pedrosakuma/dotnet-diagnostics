using System.Collections;
using System.Diagnostics.Tracing;
using System.Globalization;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.Requests;

/// <summary>
/// Tracks ASP.NET Core <c>HttpRequestIn</c> request start/stop pairs over a fixed EventPipe window
/// through the <c>Microsoft-Diagnostics-DiagnosticSource</c> provider, and reports the requests that
/// started but never stopped (still in-flight) ordered oldest-first.
/// <para>
/// Unlike <see cref="ProcessDiscovery.RequestsNowCollector"/> (which adds a ClrMD thread snapshot and
/// therefore needs <c>ptrace</c>), this collector is pure EventPipe — safe against hung production
/// processes. It subscribes to the legacy <c>Microsoft.AspNetCore</c> DiagnosticListener's
/// <c>Microsoft.AspNetCore.Hosting.HttpRequestIn</c> Start/Stop activity events and reads the request
/// path and verb directly off the <c>HttpContext</c> payload (the <c>TagObjects</c> of the
/// corresponding Activity are not yet populated at Start time, so they can't be used to name
/// in-flight requests).
/// </para>
/// </summary>
public sealed class EventPipeInFlightRequestCollector : IInFlightRequestCollector
{
    private const string ProviderName = "Microsoft-Diagnostics-DiagnosticSource";
    private const long MessagesKeyword = 0x1;
    private const long EventsKeyword = 0x2;
    private const long ProviderKeywords = MessagesKeyword | EventsKeyword;
    private const string FilterArgumentName = "FilterAndPayloadSpecs";

    // dotnet-monitor-style transform: read the request path/verb straight off the HttpContext payload
    // at the implicit Activity1 start/stop transform. The events surface as "Activity1/Start" and
    // "Activity1/Stop". This subscribes to a single DiagnosticListener event (not every ActivitySource),
    // so it stays cheap and prod-safe.
    private const string StartSpec =
        // Path/PathBase/Method only — Request.QueryString is deliberately NOT captured: query
        // strings routinely carry secrets/PII (access_token, code, id_token, email, session ids)
        // and this collector has no sensitive-value gate. Matches the path-only RequestsNowCollector.
        "Microsoft.AspNetCore/Microsoft.AspNetCore.Hosting.HttpRequestIn.Start@Activity1Start:-" +
        "Request.Method;Request.Path;Request.PathBase;" +
        "ActivityStartTime=*Activity.StartTimeUtc.Ticks;" +
        "ActivityId=*Activity.Id;ActivitySpanId=*Activity.SpanId;ActivityTraceId=*Activity.TraceId";

    private const string StopSpec =
        "Microsoft.AspNetCore/Microsoft.AspNetCore.Hosting.HttpRequestIn.Stop@Activity1Stop:-" +
        "ActivityId=*Activity.Id;ActivitySpanId=*Activity.SpanId;ActivityTraceId=*Activity.TraceId";

    private static readonly IReadOnlyDictionary<string, string> EmptyArguments = new Dictionary<string, string>(0, StringComparer.Ordinal);

    private readonly ILogger<EventPipeInFlightRequestCollector> _logger;

    public EventPipeInFlightRequestCollector(ILogger<EventPipeInFlightRequestCollector>? logger = null)
    {
        _logger = logger ?? NullLogger<EventPipeInFlightRequestCollector>.Instance;
    }

    public async Task<InFlightRequestSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        double longRunningThresholdMs = 1000,
        int maxRequests = 100,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "Process id must be positive.");
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        if (longRunningThresholdMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(longRunningThresholdMs), "longRunningThresholdMs must be >= 0.");
        }

        if (maxRequests < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRequests), "maxRequests must be >= 1.");
        }

        var client = new DiagnosticsClient(processId);
        var providerArguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FilterArgumentName] = StartSpec + "\n" + StopSpec,
        };

        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(
                [new EventPipeProvider(ProviderName, EventLevel.Verbose, ProviderKeywords, providerArguments)],
                requestRundown: false,
                circularBufferMB: 64,
                TimeSpan.FromSeconds(30),
                cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var notes = new HashSet<string>(StringComparer.Ordinal);

        long requestsStarted = 0, requestsCompleted = 0;
        var pending = new Dictionary<string, PendingRequest>(StringComparer.Ordinal);

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Dynamic.All += traceEvent =>
                {
                    if (!string.Equals(traceEvent.ProviderName, ProviderName, StringComparison.Ordinal) ||
                        !TryCreateEvent(traceEvent, out var requestEvent))
                    {
                        return;
                    }

                    try
                    {
                        if (requestEvent.IsStart)
                        {
                            requestsStarted++;
                            pending[requestEvent.Key] = requestEvent.PendingRequest!;
                        }
                        else if (pending.Remove(requestEvent.Key))
                        {
                            requestsCompleted++;
                        }
                    }
                    catch (Exception ex)
                    {
                        notes.Add($"Warning: failed to process {traceEvent.EventName}: {ex.GetType().Name}.");
                    }
                };

                source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "In-flight request EventPipe source ended for pid {Pid}.", processId);
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

        var capturedAt = DateTimeOffset.UtcNow;
        var inFlight = pending.Values
            .Select(request =>
            {
                var elapsedMs = Math.Max(0, (capturedAt - request.StartedAt).TotalMilliseconds);
                return new InFlightRequest(
                    TraceId: request.TraceId,
                    SpanId: request.SpanId,
                    Method: request.Method,
                    Path: request.Path,
                    StartedAt: request.StartedAt,
                    ElapsedMs: elapsedMs,
                    IsLongRunning: elapsedMs >= longRunningThresholdMs);
            })
            .OrderByDescending(static request => request.ElapsedMs)
            .ThenBy(static request => request.Path, StringComparer.Ordinal)
            .ToList();

        var longRunningCount = inFlight.Count(static request => request.IsLongRunning);
        var oldestElapsedMs = inFlight.Count > 0 ? inFlight[0].ElapsedMs : 0;
        var trimmed = inFlight.Take(maxRequests).ToList();

        if (requestsStarted == 0)
        {
            notes.Add("No ASP.NET Core requests started during the window. Confirm the target hosts an ASP.NET Core app and that traffic flows during collection — EventPipe sessions take ~500 ms–1 s to start, so begin collection before (or while) the load runs.");
        }
        else if (inFlight.Count == 0)
        {
            notes.Add("All requests that started during the window also completed within it — nothing is in-flight. If the app appears hung, lengthen the window or capture during the stall.");
        }

        if (inFlight.Count > trimmed.Count)
        {
            notes.Add($"Showing the {trimmed.Count} oldest of {inFlight.Count} in-flight requests (maxRequests={maxRequests}).");
        }

        return new InFlightRequestSnapshot(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            RequestsStarted: requestsStarted,
            RequestsCompleted: requestsCompleted,
            InFlightCount: inFlight.Count,
            LongRunningCount: longRunningCount,
            LongRunningThresholdMs: longRunningThresholdMs,
            OldestElapsedMs: oldestElapsedMs,
            Requests: trimmed,
            Notes: notes.OrderBy(static note => note, StringComparer.Ordinal).ToList());
    }

    private static bool TryCreateEvent(TraceEvent traceEvent, out RequestEvent requestEvent)
    {
        requestEvent = default;

        var eventName = traceEvent.EventName ?? string.Empty;
        var isStart = eventName.EndsWith("Start", StringComparison.OrdinalIgnoreCase);
        var isStop = eventName.EndsWith("Stop", StringComparison.OrdinalIgnoreCase);
        if (!isStart && !isStop)
        {
            return false;
        }

        var arguments = ExtractArguments(traceEvent.PayloadByName("Arguments"));
        if (arguments.Count == 0)
        {
            return false;
        }

        var traceId = GetArgument(arguments, "ActivityTraceId");
        var spanId = GetArgument(arguments, "ActivitySpanId");
        var activityId = GetArgument(arguments, "ActivityId");
        var key = FirstNonEmpty(activityId, ComposeActivityId(traceId, spanId));
        if (string.IsNullOrWhiteSpace(key))
        {
            // Without a correlatable id we cannot pair Start with Stop; skip.
            return false;
        }

        if (!isStart)
        {
            requestEvent = new RequestEvent(key, IsStart: false, PendingRequest: null);
            return true;
        }

        var startedAt = ParseStartedAt(arguments) ?? new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime(), TimeSpan.Zero);

        requestEvent = new RequestEvent(
            Key: key,
            IsStart: true,
            PendingRequest: new PendingRequest(
                TraceId: FirstNonEmpty(traceId, key),
                SpanId: string.IsNullOrWhiteSpace(spanId) ? null : spanId,
                Path: ResolvePath(arguments),
                Method: FirstNonEmpty(GetArgument(arguments, "Method"), "(unknown)"),
                StartedAt: startedAt));
        return true;
    }

    private static string ResolvePath(IReadOnlyDictionary<string, string> arguments)
    {
        var pathBase = GetArgument(arguments, "PathBase") ?? string.Empty;
        var path = GetArgument(arguments, "Path") ?? string.Empty;
        var composed = (pathBase + path).Trim();
        return composed.Length == 0 ? "(unknown)" : composed;
    }

    private static IReadOnlyDictionary<string, string> ExtractArguments(object? payload)
    {
        if (payload is null)
        {
            return EmptyArguments;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (payload is IEnumerable enumerable && payload is not string)
        {
            foreach (var item in enumerable)
            {
                if (item is null)
                {
                    continue;
                }

                if (TryGetKeyValue(item, out var key, out var value))
                {
                    result[key] = FormatString(value);
                }
            }
        }

        return result;
    }

    private static bool TryGetKeyValue(object item, out string key, out object? value)
    {
        if (item is IDictionary<string, object> dictionary)
        {
            key = dictionary.TryGetValue("Key", out var dictionaryKey) ? FormatString(dictionaryKey) : string.Empty;
            value = dictionary.TryGetValue("Value", out var dictionaryValue) ? dictionaryValue : null;
            return !string.IsNullOrWhiteSpace(key);
        }

        if (item is DictionaryEntry entry)
        {
            key = FormatString(entry.Key);
            value = entry.Value;
            return !string.IsNullOrWhiteSpace(key);
        }

        if (item is IDictionary nonGenericDictionary)
        {
            key = nonGenericDictionary.Contains("Key") ? FormatString(nonGenericDictionary["Key"]) : string.Empty;
            value = nonGenericDictionary.Contains("Value") ? nonGenericDictionary["Value"] : null;
            return !string.IsNullOrWhiteSpace(key);
        }

        var type = item.GetType();
        var keyProperty = type.GetProperty("Key");
        var valueProperty = type.GetProperty("Value");
        if (keyProperty is not null && valueProperty is not null)
        {
            key = FormatString(keyProperty.GetValue(item));
            value = valueProperty.GetValue(item);
            return !string.IsNullOrWhiteSpace(key);
        }

        key = string.Empty;
        value = null;
        return false;
    }

    private static DateTimeOffset? ParseStartedAt(IReadOnlyDictionary<string, string> arguments)
    {
        var raw = GetArgument(arguments, "ActivityStartTime");
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) && ticks > 0
            ? new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc))
            : null;
    }

    private static string? GetArgument(IReadOnlyDictionary<string, string> arguments, string key) =>
        arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static string? ComposeActivityId(string? traceId, string? spanId) =>
        !string.IsNullOrWhiteSpace(traceId) && !string.IsNullOrWhiteSpace(spanId)
            ? traceId + "/" + spanId
            : null;

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string FormatString(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private sealed record PendingRequest(
        string TraceId,
        string? SpanId,
        string Path,
        string Method,
        DateTimeOffset StartedAt);

    private readonly record struct RequestEvent(
        string Key,
        bool IsStart,
        PendingRequest? PendingRequest);
}
