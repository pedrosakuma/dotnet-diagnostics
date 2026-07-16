using DotnetDiagnostics.Core.Counters;

namespace DotnetDiagnostics.Core.Triage;

/// <summary>
/// Deterministic triage projection that separates observed threshold crossings from bounded
/// hypotheses. Rules are intentionally transparent and diagnosis-agnostic.
/// </summary>
public static class TriageClassifier
{
    /// <summary>Deprecated compatibility verdict for CPU-heavy windows.</summary>
    public const string CpuBound = "cpu-bound";

    /// <summary>Deprecated compatibility verdict for high GC-time windows.</summary>
    public const string GcPressure = "gc-pressure";

    /// <summary>Deprecated compatibility verdict for high allocation or gen-2 activity.</summary>
    public const string MemoryPressure = "memory-pressure";

    /// <summary>Deprecated compatibility verdict for a large ThreadPool backlog.</summary>
    public const string ThreadPoolStarvation = "threadpool-starvation";

    /// <summary>Deprecated compatibility verdict for high monitor-contention activity.</summary>
    public const string LockContention = "lock-contention";

    /// <summary>
    /// Deprecated compatibility value. Counter-only triage no longer emits <c>io-bound</c>
    /// because low CPU plus a queue does not identify where work is waiting.
    /// </summary>
    public const string IoBound = "io-bound";

    /// <summary>Compatibility verdict for windows with no salient observations.</summary>
    public const string Healthy = "healthy";

    /// <summary>Compatibility verdict for salient observations that do not support a legacy category.</summary>
    public const string Inconclusive = "inconclusive";

    /// <summary>Neutral assessment with no salient observed signals.</summary>
    public const string HealthyAssessment = "healthy";

    /// <summary>Neutral assessment with observations but insufficient evidence for a hypothesis.</summary>
    public const string InconclusiveAssessment = "inconclusive";

    /// <summary>Neutral assessment with one or more bounded hypotheses.</summary>
    public const string DegradedAssessment = "degraded";

    /// <summary>Neutral assessment containing a critical observed signal.</summary>
    public const string CriticalAssessment = "critical";

    /// <summary>High CPU compute-demand hypothesis.</summary>
    public const string CpuComputeDemandHypothesis = "cpu.compute-demand";

    /// <summary>GC overhead hypothesis.</summary>
    public const string GcOverheadHypothesis = "gc.overhead";

    /// <summary>ThreadPool backlog hypothesis.</summary>
    public const string ThreadPoolBacklogHypothesis = "threadpool.backlog";

    /// <summary>Synchronization contention hypothesis.</summary>
    public const string SynchronizationContentionHypothesis = "synchronization.contention";

    /// <summary>Managed-memory activity hypothesis.</summary>
    public const string ManagedMemoryActivityHypothesis = "managed-memory.activity";

    /// <summary>Waiting or backpressure hypothesis; deliberately does not assert I/O.</summary>
    public const string WaitingOrBackpressureHypothesis = "work.waiting-or-backpressure";

    private const double CpuCriticalThreshold = 90;
    private const double CpuDegradedThreshold = 70;
    private const double TimeInGcCriticalThreshold = 30;
    private const double TimeInGcDegradedThreshold = 15;
    private const double QueueLengthCriticalThreshold = 200;
    private const double QueueLengthDegradedThreshold = 50;
    private const double ContentionDegradedThreshold = 10;
    private const double AllocRateDegradedThreshold = 50_000_000; // 50 MB/s
    private const double AllocRateCriticalThreshold = 100_000_000; // 100 MB/s
    private const double Gen2GcDegradedThreshold = 3; // gen-2 collections per interval
    private const double Gen2GcCriticalThreshold = 10;
    private const double LowCpuThreshold = 30;
    private const double QueueLengthElevatedThreshold = 10;
    private const double ContentionCriticalThreshold = 50;
    private const double ExceptionHighThreshold = 10;
    private const double ExceptionCriticalThreshold = 50;
    private const double RequestDurationHighThresholdMilliseconds = 500;
    private const double RequestDurationCriticalThresholdMilliseconds = 2_000;
    private const double RequestDurationNormalThresholdMilliseconds = 100;

    /// <summary>Maximum number of top indicators to return.</summary>
    private const int MaxTopIndicators = 5;

    /// <summary>
    /// Projects a counter snapshot into observed signals, hypotheses, and legacy compatibility fields.
    /// </summary>
    /// <param name="snapshot">The counter snapshot to classify.</param>
    /// <param name="requestDurationP95">Optional HTTP request duration p95 from Meters.</param>
    /// <returns>A transparent <see cref="TriageResult"/>.</returns>
    public static TriageResult Classify(CounterSnapshot snapshot, double? requestDurationP95 = null)
    {
        var cpu = GetCounter(snapshot, "cpu-usage");
        var timeInGc = GetCounter(snapshot, "time-in-gc");
        var queueLength = GetCounter(snapshot, "threadpool-queue-length");
        var contention = GetCounter(snapshot, "monitor-lock-contention-count");
        var allocRate = GetCounter(snapshot, "alloc-rate");
        var gen2Count = GetCounter(snapshot, "gen-2-gc-count");
        var heapSize = GetCounter(snapshot, "gc-heap-size");
        var exceptionCount = GetCounter(snapshot, "exception-count");

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

        var topIndicators = BuildTopIndicators(evidence);
        var observedSignals = BuildObservedSignals(evidence);
        var hypotheses = BuildHypotheses(evidence, observedSignals);
        var severity = CalculateSeverity(observedSignals);
        var assessment = observedSignals.Count == 0
            ? HealthyAssessment
            : hypotheses.Count == 0
                ? InconclusiveAssessment
                : severity == TriageSeverity.Critical
                    ? CriticalAssessment
                    : DegradedAssessment;

        var verdicts = new List<string>();
        if (cpu >= CpuDegradedThreshold)
        {
            verdicts.Add(CpuBound);
        }

        if (timeInGc >= TimeInGcDegradedThreshold)
        {
            verdicts.Add(GcPressure);
        }

        if (queueLength >= QueueLengthDegradedThreshold)
        {
            verdicts.Add(ThreadPoolStarvation);
        }

        if (contention >= ContentionDegradedThreshold)
        {
            verdicts.Add(LockContention);
        }

        if (allocRate >= AllocRateDegradedThreshold || gen2Count >= Gen2GcDegradedThreshold)
        {
            verdicts.Add(MemoryPressure);
        }

        var primary = verdicts.Count > 0
            ? verdicts[0]
            : observedSignals.Count > 0
                ? Inconclusive
                : Healthy;
        var secondary = verdicts.Count > 1 ? verdicts.Skip(1).ToList() : null;

        return new TriageResult(
            primary,
            severity,
            evidence,
            secondary,
            topIndicators)
        {
            ModelVersion = 2,
            Assessment = assessment,
            ObservedSignals = observedSignals,
            Hypotheses = hypotheses,
        };
    }

    private static List<TriageObservedSignal> BuildObservedSignals(TriageEvidence evidence)
    {
        var signals = new List<TriageObservedSignal>();

        if (evidence.CpuUsage is { } cpu && cpu >= CpuDegradedThreshold)
        {
            var critical = cpu >= CpuCriticalThreshold;
            AddSignal(
                signals,
                "cpu.utilization",
                critical ? "critical" : "high",
                $"CPU utilization was {cpu:F1}%.",
                BuildThresholdEvidence(
                    "cpu-usage", cpu, "%", ">=",
                    critical ? CpuCriticalThreshold : CpuDegradedThreshold,
                    "The process crossed the configured CPU-utilization threshold."));
        }

        if (evidence.TimeInGc is { } timeInGc && timeInGc >= TimeInGcDegradedThreshold)
        {
            var critical = timeInGc >= TimeInGcCriticalThreshold;
            AddSignal(
                signals,
                "gc.time",
                critical ? "critical" : "high",
                $"Time in GC was {timeInGc:F1}%.",
                BuildThresholdEvidence(
                    "time-in-gc", timeInGc, "%", ">=",
                    critical ? TimeInGcCriticalThreshold : TimeInGcDegradedThreshold,
                    "The captured window crossed the configured GC-time threshold."));
        }

        if (evidence.ThreadPoolQueueLength is { } queue && queue >= QueueLengthElevatedThreshold)
        {
            var (level, threshold) = queue switch
            {
                >= QueueLengthCriticalThreshold => ("critical", QueueLengthCriticalThreshold),
                >= QueueLengthDegradedThreshold => ("high", QueueLengthDegradedThreshold),
                _ => ("elevated", QueueLengthElevatedThreshold),
            };
            AddSignal(
                signals,
                "threadpool.queue",
                level,
                $"The ThreadPool queue contained {queue:F0} work items.",
                BuildThresholdEvidence(
                    "threadpool-queue-length", queue, "items", ">=", threshold,
                    "The queue crossed the configured observation threshold; one window cannot establish whether it is transient."));
        }

        if (evidence.MonitorLockContentionCount is { } contention && contention >= ContentionDegradedThreshold)
        {
            var critical = contention >= ContentionCriticalThreshold;
            AddSignal(
                signals,
                "synchronization.monitor-contentions",
                critical ? "critical" : "high",
                $"The runtime reported {contention:F0} monitor contentions in the interval.",
                BuildThresholdEvidence(
                    "monitor-lock-contention-count", contention, "contentions", ">=",
                    critical ? ContentionCriticalThreshold : ContentionDegradedThreshold,
                    "The contention count crossed the configured observation threshold."));
        }

        if (evidence.AllocRate is { } allocRate && allocRate >= AllocRateDegradedThreshold)
        {
            var critical = allocRate >= AllocRateCriticalThreshold;
            AddSignal(
                signals,
                "memory.allocation-rate",
                critical ? "critical" : "high",
                $"The managed allocation rate was {allocRate / 1_000_000:F1} MB/s.",
                BuildThresholdEvidence(
                    "alloc-rate", allocRate / 1_000_000, "MB/s", ">=",
                    (critical ? AllocRateCriticalThreshold : AllocRateDegradedThreshold) / 1_000_000,
                    "The allocation rate crossed the configured observation threshold."));
        }

        if (evidence.Gen2GcCount is { } gen2Count && gen2Count >= Gen2GcDegradedThreshold)
        {
            var critical = gen2Count >= Gen2GcCriticalThreshold;
            AddSignal(
                signals,
                "memory.gen2-collections",
                critical ? "critical" : "high",
                $"The runtime reported {gen2Count:F0} gen-2 collections in the interval.",
                BuildThresholdEvidence(
                    "gen-2-gc-count", gen2Count, "collections", ">=",
                    critical ? Gen2GcCriticalThreshold : Gen2GcDegradedThreshold,
                    "The gen-2 collection count crossed the configured observation threshold."));
        }

        if (evidence.ExceptionCount is { } exceptions && exceptions >= ExceptionHighThreshold)
        {
            var critical = exceptions >= ExceptionCriticalThreshold;
            AddSignal(
                signals,
                "exceptions.rate",
                critical ? "critical" : "high",
                $"The runtime reported {exceptions:F0} exceptions in the interval.",
                BuildThresholdEvidence(
                    "exception-count", exceptions, "exceptions", ">=",
                    critical ? ExceptionCriticalThreshold : ExceptionHighThreshold,
                    "The exception count crossed the configured observation threshold."));
        }

        var requestDurationMilliseconds = ToMilliseconds(evidence.RequestDurationP95);
        if (requestDurationMilliseconds is { } p95 && p95 >= RequestDurationHighThresholdMilliseconds)
        {
            var critical = p95 >= RequestDurationCriticalThresholdMilliseconds;
            AddSignal(
                signals,
                "http.request-duration-p95",
                critical ? "critical" : "high",
                $"HTTP request duration p95 was {p95:F0} ms.",
                BuildThresholdEvidence(
                    "request-duration-p95", p95, "ms", ">=",
                    critical ? RequestDurationCriticalThresholdMilliseconds : RequestDurationHighThresholdMilliseconds,
                    "The observed latency crossed the configured request-duration threshold."));
        }

        return signals;
    }

    private static List<TriageHypothesis> BuildHypotheses(
        TriageEvidence evidence,
        IReadOnlyList<TriageObservedSignal> observedSignals)
    {
        var hypotheses = new List<TriageHypothesis>();
        var requestDurationMilliseconds = ToMilliseconds(evidence.RequestDurationP95);

        if (evidence.CpuUsage is { } cpu && cpu >= CpuDegradedThreshold)
        {
            var highConfidence = cpu >= CpuCriticalThreshold;
            hypotheses.Add(new TriageHypothesis(
                CpuComputeDemandHypothesis,
                highConfidence ? "high" : "moderate",
                "The process spent a large share of the window doing compute work; the hot code path and resource limit are not identified by counters.",
                [BuildThresholdEvidence(
                    "cpu-usage", cpu, "%", ">=",
                    highConfidence ? CpuCriticalThreshold : CpuDegradedThreshold,
                    highConfidence
                        ? "CPU crossed the critical threshold used to assign high confidence."
                        : "CPU crossed the threshold used to emit the compute-demand hypothesis.")],
                [],
                "Capture on-CPU samples and inspect exclusive hot frames before assigning a cause."));
        }

        if (evidence.TimeInGc is { } timeInGc && timeInGc >= TimeInGcDegradedThreshold)
        {
            var highConfidence = timeInGc >= TimeInGcCriticalThreshold;
            var supporting = new List<TriageEvidenceItem>
            {
                BuildThresholdEvidence(
                    "time-in-gc", timeInGc, "%", ">=",
                    highConfidence ? TimeInGcCriticalThreshold : TimeInGcDegradedThreshold,
                    highConfidence
                        ? "GC time crossed the critical threshold used to assign high confidence."
                        : "GC time crossed the threshold used to emit the GC-overhead hypothesis."),
            };
            if (evidence.AllocRate is { } gcAllocRate && gcAllocRate >= AllocRateDegradedThreshold)
            {
                supporting.Add(BuildThresholdEvidence(
                    "alloc-rate", gcAllocRate / 1_000_000, "MB/s", ">=",
                    AllocRateDegradedThreshold / 1_000_000,
                    "High allocation activity corroborates the GC-time signal."));
            }

            hypotheses.Add(new TriageHypothesis(
                GcOverheadHypothesis,
                highConfidence ? "high" : "moderate",
                "Garbage collection consumed a material share of the captured window; counters do not distinguish allocation churn, heap shape, or induced collections.",
                supporting,
                [],
                "Collect GC events and allocation evidence to distinguish pause behavior from allocation activity."));
        }

        if (evidence.ThreadPoolQueueLength is { } queue && queue >= QueueLengthDegradedThreshold)
        {
            var highConfidence = queue >= QueueLengthCriticalThreshold
                && requestDurationMilliseconds >= RequestDurationHighThresholdMilliseconds;
            var supporting = new List<TriageEvidenceItem>
            {
                BuildThresholdEvidence(
                    "threadpool-queue-length", queue, "items", ">=",
                    highConfidence ? QueueLengthCriticalThreshold : QueueLengthDegradedThreshold,
                    highConfidence
                        ? "The queue crossed the critical threshold used with latency to assign high confidence."
                        : "The queue crossed the threshold used to emit the ThreadPool-backlog hypothesis."),
            };
            if (requestDurationMilliseconds is { } highP95 && highP95 >= RequestDurationHighThresholdMilliseconds)
            {
                supporting.Add(BuildThresholdEvidence(
                    "request-duration-p95", highP95, "ms", ">=", RequestDurationHighThresholdMilliseconds,
                    "Elevated request latency corroborates that the backlog may be user-visible."));
            }

            var contradicting = BuildNormalLatencyContradiction(requestDurationMilliseconds);
            hypotheses.Add(new TriageHypothesis(
                ThreadPoolBacklogHypothesis,
                highConfidence ? "high" : "moderate",
                "Work was queued faster than the ThreadPool completed it during the window; the counters alone do not prove starvation or identify blocking.",
                supporting,
                contradicting,
                "Collect ThreadPool events and blocking stacks to distinguish sustained starvation, blocking, and transient demand."));
        }

        if (evidence.MonitorLockContentionCount is { } contention && contention >= ContentionDegradedThreshold)
        {
            var highConfidence = contention >= ContentionCriticalThreshold
                && requestDurationMilliseconds >= RequestDurationHighThresholdMilliseconds;
            var supporting = new List<TriageEvidenceItem>
            {
                BuildThresholdEvidence(
                    "monitor-lock-contention-count", contention, "contentions", ">=",
                    highConfidence ? ContentionCriticalThreshold : ContentionDegradedThreshold,
                    highConfidence
                        ? "Contention crossed the critical threshold used with latency to assign high confidence."
                        : "Contention crossed the threshold used to emit the synchronization hypothesis."),
            };
            if (requestDurationMilliseconds is { } highP95 && highP95 >= RequestDurationHighThresholdMilliseconds)
            {
                supporting.Add(BuildThresholdEvidence(
                    "request-duration-p95", highP95, "ms", ">=", RequestDurationHighThresholdMilliseconds,
                    "Elevated request latency corroborates possible user-visible impact."));
            }

            hypotheses.Add(new TriageHypothesis(
                SynchronizationContentionHypothesis,
                highConfidence ? "high" : "moderate",
                "Monitor contention was elevated; the counter does not identify the contended lock, owner, or wait duration.",
                supporting,
                BuildNormalLatencyContradiction(requestDurationMilliseconds),
                "Collect contention events and inspect call-site and owner-thread groupings."));
        }

        if (evidence.CpuUsage is { } lowCpu
            && lowCpu < LowCpuThreshold
            && evidence.ThreadPoolQueueLength is { } waitingQueue
            && waitingQueue >= QueueLengthElevatedThreshold
            && requestDurationMilliseconds is { } waitingP95
            && waitingP95 >= RequestDurationHighThresholdMilliseconds)
        {
            var highConfidence = waitingQueue >= QueueLengthDegradedThreshold
                && waitingP95 >= RequestDurationCriticalThresholdMilliseconds;
            hypotheses.Add(new TriageHypothesis(
                WaitingOrBackpressureHypothesis,
                highConfidence ? "high" : "moderate",
                "Low CPU, queued work, and elevated request latency co-occurred. This supports waiting or backpressure, but does not identify I/O, a downstream dependency, or transient demand.",
                [
                    BuildThresholdEvidence("cpu-usage", lowCpu, "%", "<", LowCpuThreshold,
                        "Low CPU makes on-CPU saturation less likely during this window."),
                    BuildThresholdEvidence("threadpool-queue-length", waitingQueue, "items", ">=",
                        highConfidence ? QueueLengthDegradedThreshold : QueueLengthElevatedThreshold,
                        highConfidence
                            ? "The queue crossed the escalation threshold used to assign high confidence."
                            : "Queued work shows that some work was waiting to run or complete."),
                    BuildThresholdEvidence("request-duration-p95", waitingP95, "ms", ">=",
                        highConfidence
                            ? RequestDurationCriticalThresholdMilliseconds
                            : RequestDurationHighThresholdMilliseconds,
                        highConfidence
                            ? "Request latency crossed the critical threshold used to assign high confidence."
                            : "Elevated request latency shows concurrent user-visible delay."),
                ],
                [],
                "Capture activities, networking events, and thread stacks to determine where work is waiting; do not infer I/O from counters alone."));
        }

        if (evidence.AllocRate >= AllocRateDegradedThreshold || evidence.Gen2GcCount >= Gen2GcDegradedThreshold)
        {
            var highConfidence = evidence.AllocRate >= AllocRateCriticalThreshold
                || evidence.Gen2GcCount >= Gen2GcCriticalThreshold;
            var supporting = new List<TriageEvidenceItem>();
            if (evidence.AllocRate is { } allocRate && allocRate >= AllocRateDegradedThreshold)
            {
                var criticalAllocation = allocRate >= AllocRateCriticalThreshold;
                supporting.Add(BuildThresholdEvidence(
                    "alloc-rate", allocRate / 1_000_000, "MB/s", ">=",
                    (criticalAllocation ? AllocRateCriticalThreshold : AllocRateDegradedThreshold) / 1_000_000,
                    criticalAllocation
                        ? "Allocation crossed the critical threshold that supports high confidence."
                        : "Allocation crossed the threshold used to emit the managed-memory hypothesis."));
            }
            if (evidence.Gen2GcCount is { } gen2Count && gen2Count >= Gen2GcDegradedThreshold)
            {
                var criticalGen2 = gen2Count >= Gen2GcCriticalThreshold;
                supporting.Add(BuildThresholdEvidence(
                    "gen-2-gc-count", gen2Count, "collections", ">=",
                    criticalGen2 ? Gen2GcCriticalThreshold : Gen2GcDegradedThreshold,
                    criticalGen2
                        ? "Gen-2 collections crossed the critical threshold that supports high confidence."
                        : "Gen-2 collections crossed the threshold used to emit the managed-memory hypothesis."));
            }

            var contradicting = new List<TriageEvidenceItem>();
            if (evidence.TimeInGc is { } lowGcTime && lowGcTime < 5)
            {
                contradicting.Add(BuildThresholdEvidence(
                    "time-in-gc", lowGcTime, "%", "<", 5,
                    "Low GC CPU cost limits what can be concluded about current runtime impact."));
            }

            hypotheses.Add(new TriageHypothesis(
                ManagedMemoryActivityHypothesis,
                highConfidence ? "high" : "moderate",
                "Managed allocation or full-collection activity was elevated; counters do not distinguish short-lived churn from retained growth.",
                supporting,
                contradicting,
                "Collect allocation samples, GC events, and a memory trend before distinguishing churn from retention."));
        }

        return hypotheses
            .OrderByDescending(static hypothesis => ConfidenceRank(hypothesis.Confidence))
            .ThenByDescending(hypothesis => StrongestObservedSignalRank(hypothesis, observedSignals))
            .ToList();
    }

    private static int StrongestObservedSignalRank(
        TriageHypothesis hypothesis,
        IReadOnlyList<TriageObservedSignal> observedSignals)
    {
        var rank = 0;
        foreach (var signal in observedSignals)
        {
            if (signal.Evidence.Any(signalEvidence =>
                    hypothesis.SupportingEvidence.Any(hypothesisEvidence =>
                        string.Equals(signalEvidence.Name, hypothesisEvidence.Name, StringComparison.Ordinal))))
            {
                rank = Math.Max(rank, SignalLevelRank(signal.Level));
            }
        }

        return rank;
    }

    private static int ConfidenceRank(string confidence) => confidence switch
    {
        "high" => 2,
        "moderate" => 1,
        _ => 0,
    };

    private static int SignalLevelRank(string level) => level switch
    {
        "critical" => 3,
        "high" => 2,
        "elevated" => 1,
        _ => 0,
    };

    private static TriageSeverity CalculateSeverity(IReadOnlyList<TriageObservedSignal> signals)
    {
        if (signals.Any(static signal => signal.Level == "critical"))
        {
            return TriageSeverity.Critical;
        }

        return signals.Any(static signal => signal.Level == "high")
            ? TriageSeverity.Degraded
            : TriageSeverity.Healthy;
    }

    private static List<TriageEvidenceItem> BuildNormalLatencyContradiction(double? requestDurationMilliseconds)
    {
        if (requestDurationMilliseconds is not { } p95 || p95 >= RequestDurationNormalThresholdMilliseconds)
        {
            return [];
        }

        return
        [
            BuildThresholdEvidence(
                "request-duration-p95", p95, "ms", "<", RequestDurationNormalThresholdMilliseconds,
                "Normal request latency weakens the case that the observed signal affected requests in this window."),
        ];
    }

    private static void AddSignal(
        List<TriageObservedSignal> signals,
        string name,
        string level,
        string summary,
        TriageEvidenceItem evidence)
        => signals.Add(new TriageObservedSignal(name, level, summary, [evidence]));

    private static TriageEvidenceItem BuildThresholdEvidence(
        string name,
        double value,
        string? unit,
        string comparison,
        double threshold,
        string rationale)
        => new(name, Math.Round(value, 2), unit, comparison, threshold, rationale);

    private static double? ToMilliseconds(double? seconds) => seconds * 1_000;

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
                >= QueueLengthElevatedThreshold => ((int)(20 + (queue - 10) / 40 * 30), "elevated"),
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

        // Exception count: 0 normal, 1-10 elevated, 10-50 high, 50+ critical.
        if (evidence.ExceptionCount.HasValue && evidence.ExceptionCount.Value > 0)
        {
            var exceptions = evidence.ExceptionCount.Value;
            var (score, level) = exceptions switch
            {
                >= 50 => ((int)Math.Min(100, 80 + (exceptions - 50) / 50 * 20), "critical"),
                >= 10 => ((int)(50 + (exceptions - 10) / 40 * 30), "high"),
                >= 1 => ((int)(20 + (exceptions - 1) / 9 * 30), "elevated"),
                _ => (0, "normal")
            };
            indicators.Add(("exception-count", exceptions, "exceptions", score, level));
        }

        // Request duration P95: <100ms normal, 100-500ms elevated, 500ms-2s high, >2s critical.
        if (evidence.RequestDurationP95.HasValue && evidence.RequestDurationP95.Value > 0)
        {
            var p95Ms = evidence.RequestDurationP95.Value * 1000; // Convert seconds to ms
            var (score, level) = p95Ms switch
            {
                >= 2000 => ((int)Math.Min(100, 80 + (p95Ms - 2000) / 3000 * 20), "critical"),
                >= 500 => ((int)(50 + (p95Ms - 500) / 1500 * 30), "high"),
                >= 100 => ((int)(20 + (p95Ms - 100) / 400 * 30), "elevated"),
                _ => ((int)(p95Ms / 100 * 20), "normal")
            };
            indicators.Add(("request-duration-p95", Math.Round(p95Ms, 0), "ms", score, level));
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
