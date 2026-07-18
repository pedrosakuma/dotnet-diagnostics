using System.Text.Json;

namespace DotnetDiagnostics.BenchmarkDotNet.Regression;

/// <summary>Compact parsed ThreadPool evidence used by the CI regression pilot.</summary>
public sealed record ThreadPoolPerfDiagnosticEvidence(
    bool HasCausalWait,
    IReadOnlyList<PerfDiagnosticSignal> Signals);

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
            return new(false, Array.Empty<PerfDiagnosticSignal>());
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Data", out var data))
        {
            return new(false, Array.Empty<PerfDiagnosticSignal>());
        }

        var hillClimbing = data.TryGetProperty("HillClimbing", out var hillClimbingElement)
            ? hillClimbingElement.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        var starvation = hillClimbing
            .Where(static sample =>
                sample.TryGetProperty("Reason", out var reason)
                && string.Equals(reason.GetString(), "Starvation", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var cooperativeBlocking = hillClimbing
            .Where(static sample =>
                sample.TryGetProperty("Reason", out var reason)
                && string.Equals(reason.GetString(), "CooperativeBlocking", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var starvationWorkerIncrease = starvation.Sum(static sample =>
        {
            var oldCount = NullableInt(sample, "OldCount");
            var newCount = NullableInt(sample, "NewCount");
            return oldCount is int oldValue && newCount is int newValue
                ? Math.Max(0, newValue - oldValue)
                : 0;
        });
        var blockingWorkerIncrease = cooperativeBlocking.Sum(static sample =>
        {
            var oldCount = NullableInt(sample, "OldCount");
            var newCount = NullableInt(sample, "NewCount");
            return oldCount is int oldValue && newCount is int newValue
                ? Math.Max(0, newValue - oldValue)
                : 0;
        });

        var workerCounts = data.TryGetProperty("WorkerThreadTimeline", out var workerTimeline)
            ? workerTimeline.EnumerateArray()
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

        IReadOnlyList<PerfDiagnosticSignal> signals =
        [
            new(
                "threadpool.starvationAdjustments",
                "Starvation worker adjustments",
                "ThreadPoolWorkerThreadAdjustmentAdjustment:Starvation",
                starvation.Length,
                "events",
                PerfSignalDirection.Lower),
            new(
                "threadpool.starvationWorkerIncrease",
                "Workers added for starvation",
                "ThreadPoolWorkerThreadAdjustmentAdjustment:Starvation",
                starvationWorkerIncrease,
                "threads",
                PerfSignalDirection.Lower),
            new(
                "threadpool.cooperativeBlockingAdjustments",
                "Cooperative-blocking worker adjustments",
                "ThreadPoolWorkerThreadAdjustmentAdjustment:CooperativeBlocking",
                cooperativeBlocking.Length,
                "events",
                PerfSignalDirection.Lower),
            new(
                "threadpool.cooperativeBlockingWorkerIncrease",
                "Workers added for cooperative blocking",
                "ThreadPoolWorkerThreadAdjustmentAdjustment:CooperativeBlocking",
                blockingWorkerIncrease,
                "threads",
                PerfSignalDirection.Lower),
            new(
                "threadpool.hillClimbingEvents",
                "Hill-climbing events",
                null,
                hillClimbing.Length,
                "events",
                PerfSignalDirection.Lower),
            new(
                "threadpool.workerPeak",
                "Peak worker threads",
                null,
                workerPeak,
                "threads",
                PerfSignalDirection.Lower),
            new(
                "threadpool.workerGrowth",
                "Worker growth",
                null,
                workerGrowth,
                "threads",
                PerfSignalDirection.Lower),
            new(
                "threadpool.enqueueEvents",
                "Enqueue events",
                null,
                enqueueEvents,
                "events",
                PerfSignalDirection.Neutral),
        ];

        return new(starvation.Length > 0 || cooperativeBlocking.Length > 0, signals);
    }

    private static int? NullableInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;
}
