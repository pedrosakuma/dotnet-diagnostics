using System.Globalization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class ThreadPoolBacklogEvidenceEvaluator
{
    public static ThreadPoolBacklogAnalysis Evaluate(ThreadPoolBacklogScenarioCase scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        Validate(scenario);

        var queueMedian = EvaluateWindows(
            scenario,
            "threadpool-queue-length",
            "items",
            "window-median",
            ScenarioStatisticalMethodIds.WindowMedian,
            scenario.Windows,
            static window => window.QueueLength);
        var queueSlope = EvaluateWindows(
            scenario,
            "threadpool-queue-length",
            "items",
            "theil-sen-slope",
            ScenarioStatisticalMethodIds.TheilSenSlope,
            scenario.Windows,
            static window => window.QueueLength,
            includeX: true);
        var workerMedian = EvaluateWindows(
            scenario,
            "threadpool-thread-count",
            "threads",
            "window-median",
            ScenarioStatisticalMethodIds.WindowMedian,
            scenario.Windows,
            static window => window.WorkerCount);
        var workerSlope = EvaluateWindows(
            scenario,
            "threadpool-thread-count",
            "threads",
            "theil-sen-slope",
            ScenarioStatisticalMethodIds.TheilSenSlope,
            scenario.Windows,
            static window => window.WorkerCount,
            includeX: true);
        var p95Windows = scenario.Windows.Where(window => window.QueueP95.HasValue).ToArray();
        var queueP95Median = p95Windows.Length == scenario.Windows.Count
            ? EvaluateWindows(
                scenario,
                "threadpool-queue-length-p95",
                "items",
                "collector-window-p95-then-window-median",
                ScenarioStatisticalMethodIds.WindowMedian,
                p95Windows,
                static window => window.QueueP95!.Value)
            : null;

        var degraded = IsDegraded(queueMedian)
            || IsDegraded(queueSlope)
            || IsDegraded(workerMedian)
            || IsDegraded(workerSlope)
            || queueP95Median is not null && IsDegraded(queueP95Median);
        if (degraded)
        {
            return new(
                ThreadPoolBacklogNextAction.RecaptureDegradedEvidence,
                ThreadPoolStarvationSupport.InconclusiveDegradedEvidence,
                scenario.Reason,
                queueMedian,
                queueP95Median,
                queueSlope,
                workerMedian,
                workerSlope,
                null,
                null,
                null,
                null);
        }

        var sufficient = IsAvailable(queueMedian)
            && IsAvailable(queueSlope)
            && IsAvailable(workerMedian)
            && IsAvailable(workerSlope);
        if (!sufficient)
        {
            return new(
                ThreadPoolBacklogNextAction.ContinueObservation,
                ThreadPoolStarvationSupport.NotEstablished,
                scenario.Reason,
                queueMedian,
                queueP95Median,
                queueSlope,
                workerMedian,
                workerSlope,
                null,
                null,
                null,
                null);
        }

        var orderedWindows = scenario.Windows
            .OrderBy(window => window.StartSeconds)
            .ToArray();
        var backlogWindows = orderedWindows.Count(window =>
            window.QueueLength >= scenario.Protocol.BacklogLevel);
        var backlogWindowProportion = (double)backlogWindows / orderedWindows.Length;
        var durationAboveLevel = LongestContiguousDurationAboveLevel(
            orderedWindows,
            scenario.Protocol.BacklogLevel);
        var tail = orderedWindows
            .TakeLast(scenario.Protocol.DrainTailWindows)
            .ToArray();
        var drainedAtTail = backlogWindows > 0
            && tail.Length == scenario.Protocol.DrainTailWindows
            && tail.All(window => window.QueueLength < scenario.Protocol.BacklogLevel);
        var lastQueue = orderedWindows[^1].QueueLength;
        var sustained = lastQueue >= scenario.Protocol.BacklogLevel
            && backlogWindowProportion >= scenario.Protocol.RequiredBacklogWindowProportion
            && durationAboveLevel >= scenario.Protocol.RequiredDurationAboveLevelSeconds;

        ThreadPoolBacklogNextAction action;
        if (drainedAtTail)
        {
            action = ThreadPoolBacklogNextAction.TreatBurstAsResolved;
        }
        else if (sustained)
        {
            action = ThreadPoolBacklogNextAction.CollectWaitsAndStacksNow;
        }
        else if (backlogWindows == 0
                 && workerMedian.Estimate >= scenario.Protocol.HighWorkerLevel)
        {
            action = ThreadPoolBacklogNextAction.InvestigateOtherSignals;
        }
        else
        {
            action = ThreadPoolBacklogNextAction.ContinueObservation;
        }

        var starvationSupport = action == ThreadPoolBacklogNextAction.CollectWaitsAndStacksNow
            && string.Equals(scenario.Reason.Value, "Starvation", StringComparison.Ordinal)
            && string.Equals(scenario.Reason.Provenance, "runtime-observed", StringComparison.Ordinal)
            && string.Equals(
                scenario.Reason.CaptureId,
                $"capture-{scenario.Id}",
                StringComparison.Ordinal)
                ? ThreadPoolStarvationSupport.ConfirmedByRuntimeReason
                : ThreadPoolStarvationSupport.NotEstablished;

        return new(
            action,
            starvationSupport,
            scenario.Reason,
            queueMedian,
            queueP95Median,
            queueSlope,
            workerMedian,
            workerSlope,
            backlogWindows,
            backlogWindowProportion,
            durationAboveLevel,
            drainedAtTail);
    }

    private static ScenarioStatisticalEvidenceResult EvaluateWindows(
        ThreadPoolBacklogScenarioCase scenario,
        string quantityId,
        string unit,
        string aggregation,
        string methodId,
        IReadOnlyList<ThreadPoolBacklogWindow> windows,
        Func<ThreadPoolBacklogWindow, double> selectValue,
        bool includeX = false)
    {
        var attempt = new ScenarioStatisticalAttempt(
            "repetition-1",
            "attempt-1",
            ScenarioStatisticalAttemptOutcome.Passed);
        var observations = windows
            .OrderBy(window => window.StartSeconds)
            .Select((window, index) => new ScenarioStatisticalObservation(
                $"{quantityId}-window-{index + 1}",
                "repetition-1",
                "attempt-1",
                "current-environment",
                $"capture-{scenario.Id}",
                window.Id,
                window.Id,
                selectValue(window),
                includeX ? window.StartSeconds : null,
                window.SourceEventCount,
                window.LostOrDropped,
                window.Representation))
            .ToArray();
        var representation = windows.Select(window => window.Representation).Distinct().Single();
        var protocol = new ScenarioStatisticalProtocol(
            $"{scenario.Protocol.Id}-{quantityId}-{aggregation}",
            scenario.Protocol.Version,
            new ScenarioStatisticalMethod(methodId, 1),
            ScenarioStatisticalIndependenceUnit.BoundedTimeWindow,
            scenario.Protocol.MinimumWindows,
            AdmitLoss: false,
            AdmitApproximation: false);
        var request = new ScenarioStatisticalEvidenceRequest(
            ScenarioStatisticalEvidenceJson.CurrentSchemaVersion,
            protocol,
            [new ScenarioStatisticalArm(
                "observed",
                ScenarioStatisticalArmRole.Observed,
                Identity(scenario, quantityId, unit, aggregation, representation),
                [attempt],
                observations)],
            "observed");

        return ScenarioStatisticalEvidenceHarness.Evaluate(request);
    }

    private static ScenarioStatisticalCompatibilityIdentity Identity(
        ThreadPoolBacklogScenarioCase scenario,
        string quantityId,
        string unit,
        string aggregation,
        ScenarioStatisticalRepresentation representation)
        => new(
        [
            new("target.runtime-flavor", "CoreCLR"),
            new("target.runtime-version-policy", "major-minor"),
            new("target.os", "scenario-declared"),
            new("target.architecture", "scenario-declared"),
            new("target.process-run-id", $"run-{scenario.Id}"),
            new("backend.id", "EventPipe"),
            new("backend.version", "1"),
            new("quantity.id", quantityId),
            new("quantity.unit", unit),
            new("quantity.aggregation", aggregation),
            new("quantity.normalization", "per-bounded-window"),
            new("quantity.filters", "provider=System.Runtime"),
            new("quantity.representation", RepresentationName(representation)),
            new("capture.duration", scenario.Windows.Sum(window => window.DurationSeconds)
                .ToString("R", CultureInfo.InvariantCulture) + "s"),
            new("capture.windowing", "non-overlapping-scenario-windows"),
            new("capture.providers", "System.Runtime"),
            new("capture.sampling-period", "scenario-window"),
            new("capture.retention", $"bounded-max-{ThreadPoolBacklogEvidenceJson.MaximumWindowsPerCase}"),
            new("capture.enrichment", "none"),
            new("workload.scenario", scenario.Protocol.Id),
            new("workload.version", scenario.Protocol.Version),
            new("workload.parameters",
                $"backlog-level={scenario.Protocol.BacklogLevel.ToString("R", CultureInfo.InvariantCulture)}"),
            new("workload.concurrency", "scenario-declared"),
            new("workload.deployment", "local-live-or-replay"),
            new("workload.variant", scenario.Id),
            new("independence.unit", "bounded_time_window"),
            new("independence.nesting", "counter-events->windows->capture->process-run"),
            new("independence.pairing-key", "none"),
            new("comparison.axis", "workload.variant"),
        ]);

    private static void Validate(ThreadPoolBacklogScenarioCase scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario.Id)
            || string.IsNullOrWhiteSpace(scenario.Protocol.Id)
            || string.IsNullOrWhiteSpace(scenario.Protocol.Version)
            || scenario.Windows.Count is < 1 or > ThreadPoolBacklogEvidenceJson.MaximumWindowsPerCase)
        {
            throw new InvalidDataException("ThreadPool backlog scenario identity or window count is invalid.");
        }

        if (!double.IsFinite(scenario.Protocol.BacklogLevel)
            || scenario.Protocol.BacklogLevel < 0
            || !double.IsFinite(scenario.Protocol.HighWorkerLevel)
            || scenario.Protocol.HighWorkerLevel < 0
            || scenario.Protocol.RequiredBacklogWindowProportion is < 0 or > 1
            || !double.IsFinite(scenario.Protocol.RequiredDurationAboveLevelSeconds)
            || scenario.Protocol.RequiredDurationAboveLevelSeconds < 0
            || scenario.Protocol.MinimumWindows is < 2
            || scenario.Protocol.DrainTailWindows is < 1
            || scenario.Protocol.DrainTailWindows > scenario.Windows.Count)
        {
            throw new InvalidDataException("ThreadPool backlog scenario protocol is invalid.");
        }

        if (scenario.Windows.Select(window => window.Id).Distinct(StringComparer.Ordinal).Count()
            != scenario.Windows.Count
            || scenario.Windows.Any(window =>
                string.IsNullOrWhiteSpace(window.Id)
                || !double.IsFinite(window.StartSeconds)
                || window.StartSeconds < 0
                || !double.IsFinite(window.DurationSeconds)
                || window.DurationSeconds <= 0
                || !double.IsFinite(window.QueueLength)
                || window.QueueLength < 0
                || !double.IsFinite(window.WorkerCount)
                || window.WorkerCount < 0
                || window.QueueP95 is double p95 && (!double.IsFinite(p95) || p95 < 0)
                || window.SourceEventCount < 0
                || window.LostOrDropped < 0))
        {
            throw new InvalidDataException("ThreadPool backlog scenario contains an invalid window.");
        }

        if (scenario.Windows.Select(window => window.Representation).Distinct().Count() != 1)
        {
            throw new InvalidDataException(
                "ThreadPool backlog scenario windows must use one declared representation.");
        }

        var orderedWindows = scenario.Windows
            .OrderBy(window => window.StartSeconds)
            .ToArray();
        for (var index = 1; index < orderedWindows.Length; index++)
        {
            var previousEnd = orderedWindows[index - 1].StartSeconds
                + orderedWindows[index - 1].DurationSeconds;
            if (orderedWindows[index].StartSeconds < previousEnd)
            {
                throw new InvalidDataException(
                    "ThreadPool backlog scenario windows must not overlap.");
            }
        }

        var firstStart = orderedWindows[0].StartSeconds;
        var lastEnd = orderedWindows[^1].StartSeconds + orderedWindows[^1].DurationSeconds;
        var expectedCaptureId = $"capture-{scenario.Id}";
        var runtimeObservedReason = string.Equals(
            scenario.Reason.Provenance,
            "runtime-observed",
            StringComparison.Ordinal);
        var missingReason = string.Equals(
            scenario.Reason.Provenance,
            "missing",
            StringComparison.Ordinal);
        if (runtimeObservedReason)
        {
            if (string.IsNullOrWhiteSpace(scenario.Reason.Value)
                || !string.Equals(
                    scenario.Reason.CaptureId,
                    expectedCaptureId,
                    StringComparison.Ordinal)
                || scenario.Reason.ObservedAtSeconds is not double observedAt
                || !double.IsFinite(observedAt)
                || observedAt < firstStart
                || observedAt > lastEnd)
            {
                throw new InvalidDataException(
                    "Runtime-observed ThreadPool reason must belong to the evaluated capture range.");
            }
        }
        else if (!missingReason
                 || scenario.Reason.Value is not null
                 || scenario.Reason.CaptureId is not null
                 || scenario.Reason.ObservedAtSeconds is not null)
        {
            throw new InvalidDataException(
                "ThreadPool reason must be a validated runtime observation or explicitly missing.");
        }
    }

    private static double LongestContiguousDurationAboveLevel(
        IReadOnlyList<ThreadPoolBacklogWindow> orderedWindows,
        double backlogLevel)
    {
        var longest = 0d;
        var current = 0d;
        double? previousEnd = null;
        foreach (var window in orderedWindows)
        {
            var contiguous = previousEnd is double end
                && Math.Abs(window.StartSeconds - end) <= 1e-9;
            if (window.QueueLength >= backlogLevel)
            {
                current = contiguous ? current + window.DurationSeconds : window.DurationSeconds;
                longest = Math.Max(longest, current);
            }
            else
            {
                current = 0;
            }

            previousEnd = window.StartSeconds + window.DurationSeconds;
        }

        return longest;
    }

    private static bool IsAvailable(ScenarioStatisticalEvidenceResult result)
        => result.Availability == ScenarioStatisticalAvailability.Available;

    private static bool IsDegraded(ScenarioStatisticalEvidenceResult result)
        => result.Availability is ScenarioStatisticalAvailability.LossDetected
            or ScenarioStatisticalAvailability.UnsupportedApproximation;

    private static string RepresentationName(ScenarioStatisticalRepresentation representation)
        => representation switch
        {
            ScenarioStatisticalRepresentation.Exact => "exact",
            ScenarioStatisticalRepresentation.ReservoirSampled => "reservoir_sampled",
            ScenarioStatisticalRepresentation.Projected => "projected",
            ScenarioStatisticalRepresentation.Truncated => "truncated",
            _ => throw new ArgumentOutOfRangeException(nameof(representation)),
        };
}
