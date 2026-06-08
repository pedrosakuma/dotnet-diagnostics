using DotnetDiagnosticsMcp.Core;

namespace DotnetDiagnosticsMcp.Core.Gc;

/// <summary>
/// Renders parameterized views over a captured <see cref="GcDatasSnapshot"/> without re-collecting:
/// <c>overview</c> (default), <c>tuning</c> (heap-count timeline, optional changes-only),
/// <c>samples</c> (per-GC measurements) and <c>gen2</c> (full-GC backstop tuning).
/// </summary>
public static class GcDatasQueryDispatcher
{
    public const string OverviewView = "overview";
    public const string TuningView = "tuning";
    public const string SamplesView = "samples";
    public const string Gen2View = "gen2";
    public const int DefaultTopN = 50;

    private const double BytesPerMB = 1024.0 * 1024.0;

    private static readonly string[] Views = { OverviewView, TuningView, SamplesView, Gen2View };

    public static IReadOnlyList<string> SessionViews => Views;

    public static bool IsKnownView(string? view)
        => view is not null && Array.Exists(Views, v => string.Equals(v, view, StringComparison.Ordinal));

    public static DiagnosticResult<object> Render(
        GcDatasSnapshot snapshot,
        string handle,
        string? view,
        int topN,
        bool changesOnly = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (topN < 1) return InvalidArg<object>(nameof(topN), "must be >= 1");

        var effectiveView = string.IsNullOrWhiteSpace(view) ? OverviewView : view.Trim();
        return effectiveView.ToLowerInvariant() switch
        {
            "overview" => Box(RenderOverview(snapshot, handle)),
            "tuning" => Box(RenderTuning(snapshot, handle, topN, changesOnly)),
            "samples" => Box(RenderSamples(snapshot, handle, topN)),
            "gen2" => Box(RenderGen2(snapshot, handle, topN)),
            _ => DiagnosticResult.Fail<object>(
                $"Unknown DATAS view '{effectiveView}' for handle '{handle}'.",
                new DiagnosticError("InvalidArgument", $"Valid views: {string.Join(", ", Views)}.", effectiveView),
                new NextActionHint("query_snapshot", "Retry with a supported DATAS view.",
                    new Dictionary<string, object?> { ["handle"] = handle, ["view"] = OverviewView })),
        };
    }

    public static DiagnosticResult<DatasOverviewView> RenderOverview(GcDatasSnapshot snapshot, string handle)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var orderedTuning = OrderTuning(snapshot.TuningEvents);
        int? minHc = orderedTuning.Count == 0 ? null : orderedTuning.Min(t => t.NewHeapCount);
        int? maxHc = orderedTuning.Count == 0 ? null : orderedTuning.Max(t => t.NewHeapCount);

        var changes = 0;
        for (var i = 1; i < orderedTuning.Count; i++)
        {
            if (orderedTuning[i].NewHeapCount != orderedTuning[i - 1].NewHeapCount)
            {
                changes++;
            }
        }

        double? meanTcp = orderedTuning.Count == 0 ? null : orderedTuning.Average(t => (double)t.MedianThroughputCostPercent);
        double? maxTcp = orderedTuning.Count == 0 ? null : orderedTuning.Max(t => (double)t.MedianThroughputCostPercent);
        double? meanBudgetMB = snapshot.Samples.Count == 0 ? null : snapshot.Samples.Average(s => s.Gen0BudgetPerHeap) / BytesPerMB;
        double? meanSohMB = snapshot.Samples.Count == 0 ? null : snapshot.Samples.Average(s => (double)s.TotalSohStableSize) / BytesPerMB;

        var overview = new DatasOverviewView(
            snapshot.ProcessId,
            snapshot.Samples.Count,
            snapshot.TuningEvents.Count,
            snapshot.FullGcTuningEvents.Count,
            minHc,
            maxHc,
            changes,
            meanTcp,
            maxTcp,
            meanBudgetMB,
            meanSohMB,
            snapshot.ParseStats);

        var summary = minHc is null
            ? $"DATAS samples={snapshot.Samples.Count}, no heap-count tuning decisions observed."
            : $"Heap count {minHc}–{maxHc} with {changes} change(s); mean median TCP {meanTcp:F2}% over {snapshot.TuningEvents.Count} tuning event(s).";

        return DiagnosticResult.Ok(overview, summary,
            new NextActionHint("query_snapshot", "Inspect the heap-count tuning timeline (use changesOnly to see transitions only).",
                new Dictionary<string, object?> { ["handle"] = handle, ["view"] = TuningView, ["changesOnly"] = true }));
    }

    public static DiagnosticResult<DatasTuningView> RenderTuning(
        GcDatasSnapshot snapshot, string handle, int topN, bool changesOnly)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (topN < 1) return InvalidArg<DatasTuningView>(nameof(topN), "must be >= 1");

        var ordered = OrderTuning(snapshot.TuningEvents);
        var rows = new List<DatasTuningRow>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var t = ordered[i];
            int? prev = i == 0 ? null : ordered[i - 1].NewHeapCount;
            var changed = prev is not null && prev.Value != t.NewHeapCount;
            rows.Add(new DatasTuningRow(
                t.Timestamp, t.GcIndex, t.NewHeapCount, prev, changed,
                t.MedianThroughputCostPercent, t.NumGcsSinceLastChange));
        }

        if (changesOnly)
        {
            // Keep the first row as a baseline, then only rows where the heap count changed.
            rows = rows.Where((r, idx) => idx == 0 || r.Changed).ToList();
        }

        var returned = rows.Take(topN).ToList();
        var view = new DatasTuningView(snapshot.ProcessId, changesOnly, snapshot.TuningEvents.Count, returned.Count, returned);
        var summary = returned.Count == 0
            ? "No heap-count tuning events captured."
            : $"Showing {returned.Count} tuning row(s){(changesOnly ? " (changes only)" : string.Empty)} of {snapshot.TuningEvents.Count}.";

        return DiagnosticResult.Ok(view, summary,
            new NextActionHint("query_snapshot", "Inspect the per-GC DATAS samples behind these decisions.",
                new Dictionary<string, object?> { ["handle"] = handle, ["view"] = SamplesView }));
    }

    public static DiagnosticResult<DatasSamplesView> RenderSamples(GcDatasSnapshot snapshot, string handle, int topN)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (topN < 1) return InvalidArg<DatasSamplesView>(nameof(topN), "must be >= 1");

        var samples = snapshot.Samples
            .OrderBy(s => s.Timestamp).ThenBy(s => s.GcIndex)
            .Take(topN)
            .ToList();
        var view = new DatasSamplesView(snapshot.ProcessId, snapshot.Samples.Count, samples.Count, samples);
        var summary = samples.Count == 0
            ? "No DATAS samples captured."
            : $"Showing {samples.Count} per-GC DATAS sample(s) of {snapshot.Samples.Count}.";

        return DiagnosticResult.Ok(view, summary,
            new NextActionHint("query_snapshot", "Return to the DATAS overview.",
                new Dictionary<string, object?> { ["handle"] = handle, ["view"] = OverviewView }));
    }

    public static DiagnosticResult<DatasGen2View> RenderGen2(GcDatasSnapshot snapshot, string handle, int topN)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (topN < 1) return InvalidArg<DatasGen2View>(nameof(topN), "must be >= 1");

        var events = snapshot.FullGcTuningEvents
            .OrderBy(e => e.Timestamp).ThenBy(e => e.GcIndex)
            .Take(topN)
            .ToList();
        var view = new DatasGen2View(snapshot.ProcessId, snapshot.FullGcTuningEvents.Count, events.Count, events);
        var summary = events.Count == 0
            ? "No gen2 full-GC backstop tuning events captured."
            : $"Showing {events.Count} gen2 backstop tuning event(s) of {snapshot.FullGcTuningEvents.Count}.";

        return DiagnosticResult.Ok(view, summary,
            new NextActionHint("query_snapshot", "Return to the DATAS overview.",
                new Dictionary<string, object?> { ["handle"] = handle, ["view"] = OverviewView }));
    }

    private static List<DatasTuningEvent> OrderTuning(IReadOnlyList<DatasTuningEvent> events)
        => events.OrderBy(t => t.Timestamp).ThenBy(t => t.GcIndex).ToList();

    private static DiagnosticResult<object> Box<T>(DiagnosticResult<T> source) where T : class
        => new(source.Summary, source.Hints, source.Error)
        {
            Data = source.Data,
            Handle = source.Handle,
            HandleExpiresAt = source.HandleExpiresAt,
            ResolvedProcess = source.ResolvedProcess,
        };

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("query_snapshot", "Re-issue with valid arguments. See tool schema for ranges and defaults."));
}
