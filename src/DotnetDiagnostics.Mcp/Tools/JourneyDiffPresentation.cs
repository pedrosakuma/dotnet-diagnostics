using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Mcp.Resources;

namespace DotnetDiagnostics.Mcp.Tools;

/// <summary>Controls how much of a journey diff is returned inline.</summary>
public enum JourneyDiffDepth
{
    /// <summary>Verdict, headline, counts, notes, and top-N metric/key deltas only.</summary>
    Compact,

    /// <summary>The complete diff when it fits under the inline threshold; otherwise a compact summary plus Resource link.</summary>
    Full,
}

/// <summary>Count summary for a full <see cref="SnapshotJourneyDiff"/>.</summary>
public sealed record JourneyDiffCounts(
    int MetricSeries,
    int KeyRows,
    int PairwiseBaselineEach,
    int PairwiseAdjacent,
    int Notes,
    int SerializedBytes);

/// <summary>Compact inline view of a journey diff; the full matrix may be pulled from <see cref="ResourceUri"/>.</summary>
public sealed record JourneyDiffCompactSummary(
    string Kind,
    JourneyMode Mode,
    IReadOnlyList<string> Labels,
    string Verdict,
    string Headline,
    JourneyDiffCounts Counts,
    IReadOnlyList<MetricSeries> MetricSeries,
    IReadOnlyList<KeyMatrixRow> KeyMatrix,
    IReadOnlyList<string> Notes,
    string Depth,
    int TopN,
    string? ResourceUri,
    string? Handle,
    DateTimeOffset? HandleExpiresAt);

internal static class JourneyDiffPresentation
{
    public const string ResourceUriScheme = "journey://diff/";
    public const string HandleKind = "journey-diff";

    /// <summary>
    /// Full journey diffs above 32 KiB are replaced inline with a compact summary and a
    /// <c>journey://diff/{handle}</c> Resource link, keeping large matrices out of model context.
    /// </summary>
    public const int InlineThresholdBytes = 32 * 1024;

    private static readonly TimeSpan HandleTtl = TimeSpan.FromMinutes(10);

    public static bool TryParseDepth(string? value, out JourneyDiffDepth depth)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            depth = JourneyDiffDepth.Full;
            return true;
        }

        if (string.Equals(value, "compact", StringComparison.OrdinalIgnoreCase))
        {
            depth = JourneyDiffDepth.Compact;
            return true;
        }

        if (string.Equals(value, "full", StringComparison.OrdinalIgnoreCase))
        {
            depth = JourneyDiffDepth.Full;
            return true;
        }

        depth = JourneyDiffDepth.Full;
        return false;
    }

    public static DiagnosticResult<object> BuildResult(
        SnapshotJourneyDiff diff,
        IDiagnosticHandleStore handles,
        int processId,
        int topN,
        JourneyDiffDepth depth,
        string summaryLine,
        bool evictWhenProcessExits,
        HandleOrigin origin,
        params NextActionHint[] hints)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN);

        var serialized = JsonSerializer.SerializeToUtf8Bytes(diff, JourneyDiffResourceJsonContext.Default.SnapshotJourneyDiff);
        var shouldStore = serialized.Length > InlineThresholdBytes;
        DiagnosticHandle? handle = null;
        if (shouldStore)
        {
            handle = handles.Register(processId, HandleKind, diff, HandleTtl, evictWhenProcessExits, origin);
        }

        var data = depth == JourneyDiffDepth.Full && !shouldStore
            ? (object)diff
            : BuildCompactSummary(diff, topN, serialized.Length, depth, handle);

        var summary = shouldStore
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{summaryLine} Full matrix is {serialized.Length} bytes; inline payload is compact and the full diff is available at {ResourceUri(handle!.Id)} until {handle.ExpiresAt:O}.")
            : summaryLine;

        return shouldStore
            ? DiagnosticResult.OkWithHandle<object>(data, summary, handle!.Id, handle.ExpiresAt, hints)
            : DiagnosticResult.Ok<object>(data, summary, hints);
    }

    public static string ResourceUri(string handle) => ResourceUriScheme + handle;

    private static JourneyDiffCompactSummary BuildCompactSummary(
        SnapshotJourneyDiff diff,
        int topN,
        int serializedBytes,
        JourneyDiffDepth depth,
        DiagnosticHandle? handle)
    {
        var metricSeries = TopMetricSeries(diff.MetricSeries, topN, diff.Mode);
        var keyRows = TopKeyRows(diff.KeyMatrix, topN, diff.Mode);

        return new JourneyDiffCompactSummary(
            diff.Kind,
            diff.Mode,
            diff.Labels,
            diff.Verdict,
            BuildHeadline(diff),
            new JourneyDiffCounts(
                diff.MetricSeries.Count,
                diff.KeyMatrix.Count,
                diff.Pairwise?.BaselineEach.Count ?? 0,
                diff.Pairwise?.Adjacent.Count ?? 0,
                diff.Notes.Count,
                serializedBytes),
            metricSeries,
            keyRows,
            diff.Notes,
            depth == JourneyDiffDepth.Compact ? "compact" : "full",
            topN,
            handle is null ? null : ResourceUri(handle.Id),
            handle?.Id,
            handle?.ExpiresAt);
    }

    private static string BuildHeadline(SnapshotJourneyDiff diff)
    {
        var headline = diff.Pairwise?.Headline;
        if (headline is null)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{diff.Mode}: {diff.Verdict} across {diff.Labels.Count} captures");
        }

        var from = LabelAt(diff.Labels, headline.FromIndex);
        var to = LabelAt(diff.Labels, headline.ToIndex);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{headline.Relation}: {headline.Verdict} ({from} → {to})");
    }

    private static string LabelAt(IReadOnlyList<string> labels, int index)
        => index >= 0 && index < labels.Count ? labels[index] : index.ToString(CultureInfo.InvariantCulture);

    private static MetricSeries[] TopMetricSeries(IReadOnlyList<MetricSeries> series, int topN, JourneyMode mode)
    {
        if (mode == JourneyMode.Dispersion)
        {
            return series
                .OrderByDescending(s => s.Dispersion?.CoefficientOfVariation ?? -1)
                .ThenBy(s => s.Definition.Name, StringComparer.Ordinal)
                .Take(topN)
                .ToArray();
        }

        return series
            .OrderByDescending(s => AbsOrMinusOne(s.DeltaPct))
            .ThenByDescending(s => AbsOrMinusOne(s.DeltaAbs))
            .ThenBy(s => s.Definition.Name, StringComparer.Ordinal)
            .Take(topN)
            .ToArray();
    }

    private static KeyMatrixRow[] TopKeyRows(IReadOnlyList<KeyMatrixRow> rows, int topN, JourneyMode mode)
    {
        if (mode == JourneyMode.Dispersion)
        {
            return rows
                .OrderByDescending(r => CoefficientOfVariation(r.Values))
                .ThenBy(r => r.DisplayName, StringComparer.Ordinal)
                .Take(topN)
                .ToArray();
        }

        return rows
            .OrderByDescending(r => AbsOrMinusOne(r.DeltaPct))
            .ThenByDescending(r => AbsOrMinusOne(r.DeltaAbs))
            .ThenBy(r => r.DisplayName, StringComparer.Ordinal)
            .Take(topN)
            .ToArray();
    }

    private static double AbsOrMinusOne(double? value) => value.HasValue ? Math.Abs(value.Value) : -1;

    private static double CoefficientOfVariation(IReadOnlyList<double?> values)
    {
        var observed = values
            .Where(static v => v.HasValue)
            .Select(static v => v!.Value)
            .ToArray();
        if (observed.Length < 2)
        {
            return -1;
        }

        var mean = observed.Average();
        var variance = observed.Select(v => Math.Pow(v - mean, 2)).Average();
        var stdDev = Math.Sqrt(variance);
        var denominator = Math.Abs(mean);
        if (denominator > 0)
        {
            return stdDev / denominator;
        }

        return stdDev == 0 ? 0 : double.PositiveInfinity;
    }
}
