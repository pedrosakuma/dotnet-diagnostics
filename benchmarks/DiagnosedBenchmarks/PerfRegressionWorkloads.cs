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
    public Task<int> WaitBaseline() => PerfRegressionWorkloads.AsyncBaseline();

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

}

public class PerfRegressionWaitDiagnosticBenchmarks
{
    private const int EventPipeStartupDelayMilliseconds = 2_000;
    private int _originalWorkerMin;
    private int _originalIocpMin;
    private int _workers;

    [GlobalSetup]
    public void Setup()
    {
        ThreadPool.GetMinThreads(out _originalWorkerMin, out _originalIocpMin);
        _workers = Math.Clamp(ThreadPool.ThreadCount + 8, 12, 64);
        if (!ThreadPool.SetMinThreads(_workers, _originalIocpMin))
        {
            throw new InvalidOperationException($"Unable to set the ThreadPool worker minimum to {_workers}.");
        }

        using var ready = new Barrier(_workers + 1);
        var warmup = Enumerable.Range(0, _workers)
            .Select(_ => Task.Run(() => ready.SignalAndWait()))
            .ToArray();
        if (!ready.SignalAndWait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("ThreadPool workers did not warm up before the diagnostic run.");
        }
        Task.WaitAll(warmup);
    }

    [GlobalCleanup]
    public void Cleanup()
        => ThreadPool.SetMinThreads(_originalWorkerMin, _originalIocpMin);

    [Benchmark]
    [DiagnosticKind(BenchmarkDiagnosticKind.ThreadPool, DurationSeconds = 8)]
    public int DiagnoseWaitCandidate()
        => RunWaitFixture(blockOnAsync: true);

    [Benchmark]
    [DiagnosticKind(BenchmarkDiagnosticKind.ThreadPool, DurationSeconds = 8)]
    public int DiagnoseWaitControl()
        => RunWaitFixture(blockOnAsync: false);

    private int RunWaitFixture(bool blockOnAsync)
    {
        Thread.Sleep(EventPipeStartupDelayMilliseconds);

        using var ready = new Barrier(_workers + 1);
        var operations = blockOnAsync
            ? Enumerable.Range(0, _workers)
                .Select(_ => Task.Run(() =>
                {
                    ready.SignalAndWait();
                    return PerfRegressionWorkloads.SyncOverAsyncCandidate();
                }))
                .ToArray()
            : Enumerable.Range(0, _workers)
                .Select(_ => Task.Run(async () =>
                {
                    ready.SignalAndWait();
                    return await PerfRegressionWorkloads.AsyncBaseline();
                }))
                .ToArray();
        if (!ready.SignalAndWait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("ThreadPool workers did not activate during the diagnostic run.");
        }

        return Task.WhenAll(operations).GetAwaiter().GetResult().Sum();
    }
}

internal static class PerfRegressionWorkloads
{
    private const int LookupPasses = 256;
    private const int BaselinePayloadCount = 10;
    private const int CandidateExtraPayloadCount = 2;
    private const int WaitOperationCount = 8;
    private const int WaitOperationDelayMilliseconds = 1;
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
    public static async Task<int> AsyncBaseline()
    {
        var operations = Enumerable.Range(0, WaitOperationCount)
            .Select(static _ => DelayedUnitAsync())
            .ToArray();
        return (await Task.WhenAll(operations)).Sum();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int SyncOverAsyncCandidate()
    {
        var result = 0;
        for (var operation = 0; operation < WaitOperationCount; operation++)
        {
            result += DelayedUnitAsync().GetAwaiter().GetResult();
        }
        return result;
    }

    private static async Task<int> DelayedUnitAsync()
    {
        await Task.Delay(WaitOperationDelayMilliseconds);
        return 1;
    }

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
