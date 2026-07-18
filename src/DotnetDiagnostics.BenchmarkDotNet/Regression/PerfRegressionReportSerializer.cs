using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.BenchmarkDotNet.Regression;

/// <summary>Persists clean measurement runs and regression reports as stable CI artifacts.</summary>
public static class PerfRegressionReportSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string SerializeRun(PerfMeasurementRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return JsonSerializer.Serialize(run, JsonOptions);
    }

    public static PerfMeasurementRun DeserializeRun(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<PerfMeasurementRun>(json, JsonOptions)
            ?? throw new JsonException("Measurement run JSON was empty.");
    }

    public static string SerializeDiagnosticRun(PerfDiagnosticRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return JsonSerializer.Serialize(run, JsonOptions);
    }

    public static PerfDiagnosticRun DeserializeDiagnosticRun(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<PerfDiagnosticRun>(json, JsonOptions)
            ?? throw new JsonException("Diagnostic run JSON was empty.");
    }

    public static string SerializeReport(PerfRegressionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public static string BuildMarkdown(PerfRegressionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("# CI performance regression report");
        sb.AppendLine();
        sb.Append("- Verdict: **").Append(report.Verdict).AppendLine("**");
        sb.Append("- Environment compatible: **").Append(report.Compatibility.Compatible ? "yes" : "no").AppendLine("**");
        sb.Append("- Gate recommendation: **").Append(report.Recommendation).AppendLine("**");
        sb.Append("- Eligible for gate: **").Append(report.EligibleForGate ? "yes" : "no").AppendLine("**");
        sb.Append("- Unchanged-control false positives: **").Append(report.FalsePositiveCount).AppendLine("**");
        sb.AppendLine();

        if (!report.Compatibility.Compatible)
        {
            sb.AppendLine("## Compatibility failures");
            sb.AppendLine();
            foreach (var mismatch in report.Compatibility.Mismatches)
            {
                sb.Append("- ").AppendLine(mismatch);
            }
            return sb.ToString();
        }

        sb.AppendLine("## Measurements");
        sb.AppendLine();
        sb.AppendLine("| scenario | metric | baseline median | candidate median | delta | CV baseline/candidate | agreement | verdict |");
        sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (var scenario in report.Scenarios)
        {
            AppendMetric(sb, scenario, scenario.Timing);
            AppendMetric(sb, scenario, scenario.Allocation);
        }

        sb.AppendLine();
        sb.AppendLine("## Diagnostic attribution");
        sb.AppendLine();
        sb.AppendLine("> Diagnostic-run timing is intentionally excluded from the measurement verdict.");
        sb.AppendLine();
        foreach (var scenario in report.Scenarios.Where(static scenario => !scenario.IsControl))
        {
            sb.Append("### ").AppendLine(scenario.Scenario);
            sb.AppendLine();
            if (scenario.Attribution.Count == 0)
            {
                sb.AppendLine("_No separate diagnostic attribution was recorded._");
                sb.AppendLine();
                continue;
            }
            foreach (var row in scenario.Attribution)
            {
                sb.Append("- `").Append(row.Kind).Append("`: ")
                    .Append(row.IsControl ? "control " : string.Empty)
                    .Append(row.Matched && !row.IsError ? "matched" : "not matched")
                    .Append(" — ").Append(EscapeInline(row.Headline))
                    .Append(" (`").Append(Path.GetFileName(row.ArtifactPath)).AppendLine("`)");
                foreach (var signal in row.Signals ?? Array.Empty<PerfDiagnosticSignal>())
                {
                    sb.Append("  - `").Append(signal.Name).Append('`')
                        .Append(signal.StableId is null ? string.Empty : $" [{signal.StableId}]")
                        .Append(": ").Append(signal.Value.ToString("0.####", CultureInfo.InvariantCulture))
                        .Append(' ').AppendLine(signal.Unit);
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Notes");
        sb.AppendLine();
        foreach (var note in report.Notes)
        {
            sb.Append("- ").AppendLine(note);
        }
        return sb.ToString();
    }

    private static void AppendMetric(
        StringBuilder sb,
        PerfScenarioRegressionResult scenario,
        PerfMetricRegressionResult metric)
    {
        sb.Append("| ").Append(EscapeCell(scenario.Scenario))
            .Append(scenario.IsControl ? " (control)" : string.Empty)
            .Append(" | ").Append(metric.Metric)
            .Append(" | ").Append(metric.BaselineMedian.ToString("0.####", CultureInfo.InvariantCulture)).Append(' ').Append(metric.Unit)
            .Append(" | ").Append(metric.CandidateMedian.ToString("0.####", CultureInfo.InvariantCulture)).Append(' ').Append(metric.Unit)
            .Append(" | ").Append(metric.DeltaPercent.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture)).Append('%')
            .Append(" | ").Append(metric.BaselineCoefficientOfVariationPercent.ToString("0.##", CultureInfo.InvariantCulture))
            .Append("% / ").Append(metric.CandidateCoefficientOfVariationPercent.ToString("0.##", CultureInfo.InvariantCulture)).Append('%')
            .Append(" | ").Append(metric.RegressionAgreementCount).Append('/').Append(metric.Repetitions)
            .Append(" | ").Append(metric.Verdict).AppendLine(" |");
    }

    private static string EscapeCell(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static string EscapeInline(string value)
        => value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
