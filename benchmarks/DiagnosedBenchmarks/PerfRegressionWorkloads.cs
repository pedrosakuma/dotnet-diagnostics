using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using DotnetDiagnostics.BenchmarkDotNet;

namespace DiagnosedBenchmarks;

public class PerfRegressionCleanBenchmarks
{
    [Benchmark]
    public int CpuLookupBaseline() => PerfRegressionWorkloads.OrdinalLookupBaseline();

    [Benchmark]
    public int CpuLookupCandidate() => PerfRegressionWorkloads.CultureAwareLookupCandidate();

    [Benchmark]
    public int AllocationBaseline() => PerfRegressionWorkloads.AllocationBaseline();

    [Benchmark]
    public int AllocationCandidate() => PerfRegressionWorkloads.AllocationCandidate();

    [Benchmark]
    public int WaitBaseline() => PerfRegressionWorkloads.AsyncBaseline();

    [Benchmark]
    public int WaitCandidate() => PerfRegressionWorkloads.SyncOverAsyncCandidate();

    [Benchmark]
    public int ControlBaseline() => PerfRegressionWorkloads.OrdinalLookupBaseline();

    [Benchmark]
    public int ControlCandidate() => PerfRegressionWorkloads.OrdinalLookupBaseline();
}

public class PerfRegressionDiagnosticBenchmarks
{
    private const int DiagnosticSeconds = 5;

    [Benchmark]
    [DiagnosticKind(BenchmarkDiagnosticKind.Cpu, DurationSeconds = 3)]
    public long DiagnoseCpuLookupCandidate()
    {
        var timer = Stopwatch.StartNew();
        long checksum = 0;
        while (timer.Elapsed.TotalSeconds < DiagnosticSeconds)
        {
            checksum += PerfRegressionWorkloads.CultureAwareLookupCandidate();
        }
        return checksum;
    }

    [Benchmark]
    [DiagnosticKind(BenchmarkDiagnosticKind.Allocation, DurationSeconds = 3)]
    public long DiagnoseAllocationCandidate()
    {
        var timer = Stopwatch.StartNew();
        long checksum = 0;
        while (timer.Elapsed.TotalSeconds < DiagnosticSeconds)
        {
            checksum += PerfRegressionWorkloads.AllocationCandidate();
        }
        return checksum;
    }

    [Benchmark]
    [DiagnosticKind(BenchmarkDiagnosticKind.ThreadPool, DurationSeconds = 3)]
    public long DiagnoseWaitCandidate()
    {
        var deadline = DateTime.UtcNow.AddSeconds(DiagnosticSeconds);
        var workers = Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount * 2, 8, 32))
            .Select(_ => Task.Run(() =>
            {
                while (DateTime.UtcNow < deadline)
                {
                    PerfRegressionWorkloads.SyncOverAsyncCandidate();
                }
            }))
            .ToArray();

        Task.WaitAll(workers);
        return workers.Length;
    }
}

internal static class PerfRegressionWorkloads
{
    private const int LookupPasses = 256;
    private const int BaselinePayloadCount = 10;
    private const int CandidateExtraPayloadCount = 2;
    private const string Needle = "repository";

    private static readonly string[] LookupValues =
    [
        "runtime", "collector", "allocation", "threadpool", "contention", "diagnostics", "benchmark", "snapshot",
        "baseline", "candidate", "variance", "confidence", "artifact", "provenance", "compatible", "regression",
        "improvement", "inconclusive", "environment", "architecture", "workload", "parameter", "throughput", "latency",
        "ordinal", "culture", "comparison", "evidence", "attribution", "measurement", "control", Needle,
    ];

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int OrdinalLookupBaseline() => OrdinalLookupCore();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int CultureAwareLookupCandidate()
    {
        var matches = OrdinalLookupCore();
        for (var repeat = 0; repeat < 8; repeat++)
        {
            for (var index = 0; index < LookupValues.Length; index++)
            {
                if (CultureInfo.InvariantCulture.CompareInfo.Compare(
                    LookupValues[index],
                    Needle,
                    CompareOptions.IgnoreCase) == 0)
                {
                    matches++;
                }
            }
        }
        return matches;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int OrdinalLookupCore()
    {
        var matches = 0;
        for (var pass = 0; pass < LookupPasses; pass++)
        {
            foreach (var value in LookupValues)
            {
                if (string.Equals(value, Needle, StringComparison.OrdinalIgnoreCase))
                {
                    matches++;
                }
            }
        }
        return matches;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int AllocationBaseline()
    {
        var payload = CreatePayload(BaselinePayloadCount);
        var result = payload[0].Length;
        GC.KeepAlive(payload);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int AllocationCandidate()
    {
        var payload = CreatePayload(BaselinePayloadCount + CandidateExtraPayloadCount);
        var result = payload[0].Length;
        GC.KeepAlive(payload);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int AsyncBaseline() => 1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int SyncOverAsyncCandidate()
        => Task.Run(static () => 1).GetAwaiter().GetResult();

    private static string[] CreatePayload(int count)
    {
        var payload = new string[count];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = new string('x', 128);
        }
        return payload;
    }
}
