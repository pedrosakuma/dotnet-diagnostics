using System.Globalization;

namespace DotnetDiagnostics.Core.Networking;

/// <summary>
/// Derived availability of the legacy duration scalars. Measured values (including zero) still
/// describe only accepted samples; correlation and capture quality independently qualify coverage.
/// </summary>
public static class NetworkingLatency
{
    public static IReadOnlyDictionary<string, string> Availability(
        NetworkingCorrelation? correlation, NetworkingCaptureQuality? quality) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["http"] = ForKind(correlation, quality, "http"),
            ["queue"] = ForKind(correlation, quality, "queue"),
            ["dns"] = ForKind(correlation, quality, "dns"),
            ["tls"] = ForKind(correlation, quality, "tls"),
        };

    private static string ForKind(NetworkingCorrelation? correlation, NetworkingCaptureQuality? quality, string kind)
    {
        if (correlation is null || !correlation.ByKind.TryGetValue(kind == "queue" ? "http" : kind, out var counts))
            return "unknown";

        var samples = kind == "queue" ? counts.QueueSamples : counts.LatencySamples;
        if (samples is null) return "unknown";
        if (samples > 0) return "measured";

        if (kind == "queue")
        {
            if (counts.QueueRejectedSamples is > 0) return "unavailable";
        }
        else
        {
            if (counts.EmptyStarts != 0 || counts.AmbiguousStarts != 0 || counts.Expired != 0
                || counts.Evicted != 0 || counts.CapacitySuppressedStarts != 0 || counts.IdentityCapacityReached
                || counts.UnmatchedStops != 0 || counts.EmptyStops != 0 || counts.InvalidTimestampStops != 0
                || counts.UnmatchedFailures != 0 || counts.InvalidTimestampFailures is > 0)
                return "uncorrelatable";
            if (counts.Unfinished != 0) return "incomplete";
        }

        if (quality is null) return "unknown";
        return quality.HasLimitations ? "incomplete" : "not-observed";
    }

    internal static string DescribeP95(string availability, TimeSpan value) =>
        availability == "measured"
            ? value.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) + "ms"
            : $"unavailable ({availability})";

    internal static string DescribeSampling(NetworkingCorrelation? correlation)
    {
        var http = GetCounts("http");
        var dns = GetCounts("dns");
        var tls = GetCounts("tls");
        return $"Percentile samples retained/accepted: HTTP {Ratio(http?.PercentileSamples, http?.LatencySamples)}, queue {Ratio(http?.QueuePercentileSamples, http?.QueueSamples)}, DNS {Ratio(dns?.PercentileSamples, dns?.LatencySamples)}, TLS {Ratio(tls?.PercentileSamples, tls?.LatencySamples)}. Fewer retained samples means reservoir approximation, independent of pairing/capture gaps; max uses all accepted samples.";

        NetworkingCorrelationCounts? GetCounts(string kind) =>
            correlation is not null && correlation.ByKind.TryGetValue(kind, out var counts) ? counts : null;
        static string Ratio(long? retained, long? accepted) =>
            $"{retained?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}/{accepted?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";
    }
}
