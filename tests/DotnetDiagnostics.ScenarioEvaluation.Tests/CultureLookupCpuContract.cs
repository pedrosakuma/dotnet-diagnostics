using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class CultureLookupCpuContract
{
    internal const string CultureMethod = "BadCodeSample.CultureLookupWorkload.RunCultureSensitive(";
    internal const string OrdinalMethod = "BadCodeSample.CultureLookupWorkload.RunOrdinal(";

    internal static IReadOnlyList<ObservedMetric> ProjectOwnership(
        CpuSampleTraceArtifact artifact, string activeMethod, string inactiveMethod, int verifiedResponses)
    {
        if (artifact.Evidence?.Kind != CpuSampleEvidenceKind.OsOnCpuSamples)
        {
            throw new InvalidOperationException("Workload ownership requires measured OS on-CPU evidence.");
        }
        return
        [
            new("owned-inclusive-samples", InclusiveSamples(artifact, activeMethod), "samples"),
            new("other-route-inclusive-samples", InclusiveSamples(artifact, inactiveMethod), "samples"),
            new("verified-responses", verifiedResponses, "responses"),
            new("measured-os-evidence", 1),
        ];
    }

    internal static long InclusiveSamples(CpuSampleTraceArtifact artifact, string method)
    {
        var query = CpuSampleQueryDispatcher.RenderCallerCallee(artifact, "replay", method, topN: 5);
        if (query.Error?.Kind == "NotFound")
        {
            return 0;
        }
        if (query.Error is not null || query.Data is not { } data)
        {
            throw new InvalidOperationException($"Cannot resolve unique workload ownership: {query.Summary}");
        }
        if (!string.Equals(Path.GetFileNameWithoutExtension(data.Module), "BadCodeSample", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The workload method resolved to unexpected module '{data.Module}'.");
        }
        if (data.InclusiveSamples < 0 || data.InclusiveSamples > artifact.TotalSamples)
        {
            throw new InvalidOperationException("Workload ownership violated distinct-stack inclusive sample accounting.");
        }
        return data.InclusiveSamples;
    }

    internal static ScenarioEvidence Combine(ScenarioEvidence culture, ScenarioEvidence ordinal, int maximumEvidenceItems)
    {
        if (culture.ScenarioId != ordinal.ScenarioId || culture.ScenarioVersion != ordinal.ScenarioVersion
            || culture.Trial != ordinal.Trial)
        {
            throw new InvalidDataException("Paired ownership evidence must belong to the same scenario version and trial.");
        }
        var framesPerPhase = Math.Clamp(maximumEvidenceItems / 2, 1, 15);
        return culture with
        {
            Activation = new(
                culture.Activation.Status == ScenarioStageStatus.Passed && ordinal.Activation.Status == ScenarioStageStatus.Passed
                    ? ScenarioStageStatus.Passed : ScenarioStageStatus.Failed,
                culture.Activation.FailureKind != ScenarioFailureKind.None
                    ? culture.Activation.FailureKind : ordinal.Activation.FailureKind,
                $"culture: {culture.Activation.Detail}; ordinal: {ordinal.Activation.Detail}",
                culture.Activation.DurationSeconds + ordinal.Activation.DurationSeconds),
            Collection = new(
                culture.Collection.Status == ScenarioStageStatus.Passed && ordinal.Collection.Status == ScenarioStageStatus.Passed
                    ? ScenarioStageStatus.Passed : ScenarioStageStatus.Failed,
                culture.Collection.FailureKind != ScenarioFailureKind.None
                    ? culture.Collection.FailureKind : ordinal.Collection.FailureKind,
                $"culture: {culture.Collection.Detail ?? "completed"}; ordinal: {ordinal.Collection.Detail ?? "completed"}",
                culture.Collection.DurationSeconds + ordinal.Collection.DurationSeconds),
            Metrics = culture.Metrics.Select(metric => metric with { Name = $"culture.{metric.Name}" })
                .Concat(ordinal.Metrics.Select(metric => metric with { Name = $"ordinal.{metric.Name}" }))
                .OrderBy(metric => metric.Name, StringComparer.Ordinal).ToArray(),
            Frames = culture.Frames.Take(framesPerPhase).Select(frame => frame with { DisplayName = $"culture: {frame.DisplayName}" })
                .Concat(ordinal.Frames.Take(framesPerPhase).Select(frame => frame with { DisplayName = $"ordinal: {frame.DisplayName}" }))
                .Take(maximumEvidenceItems).ToArray(),
            Signals = [],
            Notes = culture.Notes.Take(10).Select(note => $"culture: {note}")
                .Concat(ordinal.Notes.Take(10).Select(note => $"ordinal: {note}")).ToArray(),
        };
    }
}
