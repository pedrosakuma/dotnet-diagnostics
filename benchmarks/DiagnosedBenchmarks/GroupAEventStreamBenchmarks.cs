using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.GatedCapture;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Kestrel;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.MethodParameters;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Requests;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Startup;
using DotnetDiagnostics.Core.Symbols;
using DotnetDiagnostics.Core.ThreadPool;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DiagnosedBenchmarks;

[MemoryDiagnoser]
[EventPipeProfiler(EventPipeProfile.CpuSampling)]
public class GroupAEventStreamBenchmarks
{
    private const int CollectionDurationSeconds = 6;
    private static readonly TimeSpan CollectionDuration = TimeSpan.FromSeconds(CollectionDurationSeconds);
    private static readonly TimeSpan LoadWarmup = TimeSpan.FromMilliseconds(750);
    private static readonly SecurityOptions BenchmarkSecurityOptions = new() { AllowMethodParameterCapture = true };

    private PublishedSampleHost? _sample;
    private string? _benchmarkAssemblyPath;
    private string? _repoRoot;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _repoRoot = RepoLayout.FindRepositoryRoot();
        _benchmarkAssemblyPath = Path.Combine(AppContext.BaseDirectory, "DiagnosedBenchmarks.dll");
        var publishedDll = await PublishedSampleHost.EnsurePublishedAsync(_repoRoot, CancellationToken.None).ConfigureAwait(false);
        _sample = await PublishedSampleHost.StartAsync(publishedDll, waitForHttpReady: true, CancellationToken.None).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        if (_sample is not null)
        {
            await _sample.DisposeAsync().ConfigureAwait(false);
            _sample = null;
        }
    }

    [Benchmark(Description = "EventPipeActivityCollector")]
    public Task<int> ActivityCollector()
        => RunWithLoadAsync(LoadScenario.Activity, async pid =>
        {
            var collector = new EventPipeActivityCollector();
            await collector.CollectAsync(pid, CollectionDuration, sources: ["CoreClrSample.Activities"], maxActivities: 1_000).ConfigureAwait(false);
        });

    [Benchmark(Description = "EventPipeLogCollector")]
    public Task<int> LogCollector()
        => RunWithLoadAsync(LoadScenario.AspNetLogs, async pid =>
        {
            var collector = new EventPipeLogCollector(new SensitiveDataRedactor(BenchmarkSecurityOptions));
            await collector.CollectAsync(
                pid,
                CollectionDuration,
                categories: null,
                minLevel: Microsoft.Extensions.Logging.LogLevel.Information,
                maxEvents: 1_000,
                maxMessageBytes: 4_096,
                includeJsonPayload: true).ConfigureAwait(false);
        });

    [Benchmark(Description = "EventPipeExceptionCollector")]
    public Task<int> ExceptionCollector()
        => RunWithLoadAsync(LoadScenario.Exceptions, async pid =>
        {
            var collector = new EventPipeExceptionCollector();
            await collector.CollectAsync(pid, CollectionDuration, maxRecent: 1_000).ConfigureAwait(false);
        });

    [Benchmark(Description = "EventPipeCrashGuardCollector")]
    public Task<int> CrashGuardCollector()
        => RunWithLoadAsync(LoadScenario.Exceptions, async pid =>
        {
            var collector = new EventPipeCrashGuardCollector();
            await collector.CollectAsync(pid, CollectionDuration, maxRecent: 1_000).ConfigureAwait(false);
        });

    [Benchmark(Description = "EventPipeGcCollector")]
    public Task<int> GcCollector()
        => RunWithLoadAsync(LoadScenario.GcHeavy, async pid =>
        {
            var collector = new EventPipeGcCollector();
            await collector.CollectAsync(pid, CollectionDuration, maxEvents: 1_000).ConfigureAwait(false);
        });

    [Benchmark(Description = "EventPipeGcDatasCollector")]
    public Task<int> GcDatasCollector()
        => RunWithLoadAsync(LoadScenario.GcHeavy, async pid =>
        {
            var collector = new EventPipeGcDatasCollector();
            await collector.CollectAsync(pid, CollectionDuration, maxEvents: 1_000).ConfigureAwait(false);
        });

    [Benchmark(Description = "EventPipeCounterCollector")]
    public Task<int> CounterCollector()
        => RunWithLoadAsync(LoadScenario.Counters, async pid =>
        {
            var collector = new EventPipeCounterCollector();
            await collector.CollectAsync(pid, CollectionDuration, providers: ["System.Runtime", "Microsoft-AspNetCore-Server-Kestrel"], intervalSeconds: 1).ConfigureAwait(false);
        });

    [Benchmark(Description = "EventPipeEventSourceCollector")]
    public Task<int> EventSourceCollector()
        => RunWithLoadAsync(LoadScenario.KestrelTraffic, async pid =>
        {
            var collector = new EventPipeEventSourceCollector();
            await collector.CaptureAsync(pid, "Microsoft-AspNetCore-Server-Kestrel", CollectionDuration, keywords: -1, eventLevel: 5, maxEvents: 1_000).ConfigureAwait(false);
        });

    [Benchmark(Description = "EventPipeEventCatalogCollector")]
    public Task<int> EventCatalogCollector()
        => RunWithLoadAsync(LoadScenario.MixedTraffic, async pid =>
        {
            var collector = new EventPipeEventCatalogCollector();
            await collector.CaptureAsync(pid, CollectionDuration, providers: ["Microsoft-Windows-DotNETRuntime", "Microsoft-Diagnostics-DiagnosticSource", "Microsoft-AspNetCore-Server-Kestrel"], maxEvents: 1_000).ConfigureAwait(false);
        });

    [Benchmark(Description = "EventPipeContentionCollector")]
    public Task<int> ContentionCollector()
        => RunWithLoadAsync(LoadScenario.ThreadPoolPressure, async pid =>
        {
            var collector = new EventPipeContentionCollector();
            await collector.CollectAsync(pid, CollectionDuration).ConfigureAwait(false);
        });

    [Benchmark(Description = "EventPipeThreadPoolCollector")]
    public Task<int> ThreadPoolCollector()
        => RunWithLoadAsync(LoadScenario.ThreadPoolPressure, async pid =>
        {
            var collector = new EventPipeThreadPoolCollector();
            await collector.CollectAsync(pid, CollectionDuration).ConfigureAwait(false);
        });

    [Benchmark(Description = "EventPipeJitCollector")]
    public Task<int> JitCollector()
        => RunWithLoadAsync(LoadScenario.Generics, async pid =>
        {
            var collector = new EventPipeJitCollector();
            await collector.CollectAsync(pid, CollectionDuration).ConfigureAwait(false);
        });

    [Benchmark(Description = "EventPipeDbCollector")]
    public Task<int> DbCollector()
        => RunWithLoadAsync(LoadScenario.MixedTraffic, async pid =>
        {
            var collector = new EventPipeDbCollector(new SensitiveDataRedactor(BenchmarkSecurityOptions));
            await collector.CollectAsync(pid, CollectionDuration, intervalSeconds: 1).ConfigureAwait(false);
        });

    [Benchmark(Description = "EventPipeKestrelCollector")]
    public Task<int> KestrelCollector()
        => RunWithLoadAsync(LoadScenario.KestrelTraffic, async pid =>
        {
            var collector = new EventPipeKestrelCollector();
            await collector.CollectAsync(pid, CollectionDuration, intervalSeconds: 1).ConfigureAwait(false);
        });

    [Benchmark(Description = "EventPipeNetworkingCollector")]
    public Task<int> NetworkingCollector()
        => RunWithLoadAsync(LoadScenario.KestrelTraffic, async pid =>
        {
            var collector = new EventPipeNetworkingCollector();
            await collector.CollectAsync(pid, CollectionDuration, intervalSeconds: 1).ConfigureAwait(false);
        });

    [Benchmark(Description = "EventPipeInFlightRequestCollector")]
    public Task<int> InFlightRequestCollector()
        => RunWithLoadAsync(LoadScenario.LongRequests, async pid =>
        {
            var collector = new EventPipeInFlightRequestCollector();
            await collector.CollectAsync(pid, CollectionDuration, longRunningThresholdMs: 500, maxRequests: 256).ConfigureAwait(false);
        });

    [Benchmark(Description = "MethodParameterCaptureCollector")]
    public Task<int> MethodParameterCaptureCollector()
        => RunWithLoadAsync(LoadScenario.CpuBurnParameters, async pid =>
        {
            var collector = new MethodParameterCaptureCollector(new MvidReader(), new SensitiveDataRedactor(BenchmarkSecurityOptions));
            var result = await collector.CollectAsync(
                pid,
                new MethodParameterCaptureRequest(
                    MethodFilters:
                    [
                        new MethodFilter("CoreClrSample.dll", "Program", "<<Main>$>g__BurnCpu|0_10")
                        {
                            Signature = ["System.Int32"],
                        },
                    ],
                    Duration: CollectionDuration,
                    MaxEvents: 64,
                    PreviewCount: 32,
                    RuntimeVersion: "10.0.0",
                    ProcessContext: new ProcessContext(pid, RuntimeFlavor.CoreClr, true, true, false, "10.0.0", "explicit"))).ConfigureAwait(false);

            if (result.IsError)
            {
                throw new InvalidOperationException(result.Error?.Message ?? "Method parameter capture failed.");
            }
        });

    [Benchmark(Description = "ThresholdGatedCaptureCollector")]
    public Task<int> ThresholdGatedCaptureCollector()
        => RunWithLoadAsync(LoadScenario.Counters, async pid =>
        {
            var collector = new ThresholdGatedCaptureCollector();
            await collector.WatchAndCaptureAsync(
                pid,
                new TriggerPredicate(GatedCaptureMetric.Cpu, TriggerOperator.GreaterThan, 1),
                GatedCaptureKind.CpuSample,
                window: CollectionDuration,
                maxCaptures: 2,
                sampleInterval: TimeSpan.FromSeconds(1),
                captureCallback: static (trigger, _) => Task.FromResult(new GatedCaptureOutcome($"Captured synthetic gated event at {trigger.ObservedValue.ToString("F1", CultureInfo.InvariantCulture)} CPU."))).ConfigureAwait(false);
        });

    [Benchmark(Description = "EventPipeStartupCollector")]
    public Task<int> StartupCollector()
        => RunWithLoadAsync(LoadScenario.StartupishTraffic, async pid =>
        {
            var collector = new EventPipeStartupCollector();
            await collector.CollectAsync(pid, CollectionDuration).ConfigureAwait(false);
        });

    private async Task<int> RunWithLoadAsync(LoadScenario scenario, Func<int, Task> collectAsync)
    {
        var sample = _sample ?? throw new InvalidOperationException("Sample host is unavailable.");
        var benchmarkAssemblyPath = _benchmarkAssemblyPath ?? throw new InvalidOperationException("Benchmark assembly path is unavailable.");
        var load = await SampleLoadProcess.StartAsync(benchmarkAssemblyPath, sample.BaseUrl, scenario, CollectionDuration + TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
        await using var loadScope = load;
        await Task.Delay(LoadWarmup).ConfigureAwait(false);
        await collectAsync(sample.ProcessId).ConfigureAwait(false);
        await load.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return 0;
    }
}

internal enum LoadScenario
{
    Activity,
    AspNetLogs,
    Exceptions,
    GcHeavy,
    Counters,
    KestrelTraffic,
    MixedTraffic,
    ThreadPoolPressure,
    Generics,
    LongRequests,
    CpuBurnParameters,
    StartupishTraffic,
}

internal static class RepoLayout
{
    public static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DotnetDiagnostics.slnx")) &&
                Directory.Exists(Path.Combine(current.FullName, "samples", "CoreClrSample")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the benchmark output directory.");
    }
}

internal sealed class PublishedSampleHost : IAsyncDisposable
{
    private const string PublishRelativeDirectory = "benchmarks/DiagnosedBenchmarks/BenchmarkDotNet.Artifacts/published/CoreClrSample";
    private static readonly SemaphoreSlim PublishGate = new(1, 1);

    private readonly Process _process;
    private readonly TaskCompletionSource<string> _listeningUrlTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private readonly StringBuilder _stderr = new();

    private PublishedSampleHost(Process process)
    {
        _process = process;
        _stdoutPump = PumpStandardOutputAsync();
        _stderrPump = PumpStandardErrorAsync();
    }

    public int ProcessId => _process.Id;

    public string BaseUrl { get; private set; } = string.Empty;

    public static async Task<string> EnsurePublishedAsync(string repoRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        var publishDirectory = Path.Combine(repoRoot, PublishRelativeDirectory);
        var sampleDll = Path.Combine(publishDirectory, "CoreClrSample.dll");
        var sampleProjectDirectory = Path.Combine(repoRoot, "samples", "CoreClrSample");

        await PublishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ShouldRepublish(sampleProjectDirectory, sampleDll))
            {
                return sampleDll;
            }

            if (Directory.Exists(publishDirectory))
            {
                Directory.Delete(publishDirectory, recursive: true);
            }

            Directory.CreateDirectory(publishDirectory);
            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("publish");
            psi.ArgumentList.Add("samples/CoreClrSample/CoreClrSample.csproj");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("Release");
            psi.ArgumentList.Add("-p:UseAppHost=false");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(publishDirectory);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet publish for CoreClrSample.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(sampleDll))
            {
                throw new InvalidOperationException($"dotnet publish failed with exit code {process.ExitCode}. stdout={stdout} stderr={stderr}");
            }

            return sampleDll;
        }
        finally
        {
            PublishGate.Release();
        }
    }

    private static bool ShouldRepublish(string sampleProjectDirectory, string sampleDll)
    {
        if (!File.Exists(sampleDll))
        {
            return true;
        }

        var publishedAt = File.GetLastWriteTimeUtc(sampleDll);
        foreach (var file in Directory.EnumerateFiles(sampleProjectDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sampleProjectDirectory, file);
            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            if (File.GetLastWriteTimeUtc(file) > publishedAt)
            {
                return true;
            }
        }

        return false;
    }

    public static async Task<PublishedSampleHost> StartAsync(string sampleDll, bool waitForHttpReady, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(sampleDll) ?? AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(sampleDll);
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add("http://127.0.0.1:0");
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start published CoreClrSample.");
        var host = new PublishedSampleHost(process);
        try
        {
            host.BaseUrl = await host.WaitForListeningUrlAsync(waitForHttpReady, cancellationToken).ConfigureAwait(false);
            return host;
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }

        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        try { await _stdoutPump.ConfigureAwait(false); } catch { }
        try { await _stderrPump.ConfigureAwait(false); } catch { }
        _process.Dispose();
    }

    private async Task<string> WaitForListeningUrlAsync(bool waitForHttpReady, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        string url;
        try
        {
            url = await _listeningUrlTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"CoreClrSample did not report a listening URL. stderr={_stderr}");
        }

        if (waitForHttpReady)
        {
            await WaitForHttpReadyAsync(url, timeoutCts.Token).ConfigureAwait(false);
        }

        return url;
    }

    private async Task PumpStandardOutputAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                var marker = "Now listening on:";
                var index = line.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0 && !_listeningUrlTcs.Task.IsCompleted)
                {
                    _listeningUrlTcs.TrySetResult(line[(index + marker.Length)..].Trim());
                }
            }
        }
        catch (Exception ex)
        {
            _listeningUrlTcs.TrySetException(ex);
        }
    }

    private async Task PumpStandardErrorAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (_stderr.Length < 8_192)
                {
                    _stderr.AppendLine(line);
                }
            }
        }
        catch
        {
        }
    }

    private static async Task WaitForHttpReadyAsync(string baseUrl, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var response = await http.GetAsync("/weatherforecast", cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"CoreClrSample never became HTTP-ready at {baseUrl}.");
    }
}

internal sealed class SampleLoadProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private readonly StringBuilder _stderr = new();

    private SampleLoadProcess(Process process)
    {
        _process = process;
        _stdoutPump = DrainAsync(process.StandardOutput, append: false);
        _stderrPump = DrainAsync(process.StandardError, append: true);
    }

    public static Task<SampleLoadProcess> StartAsync(string benchmarkAssemblyPath, string baseUrl, LoadScenario scenario, TimeSpan duration, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(benchmarkAssemblyPath) ?? AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(benchmarkAssemblyPath);
        psi.ArgumentList.Add("loadgen");
        psi.ArgumentList.Add("--base-url");
        psi.ArgumentList.Add(baseUrl);
        psi.ArgumentList.Add("--scenario");
        psi.ArgumentList.Add(scenario.ToString());
        psi.ArgumentList.Add("--duration-seconds");
        psi.ArgumentList.Add(duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start sample load generator.");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SampleLoadProcess(process));
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        try { await _stdoutPump.ConfigureAwait(false); } catch { }
        try { await _stderrPump.ConfigureAwait(false); } catch { }
        if (_process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Sample load generator failed with exit code {_process.ExitCode}: {_stderr}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }

        try { await _process.WaitForExitAsync().ConfigureAwait(false); } catch { }
        try { await _stdoutPump.ConfigureAwait(false); } catch { }
        try { await _stderrPump.ConfigureAwait(false); } catch { }
        _process.Dispose();
    }

    private Task DrainAsync(StreamReader reader, bool append)
        => Task.Run(async () =>
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (append && _stderr.Length < 8_192)
                {
                    _stderr.AppendLine(line);
                }
            }
        });
}

internal static class SampleLoadGenerator
{
    public static async Task<int> RunAsync(string[] args)
    {
        var baseUrl = string.Empty;
        var scenario = LoadScenario.MixedTraffic;
        var durationSeconds = 6d;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--base-url":
                    baseUrl = args[++i];
                    break;
                case "--scenario":
                    scenario = Enum.Parse<LoadScenario>(args[++i], ignoreCase: true);
                    break;
                case "--duration-seconds":
                    durationSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Console.Error.WriteLine("--base-url is required.");
            return 2;
        }

        using var http = CreateHttpClient(baseUrl, scenario);
        var plan = ScenarioPlans[scenario];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
        var stats = new LoadStats();
        var workers = Enumerable.Range(0, plan.Concurrency)
            .Select(workerIndex => RunWorkerAsync(http, plan, workerIndex, stats, cts.Token))
            .ToArray();

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }

        if (stats.SuccessfulRequests == 0)
        {
            Console.Error.WriteLine($"Load generator observed 0 successful requests (failures={stats.FailedRequests}).");
            return 3;
        }

        if (stats.FailedRequests > Math.Max(10, stats.SuccessfulRequests / 10))
        {
            Console.Error.WriteLine($"Load generator failure rate was too high (successes={stats.SuccessfulRequests}, failures={stats.FailedRequests}).");
            return 4;
        }

        return 0;
    }

    private static HttpClient CreateHttpClient(string baseUrl, LoadScenario scenario)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 256,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        return new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static async Task RunWorkerAsync(HttpClient http, LoadPlan plan, int workerIndex, LoadStats stats, CancellationToken cancellationToken)
    {
        var endpointCount = plan.Endpoints.Length;
        var index = workerIndex % endpointCount;
        while (!cancellationToken.IsCancellationRequested)
        {
            var endpoint = plan.Endpoints[index];
            index = (index + 1) % endpointCount;
            try
            {
                using var response = await http.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                Interlocked.Increment(ref stats.SuccessfulRequests);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref stats.FailedRequests);
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static readonly Dictionary<LoadScenario, LoadPlan> ScenarioPlans = new()
    {
        [LoadScenario.Activity] = new(12,
        [
            "/activity?delayMs=25",
            "/activity?delayMs=50",
            "/weatherforecast",
        ]),
        [LoadScenario.AspNetLogs] = new(16,
        [
            "/weatherforecast",
            "/render?count=1500",
            "/activity?delayMs=25",
        ]),
        [LoadScenario.Exceptions] = new(12,
        [
            "/parse",
        ]),
        [LoadScenario.GcHeavy] = new(10,
        [
            "/render?count=4000",
            "/render?count=6000",
            "/weatherforecast",
            "/parse",
        ]),
        [LoadScenario.Counters] = new(12,
        [
            "/render?count=4000",
            "/cpu-burn?ms=200",
            "/weatherforecast",
            "/activity?delayMs=30",
        ]),
        [LoadScenario.KestrelTraffic] = new(20,
        [
            "/weatherforecast",
            "/render?count=1500",
            "/activity?delayMs=15",
        ]),
        [LoadScenario.MixedTraffic] = new(16,
        [
            "/weatherforecast",
            "/render?count=2500",
            "/parse",
            "/activity?delayMs=25",
            "/cpu-burn?ms=150",
        ]),
        [LoadScenario.ThreadPoolPressure] = new(8,
        [
            "/threadpool/queue?globalItems=192&localItems=192&blockMs=3500",
            "/cpu-burn?ms=250",
            "/activity?delayMs=150",
        ]),
        [LoadScenario.Generics] = new(8,
        [
            "/generics?iterations=180000",
            "/generics?iterations=120000",
        ]),
        [LoadScenario.LongRequests] = new(8,
        [
            "/activity?delayMs=1800",
            "/cpu-burn?ms=1800",
        ]),
        [LoadScenario.CpuBurnParameters] = new(6,
        [
            "/cpu-burn?ms=123",
            "/cpu-burn?ms=456",
            "/cpu-burn?ms=789",
        ]),
        [LoadScenario.StartupishTraffic] = new(10,
        [
            "/weatherforecast",
            "/render?count=1500",
            "/generics?iterations=60000",
            "/activity?delayMs=20",
        ]),
    };

    private sealed record LoadPlan(int Concurrency, string[] Endpoints);

    private sealed class LoadStats
    {
        public long SuccessfulRequests;
        public long FailedRequests;
    }
}

internal static class HotpathTraceAnalyzer
{
    public static Task<int> RunAsync(string[] args)
    {
        var artifactsDir = args.Length > 0 ? args[0] : Path.Combine(RepoLayout.FindRepositoryRoot(), "benchmarks", "DiagnosedBenchmarks", "BenchmarkDotNet.Artifacts");
        var traceFiles = Directory.Exists(artifactsDir)
            ? Directory.GetFiles(artifactsDir, "*.nettrace", SearchOption.AllDirectories)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

        if (traceFiles.Length == 0)
        {
            Console.Error.WriteLine($"No .nettrace files found under {artifactsDir}.");
            return Task.FromResult(1);
        }

        foreach (var traceFile in traceFiles)
        {
            AnalyzeSingleTrace(traceFile);
        }

        return Task.FromResult(0);
    }

    private static void AnalyzeSingleTrace(string traceFile)
    {
        var etlxPath = TraceLog.CreateFromEventPipeDataFile(traceFile);
        try
        {
            using var traceLog = new TraceLog(etlxPath);
            var totals = new Dictionary<string, long>(StringComparer.Ordinal);
            long totalSamples = 0;

            foreach (var process in traceLog.Processes)
            {
                foreach (var traceEvent in process.EventsInProcess)
                {
                    if (!string.Equals(traceEvent.ProviderName, "Microsoft-DotNETCore-SampleProfiler", StringComparison.Ordinal) ||
                        !string.Equals(traceEvent.EventName, "Thread/Sample", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var callStack = traceEvent.CallStack();
                    if (callStack is null)
                    {
                        continue;
                    }

                    totalSamples++;
                    var leaf = callStack.CodeAddress;
                    var methodName = FormatCodeAddress(leaf);
                    totals[methodName] = totals.TryGetValue(methodName, out var current) ? current + 1 : 1;
                }
            }

            Console.WriteLine($"## {Path.GetFileName(traceFile)}");
            Console.WriteLine();
            Console.WriteLine($"Total sampled stacks: {totalSamples}");
            Console.WriteLine();
            Console.WriteLine("| Rank | Method | Exclusive samples | % total |"
            );
            Console.WriteLine("| --- | --- | ---: | ---: |");
            foreach (var row in totals
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                .Take(10)
                .Select(static (pair, index) => new { Index = index + 1, pair.Key, pair.Value }))
            {
                var percent = totalSamples == 0 ? 0 : 100d * row.Value / totalSamples;
                Console.WriteLine($"| {row.Index} | {MarkdownCodeSpan(row.Key)} | {row.Value} | {percent:F1}% |");
            }

            Console.WriteLine();
        }
        finally
        {
            TryDelete(etlxPath);
        }
    }

    private static string FormatCodeAddress(TraceCodeAddress? codeAddress)
    {
        var method = codeAddress?.Method;
        if (method is null)
        {
            return codeAddress?.FullMethodName ?? "(unknown)";
        }

        var module = method.MethodModuleFile?.Name ?? string.Empty;
        var fullMethod = method.FullMethodName ?? "(unknown)";
        return string.IsNullOrEmpty(module) ? fullMethod : $"{module}!{fullMethod}";
    }

    private static string MarkdownCodeSpan(string value)
    {
        var escaped = value.Replace("|", "\\|", StringComparison.Ordinal);
        var longestRun = 0;
        var currentRun = 0;
        foreach (var ch in escaped)
        {
            if (ch == '`')
            {
                currentRun++;
                longestRun = Math.Max(longestRun, currentRun);
            }
            else
            {
                currentRun = 0;
            }
        }

        var fence = new string('`', longestRun + 1);
        return $"{fence}{escaped}{fence}";
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
