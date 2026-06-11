using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnostics.Core.Launch;

/// <summary>
/// Launches a target .NET application as a <b>child process</b> of the current process and waits for
/// its runtime diagnostic IPC endpoint to come up. This is the host-neutral primitive behind the
/// standalone CLI's opt-in <c>--launch</c> dev mode (issue #365): on Linux under
/// <c>kernel.yama.ptrace_scope=1</c> a tracer may <c>PTRACE_ATTACH</c> only to its own descendants,
/// so making the diagnostics process the target's parent unblocks the ClrMD-backed live-attach tools
/// (<c>inspect-heap --source live</c>) with zero privilege and zero host change.
/// </summary>
/// <remarks>
/// <para>This primitive intentionally lives in Core — it only wraps <see cref="Process"/> and the
/// existing <see cref="DiagnosticsClient.GetPublishedProcesses()"/> readiness probe, carries no MCP
/// knowledge, and never modifies the target application (only its process parentage changes).</para>
/// <para>The launched program must keep the runtime in the <b>same</b> process it was spawned as —
/// e.g. <c>dotnet App.dll</c> or a published apphost. Launchers that fork a separate runtime child
/// (notably <c>dotnet run</c>, which builds and then spawns the app) are not supported because the
/// real runtime PID would not be the direct child and would not appear as the launched pid's
/// diagnostic endpoint.</para>
/// </remarks>
public static class ChildProcessLauncher
{
    /// <summary>
    /// Spawns <paramref name="fileName"/> with <paramref name="arguments"/> as a child of the current
    /// process. The returned <see cref="LaunchedTarget"/> owns the process lifetime and terminates it on
    /// dispose. When <paramref name="consoleSink"/> is <see langword="null"/> the child inherits this
    /// process's console (real-time output for interactive use); when non-null the child's stdout and
    /// stderr are redirected and pumped to <paramref name="consoleSink"/> instead — used by the CLI in
    /// <c>--json</c> mode so the launched app's logging never contaminates the machine-readable envelope
    /// on stdout.
    /// </summary>
    public static LaunchedTarget Launch(string fileName, IReadOnlyList<string> arguments, TextWriter? consoleSink = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var redirect = consoleSink is not null;
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            RedirectStandardInput = false,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to launch '{fileName}': no process was started.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to launch '{fileName}': {ex.Message}", ex);
        }

        if (redirect)
        {
            // Pump the child's stdout+stderr to the sink (e.g. the CLI's stderr) so stdout stays clean
            // for the JSON envelope. Console writers are synchronized, so writes from the reader threads
            // are safe.
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { consoleSink!.WriteLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { consoleSink!.WriteLine(e.Data); } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        return new LaunchedTarget(process);
    }

    /// <summary>
    /// Polls <see cref="DiagnosticsClient.GetPublishedProcesses()"/> until <paramref name="processId"/>
    /// advertises a diagnostic endpoint, the <paramref name="timeout"/> elapses, or
    /// <paramref name="cancellationToken"/> is cancelled. Returns <see langword="true"/> once the
    /// endpoint is available; <see langword="false"/> on timeout.
    /// </summary>
    public static async Task<bool> WaitForDiagnosticEndpointAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (DiagnosticsClient.GetPublishedProcesses().Contains(processId))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // GetPublishedProcesses enumerates a transient socket directory and can throw while a
                // process is mid-startup; treat that as "not ready yet" and keep polling.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}
