using System.Diagnostics;

namespace DotnetDiagnosticsMcp.Core.Drilldown;

/// <summary>
/// Host-neutral driver that prunes drill-down handles whose target process has exited. Keeps the
/// in-memory <see cref="MemoryDiagnosticHandleStore"/> from leaking artifacts and avoids handing a
/// consumer a handle whose process is gone (which would otherwise linger until TTL expiry).
/// </summary>
/// <remarks>
/// <para>This is the single source of truth for the dead-PID sweep, shared by the MCP server's
/// hosted <c>HandleEvictionBackgroundService</c> and the standalone CLI <c>session</c> REPL (issue
/// #300). It deliberately depends on nothing from <c>Microsoft.Extensions.Hosting</c> /
/// <c>Microsoft.Extensions.Logging</c>: the timer loop lives in <see cref="RunAsync"/> and progress
/// is surfaced via callbacks, so each host can plug in its own logging.</para>
/// <para>Process liveness is resolved through an injectable predicate so tests can drive the sweep
/// deterministically without spawning real processes.</para>
/// </remarks>
public sealed class DeadProcessHandleEvictor
{
    private readonly IDiagnosticHandleStore _store;
    private readonly Func<int, bool> _isProcessAlive;

    /// <param name="store">The handle store to sweep. Only a <see cref="MemoryDiagnosticHandleStore"/>
    /// exposes the registered-PID set; any other implementation makes the sweep a no-op.</param>
    /// <param name="isProcessAlive">Predicate returning <c>true</c> while the given PID is running.
    /// Defaults to a <see cref="Process.GetProcessById(int)"/> probe; override in tests.</param>
    public DeadProcessHandleEvictor(IDiagnosticHandleStore store, Func<int, bool>? isProcessAlive = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _isProcessAlive = isProcessAlive ?? DefaultIsProcessAlive;
    }

    /// <summary>
    /// Runs a single sweep: invalidates every PID-evictable handle whose target process has exited.
    /// Returns the total number of handles dropped. <paramref name="onEvicted"/> (if supplied) is
    /// invoked once per exited PID that had at least one handle dropped, with <c>(pid, count)</c>.
    /// </summary>
    public int EvictDeadProcesses(Action<int, int>? onEvicted = null)
    {
        // Only the concrete in-memory store tracks which PIDs have live handles; for any other
        // implementation there is nothing to sweep.
        if (_store is not MemoryDiagnosticHandleStore memoryStore)
        {
            return 0;
        }

        var removed = 0;
        foreach (var pid in memoryStore.RegisteredProcessIds())
        {
            if (_isProcessAlive(pid))
            {
                continue;
            }

            var dropped = _store.InvalidateForProcess(pid);
            if (dropped > 0)
            {
                onEvicted?.Invoke(pid, dropped);
                removed += dropped;
            }
        }

        return removed;
    }

    /// <summary>
    /// Sweeps on a fixed <paramref name="interval"/> until <paramref name="cancellationToken"/> is
    /// cancelled. Per-tick exceptions (other than cancellation) are reported via
    /// <paramref name="onError"/> and swallowed so a transient failure never tears down the loop.
    /// </summary>
    public async Task RunAsync(
        TimeSpan interval,
        Action<int, int>? onEvicted = null,
        Action<Exception>? onError = null,
        CancellationToken cancellationToken = default)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                EvictDeadProcesses(onEvicted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                onError?.Invoke(ex);
            }
        }
    }

    private static bool DefaultIsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with that id is running.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
