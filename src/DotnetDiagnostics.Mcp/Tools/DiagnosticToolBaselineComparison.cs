using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Investigation;
using DotnetDiagnostics.Core.Memory;

namespace DotnetDiagnostics.Mcp.Tools;

internal static class DiagnosticToolBaselineComparison
{
    public static DiagnosticResult<object> CompareToBaseline(
        ISummaryComparer comparer,
        IDiagnosticHandleStore handles,
        [Description("Baseline summary JSON (from a prior export_investigation_summary). Optional when snapshotsJson is supplied.")] string? baselineSummaryJson = null,
        [Description("Current summary JSON (from export_investigation_summary on the new investigation). Optional when snapshotsJson is supplied.")] string? currentSummaryJson = null,
        [Description("Ordered ComparableSnapshot JSON bodies to compare as a journey. JSON bodies only; do not pass file paths.")] string[]? snapshotsJson = null,
        [Description("ComparableSnapshot journey only: maximum metric series / key rows returned in compact inline payloads and used to bound key-matrix construction. Must be >= 1. Defaults to 25.")] int topN = 25,
        [Description("ComparableSnapshot journey only: inline verbosity. `full` returns the full matrix when it is below the inline threshold; `compact` returns verdict/headline/counts/notes plus top-N metric and key deltas. Large full diffs always return compact inline data plus a journey://diff/{handle} Resource link. Defaults to `full`.")] string depth = "full",
        [Description("ComparableSnapshot journey only: `trend` (default) compares ordered captures over time; `dispersion` compares unordered replicas for outliers.")] string? mode = null,
        [Description("Optional orchestrator investigation handle returned by attach_to_pod. When supplied, the orchestrator routes this diagnostic call through that attached Pod instead of inferring routing from the current MCP session binding.")]
        string? investigationHandleId = null)
    {
        if (!JourneyModeParser.TryParse(mode, out var journeyMode))
        {
            return InvalidArg<object>(nameof(mode), "must be either 'trend' or 'dispersion'");
        }

        if (snapshotsJson is { Length: > 0 })
        {
            return CompareSnapshotBodies(comparer, handles, snapshotsJson, topN, depth, journeyMode);
        }

        if (string.IsNullOrWhiteSpace(baselineSummaryJson)) return InvalidArg<object>(nameof(baselineSummaryJson), "is required when snapshotsJson is omitted");
        if (string.IsNullOrWhiteSpace(currentSummaryJson)) return InvalidArg<object>(nameof(currentSummaryJson), "is required when snapshotsJson is omitted");

        return CompareInvestigationSummaries(comparer, baselineSummaryJson, currentSummaryJson);
    }

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("inspect_process", "Re-issue with valid arguments. See tool schema for ranges and defaults."));

    private static DiagnosticResult<object> CompareSnapshotBodies(ISummaryComparer comparer, IDiagnosticHandleStore handles, string[] snapshotsJson, int topN, string depth, JourneyMode mode)
    {
        var schemas = new List<string>(snapshotsJson.Length);
        for (var i = 0; i < snapshotsJson.Length; i++)
        {
            var json = snapshotsJson[i];
            if (string.IsNullOrWhiteSpace(json))
            {
                return DiagnosticResult.Fail<object>(
                    "Snapshot JSON body is empty.",
                    new DiagnosticError("InvalidSnapshotJson", $"snapshotsJson[{i}] is empty.", $"snapshotsJson[{i}]"),
                    new NextActionHint("compare_to_baseline", "Pass persisted ComparableSnapshot JSON bodies, not file paths or empty strings."));
            }

            if (!TryReadSchema(json, out var schema, out var error))
            {
                return DiagnosticResult.Fail<object>(
                    "Could not read the Schema field from one of the supplied JSON documents.",
                    new DiagnosticError("InvalidSnapshotJson", error ?? "Schema field is missing or invalid.", $"snapshotsJson[{i}]"),
                    new NextActionHint("compare_to_baseline", "Re-supply JSON exported by the current server or CLI."));
            }

            schemas.Add(schema!);
        }

        if (topN < 1)
        {
            return InvalidArg<object>(nameof(topN), "must be >= 1 when snapshotsJson is supplied");
        }

        if (!JourneyDiffPresentation.TryParseDepth(depth, out var journeyDepth))
        {
            return InvalidArg<object>(nameof(depth), "must be either 'compact' or 'full' when snapshotsJson is supplied");
        }

        var distinctSchemas = schemas.Distinct(StringComparer.Ordinal).ToArray();
        if (distinctSchemas.Length > 1)
        {
            return DiagnosticResult.Fail<object>(
                "Mixed comparison schemas are not supported in one compare_to_baseline call.",
                new DiagnosticError("MixedSchemas", $"schemas='{string.Join(", ", distinctSchemas)}'"),
                new NextActionHint("compare_to_baseline", "Compare either InvestigationSummary documents or ComparableSnapshot documents, not both."));
        }

        return distinctSchemas[0] switch
        {
            InvestigationSummary.SchemaV1 => snapshotsJson.Length == 2
                ? CompareInvestigationSummaries(comparer, snapshotsJson[0], snapshotsJson[1])
                : DiagnosticResult.Fail<object>(
                    "InvestigationSummary comparison requires exactly two JSON documents.",
                    new DiagnosticError("InvalidArgument", $"Received {snapshotsJson.Length} InvestigationSummary documents.", nameof(snapshotsJson)),
                    new NextActionHint("compare_to_baseline", "Pass exactly two InvestigationSummary JSON documents, or pass 2..N ComparableSnapshot documents.")),
            ComparableSnapshot.SchemaV1 => snapshotsJson.Length >= 2
                ? CompareComparableSnapshots(handles, snapshotsJson, topN, journeyDepth, mode)
                : DiagnosticResult.Fail<object>(
                    "ComparableSnapshot comparison requires at least two JSON documents.",
                    new DiagnosticError("InvalidArgument", $"Received {snapshotsJson.Length} ComparableSnapshot document.", nameof(snapshotsJson)),
                    new NextActionHint("compare_to_baseline", "Pass 2..N ComparableSnapshot JSON documents for a journey comparison.")),
            _ => DiagnosticResult.Fail<object>(
                "Unsupported comparison schema.",
                new DiagnosticError("UnsupportedSchema", $"schema='{distinctSchemas[0]}'"),
                new NextActionHint("compare_to_baseline", "Re-export snapshots or summaries with the current server version.")),
        };
    }

    private static DiagnosticResult<object> CompareInvestigationSummaries(
        ISummaryComparer comparer,
        string baselineSummaryJson,
        string currentSummaryJson)
    {
        InvestigationSummary baseline, current;
        try
        {
            baseline = JsonSerializer.Deserialize(
                    baselineSummaryJson,
                    InvestigationSummaryJsonContext.Default.InvestigationSummary)
                ?? throw new InvalidOperationException("baseline summary deserialized to null");
            current = JsonSerializer.Deserialize(
                    currentSummaryJson,
                    InvestigationSummaryJsonContext.Default.InvestigationSummary)
                ?? throw new InvalidOperationException("current summary deserialized to null");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return DiagnosticResult.Fail<object>(
                "Could not parse one of the supplied summary JSON documents.",
                new DiagnosticError("InvalidSummaryJson", ex.Message),
                new NextActionHint("export_investigation_summary", "Re-export the baseline and current summaries and try again."));
        }

        if (!string.Equals(baseline.Schema, InvestigationSummary.SchemaV1, StringComparison.Ordinal) ||
            !string.Equals(current.Schema, InvestigationSummary.SchemaV1, StringComparison.Ordinal))
        {
            return DiagnosticResult.Fail<object>(
                $"Unsupported schema. Expected '{InvestigationSummary.SchemaV1}'.",
                new DiagnosticError("UnsupportedSchema", $"baseline='{baseline.Schema}' current='{current.Schema}'"),
                new NextActionHint("export_investigation_summary", "Re-export both summaries with the current server version."));
        }

        if (baseline.Provenance is null || baseline.Findings is null || baseline.Findings.TopHotspots is null ||
            current.Provenance is null || current.Findings is null || current.Findings.TopHotspots is null)
        {
            return DiagnosticResult.Fail<object>(
                "Summary JSON is missing required fields (Provenance / Findings / Findings.TopHotspots).",
                new DiagnosticError("InvalidSummaryJson", "Required fields are null after deserialization."),
                new NextActionHint("export_investigation_summary", "Re-export the summaries from a fresh investigation."));
        }

        var diff = comparer.Compare(baseline, current);
        var summaryLine = $"Verdict: {diff.Verdict}. {diff.NewHotspots.Count} new, " +
                          $"{diff.RemovedHotspots.Count} removed, {diff.ChangedHotspots.Count} changed hotspots. " +
                          $"Provenance: {diff.Provenance.Summary}.";

        return DiagnosticResult.Ok<object>(diff, summaryLine,
            new NextActionHint("collect_sample",
                diff.Verdict.StartsWith("regression", StringComparison.Ordinal)
                    ? "Re-sample the regressing process and drill into the new top frame."
                    : "Optional: capture a fresh sample to confirm the improvement is stable.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "cpu",
                    ["durationSeconds"] = 20,
                }));
    }

    private static DiagnosticResult<object> CompareComparableSnapshots(IDiagnosticHandleStore handles, string[] snapshotsJson, int topN, JourneyDiffDepth depth, JourneyMode mode)
    {
        var snapshots = new List<ComparableSnapshot>(snapshotsJson.Length);
        try
        {
            for (var i = 0; i < snapshotsJson.Length; i++)
            {
                var json = snapshotsJson[i];
                if (!TryValidateComparableSnapshotJson(json, i, out var detail, out var path))
                {
                    return DiagnosticResult.Fail<object>(
                        "ComparableSnapshot JSON is missing required fields.",
                        new DiagnosticError("InvalidSnapshotJson", detail ?? "Required JSON fields are absent before deserialization.", path),
                        new NextActionHint("compare_to_baseline", "Re-export snapshots from a current comparable-snapshot producer."));
                }

                var snapshot = JsonSerializer.Deserialize(
                        json,
                        ComparableSnapshotJsonContext.Default.ComparableSnapshot)
                    ?? throw new InvalidOperationException("snapshot deserialized to null");
                snapshots.Add(snapshot);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return DiagnosticResult.Fail<object>(
                "Could not parse one of the supplied comparable snapshot JSON documents.",
                new DiagnosticError("InvalidSnapshotJson", ex.Message),
                new NextActionHint("compare_to_baseline", "Re-export the comparable snapshots and try again."));
        }

        for (var i = 0; i < snapshots.Count; i++)
        {
            if (!TryValidateComparableSnapshot(snapshots[i], i, out var detail, out var path))
            {
                return DiagnosticResult.Fail<object>(
                    "ComparableSnapshot JSON is missing required fields.",
                    new DiagnosticError("InvalidSnapshotJson", detail ?? "Required fields are null or empty after deserialization.", path),
                    new NextActionHint("compare_to_baseline", "Re-export snapshots from a current comparable-snapshot producer."));
            }
        }

        var diff = SnapshotDiffer.Compare(snapshots, mode, topN: topN);
        var headline = diff.Pairwise?.Headline;
        var headlineText = headline is null
            ? "No pairwise headline."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{headline.Relation}: {headline.Verdict} ({diff.Labels[headline.FromIndex]} → {diff.Labels[headline.ToIndex]}).");
        var summaryLine = string.Create(
            CultureInfo.InvariantCulture,
            $"Verdict: {diff.Verdict}. {headlineText} Metrics: {diff.MetricSeries.Count}; rows: {diff.KeyMatrix.Count}.");

        return JourneyDiffPresentation.BuildResult(
            diff,
            handles,
            snapshots[^1].ProcessId,
            topN,
            depth,
            summaryLine,
            evictWhenProcessExits: false,
            HandleOrigin.Imported,
            new NextActionHint("compare_to_baseline", "Optional: compare another persisted snapshot journey with the same kind."));
    }

    private static bool TryValidateComparableSnapshotJson(
        string json,
        int snapshotIndex,
        out string? detail,
        out string? path)
    {
        detail = null;
        path = null;

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!RequireString(root, "Schema", $"snapshotsJson[{snapshotIndex}].Schema", out detail, out path) ||
            !RequireString(root, "Kind", $"snapshotsJson[{snapshotIndex}].Kind", out detail, out path) ||
            !RequireString(root, "Label", $"snapshotsJson[{snapshotIndex}].Label", out detail, out path) ||
            !RequireProperty(root, "CapturedAt", $"snapshotsJson[{snapshotIndex}].CapturedAt", out detail, out path) ||
            !RequireProperty(root, "ProcessId", $"snapshotsJson[{snapshotIndex}].ProcessId", out detail, out path) ||
            !RequireArray(root, "Metrics", $"snapshotsJson[{snapshotIndex}].Metrics", out var metrics, out detail, out path) ||
            !RequireArray(root, "Rows", $"snapshotsJson[{snapshotIndex}].Rows", out var rows, out detail, out path))
        {
            return false;
        }

        var metricIndex = 0;
        foreach (var metric in metrics.EnumerateArray())
        {
            if (!TryValidateMetricJson(metric, $"snapshotsJson[{snapshotIndex}].Metrics[{metricIndex}]", out detail, out path))
            {
                return false;
            }

            metricIndex++;
        }

        var rowIndex = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var rowPath = $"snapshotsJson[{snapshotIndex}].Rows[{rowIndex}]";
            if (row.ValueKind != JsonValueKind.Object)
            {
                detail = "Each row must be a JSON object.";
                path = rowPath;
                return false;
            }

            if (!RequireString(row, "DisplayName", $"{rowPath}.DisplayName", out detail, out path) ||
                !RequireObject(row, "Key", $"{rowPath}.Key", out var key, out detail, out path) ||
                !RequireString(key, "Kind", $"{rowPath}.Key.Kind", out detail, out path) ||
                !RequireString(key, "StableId", $"{rowPath}.Key.StableId", out detail, out path) ||
                !RequireArray(row, "Metrics", $"{rowPath}.Metrics", out var rowMetrics, out detail, out path))
            {
                return false;
            }

            var rowMetricIndex = 0;
            foreach (var metric in rowMetrics.EnumerateArray())
            {
                if (!TryValidateMetricJson(metric, $"{rowPath}.Metrics[{rowMetricIndex}]", out detail, out path))
                {
                    return false;
                }

                rowMetricIndex++;
            }

            rowIndex++;
        }

        return true;
    }

    private static bool TryValidateMetricJson(JsonElement metric, string metricPath, out string? detail, out string? path)
    {
        detail = null;
        path = null;
        if (metric.ValueKind != JsonValueKind.Object)
        {
            detail = "Each metric must be a JSON object.";
            path = metricPath;
            return false;
        }

        if (!RequireObject(metric, "Definition", $"{metricPath}.Definition", out var definition, out detail, out path) ||
            !RequireProperty(metric, "Value", $"{metricPath}.Value", out detail, out path) ||
            !RequireString(definition, "Name", $"{metricPath}.Definition.Name", out detail, out path) ||
            !RequireProperty(definition, "Role", $"{metricPath}.Definition.Role", out detail, out path) ||
            !RequireProperty(definition, "BetterDirection", $"{metricPath}.Definition.BetterDirection", out detail, out path) ||
            !RequireProperty(definition, "Aggregation", $"{metricPath}.Definition.Aggregation", out detail, out path))
        {
            return false;
        }

        return true;
    }

    private static bool RequireProperty(JsonElement parent, string propertyName, string propertyPath, out string? detail, out string? path)
    {
        detail = null;
        path = null;
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(propertyName, out _))
        {
            return true;
        }

        detail = $"Required property '{propertyName}' is missing.";
        path = propertyPath;
        return false;
    }

    private static bool RequireString(JsonElement parent, string propertyName, string propertyPath, out string? detail, out string? path)
    {
        detail = null;
        path = null;
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return true;
        }

        detail = $"Required property '{propertyName}' must be a non-empty string.";
        path = propertyPath;
        return false;
    }

    private static bool RequireObject(
        JsonElement parent,
        string propertyName,
        string propertyPath,
        out JsonElement value,
        out string? detail,
        out string? path)
    {
        detail = null;
        path = null;
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(propertyName, out value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        detail = $"Required property '{propertyName}' must be an object.";
        path = propertyPath;
        return false;
    }

    private static bool RequireArray(
        JsonElement parent,
        string propertyName,
        string propertyPath,
        out JsonElement value,
        out string? detail,
        out string? path)
    {
        detail = null;
        path = null;
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(propertyName, out value) &&
            value.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        value = default;
        detail = $"Required property '{propertyName}' must be an array.";
        path = propertyPath;
        return false;
    }

    private static bool TryValidateComparableSnapshot(
        ComparableSnapshot snapshot,
        int snapshotIndex,
        out string? detail,
        out string? path)
    {
        detail = null;
        path = null;

        if (string.IsNullOrWhiteSpace(snapshot.Schema) ||
            string.IsNullOrWhiteSpace(snapshot.Kind) ||
            string.IsNullOrWhiteSpace(snapshot.Label) ||
            snapshot.Metrics is null ||
            snapshot.Rows is null)
        {
            detail = "Schema, Kind, Label, Metrics and Rows are required.";
            path = $"snapshotsJson[{snapshotIndex}]";
            return false;
        }

        for (var metricIndex = 0; metricIndex < snapshot.Metrics.Count; metricIndex++)
        {
            var metric = snapshot.Metrics[metricIndex];
            if (metric is null || metric.Definition is null || string.IsNullOrWhiteSpace(metric.Definition.Name))
            {
                detail = "Each metric must include a definition with a non-empty name.";
                path = $"snapshotsJson[{snapshotIndex}].Metrics[{metricIndex}]";
                return false;
            }
        }

        for (var rowIndex = 0; rowIndex < snapshot.Rows.Count; rowIndex++)
        {
            var row = snapshot.Rows[rowIndex];
            if (row is null || row.Key is null || row.Metrics is null ||
                string.IsNullOrWhiteSpace(row.DisplayName) ||
                string.IsNullOrWhiteSpace(row.Key.Kind) ||
                string.IsNullOrWhiteSpace(row.Key.StableId))
            {
                detail = "Each row must include a key, display name and metrics list.";
                path = $"snapshotsJson[{snapshotIndex}].Rows[{rowIndex}]";
                return false;
            }

            for (var metricIndex = 0; metricIndex < row.Metrics.Count; metricIndex++)
            {
                var metric = row.Metrics[metricIndex];
                if (metric is null || metric.Definition is null || string.IsNullOrWhiteSpace(metric.Definition.Name))
                {
                    detail = "Each row metric must include a definition with a non-empty name.";
                    path = $"snapshotsJson[{snapshotIndex}].Rows[{rowIndex}].Metrics[{metricIndex}]";
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryReadSchema(string json, out string? schema, out string? error)
    {
        schema = null;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Root JSON value must be an object.";
                return false;
            }

            if (!document.RootElement.TryGetProperty("Schema", out var schemaElement) ||
                schemaElement.ValueKind != JsonValueKind.String)
            {
                error = "Schema field is missing or not a string.";
                return false;
            }

            schema = schemaElement.GetString();
            if (string.IsNullOrWhiteSpace(schema))
            {
                error = "Schema field is empty.";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
