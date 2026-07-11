using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.Logs;

public sealed partial class EventPipeLogCollector : ILogCollector
{
    private const string ProviderName = "Microsoft-Extensions-Logging";
    private const long FormattedMessageKeyword = 0x4;
    private const long JsonMessageKeyword = 0x8;
    private const string TruncatedSuffix = "…[truncated]";

    private static readonly IReadOnlyDictionary<string, string> EmptyScopes = new Dictionary<string, string>(0, StringComparer.Ordinal);
    private static readonly Dictionary<string, Regex> WildcardRegexCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object WildcardRegexLock = new();

    private readonly SensitiveDataRedactor _redactor;
    private readonly ILogger<EventPipeLogCollector> _logger;

    public EventPipeLogCollector(SensitiveDataRedactor redactor, ILogger<EventPipeLogCollector>? logger = null)
    {
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _logger = logger ?? NullLogger<EventPipeLogCollector>.Instance;
    }

    public async Task<LogSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        IReadOnlyList<string>? categories = null,
        LogLevel minLevel = LogLevel.Information,
        int maxEvents = 500,
        int maxMessageBytes = 4096,
        bool includeJsonPayload = false,
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

        if (maxMessageBytes < Encoding.UTF8.GetByteCount(TruncatedSuffix) + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessageBytes), $"maxMessageBytes must be >= {Encoding.UTF8.GetByteCount(TruncatedSuffix) + 1}.");
        }

        if (minLevel is < LogLevel.Trace or > LogLevel.Critical)
        {
            throw new ArgumentOutOfRangeException(nameof(minLevel), "minLevel must be Trace..Critical.");
        }

        var normalizedCategories = NormalizeCategoryFilters(categories);
        var keywords = includeJsonPayload ? FormattedMessageKeyword | JsonMessageKeyword : FormattedMessageKeyword;
        var arguments = BuildProviderArguments(minLevel);
        var providers = new[]
        {
            new EventPipeProvider(ProviderName, ToEventLevel(minLevel), keywords, arguments),
        };

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(providers, requestRundown: false, circularBufferMB: 64, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var categoryCounts = new Dictionary<string, CategoryAccumulator>(StringComparer.Ordinal);
        var scopeFrames = new Dictionary<Guid, ScopeFrame>();
        var activeScopes = new List<IReadOnlyDictionary<string, string>>();
        var recent = new Queue<MutableLogEntry>(Math.Min(maxEvents, 256));
        MutableLogEntry? lastEntry = null;
        var totalEvents = 0L;
        var truncated = false;
        var levelCounts = new long[6];

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Dynamic.All += traceEvent =>
                {
                    if (!string.Equals(traceEvent.ProviderName, ProviderName, StringComparison.Ordinal))
                    {
                        return;
                    }

                    switch (traceEvent.EventName)
                    {
                        case "ActivityJsonStart":
                        case "ActivityJson/Start":
                            OnScopeStart(traceEvent, scopeFrames, activeScopes, maxMessageBytes);
                            return;
                        case "ActivityJsonStop":
                        case "ActivityJson/Stop":
                            OnScopeStop(traceEvent, scopeFrames, activeScopes);
                            return;
                        case "FormattedMessage":
                        {
                            if (!TryCreateLogEntry(traceEvent, normalizedCategories, minLevel, scopeFrames, activeScopes, maxMessageBytes, includeException: false, out var entry))
                            {
                                return;
                            }

                            totalEvents++;
                            levelCounts[(int)entry.Level]++;
                            AddCategory(categoryCounts, entry.Category, entry.Level);
                            AppendRecent(recent, entry, maxEvents, ref truncated);
                            lastEntry = entry;
                            return;
                        }
                        case "MessageJson" when includeJsonPayload:
                        {
                            if (!TryCreateLogEntry(traceEvent, normalizedCategories, minLevel, scopeFrames, activeScopes, maxMessageBytes, includeException: true, out var entry))
                            {
                                return;
                            }

                            if (lastEntry is not null && lastEntry.Matches(entry))
                            {
                                lastEntry.ExceptionType = entry.ExceptionType;
                                lastEntry.ExceptionMessage = entry.ExceptionMessage;
                                if (entry.Scopes.Count > 0)
                                {
                                    lastEntry.Scopes = entry.Scopes;
                                }

                                return;
                            }

                            totalEvents++;
                            levelCounts[(int)entry.Level]++;
                            AddCategory(categoryCounts, entry.Category, entry.Level);
                            AppendRecent(recent, entry, maxEvents, ref truncated);
                            lastEntry = entry;
                            return;
                        }
                    }
                };

                source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EventPipe ILogger source ended for pid {Pid}.", processId);
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

        var byCategory = categoryCounts.Values
            .Select(static acc => acc.ToRecord())
            .OrderByDescending(static group => group.Count)
            .ThenByDescending(static group => group.ErrorCount)
            .ThenBy(static group => group.Category, StringComparer.Ordinal)
            .ToList();

        var notes = BuildNotes(includeJsonPayload, normalizedCategories, truncated, maxEvents, maxMessageBytes);

        return new LogSnapshot(
            ProcessId: processId,
            CategoryFilters: normalizedCategories,
            MinimumLevel: minLevel.ToString(),
            StartedAt: startedAt,
            Duration: duration,
            TotalEvents: totalEvents,
            EventsByLevelTrace: levelCounts[(int)LogLevel.Trace],
            EventsByLevelDebug: levelCounts[(int)LogLevel.Debug],
            EventsByLevelInformation: levelCounts[(int)LogLevel.Information],
            EventsByLevelWarning: levelCounts[(int)LogLevel.Warning],
            EventsByLevelError: levelCounts[(int)LogLevel.Error],
            EventsByLevelCritical: levelCounts[(int)LogLevel.Critical],
            ByCategory: byCategory,
            Recent: recent.Select(static entry => entry.ToRecord()).ToList(),
            Truncated: truncated,
            Notes: notes);
    }

    private static IReadOnlyList<string> NormalizeCategoryFilters(IReadOnlyList<string>? categories)
    {
        if (categories is null || categories.Count == 0)
        {
            return Array.Empty<string>();
        }

        return categories
            .Where(static category => !string.IsNullOrWhiteSpace(category))
            .Select(static category => category.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, string>? BuildProviderArguments(LogLevel minLevel)
    {
        if (minLevel != LogLevel.Trace)
        {
            return null;
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FilterSpecs"] = "*:Trace",
        };
    }

    private static EventLevel ToEventLevel(LogLevel level) => level switch
    {
        LogLevel.Critical => EventLevel.Critical,
        LogLevel.Error => EventLevel.Error,
        LogLevel.Warning => EventLevel.Warning,
        LogLevel.Information => EventLevel.Informational,
        LogLevel.Debug or LogLevel.Trace => EventLevel.Verbose,
        _ => EventLevel.LogAlways,
    };

    private void OnScopeStart(TraceEvent traceEvent, Dictionary<Guid, ScopeFrame> scopeFrames, List<IReadOnlyDictionary<string, string>> activeScopes, int maxMessageBytes)
    {
        var scopeId = traceEvent.ActivityID;
        var scopePayload = FormatString(traceEvent.PayloadByName("ArgumentsJson"));
        var parsedScopes = ParseScopes(scopePayload, maxMessageBytes);
        if (scopeId != Guid.Empty)
        {
            scopeFrames[scopeId] = new ScopeFrame(scopeId, traceEvent.RelatedActivityID, parsedScopes);
        }
        if (parsedScopes.Count > 0)
        {
            activeScopes.Add(parsedScopes);
        }
    }

    private static void OnScopeStop(TraceEvent traceEvent, Dictionary<Guid, ScopeFrame> scopeFrames, List<IReadOnlyDictionary<string, string>> activeScopes)
    {
        var scopeId = traceEvent.ActivityID;
        if (scopeId != Guid.Empty)
        {
            scopeFrames.Remove(scopeId);
        }

        if (activeScopes.Count > 0)
        {
            activeScopes.RemoveAt(activeScopes.Count - 1);
        }
    }

    private bool TryCreateLogEntry(
        TraceEvent traceEvent,
        IReadOnlyList<string> categoryFilters,
        LogLevel minLevel,
        IReadOnlyDictionary<Guid, ScopeFrame> scopeFrames,
        IReadOnlyList<IReadOnlyDictionary<string, string>> activeScopes,
        int maxMessageBytes,
        bool includeException,
        out MutableLogEntry entry)
    {
        entry = default!;

        var category = FormatString(traceEvent.PayloadByName("LoggerName"));
        if (string.IsNullOrWhiteSpace(category) || !MatchesAnyFilter(category, categoryFilters))
        {
            return false;
        }

        var level = ParseLogLevel(traceEvent.PayloadByName("Level"));
        if (level is null || level < minLevel || level == LogLevel.None)
        {
            return false;
        }

        var eventId = ParseInt(traceEvent.PayloadByName("EventId"));
        var eventName = NullIfEmpty(FormatString(traceEvent.PayloadByName("EventName")));
        var message = TruncateAndRedact(FormatString(traceEvent.PayloadByName("FormattedMessage")), maxMessageBytes);
        var scopes = ResolveScopes(traceEvent.ActivityID, scopeFrames, activeScopes, maxMessageBytes);
        var exceptionType = (string?)null;
        var exceptionMessage = (string?)null;

        if (includeException)
        {
            var exceptionJson = FormatString(traceEvent.PayloadByName("ExceptionJson"));
            (exceptionType, exceptionMessage) = ParseException(exceptionJson, maxMessageBytes);
        }

        entry = new MutableLogEntry(
            timestamp: new DateTimeOffset(traceEvent.TimeStamp.ToUniversalTime(), TimeSpan.Zero),
            level: level.Value,
            category: category,
            eventId: eventId,
            eventName: eventName,
            message: message,
            exceptionType: exceptionType,
            exceptionMessage: exceptionMessage,
            scopes: scopes);
        return true;
    }

    private static void AddCategory(Dictionary<string, CategoryAccumulator> categoryCounts, string category, LogLevel level)
    {
        if (!categoryCounts.TryGetValue(category, out var accumulator))
        {
            accumulator = new CategoryAccumulator(category);
            categoryCounts[category] = accumulator;
        }

        accumulator.Count++;
        if (level >= LogLevel.Warning)
        {
            accumulator.WarningCount++;
        }
        if (level >= LogLevel.Error)
        {
            accumulator.ErrorCount++;
        }
    }

    private static void AppendRecent(Queue<MutableLogEntry> recent, MutableLogEntry entry, int maxEvents, ref bool truncated)
    {
        if (recent.Count >= maxEvents)
        {
            _ = recent.Dequeue();
            truncated = true;
        }

        recent.Enqueue(entry);
    }

    private IReadOnlyDictionary<string, string> ResolveScopes(Guid activityId, IReadOnlyDictionary<Guid, ScopeFrame> scopeFrames, IReadOnlyList<IReadOnlyDictionary<string, string>> activeScopes, int maxMessageBytes)
    {
        if (activityId == Guid.Empty || !scopeFrames.TryGetValue(activityId, out var frame))
        {
            return ResolveActiveScopes(activeScopes, maxMessageBytes);
        }

        var stack = new Stack<ScopeFrame>();
        var current = frame;
        while (true)
        {
            stack.Push(current);
            if (current.ParentId == Guid.Empty || !scopeFrames.TryGetValue(current.ParentId, out current!))
            {
                break;
            }
        }

        var scopes = new Dictionary<string, string>(StringComparer.Ordinal);
        while (stack.Count > 0)
        {
            foreach (var pair in stack.Pop().Scopes)
            {
                scopes[pair.Key] = TruncateAndRedact(pair.Value, maxMessageBytes);
            }
        }

        return scopes.Count == 0 ? ResolveActiveScopes(activeScopes, maxMessageBytes) : scopes;
    }

    private IReadOnlyDictionary<string, string> ResolveActiveScopes(IReadOnlyList<IReadOnlyDictionary<string, string>> activeScopes, int maxMessageBytes)
    {
        if (activeScopes.Count == 0)
        {
            return EmptyScopes;
        }

        var scopes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var frame in activeScopes)
        {
            foreach (var pair in frame)
            {
                scopes[pair.Key] = TruncateAndRedact(pair.Value, maxMessageBytes);
            }
        }

        return scopes.Count == 0 ? EmptyScopes : scopes;
    }

    private IReadOnlyDictionary<string, string> ParseScopes(string rawJson, int maxMessageBytes)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return EmptyScopes;
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var scopes = new Dictionary<string, string>(StringComparer.Ordinal);
            FlattenScopeElement(document.RootElement, scopes, maxMessageBytes);
            scopes.Remove("{OriginalFormat}");
            return scopes.Count == 0 ? EmptyScopes : scopes;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Scope"] = TruncateAndRedact(rawJson, maxMessageBytes),
            };
        }
    }

    private void FlattenScopeElement(JsonElement element, Dictionary<string, string> scopes, int maxMessageBytes)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    FlattenScopeElement(item, scopes, maxMessageBytes);
                }
                return;
            case JsonValueKind.Object:
                if (TryGetPropertyIgnoreCase(element, "Key", out var keyElement) && TryGetPropertyIgnoreCase(element, "Value", out var valueElement))
                {
                    var key = keyElement.ValueKind == JsonValueKind.String ? keyElement.GetString() : keyElement.ToString();
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        scopes[key] = TruncateAndRedact(FormatJsonValue(valueElement), maxMessageBytes);
                    }
                    return;
                }

                foreach (var property in element.EnumerateObject())
                {
                    scopes[property.Name] = TruncateAndRedact(FormatJsonValue(property.Value), maxMessageBytes);
                }
                return;
            case JsonValueKind.String:
                scopes["Scope"] = TruncateAndRedact(element.GetString() ?? string.Empty, maxMessageBytes);
                return;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return;
            default:
                scopes["Scope"] = TruncateAndRedact(FormatJsonValue(element), maxMessageBytes);
                return;
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private (string? ExceptionType, string? ExceptionMessage) ParseException(string rawJson, int maxMessageBytes)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            var exceptionType = FirstStringProperty(root, "ExceptionType", "TypeName", "Type");
            var exceptionMessage = FirstStringProperty(root, "ExceptionMessage", "Message");
            return (
                NullIfEmpty(TruncateAndRedact(exceptionType ?? string.Empty, maxMessageBytes)),
                NullIfEmpty(TruncateAndRedact(exceptionMessage ?? string.Empty, maxMessageBytes)));
        }
        catch (JsonException)
        {
            return (null, NullIfEmpty(TruncateAndRedact(rawJson, maxMessageBytes)));
        }
    }

    private static string? FirstStringProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var value))
            {
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => value.ToString(),
                };
            }
        }

        return null;
    }

    private string TruncateAndRedact(string value, int maxMessageBytes)
    {
        var redacted = _redactor.Redact(value) ?? string.Empty;
        var utf8 = Encoding.UTF8;
        if (utf8.GetByteCount(redacted) <= maxMessageBytes)
        {
            return redacted;
        }

        var allowedBytes = maxMessageBytes - utf8.GetByteCount(TruncatedSuffix);
        if (allowedBytes <= 0)
        {
            return TruncatedSuffix;
        }

        var builder = new StringBuilder(redacted.Length);
        var bytes = 0;
        foreach (var rune in redacted.EnumerateRunes())
        {
            var runeBytes = utf8.GetByteCount(rune.ToString());
            if (bytes + runeBytes > allowedBytes)
            {
                break;
            }

            builder.Append(rune.ToString());
            bytes += runeBytes;
        }

        builder.Append(TruncatedSuffix);
        return builder.ToString();
    }

    private static string FormatJsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => element.ToString(),
    };

    private static LogLevel? ParseLogLevel(object? payload)
    {
        if (payload is null)
        {
            return null;
        }

        if (payload is int intValue && Enum.IsDefined(typeof(LogLevel), intValue))
        {
            return (LogLevel)intValue;
        }

        if (payload is long longValue && longValue is >= 0 and <= 6)
        {
            return (LogLevel)longValue;
        }

        var formatted = FormatString(payload);
        if (int.TryParse(formatted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && Enum.IsDefined(typeof(LogLevel), parsed))
        {
            return (LogLevel)parsed;
        }

        return Enum.TryParse<LogLevel>(formatted, ignoreCase: true, out var named) ? named : null;
    }

    private static int ParseInt(object? payload)
    {
        if (payload is null)
        {
            return 0;
        }

        return payload switch
        {
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            _ when int.TryParse(FormatString(payload), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    private static string FormatString(object? payload) => payload switch
    {
        null => string.Empty,
        string s => s,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => payload.ToString() ?? string.Empty,
    };

    private static bool MatchesAnyFilter(string category, IReadOnlyList<string> filters)
    {
        if (filters.Count == 0)
        {
            return true;
        }

        foreach (var filter in filters)
        {
            if (GetWildcardRegex(filter).IsMatch(category))
            {
                return true;
            }
        }

        return false;
    }

    private static Regex GetWildcardRegex(string pattern)
    {
        lock (WildcardRegexLock)
        {
            if (!WildcardRegexCache.TryGetValue(pattern, out var regex))
            {
                var escaped = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
                regex = new Regex($"^{escaped}$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                WildcardRegexCache[pattern] = regex;
            }

            return regex;
        }
    }

    private static List<string> BuildNotes(bool includeJsonPayload, IReadOnlyList<string> categoryFilters, bool truncated, int maxEvents, int maxMessageBytes)
    {
        var notes = new List<string>();
        if (categoryFilters.Count > 0)
        {
            notes.Add($"Category filters: {string.Join(", ", categoryFilters)}.");
        }

        if (includeJsonPayload)
        {
            notes.Add("MessageJson payloads were enabled so exception details and scope JSON were captured.");
        }

        notes.Add($"Per-message payloads are truncated at {maxMessageBytes} UTF-8 bytes.");

        if (truncated)
        {
            notes.Add($"Recent ring buffer truncated older entries after maxEvents={maxEvents}.");
        }

        return notes;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class MutableLogEntry
    {
        public MutableLogEntry(DateTimeOffset timestamp, LogLevel level, string category, int eventId, string? eventName, string message, string? exceptionType, string? exceptionMessage, IReadOnlyDictionary<string, string> scopes)
        {
            Timestamp = timestamp;
            Level = level;
            Category = category;
            EventId = eventId;
            EventName = eventName;
            Message = message;
            ExceptionType = exceptionType;
            ExceptionMessage = exceptionMessage;
            Scopes = scopes;
        }

        public DateTimeOffset Timestamp { get; }
        public LogLevel Level { get; }
        public string Category { get; }
        public int EventId { get; }
        public string? EventName { get; }
        public string Message { get; }
        public string? ExceptionType { get; set; }
        public string? ExceptionMessage { get; set; }
        public IReadOnlyDictionary<string, string> Scopes { get; set; }

        public bool Matches(MutableLogEntry other) =>
            Level == other.Level &&
            EventId == other.EventId &&
            string.Equals(Category, other.Category, StringComparison.Ordinal) &&
            string.Equals(EventName, other.EventName, StringComparison.Ordinal) &&
            string.Equals(Message, other.Message, StringComparison.Ordinal);

        public LogEntry ToRecord() => new(
            Timestamp,
            Level.ToString(),
            Category,
            EventId,
            EventName,
            Message,
            ExceptionType,
            ExceptionMessage,
            Scopes.Count == 0 ? null : new Dictionary<string, string>(Scopes, StringComparer.Ordinal));
    }

    private sealed class CategoryAccumulator
    {
        public CategoryAccumulator(string category) => Category = category;
        public string Category { get; }
        public long Count { get; set; }
        public long ErrorCount { get; set; }
        public long WarningCount { get; set; }
        public LogCategoryGroup ToRecord() => new(Category, Count, ErrorCount, WarningCount);
    }

    private sealed record ScopeFrame(Guid Id, Guid ParentId, IReadOnlyDictionary<string, string> Scopes);
}
