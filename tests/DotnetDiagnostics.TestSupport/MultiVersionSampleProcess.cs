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

    private MultiVersionSampleProcess(Process process, string runtimeDescription)
    {
        _process = process;
        RuntimeDescription = runtimeDescription;
    }

    /// <summary>OS process id of the running sample.</summary>
    public int ProcessId => _process.Id;

    /// <summary>The <c>RuntimeInformation.FrameworkDescription</c> string reported by the sample
    /// on startup (e.g. <c>.NET 8.0.26</c>), captured from its stdout.</summary>
    public string RuntimeDescription { get; }

    /// <summary>True while the process is alive.</summary>
    public bool IsRunning => !_process.HasExited;

    /// <summary>
    /// Builds the required major version from <paramref name="targetFramework"/> (e.g. <c>net8.0</c>
    /// -&gt; <c>8</c>) and throws <see cref="SkipException"/> if that CoreCLR major isn't installed
    /// on this host, before attempting to locate or launch the sample.
    /// </summary>
    public static async Task<MultiVersionSampleProcess> StartAsync(string targetFramework, TimeSpan? timeout = null)
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
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(sampleDll)!,
        };
        psi.ArgumentList.Add(sampleDll);
        psi.Environment["DOTNET_NOLOGO"] = "1";

        var process = Process.Start(psi)
            ?? throw SkipException.ForReason($"Failed to start MultiVersionSample ({targetFramework}).");

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
            var runtimeDescription = await runtimeTcs.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            return new MultiVersionSampleProcess(process, runtimeDescription);
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
