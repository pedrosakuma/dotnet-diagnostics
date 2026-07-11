using System.Globalization;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.ThreadPool;

namespace DotnetDiagnostics.Cli;

internal static partial class CliCommands
{
    internal static string RenderJourneyDiff(SnapshotJourneyDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var sb = new StringBuilder();
        var first = diff.Labels.Count > 0 ? diff.Labels[0] : "first";
        var last = diff.Labels.Count > 0 ? diff.Labels[^1] : "last";
        sb.AppendLine(CultureInfo.InvariantCulture, $"compare: {diff.Kind} {diff.Mode} {first}→{last} verdict={diff.Verdict}");
        if (diff.Pairwise?.Headline is { } headline)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  headline: {headline.Relation} {headline.Verdict}");
        }

        AppendMetricDeltas(sb, diff.MetricSeries, diff.Mode, diff.Labels);
        AppendKeyDeltas(sb, diff.KeyMatrix, diff.Mode, diff.Labels);

        if (diff.Notes.Count > 0)
        {
            sb.AppendLine("  notes:");
            foreach (var note in diff.Notes.Take(3))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    - {note}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendMetricDeltas(StringBuilder sb, IReadOnlyList<MetricSeries> series, JourneyMode mode, IReadOnlyList<string> labels)
    {
        var rows = mode == JourneyMode.Dispersion
            ? series
                .Where(s => s.Dispersion is not null)
                .OrderByDescending(s => s.Dispersion!.CoefficientOfVariation)
                .ThenBy(s => s.Definition.Name, StringComparer.Ordinal)
                .Take(5)
                .ToArray()
            : series
                .Where(s => s.DeltaAbs.HasValue || s.DeltaPct.HasValue)
                .OrderByDescending(s => Math.Abs(s.DeltaPct ?? 0))
                .ThenBy(s => s.Definition.Name, StringComparer.Ordinal)
                .Take(5)
                .ToArray();
        if (rows.Length == 0)
        {
            return;
        }

        sb.AppendLine("  metrics:");
        foreach (var row in rows)
        {
            if (mode == JourneyMode.Dispersion && row.Dispersion is { } dispersion)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"    - {row.Definition.Name}: cv {FormatNumber(dispersion.CoefficientOfVariation)} outlier {LabelAt(labels, dispersion.OutlierIndex)} values [{FormatValues(row.Values)}]");
                continue;
            }

            var first = FirstValue(row.Values);
            var last = LastValue(row.Values);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"    - {row.Definition.Name}: {FormatNumber(first)} → {FormatNumber(last)} (Δ {FormatSigned(row.DeltaAbs)}, {FormatSignedPercent(row.DeltaPct)}, {row.Direction}, trend {row.Trend})");
        }
    }

    private static void AppendKeyDeltas(StringBuilder sb, IReadOnlyList<KeyMatrixRow> rows, JourneyMode mode, IReadOnlyList<string> labels)
    {
        var top = mode == JourneyMode.Dispersion
            ? rows
                .Where(r => r.Dispersion is not null)
                .OrderByDescending(r => r.Dispersion!.CoefficientOfVariation)
                .ThenBy(r => r.DisplayName, StringComparer.Ordinal)
                .Take(5)
                .Select(r => (Row: r, Stats: (Cv: r.Dispersion!.CoefficientOfVariation, r.Dispersion.OutlierIndex)))
                .ToArray()
            : rows
                .Where(r => r.DeltaAbs.HasValue || r.DeltaPct.HasValue)
                .OrderByDescending(r => Math.Abs(r.DeltaPct ?? 0))
                .ThenBy(r => r.DisplayName, StringComparer.Ordinal)
                .Take(5)
                .Select(r => (Row: r, Stats: (Cv: -1.0, OutlierIndex: -1)))
                .ToArray();
        if (top.Length == 0)
        {
            return;
        }

        sb.AppendLine("  keys:");
        foreach (var item in top)
        {
            var row = item.Row;
            if (mode == JourneyMode.Dispersion)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"    - {row.DisplayName}: cv {FormatNumber(item.Stats.Cv)} outlier {LabelAt(labels, item.Stats.OutlierIndex)} values [{FormatValues(row.Values)}]");
                continue;
            }

            sb.AppendLine(CultureInfo.InvariantCulture,
                $"    - {row.DisplayName}: {FormatNumber(FirstValue(row.Values))} → {FormatNumber(LastValue(row.Values))} (Δ {FormatSigned(row.DeltaAbs)}, {FormatSignedPercent(row.DeltaPct)}, {row.Direction})");
        }
    }

    private static string FormatValues(IReadOnlyList<double?> values)
        => string.Join(", ", values.Select(FormatNumber));

    private static string LabelAt(IReadOnlyList<string> labels, int index)
        => index < 0 ? "none" : index < labels.Count ? labels[index] : index.ToString(CultureInfo.InvariantCulture);

    private static double? FirstValue(IReadOnlyList<double?> values) => values.Count == 0 ? null : values[0];

    private static double? LastValue(IReadOnlyList<double?> values) => values.Count == 0 ? null : values[^1];

    private static string FormatNumber(double? value) => value?.ToString("G4", CultureInfo.InvariantCulture) ?? "n/a";

    private static string FormatSigned(double? value) => value.HasValue
        ? value.Value.ToString("+0.####;-0.####;0", CultureInfo.InvariantCulture)
        : "n/a";

    private static string FormatSignedPercent(double? value) => value.HasValue
        ? string.Concat(value.Value.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture), "%")
        : "n/a";

    internal static void RenderTopTypes(StringBuilder sb, IReadOnlyList<TypeStat> topByBytes)
    {
        if (topByBytes.Count == 0)
        {
            return;
        }

        // Types whose identity (mvid + metadata token) is known get a short numeric handle in the ID
        // column and a line in the identities block, so a human can copy the GUID straight into
        // `get-bytes --kind module --mvid <guid>` without dropping to --json (#301 #3).
        var identities = new List<(int Id, Guid Mvid, int? Token)>();
        var nextId = 1;

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"  {"BYTES%",-8} {"INSTANCES",-14} {"ID",-4} TYPE");
        foreach (var t in topByBytes)
        {
            var idColumn = string.Empty;
            if (t.Identity is { ModuleVersionId: { } mvid })
            {
                var id = nextId++;
                idColumn = id.ToString(CultureInfo.InvariantCulture);
                identities.Add((id, mvid, t.Identity.MetadataToken));
            }

            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {t.TotalBytesPercent,-8} {t.InstanceCount,-14:N0} {idColumn,-4} {t.TypeFullName}");
        }

        if (identities.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  identities (for get-bytes --kind module --mvid <guid>):");
            foreach (var (id, mvid, token) in identities)
            {
                var tokenText = token is { } tk ? string.Create(CultureInfo.InvariantCulture, $"0x{tk:X8}") : "(none)";
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {id}: mvid={mvid} token={tokenText}");
            }
        }
    }

    private static bool TryParseDumpType(string value, out ProcessDumpType dumpType) =>
        Enum.TryParse(value, ignoreCase: true, out dumpType) && Enum.IsDefined(dumpType);

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool TryParseDepth(string? value, out SamplingDepth depth)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            depth = SamplingDepth.Summary;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out depth) && Enum.IsDefined(depth);
    }

    private static string[]? NullIfEmptyArray(IReadOnlyList<string> values) =>
        values.Count == 0 ? null : values.ToArray();

    private static IReadOnlyList<string>? NullIfEmptyList(IReadOnlyList<string> values) =>
        values.Count == 0 ? null : values;

    internal static bool TrySaveComparableSnapshot(object artifact, string savePath, out ComparableSnapshot? snapshot, out string? error)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(savePath);

        snapshot = null;
        error = null;
        var projector = ComparableProjectors.FirstOrDefault(p => p.CanProject(artifact));
        if (projector is null)
        {
            error = $"kind '{InferComparableKind(artifact)}' is not yet comparable (--save supports: {SupportedComparableKinds})";
            return false;
        }

        var label = Path.GetFileNameWithoutExtension(savePath);
        if (string.IsNullOrWhiteSpace(label))
        {
            label = "capture";
        }

        snapshot = projector.Project(artifact, label);
        try
        {
            var fullPath = Path.GetFullPath(savePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = File.Create(fullPath);
            JsonSerializer.Serialize(stream, snapshot, ComparableSnapshotJsonContext.Default.ComparableSnapshot);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            error = $"failed to write comparable snapshot to '{savePath}': {ex.Message}";
            snapshot = null;
            return false;
        }
    }

    private static string InferComparableKind(object artifact) => artifact switch
    {
        CounterSnapshot => CollectionHandleKinds.Counters,
        GcDatasSnapshot => CollectionHandleKinds.GcDatas,
        ExceptionSnapshot => CollectionHandleKinds.ExceptionSnapshot,
        GcSummary => CollectionHandleKinds.GcEvents,
        EventSourceCapture => CollectionHandleKinds.EventSource,
        EventCatalogSnapshot => CollectionHandleKinds.EventCatalog,
        ActivityCapture => CollectionHandleKinds.Activities,
        LogSnapshot => CollectionHandleKinds.LogSnapshot,
        JitSnapshot => CollectionHandleKinds.JitSnapshot,
        ThreadPoolEventSnapshot => CollectionHandleKinds.ThreadPoolSnapshot,
        ContentionSnapshot => CollectionHandleKinds.ContentionSnapshot,
        DbSnapshot => CollectionHandleKinds.DbSnapshot,
        _ => artifact.GetType().Name,
    };

    private static CliCommandResult BuildResultWithComparableSave<T>(
        CliOptions options,
        DiagnosticResult<T> result,
        Action<StringBuilder, T> renderData)
    {
        if (result is { IsError: false, Data: { } data } && !string.IsNullOrWhiteSpace(options.SavePath))
        {
            if (!TrySaveComparableSnapshot(data, options.SavePath, out var saved, out var error))
            {
                var failure = DiagnosticResult.Fail<object>(
                    error!,
                    new DiagnosticError("NotSupported", "Choose a comparable collection kind and re-run collect with --save."));
                return BuildResult<object>(failure, static (_, _) => { });
            }

            var built = BuildResult(result, renderData);
            return built with
            {
                Human = string.Concat(
                    built.Human,
                    Environment.NewLine,
                    string.Create(CultureInfo.InvariantCulture, $"  saved comparable snapshot: {saved!.Label} -> {options.SavePath}")),
            };
        }

        return BuildResult(result, renderData);
    }

    /// <summary>
    /// Renders the host-neutral parts of any <see cref="DiagnosticResult{T}"/> (summary, error,
    /// resolved-process digest, next-action hints) plus a command-specific data block supplied by
    /// <paramref name="renderData"/> (skipped on error / null payload).
    /// </summary>
    private static CliCommandResult BuildResult<T>(DiagnosticResult<T> result, Action<StringBuilder, T> renderData)
    {
        // Project Core's MCP-audience hints into CLI vocabulary ONCE, before both the human table and
        // the --json envelope are produced, so neither leaks MCP tool names / call syntax (#301).
        var projected = CliHintProjection.Project(result);
        var human = RenderEnvelope(projected, renderData);
        return new CliCommandResult(projected.IsError, projected.Cancelled, projected, human)
        {
            Handle = projected.Handle,
            HandleExpiresAt = projected.HandleExpiresAt,
        };
    }

    private static string RenderEnvelope<T>(DiagnosticResult<T> result, Action<StringBuilder, T> renderData)
    {
        var sb = new StringBuilder();
        sb.Append(result.IsError ? "ERROR: " : string.Empty);
        sb.AppendLine(result.Summary);

        if (result.Error is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  kind   : {result.Error.Kind}");
            if (!string.IsNullOrWhiteSpace(result.Error.Detail))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  detail : {result.Error.Detail}");
            }
        }

        if (result.ResolvedProcess is { } ctx)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  target : pid {ctx.ProcessId}{(ctx.AutoResolved ? " (auto-resolved)" : string.Empty)}");
        }

        if (!result.IsError && result.Data is not null)
        {
            renderData(sb, result.Data);
        }

        if (result.Hints.Count > 0)
        {
            sb.AppendLine("  next:");
            foreach (var hint in result.Hints)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    - {hint.NextTool}: {hint.Reason}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string Trunc(string? value, int max)
    {
        value ??= string.Empty;
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }
}
