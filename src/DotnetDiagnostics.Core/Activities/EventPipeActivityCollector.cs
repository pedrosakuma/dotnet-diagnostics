using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DotnetDiagnostics.Core.Internal;
using DotnetDiagnostics.Core.Security;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.Activities;

/// <summary>
/// Captures <see cref="System.Diagnostics.ActivitySource"/> stop events through the
/// <c>Microsoft-Diagnostics-DiagnosticSource</c> EventPipe provider.
/// </summary>
public sealed partial class EventPipeActivityCollector : IActivityCollector
{
    private const string ProviderName = "Microsoft-Diagnostics-DiagnosticSource";
    private const long MessagesKeyword = 0x1;
    private const long EventsKeyword = 0x2;
    private const long ProviderKeywords = MessagesKeyword | EventsKeyword;
    private const string FilterArgumentName = "FilterAndPayloadSpecs";
    private const string TransformSuffix = ":-TraceId;SpanId;ParentSpanId;StartTimeTicks=StartTimeUtc.Ticks;DurationTicks=Duration.Ticks;ActivitySourceName=Source.Name;Tags=TagObjects.*Enumerate";
    internal const string HttpDestinationFilter = "HttpHandlerDiagnosticListener/System.Net.Http.HttpRequestOut.Start:-Request.RequestUri.Host;Request.RequestUri.Port;Request.RequestUri.Scheme;ActivityTraceId=*Activity.TraceId;ActivitySpanId=*Activity.SpanId";

    private static readonly BoundedWildcardRegexCache WildcardRegexCache = new();


    private readonly ILogger<EventPipeActivityCollector> _logger;
    private readonly SensitiveDataRedactor _redactor;
    internal Action? ActivityObserved { get; init; }

    public EventPipeActivityCollector(ILogger<EventPipeActivityCollector>? logger = null)
        : this(logger, new SensitiveDataRedactor())
    {
    }

    public EventPipeActivityCollector(ILogger<EventPipeActivityCollector>? logger, SensitiveDataRedactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        _logger = logger ?? NullLogger<EventPipeActivityCollector>.Instance;
        _redactor = redactor;
    }

    public Task<ActivityCapture> CollectAsync(
        int processId,
        TimeSpan duration,
        IReadOnlyList<string>? sources = null,
        int maxActivities = 200,
        CancellationToken cancellationToken = default)
        => CollectAsync(processId, duration, sources, maxActivities, null, 200, cancellationToken);

    public Task<ActivityCapture> CollectAsync(
        int processId,
        TimeSpan duration,
        IReadOnlyList<string>? sources,
        int maxActivities,
        string? traceId,
        int maxMatchedActivities,
        CancellationToken cancellationToken = default)
        => CollectAsync(processId, duration, sources, maxActivities, traceId, maxMatchedActivities, false, cancellationToken);

    public async Task<ActivityCapture> CollectAsync(
        int processId, TimeSpan duration, IReadOnlyList<string>? sources, int maxActivities,
        string? traceId, int maxMatchedActivities, bool includeHttpDestination,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        if (maxActivities < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxActivities), "maxActivities must be >= 1.");
        }

        var normalizedSourceFilters = NormalizeSourceFilters(sources);
        var providerArguments = BuildProviderArguments(normalizedSourceFilters, includeHttpDestination);
        var retention = new ActivityRetentionState(maxActivities, traceId, maxMatchedActivities);
        var destinations = includeHttpDestination && MatchesAnyFilter("System.Net.Http", normalizedSourceFilters)
            ? new HttpDestinationCorrelationState(retention.Retention.AppliedTraceId) : null;

        var processStartedAt = ProcessLifetime.TryReadStart(processId);
        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(
                [new EventPipeProvider(ProviderName, EventLevel.Verbose, ProviderKeywords, providerArguments)],
                requestRundown: false,
                circularBufferMB: 64,
                TimeSpan.FromSeconds(30),
                cancellationToken)
            .ConfigureAwait(false);

        var collectionStartedAt = DateTimeOffset.UtcNow;
        var observationEnd = collectionStartedAt;
        var completion = "normal-stop";
        long eventsLost = 0;
        await EventPipeCollectionRunner.RunAsync(session, duration,
            source =>
            {
                source.Dynamic.All += traceEvent =>
                {
                    if (!string.Equals(traceEvent.ProviderName, ProviderName, StringComparison.Ordinal)) return;
                    if (destinations is not null &&
                        FormatString(traceEvent.PayloadByName("SourceName")) == "HttpHandlerDiagnosticListener" &&
                        FormatString(traceEvent.PayloadByName("EventName")) == "System.Net.Http.HttpRequestOut.Start")
                    {
                        destinations.ObserveStart(DiagnosticSourcePayloadParser.ExtractArguments(traceEvent.PayloadByName("Arguments")));
                        return;
                    }
                    if (!IsActivityStopEvent(traceEvent.EventName) ||
                        !TryCreateActivity(traceEvent, normalizedSourceFilters, collectionStartedAt, out var activity))
                    {
                        return;
                    }

                    destinations?.ObserveStop(activity);
                    retention.Observe(activity);
                    ActivityObserved?.Invoke();
                };

            },
            ex =>
            {
                completion = "processing-failure";
                _logger.LogDebug(ex, "Activity EventPipe source ended for pid {Pid}.", processId);
            }, cancellationToken,
            (lost, early, streamStart) =>
            {
                eventsLost = lost;
                observationEnd = DateTimeOffset.UtcNow;
                if (early) completion = "early-exit";
                if (streamStart > DateTimeOffset.UnixEpoch && streamStart <= observationEnd)
                    collectionStartedAt = streamStart;
                else
                    completion = "missing-session-header";
            }).ConfigureAwait(false);

        var capturedActivities = retention.Activities
            .Select(activity => destinations?.Project(activity, eventsLost == 0 && completion == "normal-stop", _redactor) ?? activity)
            .OrderBy(activity => activity.StartedAt)
            .ThenBy(activity => activity.SourceName, StringComparer.Ordinal)
            .ThenBy(activity => activity.OperationName, StringComparer.Ordinal)
            .ToList();

        return new ActivityCapture(
            ProcessId: processId,
            SourceFilters: normalizedSourceFilters,
            StartedAt: collectionStartedAt,
            Duration: observationEnd > collectionStartedAt ? observationEnd - collectionStartedAt : TimeSpan.Zero,
            TotalActivities: retention.ObservedActivities,
            CompletedActivities: retention.ObservedActivities,
            Activities: capturedActivities,
            BySource: BuildSourceSummary(capturedActivities),
            ByOperation: BuildOperationSummary(capturedActivities),
            Retention: retention.Retention,
            ProcessStartedAt: processStartedAt)
        {
            Observation = new ActivityObservation(duration, completion, eventsLost),
            HttpDestinationCorrelation = destinations?.Snapshot(eventsLost == 0 && completion == "normal-stop",
                capturedActivities.Count(a => a.Destination?.Availability == "available")) ??
                (includeHttpDestination ? new HttpDestinationCorrelation("source-excluded",
                    HttpDestinationCorrelationState.MaxIdentities, HttpDestinationCorrelationState.MaxAuthorities,
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) : null),
        };
    }

    private static bool TryCreateActivity(
        TraceEvent traceEvent,
        List<string>? sourceFilters,
        DateTimeOffset collectionStartedAt,
        out CapturedActivity activity)
    {
        activity = default!;

        var sourceName = FirstNonEmpty(
            FormatString(traceEvent.PayloadByName("ActivitySourceName")),
            FormatString(traceEvent.PayloadByName("SourceName")));
        var operationName = FirstNonEmpty(
            FormatString(traceEvent.PayloadByName("ActivityName")),
            FormatString(traceEvent.PayloadByName("EventName")));

        if (string.IsNullOrWhiteSpace(sourceName) ||
            string.IsNullOrWhiteSpace(operationName) ||
            !MatchesAnyFilter(sourceName, sourceFilters))
        {
            return false;
        }

        var arguments = DiagnosticSourcePayloadParser.ExtractArguments(traceEvent.PayloadByName("Arguments"));
        var traceId = NullIfEmpty(GetArgument(arguments, "TraceId"));
        var spanId = NullIfEmpty(GetArgument(arguments, "SpanId"));
        var parentSpanId = NormalizeParentSpanId(GetArgument(arguments, "ParentSpanId"));
        var startedAt = ParseStartedAt(arguments) ?? traceEvent.TimeStamp.ToUniversalTime();
        var duration = ParseDuration(arguments);
        var stoppedAt = duration is { } capturedDuration
            ? startedAt + capturedDuration
            : new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime(), TimeSpan.Zero);
        var id = ComposeActivityId(traceId, spanId) ?? ComposeFallbackId(sourceName, operationName, startedAt);
        var parentId = ComposeActivityId(traceId, parentSpanId);

        activity = new CapturedActivity(
            SourceName: sourceName,
            OperationName: operationName,
            Id: id,
            ParentId: parentId,
            TraceId: traceId,
            SpanId: spanId,
            ParentSpanId: parentSpanId,
            StartedAt: new DateTimeOffset(startedAt, TimeSpan.Zero),
            StoppedAt: stoppedAt,
            Duration: duration,
            Tags: DiagnosticSourcePayloadParser.ParseBracketedTagPairs(GetArgument(arguments, "Tags")));
        return true;
    }

    private static List<string>? NormalizeSourceFilters(IReadOnlyList<string>? sources)
    {
        if (sources is null || sources.Count == 0)
        {
            return null;
        }

        var normalized = sources
            .Where(static source => !string.IsNullOrWhiteSpace(source))
            .Select(static source => source.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized.Count == 0 ? null : normalized;
    }

    internal static IDictionary<string, string> BuildProviderArguments(IReadOnlyList<string>? normalizedSourceFilters, bool includeHttpDestination = false)
    {
        var providerFilters = CanApplyProviderSideFilters(normalizedSourceFilters) ? normalizedSourceFilters! : ["*"];
        return new Dictionary<string, string>(1, StringComparer.Ordinal)
        {
            [FilterArgumentName] = BuildFilterSpec(providerFilters) +
                (includeHttpDestination && MatchesAnyFilter("System.Net.Http", normalizedSourceFilters)
                    ? "\n" + HttpDestinationFilter : string.Empty),
        };
    }

    private static bool CanApplyProviderSideFilters(IReadOnlyList<string>? normalizedSourceFilters) =>
        normalizedSourceFilters is { Count: > 0 } && normalizedSourceFilters.All(static filter => IsProviderSafeExactFilter(filter));

    private static bool IsProviderSafeExactFilter(string filter) =>
        filter == "*" || (filter.IndexOfAny(['*', '?', '+', '/', ':']) < 0);

    private static string BuildFilterSpec(IReadOnlyList<string> providerFilters) =>
        string.Join('\n', providerFilters.Select(static filter => $"[AS]{filter}/Stop{TransformSuffix}"));

    private static bool IsActivityStopEvent(string eventName) =>
        eventName.EndsWith("Stop", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan? ParseDuration(IReadOnlyDictionary<string, string> arguments)
    {
        var rawTicks = GetArgument(arguments, "DurationTicks");
        return long.TryParse(rawTicks, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) && ticks >= 0
            ? TimeSpan.FromTicks(ticks)
            : null;
    }

    private static DateTime? ParseStartedAt(IReadOnlyDictionary<string, string> arguments)
    {
        var rawTicks = GetArgument(arguments, "StartTimeTicks");
        if (!long.TryParse(rawTicks, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) || ticks <= 0)
        {
            return null;
        }

        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private static string? GetArgument(IReadOnlyDictionary<string, string> arguments, string key) =>
        DiagnosticSourcePayloadParser.GetArgument(arguments, key);

    private static string? NormalizeParentSpanId(string? value)
    {
        var normalized = NullIfEmpty(value);
        return string.IsNullOrEmpty(normalized) || normalized.All(static ch => ch == '0') ? null : normalized;
    }

    private static string? ComposeActivityId(string? traceId, string? spanId) =>
        string.IsNullOrWhiteSpace(traceId) || string.IsNullOrWhiteSpace(spanId)
            ? null
            : $"00-{traceId}-{spanId}-01";

    private static string ComposeFallbackId(string sourceName, string operationName, DateTime startedAt) =>
        $"{sourceName}:{operationName}:{startedAt.Ticks.ToString(CultureInfo.InvariantCulture)}";

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool MatchesAnyFilter(string sourceName, IReadOnlyList<string>? filters)
    {
        if (filters is null || filters.Count == 0)
        {
            return true;
        }

        return filters.Any(pattern => WildcardToRegex(pattern).IsMatch(sourceName));
    }

    private static string FormatString(object? value) => DiagnosticSourcePayloadParser.ConvertToString(value);

    private static List<ActivitySourceSummary> BuildSourceSummary(IReadOnlyList<CapturedActivity> activities) =>
        activities
            .GroupBy(activity => activity.SourceName, StringComparer.Ordinal)
            .Select(group =>
            {
                var (count, completedCount, averageMs, maxMs) = AggregateDurations(group);
                return new ActivitySourceSummary(
                    SourceName: group.Key,
                    Count: count,
                    CompletedCount: completedCount,
                    AverageDurationMs: averageMs,
                    MaxDurationMs: maxMs);
            })
            .OrderByDescending(summary => summary.Count)
            .ThenByDescending(summary => summary.MaxDurationMs)
            .ThenBy(summary => summary.SourceName, StringComparer.Ordinal)
            .ToList();

    private static List<ActivityOperationSummary> BuildOperationSummary(IReadOnlyList<CapturedActivity> activities) =>
        activities
            .GroupBy(activity => (activity.SourceName, activity.OperationName))
            .Select(group =>
            {
                var (count, completedCount, averageMs, maxMs) = AggregateDurations(group);
                return new ActivityOperationSummary(
                    SourceName: group.Key.SourceName,
                    OperationName: group.Key.OperationName,
                    Count: count,
                    CompletedCount: completedCount,
                    AverageDurationMs: averageMs,
                    MaxDurationMs: maxMs);
            })
            .OrderByDescending(summary => summary.Count)
            .ThenByDescending(summary => summary.MaxDurationMs)
            .ThenBy(summary => summary.SourceName, StringComparer.Ordinal)
            .ThenBy(summary => summary.OperationName, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Computes count, completed count, average duration (ms) and max duration (ms) for a group
    /// of activities in a single pass, avoiding the repeated LINQ enumerations and the
    /// intermediate <see cref="List{T}"/> that a per-metric approach would require.
    /// </summary>
    private static (int Count, int CompletedCount, double AverageDurationMs, double MaxDurationMs) AggregateDurations(
        IEnumerable<CapturedActivity> group)
    {
        var count = 0;
        var completedCount = 0;
        var sumMs = 0d;
        double? maxMs = null;

        foreach (var activity in group)
        {
            count++;
            if (activity.Duration is { } duration)
            {
                completedCount++;
                var durationMs = duration.TotalMilliseconds;
                sumMs += durationMs;
                if (maxMs is null || durationMs > maxMs)
                {
                    maxMs = durationMs;
                }
            }
        }

        var averageMs = completedCount == 0 ? 0 : sumMs / completedCount;
        return (count, completedCount, averageMs, maxMs ?? 0);
    }

    private static Regex WildcardToRegex(string pattern) => WildcardRegexCache.GetOrAdd(pattern, static wildcardPattern =>
    {
        var builder = new StringBuilder(wildcardPattern.Length * 2);
        builder.Append('^');
        foreach (var ch in wildcardPattern)
        {
            builder.Append(ch switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(ch.ToString()),
            });
        }

        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    });

    [GeneratedRegex("\\[(.*?)\\]", RegexOptions.CultureInvariant)]
    private static partial Regex TagPairRegex();
}
