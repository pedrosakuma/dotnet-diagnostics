using System.Collections.Concurrent;

namespace DotnetDiagnostics.Core.Threads;

/// <summary>
/// Serializes live ClrMD suspend-attaches per process id so overlapping snapshots never stack
/// ptrace attaches on one target (issue #1136). Acquisition is bounded and cancellable; the
/// returned lease releases the slot on dispose, so every exit path (including cancellation
/// thrown while the target is attached) frees the next waiter.
/// </summary>
internal static class LiveAttachGate
{
    internal static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<int, SemaphoreSlim> Gates = new();

    internal static IDisposable Acquire(int processId, CancellationToken ct, TimeSpan? timeout = null)
    {
        var gate = Gates.GetOrAdd(processId, static _ => new SemaphoreSlim(1, 1));
        var wait = timeout ?? DefaultWaitTimeout;
        if (!gate.Wait(wait, ct))
        {
            throw new TimeoutException(
                $"Timed out after {wait.TotalSeconds:0.#}s waiting for a previous live attach to process {processId} to release the target.");
        }
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
