using System.Diagnostics;

namespace DotnetDiagnosticsMcp.TestSupport;

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

    private LiveSampleProcess(Process process, string sampleDll, TaskCompletionSource<string> listeningUrlTcs)
    {
        _process = process;
        SampleDll = sampleDll;
        _listeningUrlTcs = listeningUrlTcs;
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
    public static async Task<LiveSampleProcess> StartPublishedAsync(string sampleName, LiveSampleOptions? options = null)
    {
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

        var process = Process.Start(psi)
            ?? throw SkipException.ForReason($"Failed to start {sampleName}.");

        var listeningUrlTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sample = new LiveSampleProcess(process, sampleDll, listeningUrlTcs);

        // Drain stdout so the OS pipe buffer never fills (would deadlock the sample), harvesting
        // the "Now listening on: http://127.0.0.1:NNNN" line when requested.
        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = process.StandardOutput;
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
            }
            catch
            {
                // best-effort; if the read fails the URL TCS is never set and HTTP-driven tests
                // skip via WaitForListeningUrlAsync's timeout.
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
                // best-effort
            }
        });

        try
        {
            await DiagnosticReadiness.WaitForDiagnosticEndpointAsync(process.Id, options.DiagnosticTimeout).ConfigureAwait(false);

            if (options.WaitForHttpReady)
            {
                sample._baseUrl = await sample.WaitForListeningUrlAsync(options.HttpTimeout, options.ReadinessPath).ConfigureAwait(false);
            }
        }
        catch
        {
            // Readiness failed after the process was spawned; kill the tree so we don't leak it.
            await sample.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return sample;
    }

    /// <summary>
    /// Awaits the harvested listening URL and confirms HTTP readiness against
    /// <paramref name="readinessPath"/>. Throws <see cref="SkipException"/> on timeout.
    /// </summary>
    public async Task<string> WaitForListeningUrlAsync(TimeSpan timeout, string readinessPath = "/")
    {
        using var cts = new CancellationTokenSource(timeout);
        string url;
        try
        {
            url = await _listeningUrlTcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw SkipException.ForReason($"{Path.GetFileNameWithoutExtension(SampleDll)} did not advertise an HTTP listening URL within the timeout.");
        }

        await DiagnosticReadiness.WaitForHttpReadyAsync(url, timeout, readinessPath).ConfigureAwait(false);
        _baseUrl = url;
        return url;
    }

    /// <summary>Kills the entire process tree and disposes the underlying <see cref="Process"/>.</summary>
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
                // best-effort
            }
        }

        _process.Dispose();
        return ValueTask.CompletedTask;
    }
}
