using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace BadCodeSample;

internal static class CpuHotspotWorkload
{
    private const int MaximumWorkers = 8;
    private static long _sink;

    public static Result Run(int alphaWorkers, int betaWorkers, int milliseconds)
    {
        if (alphaWorkers < 0 || betaWorkers < 0
            || alphaWorkers + betaWorkers is < 1 or > MaximumWorkers)
        {
            throw new ArgumentOutOfRangeException(
                nameof(alphaWorkers),
                $"The total worker count must be between 1 and {MaximumWorkers}.");
        }

        var duration = TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 250, 10_000));
        using var ready = new CountdownEvent(alphaWorkers + betaWorkers);
        using var start = new ManualResetEventSlim();
        var threads = new List<Thread>(alphaWorkers + betaWorkers);
        var iterations = new long[alphaWorkers + betaWorkers];

        for (var index = 0; index < alphaWorkers; index++)
        {
            var workerIndex = threads.Count;
            threads.Add(StartWorker(
                $"cpu-hotspot-alpha-{index}",
                ready,
                start,
                () => iterations[workerIndex] = RunAlpha(duration)));
        }

        for (var index = 0; index < betaWorkers; index++)
        {
            var workerIndex = threads.Count;
            threads.Add(StartWorker(
                $"cpu-hotspot-beta-{index}",
                ready,
                start,
                () => iterations[workerIndex] = RunBeta(duration)));
        }

        ready.Wait();
        start.Set();
        foreach (var thread in threads)
        {
            thread.Join();
        }

        return new(
            alphaWorkers,
            betaWorkers,
            duration.TotalMilliseconds,
            iterations.Take(alphaWorkers).Sum(),
            iterations.Skip(alphaWorkers).Sum());
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    internal static long RunAlpha(TimeSpan duration)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        ulong value = 0x9E3779B97F4A7C15;
        long iterations = 0;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            for (var index = 0; index < 4096; index++)
            {
                value ^= value << 7;
                value ^= value >> 9;
                value *= 0xD6E8FEB86659FD93;
            }
            iterations += 4096;
        }

        Volatile.Write(ref _sink, unchecked((long)value));
        return iterations;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    internal static long RunBeta(TimeSpan duration)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        ulong value = 0xA0761D6478BD642F;
        long iterations = 0;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            for (var index = 0; index < 4096; index++)
            {
                value += 0xE7037ED1A0B428DB;
                value = BitOperations.RotateLeft(value, 17);
                value ^= value >> 13;
            }
            iterations += 4096;
        }

        Volatile.Write(ref _sink, unchecked((long)value));
        return iterations;
    }

    private static Thread StartWorker(
        string name,
        CountdownEvent ready,
        ManualResetEventSlim start,
        Action action)
    {
        var thread = new Thread(() =>
        {
            ready.Signal();
            start.Wait();
            action();
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.Start();
        return thread;
    }

    internal sealed record Result(
        int AlphaWorkers,
        int BetaWorkers,
        double DurationMilliseconds,
        long AlphaIterations,
        long BetaIterations);
}
