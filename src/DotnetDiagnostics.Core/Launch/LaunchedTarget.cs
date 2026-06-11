using System.Diagnostics;

namespace DotnetDiagnostics.Core.Launch;

/// <summary>
/// Owns the lifetime of a child process spawned by <see cref="ChildProcessLauncher.Launch"/>. The
/// diagnostics process is the target's ptrace parent for as long as this handle is alive, which is
/// what unblocks descendant attach under Yama <c>ptrace_scope=1</c>. Disposing terminates the child
/// (best-effort) so a launched dev target never outlives the CLI invocation / session that owns it.
/// </summary>
public sealed class LaunchedTarget : IAsyncDisposable, IDisposable
{
    private readonly Process _process;
    private bool _disposed;

    internal LaunchedTarget(Process process)
        => _process = process ?? throw new ArgumentNullException(nameof(process));

    /// <summary>Operating-system process id of the launched target.</summary>
    public int ProcessId => _process.Id;

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
        TryKill();
        _process.Dispose();
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
        if (TryKill())
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
            {
                // Reaped elsewhere or did not exit within the grace window; nothing more we can do.
            }
        }

        _process.Dispose();
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
