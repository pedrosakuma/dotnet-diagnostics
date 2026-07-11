using System.Collections;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing;

namespace DotnetDiagnostics.Core.Db;

internal static class DbEventPipeParsing
{
    private static readonly Regex s_tagPairRegex = new(@"\[(.*?)\]", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    public static DbCounterPayload? ExtractCounterPayload(TraceEvent traceEvent)
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
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (data.TryGetValue("Mean", out var meanObj))
        {
            return new DbCounterPayload(name, ToDouble(meanObj));
        }

        if (data.TryGetValue("Increment", out var incrementObj))
        {
            return new DbCounterPayload(name, ToDouble(incrementObj));
        }

        return null;
    }

    public static string BuildProviderObjectKey(string providerName, int objectId)
        => string.Create(CultureInfo.InvariantCulture, $"{providerName}/{objectId}");

    public static string BuildAggregateKey(string commandText, string connectionString)
        => string.Create(CultureInfo.InvariantCulture, $"{HashCommandText(commandText)}\u001f{connectionString}");

    public static string BuildScopeId(string? traceId, string? parentSpanId, Guid relatedActivityId, Guid activityId)
    {
        if (!string.IsNullOrWhiteSpace(traceId) && !string.IsNullOrWhiteSpace(parentSpanId))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{traceId}:{parentSpanId}");
        }

        if (!string.IsNullOrWhiteSpace(traceId))
        {
            return traceId!;
        }

        if (relatedActivityId != Guid.Empty)
        {
            return relatedActivityId.ToString("N");
        }

        return activityId != Guid.Empty
            ? activityId.ToString("N")
            : "global";
    }

    public static string BuildConnectionString(string? dataSource, string? database)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(dataSource))
        {
            parts.Add($"Data Source={dataSource}");
        }

        if (!string.IsNullOrWhiteSpace(database))
        {
            parts.Add($"Database={database}");
        }

        return parts.Count == 0 ? string.Empty : string.Join(';', parts);
    }

    public static string HashCommandText(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    public static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    public static IReadOnlyDictionary<string, string> ExtractArguments(object? payload)
    {
        if (payload is null)
        {
            return EmptyArguments.Instance;
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
                    result[key] = value;
                }
            }
        }

        return result;
    }

    public static string ConvertToString(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    public static string? GetArgument(IReadOnlyDictionary<string, string> arguments, string key)
        => arguments.TryGetValue(key, out var value) ? value : null;

    public static string? GetTag(IReadOnlyDictionary<string, string> tags, string key)
        => tags.TryGetValue(key, out var value) ? value : null;

    public static IReadOnlyDictionary<string, string> ParseTagPairs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return EmptyArguments.Instance;
        }

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in s_tagPairRegex.Matches(raw))
        {
            var content = match.Groups[1].Value;
            var separator = content.IndexOf(", ", StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var key = content[..separator];
            var value = content[(separator + 2)..];
            if (!string.IsNullOrWhiteSpace(key))
            {
                tags[key] = value;
            }
        }

        return tags;
    }

    public static TimeSpan? ParseDuration(IReadOnlyDictionary<string, string> arguments)
    {
        var rawTicks = GetArgument(arguments, "DurationTicks");
        return long.TryParse(rawTicks, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) && ticks >= 0
            ? TimeSpan.FromTicks(ticks)
            : null;
    }

    public static DateTimeOffset? ParseStartedAt(IReadOnlyDictionary<string, string> arguments)
    {
        var rawTicks = GetArgument(arguments, "StartTimeTicks");
        return long.TryParse(rawTicks, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) && ticks > 0
            ? new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc), TimeSpan.Zero)
            : null;
    }

    public static string PayloadString(TraceEvent traceEvent, int index)
        => traceEvent.PayloadValue(index)?.ToString() ?? string.Empty;

    public static int PayloadInt32(TraceEvent traceEvent, int index)
        => traceEvent.PayloadValue(index) switch
        {
            int i => i,
            long l => checked((int)l),
            null => 0,
            var value => Convert.ToInt32(value, CultureInfo.InvariantCulture),
        };

    public static string ExtractFreeformMessage(TraceEvent traceEvent)
    {
        foreach (var payloadName in traceEvent.PayloadNames ?? Array.Empty<string>())
        {
            var value = traceEvent.PayloadByName(payloadName);
            if (value is string s && !string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
        }

        return string.Empty;
    }

    public static bool LooksLikePoolExhausted(string message) =>
        (message.Contains("connection from the pool", StringComparison.OrdinalIgnoreCase)
            && message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        || message.Contains("pool exhausted", StringComparison.OrdinalIgnoreCase)
        || message.Contains("max pool size", StringComparison.OrdinalIgnoreCase);

    private static string AsString(IDictionary<string, object> data, string key)
        => data.TryGetValue(key, out var value) && value is string s ? s : string.Empty;

    private static bool TryGetKeyValue(object item, out string key, out string value)
    {
        switch (item)
        {
            case IDictionary<string, object> dictionary:
                key = dictionary.TryGetValue("Key", out var dictionaryKey) ? ConvertToString(dictionaryKey) : string.Empty;
                value = dictionary.TryGetValue("Value", out var dictionaryValue) ? ConvertToString(dictionaryValue) : string.Empty;
                return !string.IsNullOrWhiteSpace(key);
            case DictionaryEntry entry:
                key = ConvertToString(entry.Key);
                value = ConvertToString(entry.Value);
                return !string.IsNullOrWhiteSpace(key);
            case IDictionary nonGenericDictionary:
                key = nonGenericDictionary.Contains("Key") ? ConvertToString(nonGenericDictionary["Key"]) : string.Empty;
                value = nonGenericDictionary.Contains("Value") ? ConvertToString(nonGenericDictionary["Value"]) : string.Empty;
                return !string.IsNullOrWhiteSpace(key);
            default:
                var type = item.GetType();
                var keyProperty = type.GetProperty("Key");
                var valueProperty = type.GetProperty("Value");
                if (keyProperty is not null && valueProperty is not null)
                {
                    key = ConvertToString(keyProperty.GetValue(item));
                    value = ConvertToString(valueProperty.GetValue(item));
                    return !string.IsNullOrWhiteSpace(key);
                }

                key = string.Empty;
                value = string.Empty;
                return false;
        }
    }

    private static double ToDouble(object value) => value switch
    {
        double d => d,
        float f => f,
        long l => l,
        int i => i,
        _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
    };
}

internal sealed record DbCounterPayload(string Name, double Value);

internal sealed class EmptyArguments : Dictionary<string, string>
{
    public static EmptyArguments Instance { get; } = new();
}
