using DotnetDiagnostics.Core.Counters;

namespace DotnetDiagnostics.Core.ReplicaCounters;

/// <summary>
/// Pure, side-effect-free engine that turns per-Pod <see cref="CounterSnapshot"/> windows into one
/// cross-replica <see cref="ReplicaCounterSkew"/> for the canonical triage metrics (gc-heap-size,
/// cpu, threadpool-queue). Deliberately decoupled from the orchestrator fan-out so it can be
/// unit-tested without a Kubernetes topology: the MCP layer collects the snapshots, this class
/// compares them and flags the outlier replica.
/// </summary>
/// <remarks>
/// The outlier is the replica with the largest summed per-metric z-score (deviation from the mean
/// divided by the population stddev). Metrics no Pod reported are dropped from the result; this is a
/// best-effort live comparison, not a baseline replay.
/// </remarks>
public static class ReplicaCounterSkewAnalyzer
{
    /// <summary>Canonical token → (Provider, EventCounter name) the fan-out compares across replicas.</summary>
    public static readonly IReadOnlyList<(string Metric, string Provider, string Name)> HeadlineMetrics = new[]
    {
        ("cpu", "System.Runtime", "cpu-usage"),
        ("gc-heap-size", "System.Runtime", "gc-heap-size"),
        ("threadpool-queue", "System.Runtime", "threadpool-queue-length"),
    };

    /// <summary>Projects a single Pod's snapshot to the canonical headline metric values.</summary>
    public static ReplicaCounterReading Project(string podName, CounterSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(podName);
        ArgumentNullException.ThrowIfNull(snapshot);

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (metric, provider, name) in HeadlineMetrics)
        {
            var hit = snapshot.Counters.FirstOrDefault(c =>
                string.Equals(c.Provider, provider, StringComparison.Ordinal) &&
                string.Equals(c.Name, name, StringComparison.Ordinal));
            if (hit is not null)
            {
                values[metric] = hit.Value;
            }
        }

        return new ReplicaCounterReading(podName, snapshot.ProcessId, values);
    }

    /// <summary>Compares the supplied per-Pod readings and identifies the outlier replica.</summary>
    public static ReplicaCounterSkew Analyze(IReadOnlyList<ReplicaCounterReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);

        var warnings = new List<string>();
        var dispersions = new List<ReplicaMetricDispersion>();
        var podScores = new Dictionary<string, double>(StringComparer.Ordinal);

        if (readings.Count == 0)
        {
            warnings.Add("No replica counter snapshots were collected; nothing to compare.");
            return new ReplicaCounterSkew(0, readings, dispersions, null, 0, warnings);
        }

        if (readings.Count == 1)
        {
            warnings.Add("Only one replica reported counters — dispersion needs >=2 attached Pods to identify an outlier.");
        }

        foreach (var (metric, _, _) in HeadlineMetrics)
        {
            var samples = readings
                .Where(r => r.Values.ContainsKey(metric))
                .Select(r => (r.PodName, Value: r.Values[metric]))
                .ToArray();

            if (samples.Length == 0)
            {
                warnings.Add($"No attached Pod reported metric '{metric}'.");
                continue;
            }

            var values = samples.Select(s => s.Value).ToArray();
            var mean = values.Average();
            var min = values.Min();
            var max = values.Max();
            var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Length;
            var stdDev = Math.Sqrt(variance);
            var spread = max - min;
            var relativeSpread = Math.Abs(mean) > double.Epsilon ? spread / Math.Abs(mean) : 0;

            dispersions.Add(new ReplicaMetricDispersion(
                metric,
                Round(min),
                Round(max),
                Round(mean),
                Round(stdDev),
                Round(spread),
                Round(relativeSpread),
                samples.OrderBy(s => s.Value).First().PodName,
                samples.OrderByDescending(s => s.Value).First().PodName));

            if (stdDev > double.Epsilon)
            {
                foreach (var sample in samples)
                {
                    var z = Math.Abs(sample.Value - mean) / stdDev;
                    podScores[sample.PodName] = podScores.GetValueOrDefault(sample.PodName) + z;
                }
            }
        }

        string? outlierPod = null;
        double outlierScore = 0;
        // With only two replicas every metric's absolute z-score is symmetric, so podScores ties
        // and the "outlier" would just be whichever pod sorted first — meaningless. Require ≥3
        // replicas before naming an outlier; below that report dispersion only.
        if (podScores.Count >= 3)
        {
            var top = podScores.OrderByDescending(kv => kv.Value).First();
            if (top.Value > double.Epsilon)
            {
                outlierPod = top.Key;
                outlierScore = Round(top.Value);
            }
        }

        if (outlierPod is null && readings.Count == 2)
        {
            warnings.Add("Only two replicas: z-score deviations are symmetric so no single outlier can be named — compare the dispersion spreads directly.");
        }
        else if (outlierPod is null && readings.Count > 2)
        {
            warnings.Add("Replicas are within noise on every metric — no outlier stands out.");
        }

        return new ReplicaCounterSkew(readings.Count, readings, dispersions, outlierPod, outlierScore, warnings);
    }

    private static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
