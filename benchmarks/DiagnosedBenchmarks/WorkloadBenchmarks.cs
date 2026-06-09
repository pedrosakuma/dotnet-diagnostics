using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using DotnetDiagnostics.BenchmarkDotNet;

namespace DiagnosedBenchmarks;

/// <summary>
/// Two intentionally pathological workloads, each tagged with the diagnostics <c>collect</c> kind
/// that explains it. Each method runs for a few seconds so the EventPipe collection window has real
/// activity to observe — these are diagnosis fixtures, not micro-benchmarks.
/// </summary>
public class WorkloadBenchmarks
{
    private const int RunSeconds = 6;

    /// <summary>Allocation churn → high Gen0 collection rate. Diagnosed with the GC collector.</summary>
    [Benchmark]
    [DiagnosticKind("gc", durationSeconds: 5)]
    public long GcChurn()
    {
        var sw = Stopwatch.StartNew();
        long checksum = 0;
        while (sw.Elapsed.TotalSeconds < RunSeconds)
        {
            for (var i = 0; i < 2_000; i++)
            {
                var buffer = new byte[4_096];
                buffer[i % buffer.Length] = (byte)i;
                checksum += buffer[(i * 7) % buffer.Length];
            }
        }

        return checksum;
    }

    /// <summary>Many threads hammering one lock → heavy contention. Diagnosed with the contention collector.</summary>
    [Benchmark]
    [DiagnosticKind("contention", durationSeconds: 5)]
    public long LockStorm()
    {
        var gate = new object();
        long counter = 0;
        var deadline = Stopwatch.StartNew();

        var workers = Enumerable.Range(0, Environment.ProcessorCount * 2)
            .Select(_ => Task.Run(() =>
            {
                while (deadline.Elapsed.TotalSeconds < RunSeconds)
                {
                    for (var i = 0; i < 200; i++)
                    {
                        lock (gate)
                        {
                            counter++;
                        }
                    }
                }
            }))
            .ToArray();

        Task.WaitAll(workers);
        return counter;
    }
}
