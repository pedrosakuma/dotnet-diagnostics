using DotnetDiagnosticsMcp.Core.Counters;

namespace DotnetDiagnosticsMcp.Core.Triage;

/// <summary>
/// Phase 12 triage classifier — extracts the auto-hint logic into a reusable classification engine.
/// Produces a <see cref="TriageResult"/> with verdict, severity, and evidence from counter snapshots.
/// </summary>
public static class TriageClassifier
{
    /// <summary>Well-known verdict for CPU-bound workloads (cpu-usage &gt; 70%).</summary>
    public const string CpuBound = "cpu-bound";

    /// <summary>Well-known verdict for GC pressure (time-in-gc &gt; 15%).</summary>
    public const string GcPressure = "gc-pressure";

    /// <summary>Well-known verdict for ThreadPool starvation (queue-length &gt; 50).</summary>
    public const string ThreadPoolStarvation = "threadpool-starvation";

    /// <summary>Well-known verdict for lock contention (contention-count &gt; 10).</summary>
    public const string LockContention = "lock-contention";

    /// <summary>Well-known verdict for I/O-bound workloads (low CPU + queue buildup).</summary>
    public const string IoBound = "io-bound";

    /// <summary>Well-known verdict for healthy systems (no triggers fired).</summary>
    public const string Healthy = "healthy";

    // Thresholds (same as auto-hints in DiagnosticTools.SnapshotCounters).
    private const double CpuCriticalThreshold = 90;
    private const double CpuDegradedThreshold = 70;
    private const double TimeInGcCriticalThreshold = 30;
    private const double TimeInGcDegradedThreshold = 15;
    private const double QueueLengthCriticalThreshold = 200;
    private const double QueueLengthDegradedThreshold = 50;
    private const double ContentionDegradedThreshold = 10;
    private const double AllocRateDegradedThreshold = 50_000_000; // 50 MB/s
    private const double IoBoundCpuThreshold = 30;
    private const double IoBoundQueueThreshold = 10;

    /// <summary>Maximum number of top indicators to return.</summary>
    private const int MaxTopIndicators = 5;

    /// <summary>
    /// Classifies a counter snapshot into a triage result with verdict, severity, and evidence.
    /// </summary>
    /// <param name="snapshot">The counter snapshot to classify.</param>
    /// <param name="requestDurationP95">Optional HTTP request duration p95 from Meters.</param>
    /// <returns>A <see cref="TriageResult"/> with the primary verdict and any secondary findings.</returns>
    public static TriageResult Classify(CounterSnapshot snapshot, double? requestDurationP95 = null)
    {
        // Extract key counters.
        var cpu = GetCounter(snapshot, "cpu-usage");
        var timeInGc = GetCounter(snapshot, "time-in-gc");
        var queueLength = GetCounter(snapshot, "threadpool-queue-length");
        var contention = GetCounter(snapshot, "monitor-lock-contention-count");
        var allocRate = GetCounter(snapshot, "alloc-rate");
        var gen2Count = GetCounter(snapshot, "gen-2-gc-count");
        var heapSize = GetCounter(snapshot, "gc-heap-size");
        var exceptionCount = GetCounter(snapshot, "exception-count");

        // Build evidence.
        var evidence = new TriageEvidence(
            CpuUsage: cpu,
            TimeInGc: timeInGc,
            ThreadPoolQueueLength: queueLength,
            MonitorLockContentionCount: contention,
            AllocRate: allocRate,
            Gen2GcCount: gen2Count,
            GcHeapSize: heapSize,
            ExceptionCount: exceptionCount,
            RequestDurationP95: requestDurationP95);

        // Classify by priority order (same as auto-hints).
        var verdicts = new List<string>();
        var severity = TriageSeverity.Healthy;

        // CPU-bound check.
        if (cpu >= CpuDegradedThreshold)
        {
            verdicts.Add(CpuBound);
            severity = cpu >= CpuCriticalThreshold ? TriageSeverity.Critical : TriageSeverity.Degraded;
        }

        // GC pressure check.
        if (timeInGc >= TimeInGcDegradedThreshold)
        {
            verdicts.Add(GcPressure);
            var gcSeverity = timeInGc >= TimeInGcCriticalThreshold ? TriageSeverity.Critical : TriageSeverity.Degraded;
            severity = (TriageSeverity)Math.Max((int)severity, (int)gcSeverity);
        }

        // ThreadPool starvation check.
        if (queueLength >= QueueLengthDegradedThreshold)
        {
            verdicts.Add(ThreadPoolStarvation);
            var tpSeverity = queueLength >= QueueLengthCriticalThreshold ? TriageSeverity.Critical : TriageSeverity.Degraded;
            severity = (TriageSeverity)Math.Max((int)severity, (int)tpSeverity);
        }

        // Lock contention check.
        if (contention >= ContentionDegradedThreshold)
        {
            verdicts.Add(LockContention);
            severity = (TriageSeverity)Math.Max((int)severity, (int)TriageSeverity.Degraded);
        }

        // I/O-bound check (low CPU + queue buildup).
        if (cpu < IoBoundCpuThreshold && queueLength >= IoBoundQueueThreshold)
        {
            verdicts.Add(IoBound);
            severity = (TriageSeverity)Math.Max((int)severity, (int)TriageSeverity.Degraded);
        }

        // Determine primary verdict and secondaries.
        if (verdicts.Count == 0)
        {
            return new TriageResult(Healthy, TriageSeverity.Healthy, evidence, null, BuildTopIndicators(evidence));
        }

        var primary = verdicts[0];
        var secondary = verdicts.Count > 1 ? verdicts.Skip(1).ToList() : null;

        return new TriageResult(primary, severity, evidence, secondary, BuildTopIndicators(evidence));
    }

    /// <summary>
    /// Builds a ranked list of the most notable indicators, always populated regardless of verdict.
    /// This enables proactive optimization even when the system is "healthy".
    /// </summary>
    private static List<TriageIndicator> BuildTopIndicators(TriageEvidence evidence)
    {
        var indicators = new List<(string Name, double Value, string? Unit, int Score, string Level)>();

        // CPU usage: 0-30 normal, 30-70 elevated, 70-90 high, 90+ critical.
        if (evidence.CpuUsage.HasValue)
        {
            var cpu = evidence.CpuUsage.Value;
            var (score, level) = cpu switch
            {
                >= CpuCriticalThreshold => ((int)Math.Min(100, 80 + (cpu - 90) / 10 * 20), "critical"),
                >= CpuDegradedThreshold => ((int)(50 + (cpu - 70) / 20 * 30), "high"),
                >= 30 => ((int)(20 + (cpu - 30) / 40 * 30), "elevated"),
                _ => ((int)(cpu / 30 * 20), "normal")
            };
            indicators.Add(("cpu-usage", cpu, "%", score, level));
        }

        // Time in GC: 0-5 normal, 5-15 elevated, 15-30 high, 30+ critical.
        if (evidence.TimeInGc.HasValue)
        {
            var gc = evidence.TimeInGc.Value;
            var (score, level) = gc switch
            {
                >= TimeInGcCriticalThreshold => ((int)Math.Min(100, 80 + (gc - 30) / 20 * 20), "critical"),
                >= TimeInGcDegradedThreshold => ((int)(50 + (gc - 15) / 15 * 30), "high"),
                >= 5 => ((int)(20 + (gc - 5) / 10 * 30), "elevated"),
                _ => ((int)(gc / 5 * 20), "normal")
            };
            indicators.Add(("time-in-gc", gc, "%", score, level));
        }

        // ThreadPool queue: 0-10 normal, 10-50 elevated, 50-200 high, 200+ critical.
        if (evidence.ThreadPoolQueueLength.HasValue)
        {
            var queue = evidence.ThreadPoolQueueLength.Value;
            var (score, level) = queue switch
            {
                >= QueueLengthCriticalThreshold => ((int)Math.Min(100, 80 + (queue - 200) / 300 * 20), "critical"),
                >= QueueLengthDegradedThreshold => ((int)(50 + (queue - 50) / 150 * 30), "high"),
                >= IoBoundQueueThreshold => ((int)(20 + (queue - 10) / 40 * 30), "elevated"),
                _ => ((int)(queue / 10 * 20), "normal")
            };
            indicators.Add(("threadpool-queue-length", queue, "items", score, level));
        }

        // Lock contention: 0-3 normal, 3-10 elevated, 10-50 high, 50+ critical.
        if (evidence.MonitorLockContentionCount.HasValue)
        {
            var contention = evidence.MonitorLockContentionCount.Value;
            var (score, level) = contention switch
            {
                >= 50 => ((int)Math.Min(100, 80 + (contention - 50) / 50 * 20), "critical"),
                >= ContentionDegradedThreshold => ((int)(50 + (contention - 10) / 40 * 30), "high"),
                >= 3 => ((int)(20 + (contention - 3) / 7 * 30), "elevated"),
                _ => ((int)(contention / 3 * 20), "normal")
            };
            indicators.Add(("monitor-lock-contention-count", contention, "contentions", score, level));
        }

        // Allocation rate: 0-10MB/s normal, 10-50MB/s elevated, 50-100MB/s high, 100+ critical.
        if (evidence.AllocRate.HasValue)
        {
            var allocMbps = evidence.AllocRate.Value / 1_000_000.0;
            var (score, level) = allocMbps switch
            {
                >= 100 => ((int)Math.Min(100, 80 + (allocMbps - 100) / 100 * 20), "critical"),
                >= 50 => ((int)(50 + (allocMbps - 50) / 50 * 30), "high"),
                >= 10 => ((int)(20 + (allocMbps - 10) / 40 * 30), "elevated"),
                _ => ((int)(allocMbps / 10 * 20), "normal")
            };
            indicators.Add(("alloc-rate", allocMbps, "MB/s", score, level));
        }

        // Gen2 GC count: 0 normal, 1-3 elevated, 3-10 high, 10+ critical.
        if (evidence.Gen2GcCount.HasValue)
        {
            var gen2 = evidence.Gen2GcCount.Value;
            var (score, level) = gen2 switch
            {
                >= 10 => ((int)Math.Min(100, 80 + (gen2 - 10) / 10 * 20), "critical"),
                >= 3 => ((int)(50 + (gen2 - 3) / 7 * 30), "high"),
                >= 1 => ((int)(20 + (gen2 - 1) / 2 * 30), "elevated"),
                _ => (0, "normal")
            };
            indicators.Add(("gen-2-gc-count", gen2, "collections", score, level));
        }

        // Sort by score descending and take top N.
        return indicators
            .OrderByDescending(i => i.Score)
            .Take(MaxTopIndicators)
            .Select(i => new TriageIndicator(i.Name, Math.Round(i.Value, 2), i.Unit, i.Score, i.Level))
            .ToList();
    }

    private static double? GetCounter(CounterSnapshot snapshot, string name)
    {
        var counter = snapshot.Counters.FirstOrDefault(
            c => c.Provider == "System.Runtime" && c.Name == name);
        return counter?.Value;
    }
}
