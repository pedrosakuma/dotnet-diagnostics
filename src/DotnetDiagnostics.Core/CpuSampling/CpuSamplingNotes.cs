using System.Globalization;

namespace DotnetDiagnostics.Core.CpuSampling;

/// <summary>
/// Interpretation notes for CPU snapshots (issue #1147). They describe the measured sample rate
/// and a backend-specific caveat; they never change a sample count.
/// </summary>
internal static class CpuSamplingNotes
{
    /// <summary>Distinct thread ids tracked per capture; beyond this the count is a lower bound.</summary>
    internal const int MaxTrackedThreads = 4096;

    /// <summary>
    /// Tracks distinct sampled thread ids up to <see cref="MaxTrackedThreads"/>. A thread counts as
    /// sampled when at least one retained sample carries its id.
    /// </summary>
    internal sealed class ThreadTracker
    {
        private readonly HashSet<int> _ids = [];

        public bool Saturated { get; private set; }

        public int Count => _ids.Count;

        public void Add(int threadId)
        {
            if (_ids.Count >= MaxTrackedThreads)
            {
                Saturated |= !_ids.Contains(threadId);
                return;
            }

            _ids.Add(threadId);
        }
    }

    internal static IReadOnlyList<string> ForEventPipe(
        long totalSamples, TimeSpan duration, int sampledThreads, bool threadsSaturated)
    {
        var notes = new List<string>(2);
        var rate = RateNote(totalSamples, duration, sampledThreads, threadsSaturated);
        if (rate is not null)
        {
            notes.Add(rate);
        }

        notes.Add(
            "EventPipe sampling under-counts short calls: in measured runs, code paths made of calls under about 100 µs " +
            "were observed at roughly 1-10% of their expected sample share (about 60% at 100 µs, about 86% at 200 µs). " +
            "Treat small percentages for such paths as lower bounds that may be off by 10x or more, and cross-check them with " +
            "`--cpu-backend os` or a Stopwatch measurement. Sample counts are reported as captured and are not adjusted.");
        return notes;
    }

    internal static IReadOnlyList<string> ForOsBackend(
        long totalSamples, TimeSpan duration, int sampledThreads, bool threadsSaturated, int configuredHz)
    {
        var notes = new List<string>(2)
        {
            string.Create(CultureInfo.InvariantCulture,
                $"The os backend (perf) samples at a fixed {configuredHz} Hz. Sample counts are reported as captured and are not adjusted."),
        };
        var rate = RateNote(totalSamples, duration, sampledThreads, threadsSaturated);
        if (rate is not null)
        {
            notes.Add(rate);
        }

        return notes;
    }

    private static string? RateNote(long totalSamples, TimeSpan duration, int sampledThreads, bool threadsSaturated)
    {
        if (totalSamples <= 0 || duration <= TimeSpan.Zero || sampledThreads <= 0)
        {
            return null;
        }

        var perThread = totalSamples / duration.TotalSeconds / sampledThreads;
        var bound = threadsSaturated ? "at most " : string.Empty;
        var threads = threadsSaturated ? string.Create(CultureInfo.InvariantCulture, $"at least {sampledThreads:N0}") : sampledThreads.ToString("N0", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture,
            $"Effective sample rate: {bound}{perThread:F0} samples/s per sampled thread ({totalSamples:N0} samples over the requested {duration.TotalSeconds:F1} s across {threads} thread(s) with at least one sample).");
    }
}
