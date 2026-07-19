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

    public static string SerializeFeasibility(PerfExperimentFeasibility feasibility)
    {
        ArgumentNullException.ThrowIfNull(feasibility);
        return JsonSerializer.Serialize(feasibility, JsonOptions);
    }

    public static PerfExperimentFeasibility DeserializeFeasibility(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<PerfExperimentFeasibility>(json, JsonOptions)
            ?? throw new JsonException("Experiment feasibility JSON was empty.");
    }

    public static string SerializePairedManifest(PerfPairedExperimentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    public static PerfPairedExperimentManifest DeserializePairedManifest(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<PerfPairedExperimentManifest>(json, JsonOptions)
            ?? throw new JsonException("Paired experiment manifest JSON was empty.");
    }

    public static string SerializePairedReport(PerfPairedRegressionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public static PerfPairedRegressionReport DeserializePairedReport(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<PerfPairedRegressionReport>(json, JsonOptions)
            ?? throw new JsonException("Paired regression report JSON was empty.");
    }

    public static string SerializeCalibrationCohort(PerfCalibrationCohortEvidence cohort)
    {
        ArgumentNullException.ThrowIfNull(cohort);
        return JsonSerializer.Serialize(cohort, JsonOptions);
    }

    public static PerfCalibrationCohortEvidence DeserializeCalibrationCohort(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<PerfCalibrationCohortEvidence>(json, JsonOptions)
            ?? throw new JsonException("Calibration cohort JSON was empty.");
    }

    public static string SerializeCalibrationEvidence(PerfCalibrationEvidencePackage evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return JsonSerializer.Serialize(evidence, JsonOptions);
    }

    public static PerfCalibrationEvidencePackage DeserializeCalibrationEvidence(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<PerfCalibrationEvidencePackage>(json, JsonOptions)
            ?? throw new JsonException("Calibration evidence JSON was empty.");
    }

    public static string SerializeCalibrationReport(PerfCalibrationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public static PerfCalibrationReport DeserializeCalibrationReport(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<PerfCalibrationReport>(json, JsonOptions)
            ?? throw new JsonException("Calibration report JSON was empty.");
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

    public static string BuildPairedMarkdown(PerfPairedRegressionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("# Paired CI performance experiment");
        sb.AppendLine();
        sb.Append("- Verdict: **").Append(report.Verdict).AppendLine("**");
        sb.Append("- Decision: **").Append(report.Decision).AppendLine("**");
        sb.Append("- Policy: `").Append(report.Policy.Version).AppendLine("`");
        sb.Append("- Environment compatible: **").Append(report.Compatibility.Compatible ? "yes" : "no").AppendLine("**");
        sb.Append("- Attribution compatible: **")
            .Append(report.AttributionCompatibility.Compatible ? "yes" : "no")
            .AppendLine("**");
        sb.Append("- Recommendation: **advisory** (never gate-eligible from one cohort)").AppendLine();
        sb.Append("- Total observed runner time: **")
            .Append(report.Feasibility.TotalRunnerMinutes.ToString("0.##", CultureInfo.InvariantCulture))
            .AppendLine(" minutes**");
        sb.Append("- Compact/raw artifact input: **")
            .Append(FormatBytes(report.Feasibility.CompactArtifactBytes))
            .Append(" / ")
            .Append(FormatBytes(report.Feasibility.RawArtifactBytes))
            .AppendLine("**");
        sb.AppendLine();

        if (!report.Compatibility.Compatible)
        {
            sb.AppendLine("## Compatibility failures");
            sb.AppendLine();
            foreach (var mismatch in report.Compatibility.Mismatches)
            {
                sb.Append("- ").AppendLine(mismatch);
            }
            sb.AppendLine();
        }
        if (!report.AttributionCompatibility.Compatible)
        {
            sb.AppendLine("## Attribution compatibility failures");
            sb.AppendLine();
            foreach (var mismatch in report.AttributionCompatibility.Mismatches)
            {
                sb.Append("- ").AppendLine(mismatch);
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Workload set");
        sb.AppendLine();
        sb.AppendLine("| workload | status | main version | PR version | variants main / PR | verdict |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var workload in report.Workloads)
        {
            sb.Append("| ").Append(EscapeCell(workload.WorkloadId))
                .Append(" | ").Append(workload.Status)
                .Append(" | ").Append(EscapeCell(workload.MainVersion))
                .Append(" | ").Append(EscapeCell(workload.PullRequestVersion))
                .Append(" | ").Append(EscapeCell(string.Join(", ", workload.MainVariants)))
                .Append(" / ").Append(EscapeCell(string.Join(", ", workload.PullRequestVariants)))
                .Append(" | ").Append(workload.Verdict).AppendLine(" |");
        }
        sb.AppendLine();

        sb.AppendLine("## Comparable measurements");
        sb.AppendLine();
        sb.AppendLine("| workload | variant | metric | main median | PR median | delta | CV main/PR | agreement | verdict |");
        sb.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (var workload in report.Workloads.Where(static workload =>
                     workload.Status == PerfWorkloadSetStatus.Comparable))
        {
            foreach (var variant in workload.Variants)
            {
                AppendPairedMetric(sb, workload.WorkloadId, variant.Variant, variant.Timing);
                AppendPairedMetric(sb, workload.WorkloadId, variant.Variant, variant.Allocation);
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Fixture calibration");
        sb.AppendLine();
        sb.AppendLine("| ref | injected regressions detected | detection rate | unchanged-control false positives | false-positive rate |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var calibration in report.Calibration)
        {
            sb.Append("| ").Append(calibration.Ref)
                .Append(" | ").Append(calibration.DetectedRegressionCount).Append('/').Append(calibration.InjectedRegressionCount)
                .Append(" | ").Append(calibration.DetectionRatePercent.ToString("0.##", CultureInfo.InvariantCulture)).Append('%')
                .Append(" | ").Append(calibration.FalsePositiveCount).Append('/').Append(calibration.UnchangedControlCount)
                .Append(" | ").Append(calibration.FalsePositiveRatePercent.ToString("0.##", CultureInfo.InvariantCulture)).AppendLine("% |");
        }
        sb.AppendLine();

        sb.AppendLine("## Separate diagnostic attribution");
        sb.AppendLine();
        sb.AppendLine("> Attribution runs only after every clean pair. Diagnostic elapsed time never enters a regression verdict.");
        sb.AppendLine();
        foreach (var row in report.Attribution)
        {
            sb.Append("- `").Append(row.Scenario).Append('/').Append(row.Kind).Append("`: ")
                .Append(row.Matched && !row.IsError ? "matched" : "not matched")
                .Append(" - ").Append(EscapeInline(row.Headline)).AppendLine();
        }
        if (report.Attribution.Count == 0)
        {
            sb.AppendLine("_No diagnostic attribution was supplied._");
        }
        sb.AppendLine();

        sb.AppendLine("## Operational feasibility");
        sb.AppendLine();
        sb.AppendLine("| phase | name | duration | artifact input | ref | pair |");
        sb.AppendLine("| --- | --- | ---: | ---: | --- | ---: |");
        foreach (var stage in report.Feasibility.Stages)
        {
            sb.Append("| ").Append(stage.Kind)
                .Append(" | ").Append(EscapeCell(stage.Name))
                .Append(" | ").Append(stage.DurationSeconds.ToString("0.##", CultureInfo.InvariantCulture)).Append(" s")
                .Append(" | ").Append(FormatBytes(stage.ArtifactBytes))
                .Append(" | ").Append(EscapeCell(stage.Ref ?? string.Empty))
                .Append(" | ").Append(stage.PairNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
                .AppendLine(" |");
        }
        sb.AppendLine();
        sb.AppendLine("| cadence | budget | suitability | rationale |");
        sb.AppendLine("| --- | ---: | --- | --- |");
        foreach (var assessment in report.Cadence)
        {
            sb.Append("| ").Append(assessment.Cadence)
                .Append(" | ").Append(assessment.BudgetMinutes.ToString("0.##", CultureInfo.InvariantCulture)).Append(" min")
                .Append(" | ").Append(assessment.Suitability)
                .Append(" | ").Append(EscapeCell(assessment.Rationale)).AppendLine(" |");
        }
        sb.AppendLine();

        sb.AppendLine("## Evidence boundary");
        sb.AppendLine();
        foreach (var note in report.Notes)
        {
            sb.Append("- ").AppendLine(note);
        }
        return sb.ToString();
    }

    public static string BuildCalibrationMarkdown(PerfCalibrationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("# Paired CI performance calibration");
        sb.AppendLine();
        sb.Append("- Decision: **").Append(report.Decision).AppendLine("**");
        sb.Append("- Policy: `").Append(report.Policy.Version).AppendLine("`");
        sb.Append("- Recommendation: **advisory**").AppendLine();
        sb.Append("- Eligible for gate: **no**").AppendLine();
        sb.Append("- Hosted cohorts/days: **").Append(report.HostedCohortCount)
            .Append(" / ").Append(report.HostedDistinctDayCount).AppendLine("**");
        sb.Append("- Dedicated cohorts/days: **").Append(report.DedicatedCohortCount)
            .Append(" / ").Append(report.DedicatedDistinctDayCount).AppendLine("**");
        sb.Append("- Timing gate consideration supported: **")
            .Append(report.EvidenceSupportsTimingGateConsideration ? "yes" : "no")
            .AppendLine("**");
        sb.AppendLine();

        foreach (var group in report.Groups)
        {
            sb.Append("## Compatibility group `").Append(group.GroupId).AppendLine("`");
            sb.AppendLine();
            sb.Append("- Runner: `").Append(group.RunnerKind).Append("` / `")
                .Append(EscapeInline(group.RunnerLabel)).AppendLine("`");
            sb.Append("- SDK/runtime/image: `").Append(EscapeInline(group.SelectedSdkVersion))
                .Append("` / `").Append(EscapeInline(group.Environment.RuntimeVersion))
                .Append("` / `").Append(EscapeInline(group.Environment.RunnerImage ?? "unknown"))
                .AppendLine("`");
            sb.Append("- Main/PR: `").Append(EscapeInline(group.MainBuild.CommitSha ?? group.MainBuild.Id))
                .Append("` / `").Append(EscapeInline(group.PullRequestBuild.CommitSha ?? group.PullRequestBuild.Id))
                .AppendLine("`");
            sb.Append("- Independent allocations / UTC days: **")
                .Append(group.IndependentAllocationCount).Append(" / ").Append(group.DistinctDayCount)
                .AppendLine("**");
            sb.Append("- Runner minutes: **")
                .Append(group.TotalRunnerMinutes.ToString("0.##", CultureInfo.InvariantCulture))
                .AppendLine("**");
            sb.Append("- Compact/raw inputs: **").Append(FormatBytes(group.CompactArtifactBytes))
                .Append(" / ").Append(FormatBytes(group.RawArtifactBytes)).AppendLine("**");
            sb.Append("- Calibration targets met: **").Append(group.MeetsCalibrationTargets ? "yes" : "no")
                .AppendLine("**");
            sb.AppendLine();

            sb.AppendLine("### Detection and false positives");
            sb.AppendLine();
            sb.AppendLine("| ref | detection (95% CI) | false positives (95% CI) |");
            sb.AppendLine("| --- | ---: | ---: |");
            foreach (var detection in group.DetectionRates)
            {
                var falsePositive = group.FalsePositiveRates.Single(rate =>
                    string.Equals(rate.Ref, detection.Ref, StringComparison.Ordinal));
                sb.Append("| ").Append(detection.Ref)
                    .Append(" | ").Append(detection.PositiveCount).Append('/').Append(detection.ObservationCount)
                    .Append(" = ").Append(detection.RatePercent.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append("% (").Append(detection.Lower95Percent.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append('-').Append(detection.Upper95Percent.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append("%) | ").Append(falsePositive.PositiveCount).Append('/')
                    .Append(falsePositive.ObservationCount).Append(" = ")
                    .Append(falsePositive.RatePercent.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append("% (").Append(falsePositive.Lower95Percent.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append('-').Append(falsePositive.Upper95Percent.ToString("0.##", CultureInfo.InvariantCulture))
                    .AppendLine("%) |");
            }
            sb.AppendLine();

            sb.AppendLine("### Timing variance");
            sb.AppendLine();
            sb.AppendLine("| workload | variant | ref | within-cohort CV min/median/max | cross-allocation CV | cross-day CV |");
            sb.AppendLine("| --- | --- | --- | ---: | ---: | ---: |");
            foreach (var row in group.Variance.Where(static row => row.Metric == PerfMetricKind.Time))
            {
                sb.Append("| ").Append(EscapeCell(row.WorkloadId))
                    .Append(" | ").Append(EscapeCell(row.Variant))
                    .Append(" | ").Append(EscapeCell(row.Ref))
                    .Append(" | ").Append(row.MinimumWithinCohortCoefficientOfVariationPercent.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append("% / ").Append(row.MedianWithinCohortCoefficientOfVariationPercent.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append("% / ").Append(row.MaximumWithinCohortCoefficientOfVariationPercent.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append("% | ").Append(row.CrossAllocationCoefficientOfVariationPercent.ToString("0.##", CultureInfo.InvariantCulture))
                    .Append("% | ").Append(row.CrossDayCoefficientOfVariationPercent?.ToString("0.##", CultureInfo.InvariantCulture) ?? "missing")
                    .AppendLine(row.CrossDayCoefficientOfVariationPercent.HasValue ? "% |" : " |");
            }
            sb.AppendLine();

            if (group.TargetFailures.Count > 0)
            {
                sb.AppendLine("### Target gaps");
                sb.AppendLine();
                foreach (var failure in group.TargetFailures)
                {
                    sb.Append("- ").AppendLine(failure);
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Evidence boundary");
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

    private static void AppendPairedMetric(
        StringBuilder sb,
        string workload,
        string variant,
        PerfMetricRegressionResult metric)
    {
        sb.Append("| ").Append(EscapeCell(workload))
            .Append(" | ").Append(EscapeCell(variant))
            .Append(" | ").Append(metric.Metric)
            .Append(" | ").Append(metric.BaselineMedian.ToString("0.####", CultureInfo.InvariantCulture)).Append(' ').Append(metric.Unit)
            .Append(" | ").Append(metric.CandidateMedian.ToString("0.####", CultureInfo.InvariantCulture)).Append(' ').Append(metric.Unit)
            .Append(" | ").Append(metric.DeltaPercent.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture)).Append('%')
            .Append(" | ").Append(metric.BaselineCoefficientOfVariationPercent.ToString("0.##", CultureInfo.InvariantCulture))
            .Append("% / ").Append(metric.CandidateCoefficientOfVariationPercent.ToString("0.##", CultureInfo.InvariantCulture)).Append('%')
            .Append(" | ").Append(metric.RegressionAgreementCount).Append('/').Append(metric.Repetitions)
            .Append(" | ").Append(metric.Verdict).AppendLine(" |");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static string EscapeCell(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static string EscapeInline(string value)
        => value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
