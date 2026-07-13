using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using DotnetDiagnostics.Core.Container;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Preflight;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.TestSupport;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DiagnosedBenchmarks;

[MemoryDiagnoser]
[EventPipeProfiler(EventPipeProfile.CpuSampling)]
public class GroupCRemainingBenchmarks : IAsyncDisposable
{
    private const int PreflightRepeatCount = 50;
    private const int CgroupRepeatCount = 200;

    private static readonly DumpInspectionOptions DumpOptions = new(
        TopTypes: 20,
        SnapshotTopTypes: 200,
        IncludeStaticFields: true,
        SnapshotStaticFieldTopN: 200,
        IncludeDuplicateStrings: true,
        SnapshotDuplicateStringTopN: 200);

    private static readonly GcDumpOptions GcDumpOptions = new(
        TopTypes: 20,
        SnapshotTopTypes: 200,
        Timeout: TimeSpan.FromSeconds(30),
        ExportTrace: false);

    private static readonly ThreadSnapshotOptions ThreadSnapshotOptions = new(
        MaxFramesPerThread: 64,
        IncludeRuntimeFrames: false,
        IncludeNativeFrames: false);

    private readonly ClrMdDumpInspector _dumpInspector = new();
    private readonly GcDumpHeapSnapshotCollector _gcDumpCollector = new();
    private readonly ClrMdThreadSnapshotInspector _clrMdThreadInspector = new();
    private readonly RequestsNowCollector _requestsNowCollector;
    private readonly ProcessResourcesCollector _processResourcesCollector = new();
    private readonly RuntimeConfigInspector _runtimeConfigInspector = new();
    private readonly PreflightInspector _preflightInspector = new();
    private readonly CgroupV2SignalsCollector _cgroupCollector = new();

    private BenchmarkSampleProcess? _sample;
    private HttpClient? _http;

    public GroupCRemainingBenchmarks()
    {
        _requestsNowCollector = new RequestsNowCollector(new ClrMdThreadSnapshotInspector());
    }

    private int ProcessId => _sample?.ProcessId ?? throw new InvalidOperationException("Sample not started.");

    private HttpClient Http => _http ?? throw new InvalidOperationException("HTTP client not initialized.");

    [IterationSetup]
    public void IterationSetup()
        => StartFreshSampleAsync().GetAwaiter().GetResult();

    [IterationCleanup]
    public void IterationCleanup()
        => DisposeCurrentSampleAsync().GetAwaiter().GetResult();

    [Benchmark]
    public async Task<long> ClrMdDumpInspectorLiveHeapWalk()
    {
        var snapshot = await _dumpInspector.InspectLiveAsync(ProcessId, DumpOptions, CancellationToken.None).ConfigureAwait(false);
        return snapshot.Heap.TotalBytes
               + (snapshot.StaticFields?.Count ?? 0)
               + (snapshot.GcHandles?.TotalHandles ?? 0);
    }

    [Benchmark]
    public async Task<long> GcDumpHeapSnapshotCollectorEventPipeWalk()
    {
        var snapshot = await _gcDumpCollector.CollectAsync(ProcessId, GcDumpOptions, CancellationToken.None).ConfigureAwait(false);
        return snapshot.Heap.TotalBytes + snapshot.TopTypesByBytes.Count;
    }

    [Benchmark]
    public async Task<int> ClrMdThreadSnapshotInspectorLiveSnapshot()
    {
        var snapshot = await WithCpuLoadAsync(
            concurrency: 4,
            burnMilliseconds: 2_000,
            () => _clrMdThreadInspector.InspectLiveAsync(ProcessId, ThreadSnapshotOptions, CancellationToken.None)).ConfigureAwait(false);
        return snapshot.Threads.Count + snapshot.Locks.Count;
    }

    [Benchmark]
    public async Task<int> RequestsNowCollectorConcurrentRequests()
    {
        var longRunning = StartBurnRequests(concurrency: 6, burnMilliseconds: 4_000);
        await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None).ConfigureAwait(false);
        try
        {
            var snapshot = await _requestsNowCollector
                .CollectAsync(ProcessId, TimeSpan.FromSeconds(2), topFrames: 8, CancellationToken.None)
                .ConfigureAwait(false);
            return snapshot.Requests.Count;
        }
        finally
        {
            await Task.WhenAll(longRunning).ConfigureAwait(false);
        }
    }

    [Benchmark]
    public async Task<long> ProcessResourcesCollectorOneShot()
    {
        var snapshot = await _processResourcesCollector
            .CollectAsync(ProcessId, durationSeconds: 0, sampleEverySeconds: 1, CancellationToken.None)
            .ConfigureAwait(false);
        return (snapshot.FdCount ?? 0)
               + (snapshot.HandleCount ?? 0)
               + (snapshot.ManagedVsNative?.GcHeapBytes ?? 0);
    }

    [Benchmark]
    public async Task<int> RuntimeConfigInspectorOneShot()
    {
        var view = await _runtimeConfigInspector.InspectAsync(ProcessId, CancellationToken.None).ConfigureAwait(false);
        return view.EnvVars.Count + view.AppContextSwitches.Count + view.Notes.Count;
    }

    [Benchmark(OperationsPerInvoke = PreflightRepeatCount)]
    public int PreflightInspectorOneShot()
    {
        var sum = 0;
        for (var i = 0; i < PreflightRepeatCount; i++)
        {
            var report = _preflightInspector.Inspect(ProcessId);
            sum += report.Checks.Count + (report.HasBlocker ? 1 : 0);
        }

        return sum;
    }

    [Benchmark(OperationsPerInvoke = CgroupRepeatCount)]
    public async Task<long> CgroupV2SignalsCollectorOneShot()
    {
        long sum = 0;
        for (var i = 0; i < CgroupRepeatCount; i++)
        {
            var signals = await _cgroupCollector.CollectAsync(ProcessId, CancellationToken.None).ConfigureAwait(false);
            sum += (signals.Cpu?.UsageUsec ?? 0)
                   + (signals.Memory?.CurrentBytes ?? 0)
                   + signals.Notes.Count;
        }

        return sum;
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await DisposeCurrentSampleAsync().ConfigureAwait(false);
    }

    private async Task StartFreshSampleAsync()
    {
        await DisposeCurrentSampleAsync().ConfigureAwait(false);

        _sample = await BenchmarkSampleProcess.StartAsync(
            new Dictionary<string, string>
            {
                ["DOTNET_NOLOGO"] = "1",
                ["DOTNET_gcServer"] = "0",
                ["DOTNET_TieredCompilation"] = "1",
                ["DOTNET_TC_QuickJit"] = "1",
                ["DOTNET_TieredPGO"] = "1",
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
            }).ConfigureAwait(false);

        _http = new HttpClient
        {
            BaseAddress = new Uri(_sample.BaseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30),
        };

        await BuildRetainedHeapAsync().ConfigureAwait(false);
        await WarmRuntimeStateAsync().ConfigureAwait(false);
    }

    private async Task DisposeCurrentSampleAsync()
    {
        _http?.Dispose();
        _http = null;

        if (_sample is not null)
        {
            await _sample.DisposeAsync().ConfigureAwait(false);
            _sample = null;
        }
    }

    private async Task BuildRetainedHeapAsync()
    {
        await IssueSequentialGetsAsync(
            "/render?count=12000",
            "/render?count=12000",
            "/generics?iterations=120000",
            "/generics?iterations=120000",
            "/async-pending?count=16",
            "/async-pending?count=16").ConfigureAwait(false);

        for (var i = 0; i < 8; i++)
        {
            await GetEnsureSuccessAsync("/leak?mb=1").ConfigureAwait(false);
        }
    }

    private async Task WarmRuntimeStateAsync()
    {
        var requests = new[]
        {
            GetEnsureSuccessAsync("/render?count=8000"),
            GetEnsureSuccessAsync("/generics?iterations=90000"),
            GetEnsureSuccessAsync("/cpu-burn?ms=500"),
            GetEnsureSuccessAsync("/async-pending?count=4"),
        };

        await Task.WhenAll(requests).ConfigureAwait(false);
    }

    private async Task IssueSequentialGetsAsync(params string[] relativePaths)
    {
        foreach (var relativePath in relativePaths)
        {
            await GetEnsureSuccessAsync(relativePath).ConfigureAwait(false);
        }
    }

    private async Task GetEnsureSuccessAsync(string relativePath)
    {
        using var response = await Http.GetAsync(relativePath, CancellationToken.None).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        _ = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private Task[] StartBurnRequests(int concurrency, int burnMilliseconds)
        => Enumerable.Range(0, concurrency)
            .Select(_ => GetEnsureSuccessAsync($"/cpu-burn?ms={burnMilliseconds.ToString(CultureInfo.InvariantCulture)}"))
            .ToArray();

    private async Task<T> WithCpuLoadAsync<T>(int concurrency, int burnMilliseconds, Func<Task<T>> action)
    {
        var requests = StartBurnRequests(concurrency, burnMilliseconds);
        await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            await Task.WhenAll(requests).ConfigureAwait(false);
        }
    }
}

internal static class GroupCHotpathTraceAnalyzer
{
    public static void PrintRecentReports(string artifactsRoot, DateTimeOffset runStartedUtc)
    {
        if (!Directory.Exists(artifactsRoot))
        {
            return;
        }

        var traces = Directory.EnumerateFiles(artifactsRoot, "*.nettrace", SearchOption.AllDirectories)
            .Where(path => File.GetLastWriteTimeUtc(path) >= runStartedUtc.UtcDateTime.AddSeconds(-5))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (traces.Length == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("// * GroupCHotpathTraceAnalyzer *");
        foreach (var tracePath in traces)
        {
            var report = Analyze(tracePath);
            Console.WriteLine($"//   {Path.GetFileName(tracePath)} | pid {report.ProcessId} | samples {report.TotalSamples}");
            foreach (var method in report.Methods)
            {
                Console.WriteLine($"//     {method.ExclusivePercent,5:0.0}% {method.Method}");
            }
        }
    }

    public static HotpathTraceReport Analyze(string tracePath)
    {
        var etlxPath = TraceLog.CreateFromEventPipeDataFile(tracePath);
        try
        {
            using var traceLog = new TraceLog(etlxPath);
            var process = FindProcessWithMostSamples(traceLog);
            if (process is null)
            {
                return new HotpathTraceReport(tracePath, 0, 0, Array.Empty<HotpathMethod>());
            }

            var exclusiveByMethod = new Dictionary<string, long>(StringComparer.Ordinal);
            long totalSamples = 0;

            foreach (var traceEvent in process.EventsInProcess)
            {
                if (!string.Equals(traceEvent.ProviderName, "Microsoft-DotNETCore-SampleProfiler", StringComparison.Ordinal) ||
                    !string.Equals(traceEvent.EventName, "Thread/Sample", StringComparison.Ordinal))
                {
                    continue;
                }

                var leaf = traceEvent.CallStack();
                if (leaf is null)
                {
                    continue;
                }

                totalSamples++;
                var method = FormatFrame(leaf);
                exclusiveByMethod[method] = exclusiveByMethod.GetValueOrDefault(method) + 1;
            }

            var methods = totalSamples == 0
                ? Array.Empty<HotpathMethod>()
                : exclusiveByMethod
                    .OrderByDescending(static kvp => kvp.Value)
                    .ThenBy(static kvp => kvp.Key, StringComparer.Ordinal)
                    .Take(10)
                    .Select(kvp => new HotpathMethod(
                        kvp.Key,
                        kvp.Value,
                        100.0 * kvp.Value / totalSamples))
                    .ToArray();

            return new HotpathTraceReport(tracePath, process.ProcessID, totalSamples, methods);
        }
        finally
        {
            TryDelete(etlxPath);
        }
    }

    private static TraceProcess? FindProcessWithMostSamples(TraceLog traceLog)
    {
        TraceProcess? best = null;
        var bestCount = -1;

        foreach (var process in traceLog.Processes)
        {
            var count = 0;
            foreach (var traceEvent in process.EventsInProcess)
            {
                if (string.Equals(traceEvent.ProviderName, "Microsoft-DotNETCore-SampleProfiler", StringComparison.Ordinal) &&
                    string.Equals(traceEvent.EventName, "Thread/Sample", StringComparison.Ordinal))
                {
                    count++;
                }
            }

            if (count > bestCount)
            {
                best = process;
                bestCount = count;
            }
        }

        return best;
    }

    private static string FormatFrame(TraceCallStack frame)
    {
        var address = frame.CodeAddress;
        if (address?.Method is { } method)
        {
            return method.FullMethodName;
        }

        if (address?.ModuleFile is { } module)
        {
            return $"{module.Name}!0x{address.Address:x}";
        }

        return $"0x{address?.Address ?? 0:x}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

internal sealed record HotpathTraceReport(
    string TracePath,
    int ProcessId,
    long TotalSamples,
    IReadOnlyList<HotpathMethod> Methods);

internal sealed record HotpathMethod(
    string Method,
    long ExclusiveSamples,
    double ExclusivePercent);

internal sealed class BenchmarkSampleProcess : IAsyncDisposable
{
    private readonly Process _process;

    private BenchmarkSampleProcess(Process process, string baseUrl)
    {
        _process = process;
        BaseUrl = baseUrl;
    }

    public int ProcessId => _process.Id;

    public string BaseUrl { get; }

    public static async Task<BenchmarkSampleProcess> StartAsync(IReadOnlyDictionary<string, string> environment)
    {
        var sampleDll = LocateCoreClrSampleDll()
            ?? throw new InvalidOperationException("CoreClrSample.dll not found beside the benchmark host or under samples/CoreClrSample/bin/Release/net10.0.");

        var listeningUrl = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(sampleDll)!,
        };
        psi.ArgumentList.Add(sampleDll);
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add("http://127.0.0.1:0");
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        foreach (var pair in environment)
        {
            psi.Environment[pair.Key] = pair.Value;
        }

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start CoreClrSample.");

        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = process.StandardOutput;
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    var idx = line.IndexOf("Now listening on:", StringComparison.Ordinal);
                    if (idx >= 0 && !listeningUrl.Task.IsCompleted)
                    {
                        listeningUrl.TrySetResult(line[(idx + "Now listening on:".Length)..].Trim());
                    }
                }
            }
            catch
            {
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = process.StandardError;
                while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
                {
                }
            }
            catch
            {
            }
        });

        try
        {
            await DiagnosticReadiness.WaitForDiagnosticEndpointAsync(process.Id, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var baseUrl = await listeningUrl.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await DiagnosticReadiness.WaitForHttpReadyAsync(baseUrl, TimeSpan.FromSeconds(30), "/weatherforecast").ConfigureAwait(false);
            return new BenchmarkSampleProcess(process, baseUrl);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
                }
            }
            catch
            {
            }

            process.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5_000);
            }
            catch
            {
            }
        }

        _process.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string? LocateCoreClrSampleDll()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var direct = Path.Combine(baseDirectory, "CoreClrSample.dll");
        if (File.Exists(direct))
        {
            return direct;
        }

        var probe = baseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(probe, "samples", "CoreClrSample", "bin", "Release", "net10.0", "CoreClrSample.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            probe = Path.GetFullPath(Path.Combine(probe, ".."));
        }

        return null;
    }
}
