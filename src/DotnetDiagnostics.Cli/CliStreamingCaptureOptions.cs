using System.Text.Json;

namespace DotnetDiagnostics.Cli;

internal static class CliStreamingCaptureOptions
{
    internal static CliOptions Parse(JsonElement root, string kind, int pid, int duration)
    {
        var top = kind == "gc-activities" ? 20 : 25;
        if (root.TryGetProperty("topN", out var element)
            && (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out top) || top is < 1 or > 100))
            throw new ArgumentException("'topN' must be an integer between 1 and 100.");

        string? symbols = null;
        if (root.TryGetProperty("symbolPath", out element))
        {
            if (kind != "off_cpu" || element.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(element.GetString()) || element.GetString()!.Length > 1024
                || element.GetString()!.Any(char.IsControl))
                throw new ArgumentException("'symbolPath' is only supported for off_cpu and must be a non-empty path of at most 1024 characters without control characters.");
            symbols = element.GetString()!.Trim();
        }
        var sources = new List<string>();
        if (root.TryGetProperty("sources", out element))
        {
            if (kind != "gc-activities" || element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 20)
                throw new ArgumentException("'sources' is only supported for gc-activities and must contain at most 20 ActivitySource filters.");
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())
                    || item.GetString()!.Length > 200 || item.GetString()!.Any(char.IsControl))
                    throw new ArgumentException("Each ActivitySource filter must be non-empty, at most 200 characters, and contain no control characters.");
                sources.Add(item.GetString()!.Trim());
            }
        }
        var threshold = 1000;
        if (root.TryGetProperty("thresholdMs", out element)
            && (kind != "requests" || element.ValueKind != JsonValueKind.Number
                || !element.TryGetInt32(out threshold) || threshold is < 0 or > 3_600_000))
            throw new ArgumentException("'thresholdMs' is only supported for requests and must be an integer between 0 and 3600000.");
        return new()
        {
            Command = "collect", Kind = kind, Pid = pid, DurationSeconds = duration, Top = top,
            Depth = "detail", SymbolPath = symbols, Sources = sources, Threshold = threshold,
            AcknowledgeRisk = root.TryGetProperty("acknowledgeRisk", out element) && element.ValueKind == JsonValueKind.String
                ? element.GetString() : null,
        };
    }
}
