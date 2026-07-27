using System.Diagnostics;
using System.Globalization;

namespace DotnetDiagnostics.Core.Launch;

/// <summary>
/// Owns the lifetime of a child process spawned by <see cref="ChildProcessLauncher.Launch"/>. The
/// diagnostics process is the target's ptrace parent for as long as this handle is alive, which is
/// what unblocks descendant attach under Yama <c>ptrace_scope=1</c>. Disposing terminates the child
/// (best-effort) and removes Unix diagnostic artifacts matching both its pid and launch-specific
/// runtime identity, so a launched dev target never outlives the CLI invocation / session that owns it.
/// </summary>
public sealed class LaunchedTarget : IAsyncDisposable, IDisposable
{
    internal enum CleanupStage
    {
        AfterInitialScan,
        AfterPreExitScan,
    }

    private static readonly TimeSpan TerminationWaitTimeout = TimeSpan.FromSeconds(5);
    private readonly Process _process;
    private readonly int _processId;
    private readonly string? _diagnosticArtifactIdentity;
    private readonly string[] _diagnosticArtifactDirectories;
    private readonly Action<CleanupStage, int, string>? _cleanupObserver;
    private bool _disposed;

    internal LaunchedTarget(Process process, string[]? diagnosticArtifactDirectories = null)
        : this(process, diagnosticArtifactDirectories, cleanupObserver: null)
    {
    }

    internal LaunchedTarget(
        Process process,
        string[]? diagnosticArtifactDirectories,
        Action<CleanupStage, int, string>? cleanupObserver)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _processId = process.Id;
        _diagnosticArtifactIdentity = TryGetDiagnosticArtifactIdentity(process);
        _diagnosticArtifactDirectories = diagnosticArtifactDirectories ?? Array.Empty<string>();
        _cleanupObserver = cleanupObserver;
    }

    /// <summary>Operating-system process id of the launched target.</summary>
    public int ProcessId => _processId;

    internal string? DiagnosticArtifactIdentity => _diagnosticArtifactIdentity;

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
        try
        {
            // TryKill probes HasExited and may reap an already-finished child, so remove the initial
            // set first while the pid still unambiguously belongs to this Process instance.
            CaptureDiagnosticArtifacts(diagnosticArtifacts);
            DeleteDiagnosticArtifacts(diagnosticArtifacts);
            NotifyCleanupObserver(CleanupStage.AfterInitialScan);

            var killSignaled = TryKill();
            if (killSignaled)
            {
                // Scan again after signaling kill but before WaitForExit can reap and release the pid.
                CaptureDiagnosticArtifacts(diagnosticArtifacts);
                DeleteDiagnosticArtifacts(diagnosticArtifacts);
                NotifyCleanupObserver(CleanupStage.AfterPreExitScan);
            }

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
            try
            {
                // Exact pid + launch identity matching remains safe even if the child was reaped and
                // the numeric pid has already been reused by another process.
                CaptureDiagnosticArtifacts(diagnosticArtifacts);
                DeleteDiagnosticArtifacts(diagnosticArtifacts);
            }
            finally
            {
                _process.Dispose();
            }
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
        try
        {
            // TryKill probes HasExited and may reap an already-finished child, so remove the initial
            // set first while the pid still unambiguously belongs to this Process instance.
            CaptureDiagnosticArtifacts(diagnosticArtifacts);
            DeleteDiagnosticArtifacts(diagnosticArtifacts);
            NotifyCleanupObserver(CleanupStage.AfterInitialScan);

            var killSignaled = TryKill();
            if (killSignaled)
            {
                // Scan again after signaling kill but before WaitForExitAsync can reap and release the pid.
                CaptureDiagnosticArtifacts(diagnosticArtifacts);
                DeleteDiagnosticArtifacts(diagnosticArtifacts);
                NotifyCleanupObserver(CleanupStage.AfterPreExitScan);
            }

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
            try
            {
                // Exact pid + launch identity matching remains safe even if the child was reaped and
                // the numeric pid has already been reused by another process.
                CaptureDiagnosticArtifacts(diagnosticArtifacts);
                DeleteDiagnosticArtifacts(diagnosticArtifacts);
            }
            finally
            {
                _process.Dispose();
            }
        }
    }

    private void NotifyCleanupObserver(CleanupStage stage)
    {
        if (_diagnosticArtifactIdentity is not null)
        {
            _cleanupObserver?.Invoke(stage, _processId, _diagnosticArtifactIdentity);
        }
    }

    private void CaptureDiagnosticArtifacts(HashSet<string> artifacts)
    {
        if (OperatingSystem.IsWindows()
            || _diagnosticArtifactIdentity is null
            || _diagnosticArtifactDirectories.Length == 0)
        {
            return;
        }

        var pid = _processId.ToString(CultureInfo.InvariantCulture);
        string[] names =
        [
            $"dotnet-diagnostic-{pid}-{_diagnosticArtifactIdentity}-socket",
            $"clr-debug-pipe-{pid}-{_diagnosticArtifactIdentity}-in",
            $"clr-debug-pipe-{pid}-{_diagnosticArtifactIdentity}-out",
        ];

        foreach (var directory in _diagnosticArtifactDirectories)
        {
            try
            {
                foreach (var name in names)
                {
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory, name, SearchOption.TopDirectoryOnly))
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

    private static string? TryGetDiagnosticArtifactIdentity(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            if (OperatingSystem.IsLinux())
            {
                var processId = process.Id.ToString(CultureInfo.InvariantCulture);
                var stat = File.ReadAllText($"/proc/{processId}/stat");
                var commandEnd = stat.LastIndexOf(')');
                if (commandEnd < 0 || commandEnd + 2 >= stat.Length)
                {
                    return null;
                }

                var fields = stat[(commandEnd + 2)..]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // CoreCLR's GetProcessIdDisambiguationKey uses field 22 (starttime jiffies since boot).
                const int StartTimeIndexAfterCommand = 19;
                if (fields.Length <= StartTimeIndexAfterCommand
                    || !ulong.TryParse(
                        fields[StartTimeIndexAfterCommand],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var startTime))
                {
                    return null;
                }

                return startTime.ToString(CultureInfo.InvariantCulture);
            }

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            {
                var startTime = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds();
                return startTime.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            // Without the runtime's launch-specific key, pid-only cleanup is unsafe.
        }

        return null;
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
