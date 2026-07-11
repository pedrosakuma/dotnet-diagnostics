using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DotnetDiagnostics.Core.Internal;

internal static partial class DiagnosticSourcePayloadParser
{
    private static readonly IReadOnlyDictionary<string, string> EmptyArguments = new Dictionary<string, string>(0, StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> ExtractArguments(object? payload)
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
                    result[key] = value;
                }
            }
        }

        return result;
    }

    public static IReadOnlyDictionary<string, string> ParseBracketedTagPairs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return EmptyArguments;
        }

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in TagPairRegex().Matches(raw))
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

    public static string ConvertToString(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    public static string? GetArgument(IReadOnlyDictionary<string, string> arguments, string key)
        => arguments.TryGetValue(key, out var value) ? value : null;

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

    [GeneratedRegex(@"\[(.*?)\]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex TagPairRegex();
}
