using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace DotnetDiagnostics.TestSupport;

/// <summary>
/// A live published-DLL sample process (e.g. <c>CoreClrSample</c>, <c>BadCodeSample</c>) launched
/// via <c>dotnet &lt;dll&gt;</c> so the captured PID *is* the application (no <c>dotnet run</c>
/// wrapper host). Owns spawn, stdio draining, the diagnostic-endpoint readiness gate, optional
/// listening-URL harvesting / HTTP readiness, and process-tree teardown. Dispose to kill the tree.
/// </summary>
public sealed class LiveSampleProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly TaskCompletionSource<string> _listeningUrlTcs;
    private readonly StreamReader _stdoutReader;
    private readonly StreamReader _stderrReader;
    private Task _stdout = Task.CompletedTask;
    private Task _stderr = Task.CompletedTask;
    private Task? _disposal;
    private readonly object _disposeGate = new();
    internal static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    private LiveSampleProcess(Process process, string sampleDll, TaskCompletionSource<string> listeningUrlTcs)
    {
        _process = process;
        SampleDll = sampleDll;
        _listeningUrlTcs = listeningUrlTcs;
        _stdoutReader = process.StandardOutput;
        _stderrReader = process.StandardError;
    }

    /// <summary>The spawned process.</summary>
    public Process Process => _process;

    /// <summary>OS process id of the running sample.</summary>
    public int ProcessId => _process.Id;

    /// <summary>Absolute path of the sample DLL that was launched.</summary>
    public string SampleDll { get; }

    /// <summary>The base URL once HTTP readiness has completed. Throws if accessed beforehand.</summary>
    public string BaseUrl => _baseUrl
        ?? throw new InvalidOperationException("BaseUrl is unavailable until HTTP readiness has completed (set WaitForHttpReady or call WaitForListeningUrlAsync).");

    private string? _baseUrl;

    /// <summary>True while the process is alive.</summary>
    public bool IsRunning => !_process.HasExited;

    /// <summary>
    /// Launches <paramref name="sampleName"/>'s published DLL and returns once the diagnostic
    /// endpoint is up (and, when <see cref="LiveSampleOptions.WaitForHttpReady"/> is set, once the
    /// HTTP endpoint accepts requests). Throws <see cref="SkipException"/> when the sample binary
    /// is missing or fails to start.
    /// </summary>
    public static Task<LiveSampleProcess> StartPublishedAsync(string sampleName, LiveSampleOptions? options = null)
        => StartPublishedAsync(sampleName, options, CancellationToken.None);

    /// <summary>Includes post-spawn readiness in caller cancellation and observes owned cleanup before failure returns.</summary>
    public static async Task<LiveSampleProcess> StartPublishedAsync(string sampleName, LiveSampleOptions? options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new LiveSampleOptions();
        var harvestUrl = options.HarvestListeningUrl || options.WaitForHttpReady;

        var sampleDll = SampleLocator.LocateSampleDll(sampleName)
            ?? throw SkipException.ForReason($"{sampleName}.dll not found. Build the sample before running this test.");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(sampleDll)!,
        };
        psi.ArgumentList.Add(sampleDll);
        if (options.BindHttpPort)
        {
            psi.ArgumentList.Add("--urls");
            psi.ArgumentList.Add("http://127.0.0.1:0");
        }

        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        if (options.Environment is not null)
        {
            foreach (var (key, value) in options.Environment)
            {
                psi.Environment[key] = value;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var process = Process.Start(psi)
            ?? throw SkipException.ForReason($"Failed to start {sampleName}.");

        var listeningUrlTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sample = new LiveSampleProcess(process, sampleDll, listeningUrlTcs);

        await CompleteStartupAsync(async () =>
        {
            // Keep the existing line readers, but retain their completion for owned cleanup.
            sample._stdout = Task.Run(async () =>
            {
                using var reader = sample._stdoutReader;
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    if (!harvestUrl)
                    {
                        continue;
                    }

                    var idx = line.IndexOf("Now listening on:", StringComparison.Ordinal);
                    if (idx >= 0 && !listeningUrlTcs.Task.IsCompleted)
                    {
                        listeningUrlTcs.TrySetResult(line[(idx + "Now listening on:".Length)..].Trim());
                    }
                }
            });

            sample._stderr = Task.Run(async () =>
            {
                using var reader = sample._stderrReader;
                while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
                {
                }
            });

            await DiagnosticReadiness.WaitForDiagnosticEndpointAsync(process.Id, options.DiagnosticTimeout, cancellationToken).ConfigureAwait(false);

            if (options.WaitForHttpReady)
            {
                sample._baseUrl = await sample.WaitForListeningUrlAsync(options.HttpTimeout, options.ReadinessPath, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }, sample.DisposeAsync).ConfigureAwait(false);

        return sample;
    }

    internal static async Task CompleteStartupAsync(Func<Task> readiness, Func<ValueTask> cleanup)
    {
        try { await readiness().ConfigureAwait(false); }
        catch (Exception primary)
        {
            try { await cleanup().ConfigureAwait(false); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Sample startup failed and owned cleanup also failed.", primary, cleanupFailure);
            }
            throw;
        }
    }

    /// <summary>
    /// Awaits the harvested listening URL and confirms HTTP readiness against
    /// <paramref name="readinessPath"/>. Throws <see cref="SkipException"/> on timeout.
    /// </summary>
    public Task<string> WaitForListeningUrlAsync(TimeSpan timeout, string readinessPath = "/")
        => WaitForListeningUrlAsync(timeout, readinessPath, CancellationToken.None);

    /// <summary>Waits for the URL and HTTP readiness without relabeling caller cancellation as a timeout.</summary>
    public async Task<string> WaitForListeningUrlAsync(TimeSpan timeout, string readinessPath, CancellationToken cancellationToken)
    {
        var url = await WaitForUrlAsync(_listeningUrlTcs.Task, SampleDll, timeout, TimeProvider.System, cancellationToken).ConfigureAwait(false);
        await DiagnosticReadiness.WaitForHttpReadyAsync(url, timeout, readinessPath, cancellationToken).ConfigureAwait(false);
        _baseUrl = url;
        return url;
    }

    internal static async Task<string> WaitForUrlAsync(Task<string> listeningUrl, string sampleDll, TimeSpan timeout,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await listeningUrl.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw SkipException.ForReason($"{Path.GetFileNameWithoutExtension(sampleDll)} did not advertise an HTTP listening URL within the timeout.");
        }
    }

    /// <summary>Kills the owned process tree and observes exit and both line readers within one
    /// independent five-second deadline. Failed or incomplete cleanup is reported, not ignored.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _listeningUrlTcs.TrySetCanceled();
        var failures = new List<Exception>();
        try
        {
            await ObserveCleanupAsync(() =>
            {
                if (!_process.HasExited)
                {
                    try { _process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (_process.HasExited) { }
                }
            }, _process.WaitForExitAsync(), _stdout, _stderr, CleanupTimeout, TimeProvider.System).ConfigureAwait(false);
        }
        catch (Exception error) { failures.Add(error); }
        DisposeResource(_stdoutReader, failures);
        DisposeResource(_stderrReader, failures);
        DisposeResource(_process, failures);
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Owned sample cleanup had multiple failures.", failures);
    }

    private static void DisposeResource(IDisposable resource, List<Exception> failures)
    {
        try { resource.Dispose(); }
        catch (Exception error) { failures.Add(error); }
    }

    internal static async Task ObserveCleanupAsync(Action terminate, Task exit, Task stdout, Task stderr,
        TimeSpan timeout, TimeProvider timeProvider)
    {
        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        Exception? terminationFailure = null;
        try { terminate(); }
        catch (Exception error) { terminationFailure = error; }
        var settled = Task.WhenAll(exit, stdout, stderr);
        try
        {
            await settled.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception observationFailure)
        {
            // A bounded failure is not quiescence. Observe any later aggregate fault as well.
            _ = settled.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var failure = observationFailure is OperationCanceledException && deadline.IsCancellationRequested
                ? new TimeoutException($"Owned sample cleanup exceeded {timeout}: exit={exit.Status}, stdout={stdout.Status}, stderr={stderr.Status}.", observationFailure)
                : observationFailure;
            if (terminationFailure is not null)
                throw new AggregateException("Owned termination and exit/reader observation failed.", terminationFailure, failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (terminationFailure is not null)
            throw new AggregateException("Owned process termination failed.", terminationFailure);
    }
}
