using System.Diagnostics;
using System.Net.Http;
using DotnetDiagnostics.TestSupport;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal sealed class AgentScenarioTarget : IAsyncDisposable
{
    private readonly LiveSampleProcess _sample;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _loadCancellation;
    private readonly Task _loadDriver;
    private readonly Process _terminationObserver;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;

    private AgentScenarioTarget(
        LiveSampleProcess sample,
        HttpClient http,
        CancellationTokenSource loadCancellation,
        Task loadDriver,
        Process terminationObserver)
    {
        _sample = sample;
        _http = http;
        _loadCancellation = loadCancellation;
        _loadDriver = loadDriver;
        _terminationObserver = terminationObserver;
    }

    public const string AuthorizedTargetId = "target-1";

    public int ProcessId => _sample.ProcessId;

    public static async Task<AgentScenarioTarget> StartAsync(
        ScenarioManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!ScenarioLiveRunner.SupportsCurrentPlatform(manifest))
        {
            throw new PlatformNotSupportedException(
                $"Scenario '{manifest.Id}' is not supported on the current platform.");
        }

        var stageTimeout = TimeSpan.FromSeconds(Math.Max(2, manifest.Budget.MaximumRuntimeSeconds / 3.0));
        var sample = await LiveSampleProcess.StartPublishedAsync(
            "BadCodeSample",
            new LiveSampleOptions
            {
                HarvestListeningUrl = true,
                WaitForHttpReady = true,
                ReadinessPath = "/",
                DiagnosticTimeout = stageTimeout,
                HttpTimeout = stageTimeout,
                Environment = new Dictionary<string, string>
                {
                    ["DOTNET_gcServer"] = "0",
                    ["DOTNET_TieredCompilation"] = "1",
                    ["DOTNET_TC_QuickJit"] = "1",
                    ["DOTNET_TieredPGO"] = "1",
                },
            }).ConfigureAwait(false);

        Process? terminationObserver = null;
        HttpClient? http = null;
        CancellationTokenSource? loadCancellation = null;
        try
        {
            terminationObserver = AcquireTerminationObserver(sample);
            http = new HttpClient
            {
                BaseAddress = new Uri(sample.BaseUrl),
                Timeout = TimeSpan.FromSeconds(30),
            };
            loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var (path, workers) = Activation(manifest);
            var loadDriver = DriveAsync(
                http,
                path,
                workers,
                TimeSpan.FromMilliseconds(manifest.Workload.WarmupMilliseconds),
                loadCancellation.Token);
            return new AgentScenarioTarget(sample, http, loadCancellation, loadDriver, terminationObserver);
        }
        catch (Exception startupFailure)
        {
            loadCancellation?.Dispose();
            http?.Dispose();
            if (terminationObserver is null)
            {
                await sample.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            try
            {
                await TerminateOwnedProcessAsync(sample, terminationObserver).ConfigureAwait(false);
            }
            catch (AgentScenarioCleanupException cleanupFailure)
            {
                throw new AgentScenarioCleanupException(
                    "Target startup failed and termination of the spawned process could not be confirmed.",
                    new AggregateException(startupFailure, cleanupFailure));
            }
            finally
            {
                terminationObserver.Dispose();
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeLock)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }

        await disposeTask.ConfigureAwait(false);
    }

    internal static async Task WaitForTerminationAsync(Process process, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new AgentScenarioCleanupException(
                $"Owned process {process.Id} did not terminate within {timeout.TotalSeconds:0.###} seconds.",
                exception);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new AgentScenarioCleanupException(
                $"Termination of owned process {process.Id} could not be observed.",
                exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Exception? loadFailure = null;
        try
        {
            await _loadCancellation.CancelAsync().ConfigureAwait(false);
            await _loadDriver.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            loadFailure = exception;
        }

        AgentScenarioCleanupException? cleanupFailure = null;
        try
        {
            await TerminateOwnedProcessAsync(_sample, _terminationObserver).ConfigureAwait(false);
        }
        catch (AgentScenarioCleanupException exception)
        {
            cleanupFailure = exception;
        }
        finally
        {
            _terminationObserver.Dispose();
            _loadCancellation.Dispose();
            _http.Dispose();
        }

        if (cleanupFailure is not null)
        {
            throw loadFailure is null
                ? cleanupFailure
                : new AgentScenarioCleanupException(
                    cleanupFailure.Message,
                    new AggregateException(loadFailure, cleanupFailure));
        }

        if (loadFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(loadFailure).Throw();
        }
    }

    private static Process AcquireTerminationObserver(LiveSampleProcess sample)
    {
        var expectedStartTime = sample.Process.StartTime;
        var observer = Process.GetProcessById(sample.ProcessId);
        try
        {
            if (observer.StartTime != expectedStartTime)
            {
                throw new InvalidOperationException("The spawned process identity changed before ownership was established.");
            }

            return observer;
        }
        catch
        {
            observer.Dispose();
            throw;
        }
    }

    private static async Task TerminateOwnedProcessAsync(
        LiveSampleProcess sample,
        Process terminationObserver)
    {
        try
        {
            if (!terminationObserver.HasExited)
            {
                terminationObserver.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // The shared fixture performs a second teardown attempt below.
        }

        try
        {
            await sample.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Actual termination observation below is authoritative.
        }

        await WaitForTerminationAsync(terminationObserver, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    private static (string Path, int Workers) Activation(ScenarioManifest manifest)
        => manifest.Id switch
        {
            "culture-lookup" => (
                $"{Parameter(manifest, "endpoint")}?iterations={PositiveInt(manifest, "iterations")}",
                Math.Clamp(
                    Environment.ProcessorCount,
                    PositiveInt(manifest, "minimumWorkers"),
                    PositiveInt(manifest, "maximumWorkers"))),
            "sync-over-async" => (
                $"{Parameter(manifest, "endpoint")}?n={PositiveInt(manifest, "n")}&delaySeconds={PositiveInt(manifest, "delaySeconds")}",
                PositiveInt(manifest, "concurrentRequests")),
            "healthy-sync-over-async" => (
                $"{Parameter(manifest, "endpoint")}?n={PositiveInt(manifest, "n")}&delaySeconds={PositiveInt(manifest, "delaySeconds")}",
                PositiveInt(manifest, "concurrentRequests")),
            "lock-storm" => (
                $"{Parameter(manifest, "endpoint")}?seconds={PositiveInt(manifest, "seconds")}&blockers={PositiveInt(manifest, "blockers")}",
                1),
            "gc-storm" => (
                $"{Parameter(manifest, "endpoint")}?count={PositiveInt(manifest, "count")}",
                PositiveInt(manifest, "concurrentRequests")),
            _ => throw new InvalidDataException($"No blinded-agent activation is registered for '{manifest.Id}'."),
        };

    private static async Task DriveAsync(
        HttpClient http,
        string path,
        int workers,
        TimeSpan warmup,
        CancellationToken cancellationToken)
    {
        if (warmup > TimeSpan.Zero)
        {
            await Task.Delay(warmup, cancellationToken).ConfigureAwait(false);
        }

        await Task.WhenAll(Enumerable.Range(0, workers).Select(async _ =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var response = await http.GetAsync(
                        path,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }
        })).ConfigureAwait(false);
    }

    private static string Parameter(ScenarioManifest manifest, string name)
        => manifest.Workload.Parameters.TryGetValue(name, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new InvalidDataException($"Scenario '{manifest.Id}' is missing parameter '{name}'.");

    private static int PositiveInt(ScenarioManifest manifest, string name)
        => int.TryParse(Parameter(manifest, name), out var value) && value > 0
            ? value
            : throw new InvalidDataException($"Scenario '{manifest.Id}' parameter '{name}' must be positive.");
}

internal sealed class AgentScenarioCleanupException(string message, Exception innerException)
    : Exception(message, innerException);
