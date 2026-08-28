using System.Diagnostics;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using DotnetDiagnostics.BenchmarkDotNet;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.ProcessDiscovery;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnostics.MultiTargetSmoke;

public static class MultiTargetSmokeProgram
{
    public static Task<int> Main(string[] args)
        => RunAsync(args);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], "--worker", StringComparison.Ordinal))
        {
            await RunWorkerAsync().ConfigureAwait(false);
            return 0;
        }

        try
        {
            Console.WriteLine($"HostRuntime: {RuntimeInformation.FrameworkDescription}");
            await ExerciseCoreAsync().ConfigureAwait(false);
            ExerciseBenchmarkDotNet();
            Console.WriteLine("SMOKE_OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task ExerciseCoreAsync()
    {
        await using var worker = await WorkerProcess.StartAsync().ConfigureAwait(false);

        var discovery = new LocalProcessDiscovery();
        var published = discovery.ListProcesses();
        if (!published.Any(process => process.ProcessId == worker.ProcessId))
        {
            throw new InvalidOperationException($"Process discovery did not surface worker pid {worker.ProcessId}.");
        }

        var collector = new EventPipeCounterCollector();
        var snapshot = await collector.CollectAsync(
            worker.ProcessId,
            TimeSpan.FromSeconds(6),
            providers: ["System.Runtime"],
            intervalSeconds: 1,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        if (!snapshot.Counters.Any(counter => counter.Provider == "System.Runtime"))
        {
            throw new InvalidOperationException("Counter collection returned no System.Runtime counters.");
        }

        if (!snapshot.Counters.Any(counter => counter.Provider == "System.Runtime" && counter.Name == "cpu-usage"))
        {
            throw new InvalidOperationException("Counter collection did not report System.Runtime/cpu-usage.");
        }

        Console.WriteLine($"CoreTargetRuntime: {worker.RuntimeDescription}");
        Console.WriteLine("CORE_OK");
    }

    private static void ExerciseBenchmarkDotNet()
    {
        var artifactsPath = Path.Combine(AppContext.BaseDirectory, "BenchmarkDotNet.Artifacts", "multitarget-smoke");
        if (Directory.Exists(artifactsPath))
        {
            Directory.Delete(artifactsPath, recursive: true);
        }

        Directory.CreateDirectory(artifactsPath);

        using var diagnoser = new DotnetDiagnosticsDiagnoser();
        var summary = BenchmarkRunner.Run<SmokeBenchmarks>(new SmokeBenchmarkConfig(artifactsPath, diagnoser));
        EnsureSuccessful(summary, expectedReports: 1);

        var entries = diagnoser.Entries.ToArray();
        if (entries.Length != 1)
        {
            throw new InvalidOperationException($"Expected one diagnostic capture, but BenchmarkDotNet produced {entries.Length}.");
        }

        var entry = entries[0];
        if (entry.IsError)
        {
            throw new InvalidOperationException($"Benchmark diagnoser returned an error capture: {entry.Headline}");
        }

        if (!string.Equals(entry.Kind, "gc", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected a gc capture, but saw '{entry.Kind}'.");
        }

        if (!File.Exists(entry.ArtifactPath))
        {
            throw new FileNotFoundException("Benchmark diagnoser did not write its artifact.", entry.ArtifactPath);
        }

        var artifactJson = File.ReadAllText(entry.ArtifactPath);
        if (!artifactJson.Contains("totalCollections", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Benchmark diagnoser artifact did not contain a GC summary payload.");
        }

        Console.WriteLine($"BenchmarkArtifact: {Path.GetFileName(entry.ArtifactPath)}");
        Console.WriteLine("BENCHMARK_OK");

        try
        {
            Directory.Delete(artifactsPath, recursive: true);
        }
        catch
        {
        }
    }

    private static void EnsureSuccessful(Summary summary, int expectedReports)
    {
        var failed = summary.Reports.Where(static report => !report.Success).ToArray();
        if (failed.Length > 0)
        {
            throw new InvalidOperationException(
                $"BenchmarkDotNet reported {failed.Length} failed benchmark case(s): "
                + string.Join(", ", failed.Select(static report => report.BenchmarkCase.DisplayInfo)));
        }

        if (summary.Reports.Length != expectedReports)
        {
            throw new InvalidOperationException(
                $"BenchmarkDotNet produced {summary.Reports.Length} report(s); expected {expectedReports}.");
        }
    }

    private static async Task RunWorkerAsync()
    {
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine("READY");
        Console.Out.Flush();

        var churn = new List<byte[]>();
        while (true)
        {
            churn.Add(new byte[64 * 1024]);
            if (churn.Count >= 64)
            {
                churn.Clear();
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Thread.SpinWait(50_000);
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    public class SmokeBenchmarks
    {
        private int _sink;

        [Benchmark]
        [DiagnosticKind(BenchmarkDiagnosticKind.Gc, DurationSeconds = 6)]
        public int AllocationChurn()
        {
            var allocations = new List<byte[]>();
            var sw = Stopwatch.StartNew();
            var total = 0;
            while (sw.Elapsed < TimeSpan.FromSeconds(7))
            {
                var bytes = new byte[64 * 1024];
                bytes[0] = 1;
                allocations.Add(bytes);
                total += bytes[0];

                if (allocations.Count >= 64)
                {
                    allocations.Clear();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }

            Volatile.Write(ref _sink, total);
            return _sink;
        }
    }

    private sealed class SmokeBenchmarkConfig : ManualConfig
    {
        public SmokeBenchmarkConfig(string artifactsPath, DotnetDiagnosticsDiagnoser diagnoser)
        {
            ArtifactsPath = artifactsPath;

            AddJob(Job.Dry
                .WithId("Smoke")
                .WithLaunchCount(1)
                .WithWarmupCount(0)
                .WithIterationCount(1)
                .WithInvocationCount(1)
                .WithUnrollFactor(1)
                .WithStrategy(RunStrategy.Monitoring)
                .WithToolchain(InProcessNoEmitToolchain.Instance));
            AddDiagnoser(diagnoser);
            AddLogger(ConsoleLogger.Default);
        }
    }

    private sealed class WorkerProcess : IAsyncDisposable
    {
        private readonly Process _process;

        private WorkerProcess(Process process, string runtimeDescription)
        {
            _process = process;
            RuntimeDescription = runtimeDescription;
        }

        public int ProcessId => _process.Id;

        public string RuntimeDescription { get; }

        public static async Task<WorkerProcess> StartAsync()
        {
            var currentDll = typeof(MultiTargetSmokeProgram).Assembly.Location;
            if (string.IsNullOrWhiteSpace(currentDll))
            {
                throw new InvalidOperationException("Could not resolve the current smoke host dll path.");
            }

            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            psi.ArgumentList.Add(currentDll);
            psi.ArgumentList.Add("--worker");
            psi.Environment["DOTNET_NOLOGO"] = "1";

            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start the smoke worker process.");

            _ = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null)
                    {
                    }
                }
                catch
                {
                }
            });

            try
            {
                var runtimeDescription = string.Empty;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(cts.Token).ConfigureAwait(false);
                    if (line is null)
                    {
                        throw new InvalidOperationException("Smoke worker exited before becoming ready.");
                    }

                    if (line.StartsWith("Runtime: ", StringComparison.Ordinal))
                    {
                        runtimeDescription = line["Runtime: ".Length..];
                        continue;
                    }

                    if (line == "READY")
                    {
                        break;
                    }
                }

                await WaitForDiagnosticEndpointAsync(process.Id, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                return new WorkerProcess(process, runtimeDescription);
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

        private static async Task WaitForDiagnosticEndpointAsync(int processId, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                try
                {
                    if (DiagnosticsClient.GetPublishedProcesses().Contains(processId))
                    {
                        return;
                    }
                }
                catch
                {
                }

                await Task.Delay(100).ConfigureAwait(false);
            }

            throw new TimeoutException($"Smoke worker pid {processId} did not publish its diagnostic endpoint within {timeout}.");
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
    }
}
