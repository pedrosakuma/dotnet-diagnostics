using System.Text;

namespace DotnetDiagnostics.Core.Memory;

/// <summary>
/// Builds stable metric-series identities. Components use uppercase UTF-8 percent encoding;
/// meter tags are ordered by ordinal key and encode null as <c>n</c> and strings as
/// <c>s:&lt;escaped-value&gt;</c>.
/// </summary>
internal static class InvestigationMetricIdentity
{
    internal static string EventCounter(
        string provider,
        string name,
        Counters.CounterKind kind,
        string? statistic = null)
        => $"eventcounter|provider={Escape(provider)}|name={Escape(name)}|kind={kind.ToString().ToLowerInvariant()}" +
           (statistic is null ? string.Empty : $"|stat={statistic}");

    internal static string Meter(
        string meter,
        string instrument,
        string kind,
        IReadOnlyDictionary<string, string?> tags,
        string statistic)
        => $"meter|meter={Escape(meter)}|instrument={Escape(instrument)}|kind={Escape(kind)}" +
           $"|tags={CanonicalTags(tags)}|stat={statistic}";

    internal static string ComparableName(string identity)
    {
        if (identity.StartsWith("eventcounter|", StringComparison.Ordinal))
        {
            return TryReadComponent(identity, "name") ?? identity;
        }

        if (identity.StartsWith("meter|", StringComparison.Ordinal))
        {
            var instrument = TryReadComponent(identity, "instrument");
            var statistic = TryReadComponent(identity, "stat");
            return instrument is null
                ? identity
                : statistic is null or "last" or "rate"
                    ? instrument
                    : $"{instrument}.{statistic}";
        }

        return identity;
    }

    internal static bool IsCumulativeMeterLast(string identity)
    {
        if (!identity.StartsWith("meter|", StringComparison.Ordinal)
            || !string.Equals(TryReadComponent(identity, "stat"), "last", StringComparison.Ordinal))
        {
            return false;
        }

        var kind = TryReadComponent(identity, "kind");
        return kind?.EndsWith("Counter", StringComparison.OrdinalIgnoreCase) == true;
    }

    internal static bool IsEventCounterRawIncrement(string identity)
        => identity.StartsWith("eventcounter|", StringComparison.Ordinal)
            && string.Equals(TryReadComponent(identity, "kind"), "sum", StringComparison.Ordinal)
            && !string.Equals(TryReadComponent(identity, "stat"), "rate", StringComparison.Ordinal);

    internal static bool IsUnnormalizedEventCounterIncrement(string identity)
    {
        if (!IsEventCounterRawIncrement(identity))
        {
            return false;
        }

        var statistic = TryReadComponent(identity, "stat");
        return statistic is null or "unnormalized-increment";
    }

    internal static bool IsNormalizedRawEventCounterIncrement(string identity)
        => IsEventCounterRawIncrement(identity)
            && string.Equals(TryReadComponent(identity, "stat"), "increment", StringComparison.Ordinal);

    internal static bool IsCanonical(string identity)
        => identity.StartsWith("eventcounter|", StringComparison.Ordinal)
            || identity.StartsWith("meter|", StringComparison.Ordinal);

    private static string CanonicalTags(IReadOnlyDictionary<string, string?> tags)
        => "{" + string.Join(
            ",",
            tags.OrderBy(static tag => tag.Key, StringComparer.Ordinal)
                .Select(static tag =>
                    $"{Escape(tag.Key)}={(tag.Value is null ? "n" : $"s:{Escape(tag.Value)}")}")) + "}";

    private static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        var builder = new StringBuilder(bytes.Length);
        foreach (var valueByte in bytes)
        {
            var character = (char)valueByte;
            if ((character >= 'a' && character <= 'z') ||
                (character >= 'A' && character <= 'Z') ||
                (character >= '0' && character <= '9') ||
                character is '-' or '.' or '_' or '~')
            {
                builder.Append(character);
            }
            else
            {
                builder.Append('%')
                    .Append(valueByte.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static string? TryReadComponent(string identity, string name)
    {
        var prefix = $"{name}=";
        foreach (var component in identity.Split('|'))
        {
            if (component.StartsWith(prefix, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(component[prefix.Length..]);
            }
        }

        return null;
    }
}
