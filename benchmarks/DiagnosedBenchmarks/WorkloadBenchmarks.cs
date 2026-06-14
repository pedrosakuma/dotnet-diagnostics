using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using DotnetDiagnostics.BenchmarkDotNet;

namespace DiagnosedBenchmarks;

/// <summary>
/// Four intentionally pathological workloads, each tagged with the diagnostics <c>collect</c> kind
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

    /// <summary>
    /// Tight numeric loop with a real self-cost hotspot. Diagnosed with the CPU sampler, which
    /// attributes per-frame exclusive (self) vs inclusive (self + callees) samples.
    /// </summary>
    [Benchmark]
    [DiagnosticKind("cpu", durationSeconds: 5)]
    public long CpuHotPath()
    {
        var sw = Stopwatch.StartNew();
        long acc = 0;
        while (sw.Elapsed.TotalSeconds < RunSeconds)
        {
            acc += Crunch(acc);
        }

        return acc;
    }

    private static long Crunch(long seed)
    {
        long sum = seed;
        for (var i = 1; i < 50_000; i++)
        {
            sum += (i * 2654435761L) ^ (sum >> 3);
        }

        return sum;
    }

    /// <summary>
    /// Per-type allocation churn. Diagnosed with the allocation sampler (GCAllocationTick), which
    /// attributes allocated bytes by managed type (SOH/LOH) — the per-type complement to
    /// MemoryDiagnoser's Allocated column.
    /// </summary>
    [Benchmark]
    [DiagnosticKind("allocation", durationSeconds: 5)]
    public long AllocChurn()
    {
        var sw = Stopwatch.StartNew();
        long checksum = 0;
        var sink = new List<string>(1_024);
        while (sw.Elapsed.TotalSeconds < RunSeconds)
        {
            for (var i = 0; i < 5_000; i++)
            {
                var text = new string('x', 256);
                var payload = new byte[1_024];
                sink.Add(text);
                checksum += text.Length + payload.Length;
                if (sink.Count > 512)
                {
                    sink.Clear();
                }
            }
        }

        return checksum;
    }
}
