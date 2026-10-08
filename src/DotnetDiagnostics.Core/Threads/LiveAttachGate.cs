namespace DotnetDiagnostics.Core.Threads;

/// <summary>
/// Serializes live ClrMD suspend-attaches per process id so overlapping snapshots never stack
/// ptrace attaches on one target (issue #1136). Acquisition is bounded and cancellable; the
/// returned lease releases the slot on dispose, so every exit path (including cancellation
/// thrown while the target is attached) frees the next waiter. Gates are reference-counted and
/// removed when idle so short-lived pids do not accumulate.
/// </summary>
internal static class LiveAttachGate
{
    internal static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<int, Gate> Gates = new();

    internal static IDisposable Acquire(int processId, CancellationToken ct, TimeSpan? timeout = null)
    {
        Gate gate;
        lock (Gates)
        {
            if (!Gates.TryGetValue(processId, out gate!))
            {
                Gates[processId] = gate = new Gate();
            }
            gate.Users++;
        }

        var wait = timeout ?? DefaultWaitTimeout;
        bool acquired;
        try
        {
            acquired = gate.Semaphore.Wait(wait, ct);
        }
        catch
        {
            Release(processId, gate, permitHeld: false);
            throw;
        }

        if (!acquired)
        {
            Release(processId, gate, permitHeld: false);
            throw new TimeoutException(
                $"Timed out after {wait.TotalSeconds:0.#}s waiting for a previous live attach to process {processId} to release the target.");
        }
        return new Lease(processId, gate);
    }

    private static void Release(int processId, Gate gate, bool permitHeld)
    {
        lock (Gates)
        {
            if (permitHeld)
            {
                gate.Semaphore.Release();
            }
            if (--gate.Users <= 0)
            {
                Gates.Remove(processId);
                gate.Semaphore.Dispose();
            }
        }
    }

    internal static bool IsTracked(int processId)
    {
        lock (Gates) { return Gates.ContainsKey(processId); }
    }

    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }

    private sealed class Lease(int processId, Gate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Release(processId, gate, permitHeld: true);
            }
        }
    }
}
