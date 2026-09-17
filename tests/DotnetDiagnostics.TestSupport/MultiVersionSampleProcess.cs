using System.Diagnostics;

namespace DotnetDiagnostics.TestSupport;

/// <summary>
/// Launches the multi-targeted <c>MultiVersionSample</c> console app (see
/// samples/MultiVersionSample) built for a specific TFM, so cross-version tests can attach to a
/// real target running an older CoreCLR without needing an HTTP listener. Skips cleanly (via
/// <see cref="SkipException"/>) when either the TFM's build output or its underlying shared
/// runtime isn't present on the host — see docs/research/multi-version-target-support.md for why
/// this repo treats "runtime not installed on this host" as a skip, not a failure.
/// </summary>
public sealed class MultiVersionSampleProcess : IAsyncDisposable
{
    private readonly Process _process;
    private string _lastOutputLine = "(no output)";
    private readonly System.Collections.Concurrent.ConcurrentQueue<long> _heartbeats = new();
    public IReadOnlyList<long> Heartbeats => _heartbeats.ToArray();

    public async Task RequestGcAsync(string command)
    {
        await _process.StandardInput.WriteLineAsync(command).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Closes the pause-workload protocol and waits for its owned witness to join and exit.</summary>
    public async Task StopGcWorkloadAsync(CancellationToken cancellationToken)
    {
        _process.StandardInput.Close();
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (_process.ExitCode != 0)
            throw new InvalidOperationException($"GC pause workload exited with code {_process.ExitCode}: {LastOutputLine}");
    }

    private MultiVersionSampleProcess(Process process)
    {
        _process = process;
    }

    /// <summary>OS process id of the running sample.</summary>
    public int ProcessId => _process.Id;

    /// <summary>The <c>RuntimeInformation.FrameworkDescription</c> string reported by the sample
    /// on startup (e.g. <c>.NET 8.0.26</c>), captured from its stdout.</summary>
    public string RuntimeDescription { get; private set; } = "(not reported)";

    /// <summary>Latest stdout line, including bounded workload/GC progress for assertion diagnostics.</summary>
    public string LastOutputLine => Volatile.Read(ref _lastOutputLine);

    /// <summary>True while the process is alive.</summary>
    public bool IsRunning => !_process.HasExited;

    /// <summary>
    /// Builds the required major version from <paramref name="targetFramework"/> (e.g. <c>net8.0</c>
    /// -&gt; <c>8</c>) and throws <see cref="SkipException"/> if that CoreCLR major isn't installed
    /// on this host, before attempting to locate or launch the sample.
    /// When <paramref name="generateGcEvents"/> is true, the test fixture induces periodic
    /// collections until disposal so GC-event tests do not depend on allocation throughput.
    /// </summary>
    public static async Task<MultiVersionSampleProcess> StartAsync(
        string targetFramework,
        TimeSpan? timeout = null,
        bool generateGcEvents = false,
        bool gcPauseWorkload = false)
    {
        var major = ParseMajorVersion(targetFramework);
        if (!InstalledRuntimes.HasMajorVersion(major))
        {
            throw SkipException.ForReason($"Microsoft.NETCore.App {major}.x is not installed on this host; skipping {targetFramework} cross-version test.");
        }

        var sampleDll = SampleLocator.LocateMultiVersionSampleDll(targetFramework)
            ?? throw SkipException.ForReason($"MultiVersionSample.dll ({targetFramework}) not found. Build samples/MultiVersionSample for that TFM before running this test.");

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = gcPauseWorkload,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(sampleDll)!,
        };
        psi.ArgumentList.Add(sampleDll);
        if (generateGcEvents)
        {
            psi.ArgumentList.Add("--gc-events");
        }
        psi.Environment["DOTNET_NOLOGO"] = "1";
        if (gcPauseWorkload)
        {
            psi.ArgumentList.Add("--gc-pause-workload");
            psi.Environment["DOTNET_gcConcurrent"] = "1";
            psi.Environment["DOTNET_gcServer"] = "0";
        }

        var process = Process.Start(psi)
            ?? throw SkipException.ForReason($"Failed to start MultiVersionSample ({targetFramework}).");
        var sample = new MultiVersionSampleProcess(process);

        var runtimeTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = process.StandardOutput;
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    Volatile.Write(ref sample._lastOutputLine, line);
                    if (line.StartsWith("Heartbeat: ", StringComparison.Ordinal) &&
                        long.TryParse(line.AsSpan("Heartbeat: ".Length), out var ticks))
                    {
                        sample._heartbeats.Enqueue(ticks);
                        while (sample._heartbeats.Count > 800) sample._heartbeats.TryDequeue(out _);
                    }
                    if (line.StartsWith("Runtime: ", StringComparison.Ordinal) && !runtimeTcs.Task.IsCompleted)
                    {
                        runtimeTcs.TrySetResult(line["Runtime: ".Length..]);
                    }
                    else if (line == "READY")
                    {
                        readyTcs.TrySetResult();
                    }
                }
            }
            catch
            {
                // best-effort; readiness waits below time out if this drain loop fails.
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
            using var cts = new CancellationTokenSource(effectiveTimeout);
            await DiagnosticReadiness.WaitForDiagnosticEndpointAsync(process.Id, effectiveTimeout).ConfigureAwait(false);
            await readyTcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            sample.RuntimeDescription = await runtimeTcs.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            return sample;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
            throw SkipException.ForReason($"MultiVersionSample ({targetFramework}) did not become ready within {effectiveTimeout}.");
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    private static int ParseMajorVersion(string targetFramework)
    {
        // "net8.0" -> 8, "net10.0" -> 10
        var digits = targetFramework
            .TrimStart('n', 'e', 't')
            .Split('.')[0];
        return int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
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
