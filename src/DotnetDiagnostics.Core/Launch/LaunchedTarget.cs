using System.Diagnostics;
using System.Globalization;

namespace DotnetDiagnostics.Core.Launch;

/// <summary>
/// Owns the lifetime of a child process spawned by <see cref="ChildProcessLauncher.Launch"/>. The
/// diagnostics process is the target's ptrace parent for as long as this handle is alive, which is
/// what unblocks descendant attach under Yama <c>ptrace_scope=1</c>. Disposing terminates the child
/// (best-effort) and removes pid-qualified Unix diagnostic artifacts observed while the child is still
/// owned, so a launched dev target never outlives the CLI invocation / session that owns it.
/// </summary>
public sealed class LaunchedTarget : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan TerminationWaitTimeout = TimeSpan.FromSeconds(5);
    private readonly Process _process;
    private readonly int _processId;
    private readonly string[] _diagnosticArtifactDirectories;
    private readonly Action? _postKillObserver;
    private bool _disposed;

    internal LaunchedTarget(Process process, string[]? diagnosticArtifactDirectories = null)
        : this(process, diagnosticArtifactDirectories, postKillObserver: null)
    {
    }

    internal LaunchedTarget(
        Process process,
        string[]? diagnosticArtifactDirectories,
        Action? postKillObserver)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _processId = process.Id;
        _diagnosticArtifactDirectories = diagnosticArtifactDirectories ?? Array.Empty<string>();
        _postKillObserver = postKillObserver;
    }

    /// <summary>Operating-system process id of the launched target.</summary>
    public int ProcessId => _processId;

    /// <summary>True once the launched target has exited.</summary>
    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                // No process is associated (already disposed); treat as exited.
                return true;
            }
        }
    }

    /// <summary>
    /// Terminates the launched target (best-effort: it may already have exited) and releases the
    /// underlying <see cref="Process"/> handle. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var diagnosticArtifacts = new HashSet<string>(StringComparer.Ordinal);
        // TryKill probes HasExited and may reap an already-finished child, so remove the initial set
        // first while the pid still unambiguously belongs to this Process instance.
        CaptureDiagnosticArtifacts(diagnosticArtifacts);
        DeleteDiagnosticArtifacts(diagnosticArtifacts);
        try
        {
            var killSignaled = TryKill();
            if (killSignaled)
            {
                _postKillObserver?.Invoke();
                // Scan again after signaling kill but before WaitForExit can reap and release the pid.
                CaptureDiagnosticArtifacts(diagnosticArtifacts);
            }

            DeleteDiagnosticArtifacts(diagnosticArtifacts);

            if (killSignaled)
            {
                try
                {
                    _process.WaitForExit((int)TerminationWaitTimeout.TotalMilliseconds);
                }
                catch (InvalidOperationException)
                {
                    // Reaped elsewhere; pid-qualified artifacts were already removed before this wait.
                }
            }
        }
        finally
        {
            _process.Dispose();
        }
    }

    /// <summary>
    /// Terminates the launched target and waits briefly for it to exit so the process is reaped before
    /// the owning CLI invocation / session returns. Idempotent.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var diagnosticArtifacts = new HashSet<string>(StringComparer.Ordinal);
        // TryKill probes HasExited and may reap an already-finished child, so remove the initial set
        // first while the pid still unambiguously belongs to this Process instance.
        CaptureDiagnosticArtifacts(diagnosticArtifacts);
        DeleteDiagnosticArtifacts(diagnosticArtifacts);
        try
        {
            var killSignaled = TryKill();
            if (killSignaled)
            {
                _postKillObserver?.Invoke();
                // Scan again after signaling kill but before WaitForExitAsync can reap and release the pid.
                CaptureDiagnosticArtifacts(diagnosticArtifacts);
            }

            DeleteDiagnosticArtifacts(diagnosticArtifacts);

            if (killSignaled)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TerminationWaitTimeout);
                    await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
                {
                    // Reaped elsewhere or did not exit within the grace window; nothing more we can do.
                }
            }
        }
        finally
        {
            _process.Dispose();
        }
    }

    private void CaptureDiagnosticArtifacts(HashSet<string> artifacts)
    {
        if (OperatingSystem.IsWindows() || _diagnosticArtifactDirectories.Length == 0)
        {
            return;
        }

        var pid = _processId.ToString(CultureInfo.InvariantCulture);
        string[] patterns =
        [
            $"dotnet-diagnostic-{pid}-*-socket",
            $"clr-debug-pipe-{pid}-*-in",
            $"clr-debug-pipe-{pid}-*-out",
        ];

        foreach (var directory in _diagnosticArtifactDirectories)
        {
            try
            {
                var legacySocket = Path.Combine(directory, $"dotnet-diagnostic-{pid}-socket");
                if (File.Exists(legacySocket))
                {
                    artifacts.Add(legacySocket);
                }

                foreach (var pattern in patterns)
                {
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory, pattern, SearchOption.TopDirectoryOnly))
                    {
                        artifacts.Add(path);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort only: cleanup must never hide the command's real result.
            }
        }
    }

    private static void DeleteDiagnosticArtifacts(HashSet<string> artifacts)
    {
        foreach (var path in artifacts)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of artifacts observed while this launcher still owned the pid.
            }
        }
    }

    private bool TryKill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                return true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Already exited, no associated process, or the OS refused the kill — best-effort only.
        }

        return false;
    }
}
