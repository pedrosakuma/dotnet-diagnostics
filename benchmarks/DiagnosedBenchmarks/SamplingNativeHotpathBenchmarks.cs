using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.NativeAlloc;
using DotnetDiagnostics.Core.OffCpu;

namespace DiagnosedBenchmarks;

[MemoryDiagnoser]
[EventPipeProfiler(EventPipeProfile.CpuSampling)]
public class SamplingNativeHotpathBenchmarks : IAsyncDisposable
{
    private static readonly TimeSpan CollectionWindow = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan LoadLeadIn = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LoadDuration = TimeSpan.FromSeconds(5);

    private PublishedSampleHost? _coreClrSample;
    private PublishedSampleHost? _nativeAotSample;
    private EventPipeCpuSampler? _eventPipeCpuSampler;
    private RoutingCpuSampler? _routingCpuSampler;
    private EventPipeAllocationSampler? _eventPipeAllocationSampler;
    private PerfNativeAotCpuSampler? _perfNativeAotCpuSampler;
    private PerfNativeAllocSampler? _perfNativeAllocSampler;
    private RoutingNativeAllocSampler? _routingNativeAllocSampler;
    private PerfSchedOffCpuSampler? _perfSchedOffCpuSampler;
    private RoutingOffCpuSampler? _routingOffCpuSampler;
    private MemoryTrendCollector? _memoryTrendCollector;
    private string? _nativeAotMapPath;

    [GlobalSetup(Targets =
    [
        nameof(EventPipeCpuSamplerCoreClrSample),
        nameof(RoutingCpuSamplerCoreClrSample),
        nameof(EventPipeAllocationSamplerCoreClrSample),
        nameof(PerfNativeAllocSamplerCoreClrSample),
        nameof(RoutingNativeAllocSamplerCoreClrSample),
        nameof(PerfSchedOffCpuSamplerCoreClrSample),
        nameof(RoutingOffCpuSamplerCoreClrSample),
        nameof(MemoryTrendCollectorCoreClrSample)
    ])]
    public async Task SetupCoreClrAsync()
    {
        _coreClrSample ??= await PublishedSampleHost.StartAsync(
            sampleName: "CoreClrSample",
            publishArguments: ["publish", "samples/CoreClrSample/CoreClrSample.csproj", "-c", "Release", "-o", PublishedSampleHost.ResolvePublishDirectory("CoreClrSample")],
            launchViaDotnet: true,
            entryRelativePath: "CoreClrSample.dll",
            readinessPath: "/weatherforecast").ConfigureAwait(false);

        _eventPipeCpuSampler ??= new EventPipeCpuSampler();
        _routingCpuSampler ??= new RoutingCpuSampler(
            new FixedCapabilityDetector(RuntimeFlavor.CoreClr),
            _eventPipeCpuSampler,
            new PerfNativeAotCpuSampler(),
            new EtwNativeAotCpuSampler());
        _eventPipeAllocationSampler ??= new EventPipeAllocationSampler();
        _perfNativeAllocSampler ??= new PerfNativeAllocSampler();
        _routingNativeAllocSampler ??= new RoutingNativeAllocSampler(
            _perfNativeAllocSampler,
            new EtwNativeAllocSampler());
        _perfSchedOffCpuSampler ??= new PerfSchedOffCpuSampler();
        _routingOffCpuSampler ??= new RoutingOffCpuSampler(
            _perfSchedOffCpuSampler,
            new EtwOffCpuSampler());
        _memoryTrendCollector ??= new MemoryTrendCollector();
    }

    [GlobalSetup(Target = nameof(PerfNativeAotCpuSamplerNativeAotSample))]
    public async Task SetupNativeAotAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        var publishDir = PublishedSampleHost.ResolvePublishDirectory("NativeAotSample");
        _nativeAotSample ??= await PublishedSampleHost.StartAsync(
            sampleName: "NativeAotSample",
            publishArguments:
            [
                "publish",
                "samples/NativeAotSample/NativeAotSample.csproj",
                "-c", "Release",
                "-r", RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64",
                "-p:PublishAot=true",
                "-p:IlcGenerateMapFile=true",
                "--self-contained", "true",
                "-o", publishDir
            ],
            launchViaDotnet: false,
            entryRelativePath: RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "NativeAotSample.exe" : "NativeAotSample",
            readinessPath: "/weatherforecast",
            allowServerErrorsDuringReadiness: true).ConfigureAwait(false);

        _nativeAotMapPath = Path.Combine(
            PublishedSampleHost.FindRepositoryRoot(),
            "samples",
            "NativeAotSample",
            "obj",
            "Release",
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64",
            "native",
            "NativeAotSample.map.xml");

        _perfNativeAotCpuSampler ??= new PerfNativeAotCpuSampler();
    }

    [GlobalCleanup(Targets =
    [
        nameof(EventPipeCpuSamplerCoreClrSample),
        nameof(RoutingCpuSamplerCoreClrSample),
        nameof(EventPipeAllocationSamplerCoreClrSample),
        nameof(PerfNativeAllocSamplerCoreClrSample),
        nameof(RoutingNativeAllocSamplerCoreClrSample),
        nameof(PerfSchedOffCpuSamplerCoreClrSample),
        nameof(RoutingOffCpuSamplerCoreClrSample),
        nameof(MemoryTrendCollectorCoreClrSample)
    ])]
    public Task CleanupCoreClrAsync() => DisposeCoreClrAsync();

    [GlobalCleanup(Target = nameof(PerfNativeAotCpuSamplerNativeAotSample))]
    public async Task CleanupNativeAotAsync()
    {
        await DisposeNativeAotAsync().ConfigureAwait(false);
        await DisposeCoreClrAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public async Task<long> EventPipeCpuSamplerCoreClrSample()
    {
        await EnsureCoreClrReadyAsync().ConfigureAwait(false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var load = DriveCoreClrCpuLoadAsync(cts.Token);
        var result = await _eventPipeCpuSampler!.SampleAsync(
            _coreClrSample!.ProcessId,
            CollectionWindow,
            topN: 10,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        await load.ConfigureAwait(false);
        return result.Summary.TotalSamples;
    }

    [Benchmark]
    public async Task<long> RoutingCpuSamplerCoreClrSample()
    {
        await EnsureCoreClrReadyAsync().ConfigureAwait(false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var load = DriveCoreClrCpuLoadAsync(cts.Token);
        var result = await _routingCpuSampler!.SampleAsync(
            _coreClrSample!.ProcessId,
            CollectionWindow,
            topN: 10,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        await load.ConfigureAwait(false);
        return result.Summary.TotalSamples;
    }

    [Benchmark]
    public async Task<long> EventPipeAllocationSamplerCoreClrSample()
    {
        await EnsureCoreClrReadyAsync().ConfigureAwait(false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var load = DriveCoreClrAllocationLoadAsync(cts.Token);
        var result = await _eventPipeAllocationSampler!.SampleAsync(
            _coreClrSample!.ProcessId,
            CollectionWindow,
            topN: 10,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        await load.ConfigureAwait(false);
        return result.Summary.TotalBytes;
    }

    [Benchmark]
    public async Task<long> PerfNativeAotCpuSamplerNativeAotSample()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return -1;
        }

        await EnsureNativeAotReadyAsync().ConfigureAwait(false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var load = DriveNativeAotLoadAsync(cts.Token);
        var result = await _perfNativeAotCpuSampler!.SampleAsync(
            _nativeAotSample!.ProcessId,
            CollectionWindow,
            topN: 10,
            nativeAotSymbols: File.Exists(_nativeAotMapPath) ? new NativeAotSymbolResolutionOptions(_nativeAotMapPath) : null,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        await load.ConfigureAwait(false);
        return result.Summary.TotalSamples;
    }

    [Benchmark]
    public async Task<long> PerfNativeAllocSamplerCoreClrSample()
    {
        await EnsureCoreClrReadyAsync().ConfigureAwait(false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var load = DriveCoreClrAllocationLoadAsync(cts.Token);
        var result = await _perfNativeAllocSampler!.SampleAsync(
            _coreClrSample!.ProcessId,
            CollectionWindow,
            topN: 10,
            samplePeriod: 97,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        await load.ConfigureAwait(false);
        return result.Summary.TotalSampledAllocations;
    }

    [Benchmark]
    public async Task<long> RoutingNativeAllocSamplerCoreClrSample()
    {
        await EnsureCoreClrReadyAsync().ConfigureAwait(false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var load = DriveCoreClrAllocationLoadAsync(cts.Token);
        var result = await _routingNativeAllocSampler!.SampleAsync(
            _coreClrSample!.ProcessId,
            CollectionWindow,
            topN: 10,
            samplePeriod: 97,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        await load.ConfigureAwait(false);
        return result.Summary.TotalSampledAllocations;
    }

    [Benchmark]
    public async Task<long> PerfSchedOffCpuSamplerCoreClrSample()
    {
        await EnsureCoreClrReadyAsync().ConfigureAwait(false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var load = DriveCoreClrOffCpuLoadAsync(cts.Token);
        var result = await _perfSchedOffCpuSampler!.SampleAsync(
            _coreClrSample!.ProcessId,
            CollectionWindow,
            topN: 10,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        await load.ConfigureAwait(false);
        return result.Summary.TotalOffCpuMicros;
    }

    [Benchmark]
    public async Task<long> RoutingOffCpuSamplerCoreClrSample()
    {
        await EnsureCoreClrReadyAsync().ConfigureAwait(false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var load = DriveCoreClrOffCpuLoadAsync(cts.Token);
        var result = await _routingOffCpuSampler!.SampleAsync(
            _coreClrSample!.ProcessId,
            CollectionWindow,
            topN: 10,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        await load.ConfigureAwait(false);
        return result.Summary.TotalOffCpuMicros;
    }

    [Benchmark]
    public async Task<long> MemoryTrendCollectorCoreClrSample()
    {
        await EnsureCoreClrReadyAsync().ConfigureAwait(false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var load = DriveCoreClrMemoryGrowthAsync(cts.Token);
        var result = await _memoryTrendCollector!.CollectAsync(
            _coreClrSample!.ProcessId,
            durationSeconds: (int)CollectionWindow.TotalSeconds,
            sampleEverySeconds: 1,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        await load.ConfigureAwait(false);
        return result.Samples.Count;
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        await DisposeNativeAotAsync().ConfigureAwait(false);
        await DisposeCoreClrAsync().ConfigureAwait(false);
    }

    private async Task EnsureCoreClrReadyAsync()
    {
        if (_coreClrSample is null)
        {
            await SetupCoreClrAsync().ConfigureAwait(false);
        }
    }

    private async Task EnsureNativeAotReadyAsync()
    {
        if (_nativeAotSample is null)
        {
            await SetupNativeAotAsync().ConfigureAwait(false);
        }
    }

    private async Task DisposeNativeAotAsync()
    {
        if (_nativeAotSample is not null)
        {
            await _nativeAotSample.DisposeAsync().ConfigureAwait(false);
            _nativeAotSample = null;
        }
    }

    private async Task DisposeCoreClrAsync()
    {
        if (_coreClrSample is not null)
        {
            await _coreClrSample.DisposeAsync().ConfigureAwait(false);
            _coreClrSample = null;
        }
    }

    private Task DriveCoreClrCpuLoadAsync(CancellationToken cancellationToken)
        => DriveHttpBurstAsync(
            _coreClrSample!,
            LoadLeadIn,
            LoadDuration,
            workerCount: Math.Max(4, Environment.ProcessorCount),
            static worker => worker switch
            {
                0 => "/cpu-burn?ms=350",
                1 => "/generics?iterations=200000",
                _ => "/render?count=3000",
            },
            cancellationToken);

    private Task DriveCoreClrAllocationLoadAsync(CancellationToken cancellationToken)
        => DriveHttpBurstAsync(
            _coreClrSample!,
            LoadLeadIn,
            LoadDuration,
            workerCount: Math.Max(4, Environment.ProcessorCount / 2),
            static worker => worker % 2 == 0 ? "/render?count=5000" : "/generics?iterations=120000",
            cancellationToken);

    private Task DriveCoreClrOffCpuLoadAsync(CancellationToken cancellationToken)
        => DriveHttpBurstAsync(
            _coreClrSample!,
            LoadLeadIn,
            LoadDuration,
            workerCount: 48,
            static worker => worker % 2 == 0 ? "/activity?delayMs=1200" : "/activity?delayMs=900",
            cancellationToken);

    private Task DriveCoreClrMemoryGrowthAsync(CancellationToken cancellationToken)
        => DriveHttpBurstAsync(
            _coreClrSample!,
            LoadLeadIn,
            LoadDuration,
            workerCount: 1,
            static _ => "/leak?mb=1",
            cancellationToken,
            perRequestDelay: TimeSpan.FromMilliseconds(200));

    private Task DriveNativeAotLoadAsync(CancellationToken cancellationToken)
        => DriveHttpBurstAsync(
            _nativeAotSample!,
            LoadLeadIn,
            LoadDuration,
            workerCount: Math.Max(8, Environment.ProcessorCount),
            static _ => "/weatherforecast",
            cancellationToken,
            requireSuccessStatusCode: false);

    private static async Task DriveHttpBurstAsync(
        PublishedSampleHost host,
        TimeSpan leadIn,
        TimeSpan runFor,
        int workerCount,
        Func<int, string> pathFactory,
        CancellationToken cancellationToken,
        TimeSpan? perRequestDelay = null,
        bool requireSuccessStatusCode = true)
    {
        await Task.Delay(leadIn, cancellationToken).ConfigureAwait(false);
        using var client = new HttpClient
        {
            BaseAddress = new Uri(host.BaseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };
        using var localCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        localCts.CancelAfter(runFor);

        var workers = Enumerable.Range(0, workerCount)
            .Select(worker => Task.Run(async () =>
            {
                while (!localCts.IsCancellationRequested)
                {
                    using var response = await client.GetAsync(pathFactory(worker), localCts.Token).ConfigureAwait(false);
                    if (requireSuccessStatusCode)
                    {
                        response.EnsureSuccessStatusCode();
                    }
                    if (perRequestDelay is { } delay && delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, localCts.Token).ConfigureAwait(false);
                    }
                }
            }, CancellationToken.None))
            .ToArray();

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (localCts.IsCancellationRequested)
        {
        }
    }

    private sealed class FixedCapabilityDetector(RuntimeFlavor runtimeFlavor) : ICapabilityDetector
    {
        public Task<DiagnosticCapabilities> DetectAsync(int processId, CancellationToken cancellationToken = default)
            => Task.FromResult(new DiagnosticCapabilities(
                processId,
                runtimeFlavor,
                RuntimeInformation.FrameworkDescription,
                CanReadEventCounters: runtimeFlavor == RuntimeFlavor.CoreClr,
                CanSampleCpu: true,
                CanCollectGcDump: runtimeFlavor == RuntimeFlavor.CoreClr,
                CanCollectExceptions: runtimeFlavor == RuntimeFlavor.CoreClr,
                CanCollectHttpActivity: runtimeFlavor == RuntimeFlavor.CoreClr,
                CanCollectCustomEventSource: runtimeFlavor == RuntimeFlavor.CoreClr,
                CanCollectProcessDump: true,
                Notes: "Benchmark stub capability detector."));
    }

    private sealed class PublishedSampleHost : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly TaskCompletionSource<string> _listeningUrl;

        private PublishedSampleHost(Process process, TaskCompletionSource<string> listeningUrl)
        {
            _process = process;
            _listeningUrl = listeningUrl;
        }

        public int ProcessId => _process.Id;

        public string BaseUrl { get; private set; } = string.Empty;

        public static string FindRepositoryRoot()
        {
            var current = AppContext.BaseDirectory;
            for (var i = 0; i < 10; i++)
            {
                if (File.Exists(Path.Combine(current, "DotnetDiagnostics.slnx")))
                {
                    return current;
                }

                current = Path.GetFullPath(Path.Combine(current, ".."));
            }

            throw new DirectoryNotFoundException("Could not locate repository root from BenchmarkDotNet child process.");
        }

        public static string ResolvePublishDirectory(string sampleName)
            => Path.Combine(FindRepositoryRoot(), "benchmarks", "DiagnosedBenchmarks", "published", sampleName);

        public static async Task<PublishedSampleHost> StartAsync(
            string sampleName,
            IReadOnlyList<string> publishArguments,
            bool launchViaDotnet,
            string entryRelativePath,
            string readinessPath,
            bool allowServerErrorsDuringReadiness = false)
        {
            var publishDirectory = ResolvePublishDirectory(sampleName);
            Directory.CreateDirectory(publishDirectory);
            await PublishIfNeededAsync(publishArguments, publishDirectory).ConfigureAwait(false);

            var entryPath = Path.Combine(publishDirectory, entryRelativePath);
            if (!File.Exists(entryPath))
            {
                throw new FileNotFoundException($"Published entrypoint not found for {sampleName}.", entryPath);
            }

            var psi = new ProcessStartInfo(launchViaDotnet ? "dotnet" : entryPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = publishDirectory,
            };
            if (launchViaDotnet)
            {
                psi.ArgumentList.Add(entryPath);
            }

            psi.ArgumentList.Add("--urls");
            psi.ArgumentList.Add("http://127.0.0.1:0");
            psi.Environment["DOTNET_NOLOGO"] = "1";
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

            var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start {sampleName}.");
            var listeningUrl = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var host = new PublishedSampleHost(process, listeningUrl);

            _ = Task.Run(() => DrainStdoutAsync(process, listeningUrl));
            _ = Task.Run(() => DrainStderrAsync(process));

            try
            {
                await DiagnosticReadiness.WaitForDiagnosticEndpointAsync(process.Id, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
                host.BaseUrl = await host.WaitForListeningUrlAsync(readinessPath, allowServerErrorsDuringReadiness).ConfigureAwait(false);
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
                    _process.WaitForExit(5_000);
                }
                catch
                {
                }
            }

            _process.Dispose();
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private static async Task PublishIfNeededAsync(IReadOnlyList<string> arguments, string publishDirectory)
        {
            if (Directory.EnumerateFileSystemEntries(publishDirectory).Any())
            {
                return;
            }

            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = FindRepositoryRoot(),
            };

            foreach (var argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to launch dotnet publish.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"dotnet publish failed for {publishDirectory}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
            }
        }

        private async Task<string> WaitForListeningUrlAsync(string readinessPath, bool allowServerErrorsDuringReadiness)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var url = await _listeningUrl.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            await WaitForHttpReadyAllowingRedirectsAsync(url, readinessPath, allowServerErrorsDuringReadiness).ConfigureAwait(false);
            return url;
        }

        private static async Task WaitForHttpReadyAllowingRedirectsAsync(string baseUrl, string readinessPath, bool allowServerErrors)
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var response = await client.GetAsync(readinessPath, CancellationToken.None).ConfigureAwait(false);
                    var code = (int)response.StatusCode;
                    if (code is >= 200 and < 400 || (allowServerErrors && code >= 500))
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                }

                await Task.Delay(250).ConfigureAwait(false);
            }

            throw SkipException.ForReason($"Sample did not accept HTTP requests on {baseUrl}{readinessPath} within the timeout.");
        }

        private static async Task DrainStdoutAsync(Process process, TaskCompletionSource<string> listeningUrl)
        {
            try
            {
                using var reader = process.StandardOutput;
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    var index = line.IndexOf("Now listening on:", StringComparison.Ordinal);
                    if (index >= 0 && !listeningUrl.Task.IsCompleted)
                    {
                        listeningUrl.TrySetResult(line[(index + "Now listening on:".Length)..].Trim());
                    }
                }
            }
            catch
            {
            }
        }

        private static async Task DrainStderrAsync(Process process)
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
        }
    }
}
