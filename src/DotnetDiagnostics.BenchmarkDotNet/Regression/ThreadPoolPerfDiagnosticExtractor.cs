using System.Text.Json;

namespace DotnetDiagnostics.BenchmarkDotNet.Regression;

/// <summary>Compact parsed ThreadPool evidence used by the CI regression pilot.</summary>
public sealed record ThreadPoolPerfDiagnosticEvidence(
    bool HasCausalWait,
    bool HasConclusiveCausalAssessment,
    IReadOnlyList<PerfDiagnosticSignal> Signals,
    string EvidenceConclusion,
    IReadOnlyList<string> QualityLimitations);

/// <summary>Extracts causal blocking/starvation evidence from a structured ThreadPool diagnostic envelope.</summary>
public static class ThreadPoolPerfDiagnosticExtractor
{
    /// <summary>
    /// Parses bounded ThreadPool signals and matches only positive blocking/starvation adjustments.
    /// Generic summary text and unrelated hill-climbing are intentionally ignored.
    /// </summary>
    public static ThreadPoolPerfDiagnosticEvidence Extract(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.StartsWith("//", StringComparison.Ordinal))
        {
            return Unknown();
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Data", out var data))
        {
            return Unknown();
        }

        var hillClimbing = data.TryGetProperty("HillClimbing", out var hillClimbingElement)
            ? hillClimbingElement.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        var hasSummary = data.TryGetProperty("Evidence", out var summary)
            && summary.ValueKind == JsonValueKind.Object;
        JsonElement hasProvenance = default;
        var hasReasonProvenanceProperty = hasSummary
            && summary.TryGetProperty("HasCompleteRuntimeReasonEvidence", out hasProvenance)
            && hasProvenance.ValueKind is JsonValueKind.True or JsonValueKind.False;
        var hillClimbingCount = hasSummary ? NullableInt(summary, "HillClimbingEvents") : hillClimbing.Length;
        var reasonProvenanceComplete = hasSummary
            ? hillClimbingCount > 0 && hasReasonProvenanceProperty && hasProvenance.GetBoolean()
            : hillClimbing.Length > 0
                && hillClimbing.All(static sample => HasRuntimeProvenanceProperty(sample));
        var starvation = hillClimbing.Where(static sample => IsConfirmedReason(sample, "Starvation")).ToArray();
        var cooperativeBlocking = hillClimbing.Where(static sample => IsConfirmedReason(sample, "CooperativeBlocking")).ToArray();
        var starvationCount = hasSummary ? NullableInt(summary, "ConfirmedStarvationAdjustments") : starvation.Length;
        var cooperativeBlockingCount = hasSummary ? NullableInt(summary, "ConfirmedCooperativeBlockingAdjustments") : cooperativeBlocking.Length;
        var hasConfirmedCausalEvidence = starvationCount > 0 || cooperativeBlockingCount > 0;
        var causalCountsAvailable = hasSummary || reasonProvenanceComplete || hasConfirmedCausalEvidence;
        var qualityLimitations = ReadQualityLimitations(data);
        var qualitySupportsConclusions = QualitySupportsConclusions(data);
        var conclusiveAssessment = reasonProvenanceComplete && qualitySupportsConclusions;
        var hasMeasuredStarvationWorkerIncrease = TryGetMeasuredWorkerIncrease(starvation, starvationCount, out var starvationWorkerIncrease);
        var hasMeasuredBlockingWorkerIncrease = TryGetMeasuredWorkerIncrease(cooperativeBlocking, cooperativeBlockingCount, out var blockingWorkerIncrease);

        var workerCounts = data.TryGetProperty("WorkerThreadTimeline", out var workerTimeline)
            ? workerTimeline.EnumerateArray()
                .Where(static sample => HasRuntimeCountProvenance(sample, "CountProvenance"))
                .Select(static sample => NullableInt(sample, "Count"))
                .Where(static count => count.HasValue)
                .Select(static count => count!.Value)
                .ToArray()
            : Array.Empty<int>();
        var workerPeak = workerCounts.Length == 0 ? 0 : workerCounts.Max();
        var workerGrowth = workerCounts.Length == 0 ? 0 : workerPeak - workerCounts.Min();
        var enqueueEvents = data.TryGetProperty("TotalEnqueueEvents", out var enqueue)
            && enqueue.TryGetInt64(out var enqueueCount)
                ? enqueueCount
                : 0;

        var signals = new List<PerfDiagnosticSignal>();
        if (causalCountsAvailable
            && starvationCount.HasValue
            && cooperativeBlockingCount.HasValue
            && (hasConfirmedCausalEvidence || conclusiveAssessment))
        {
            signals.Add(new(
                "threadpool.starvationAdjustments",
                "Starvation worker adjustments",
                "ThreadPoolWorkerThreadAdjustmentAdjustment:Starvation",
                starvationCount.Value,
                "events",
                PerfSignalDirection.Lower));
            if (hasMeasuredStarvationWorkerIncrease)
            {
                signals.Add(new(
                    "threadpool.starvationWorkerIncrease",
                    "Workers added for starvation",
                    "ThreadPoolWorkerThreadAdjustmentAdjustment:Starvation",
                    starvationWorkerIncrease,
                    "threads",
                    PerfSignalDirection.Lower));
            }
            signals.Add(new(
                "threadpool.cooperativeBlockingAdjustments",
                "Cooperative-blocking worker adjustments",
                "ThreadPoolWorkerThreadAdjustmentAdjustment:CooperativeBlocking",
                cooperativeBlockingCount.Value,
                "events",
                PerfSignalDirection.Lower));
            if (hasMeasuredBlockingWorkerIncrease)
            {
                signals.Add(new(
                    "threadpool.cooperativeBlockingWorkerIncrease",
                    "Workers added for cooperative blocking",
                    "ThreadPoolWorkerThreadAdjustmentAdjustment:CooperativeBlocking",
                    blockingWorkerIncrease,
                    "threads",
                    PerfSignalDirection.Lower));
            }
        }

        signals.Add(new(
                "threadpool.hillClimbingEvents",
                "Hill-climbing events",
                null,
                hillClimbingCount ?? hillClimbing.Length,
                "events",
                PerfSignalDirection.Neutral));
        if (workerCounts.Length > 0)
        {
            signals.Add(new(
                "threadpool.workerPeak",
                "Peak worker threads",
                null,
                workerPeak,
                "threads",
                PerfSignalDirection.Neutral));
            signals.Add(new(
                "threadpool.workerGrowth",
                "Worker growth",
                null,
                workerGrowth,
                "threads",
                PerfSignalDirection.Neutral));
        }

        signals.Add(new(
                "threadpool.enqueueEvents",
                "Enqueue events",
                null,
                enqueueEvents,
                "events",
                PerfSignalDirection.Neutral));

        return new(
            hasConfirmedCausalEvidence,
            conclusiveAssessment,
            signals,
            hasConfirmedCausalEvidence
                ? "retained-explicit-positive-evidence"
                : conclusiveAssessment ? "absence-assessment-supported" : "inconclusive",
            qualityLimitations);
    }

    private static ThreadPoolPerfDiagnosticEvidence Unknown()
        => new(
            false,
            false,
            Array.Empty<PerfDiagnosticSignal>(),
            "legacy-or-missing-quality-unknown",
            ["Structured ThreadPool evidence quality is unavailable."]);

    private static bool QualitySupportsConclusions(JsonElement data)
        => data.TryGetProperty("Quality", out var quality)
            && quality.ValueKind == JsonValueKind.Object
            && quality.TryGetProperty("Conclusions", out var conclusions)
            && conclusions.ValueKind == JsonValueKind.Object
            && conclusions.TryGetProperty("RegressionOrHealthyControl", out var support)
            && support.ValueKind == JsonValueKind.String
            && string.Equals(support.GetString(), "Supported", StringComparison.Ordinal);

    private static string[] ReadQualityLimitations(JsonElement data)
    {
        if (!data.TryGetProperty("Quality", out var quality)
            || quality.ValueKind != JsonValueKind.Object
            || !quality.TryGetProperty("Limitations", out var limitations)
            || limitations.ValueKind != JsonValueKind.Array)
        {
            return ["Legacy or missing structured evidence quality."];
        }

        return limitations
            .EnumerateArray()
            .Select(static limitation =>
            {
                var category = limitation.TryGetProperty("Category", out var categoryElement)
                    ? categoryElement.ToString()
                    : "Unknown";
                var scope = limitation.TryGetProperty("Scope", out var scopeElement)
                    ? scopeElement.GetString()
                    : null;
                return string.IsNullOrWhiteSpace(scope) ? category : $"{category}:{scope}";
            })
            .Take(16)
            .ToArray();
    }

    private static bool IsConfirmedReason(JsonElement sample, string reason)
        => sample.TryGetProperty("Reason", out var reasonElement)
            && string.Equals(reasonElement.GetString(), reason, StringComparison.OrdinalIgnoreCase)
            && sample.TryGetProperty("ReasonProvenance", out var provenance)
            && string.Equals(provenance.GetString(), "runtime-observed", StringComparison.Ordinal);

    private static bool HasRuntimeProvenanceProperty(JsonElement sample)
        => sample.TryGetProperty("ReasonProvenance", out var provenance)
            && string.Equals(provenance.GetString(), "runtime-observed", StringComparison.Ordinal);

    private static bool TryGetMeasuredWorkerIncrease(JsonElement[] samples, int? expectedCount, out int workerIncrease)
    {
        workerIncrease = 0;
        if (samples.Length == 0 || expectedCount != samples.Length)
        {
            return false;
        }

        foreach (var sample in samples)
        {
            var oldCount = NullableInt(sample, "OldCount");
            var newCount = NullableInt(sample, "NewCount");
            if (!oldCount.HasValue
                || !newCount.HasValue
                || !HasRuntimeCountProvenance(sample, "OldCountProvenance")
                || !HasRuntimeCountProvenance(sample, "NewCountProvenance"))
            {
                workerIncrease = 0;
                return false;
            }

            workerIncrease += Math.Max(0, newCount.Value - oldCount.Value);
        }

        return true;
    }

    private static bool HasRuntimeCountProvenance(JsonElement sample, string propertyName)
        => sample.TryGetProperty(propertyName, out var provenance)
            && provenance.ValueKind == JsonValueKind.String
            && string.Equals(provenance.GetString(), "runtime-observed", StringComparison.Ordinal);

    private static int? NullableInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;
}
