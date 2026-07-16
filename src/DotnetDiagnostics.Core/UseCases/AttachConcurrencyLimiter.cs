

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>
/// Per-pid concurrency gate for live diagnostic attaches (#452, D2). Diagnostic IPC and
/// ptrace-based operations can suspend a given target process from only one attacher at a time, so two
/// simultaneous live attaches (collect_thread_snapshot, inspect_heap source=live,
/// collect_process_dump, capture_method_bytes) against the same pid collide and one fails
/// hard. This limiter serializes attaches keyed per pid (default 1 in flight per pid); when a
/// permit is unavailable within <see cref="AcquireTimeout"/> the caller receives a structured
/// retriable "busy" envelope rather than a crash. Dump-based work (no live pid) is never gated.
/// </summary>
public sealed class AttachConcurrencyLimiter
{
    /// <summary>Environment variable overriding the per-pid attach permit count (default 1).</summary>
    public const string MaxPerProcessEnvironmentVariable = "MCP_ATTACH_MAX_PER_PID";

    /// <summary>Environment variable overriding the busy-wait timeout in milliseconds (default 0 = fail fast).</summary>
    public const string AcquireTimeoutMsEnvironmentVariable = "MCP_ATTACH_WAIT_MS";

    /// <summary>Process-wide default instance honouring the environment overrides.</summary>
    public static AttachConcurrencyLimiter Shared { get; } = CreateFromEnvironment();

    private readonly Dictionary<int, Gate> _gates = new();
    private readonly int _maxPerProcess;

    public AttachConcurrencyLimiter(int maxPerProcess = 1, TimeSpan? acquireTimeout = null)
    {
        _maxPerProcess = maxPerProcess < 1 ? 1 : maxPerProcess;
        AcquireTimeout = acquireTimeout ?? TimeSpan.Zero;
    }

    /// <summary>Maximum live attaches in flight per pid.</summary>
    public int MaxPerProcess => _maxPerProcess;

    /// <summary>How long to wait for a permit before reporting busy.</summary>
    public TimeSpan AcquireTimeout { get; }

    /// <summary>
    /// Attempts to acquire a permit for <paramref name="processId"/>. Returns a releaser that
    /// must be disposed, or <c>null</c> when the gate is saturated and no permit became free
    /// within <see cref="AcquireTimeout"/>.
    /// </summary>
    public Task<IDisposable?> TryAcquireAsync(int processId, CancellationToken cancellationToken)
    {
        // Reference-count gates so a long-running server attaching to many short-lived pids does
        // not accumulate idle SemaphoreSlim entries forever; the last releaser removes the pid.
        // Get-or-create + Waiters++ runs under the lock so a gate we hold a waiter on can never be
        // removed/disposed concurrently; the (possibly blocking) WaitAsync happens outside the lock.
        Gate gate;
        lock (_gates)
        {
            gate = _gates.TryGetValue(processId, out var existing)
                ? existing
                : _gates[processId] = new Gate(_maxPerProcess);
            gate.Waiters++;
        }

        return AcquireCoreAsync(processId, gate, cancellationToken);
    }

    private async Task<IDisposable?> AcquireCoreAsync(int processId, Gate gate, CancellationToken cancellationToken)
    {
        bool acquired;
        try
        {
            acquired = await gate.Semaphore.WaitAsync(AcquireTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseWaiter(processId, gate, permitHeld: false);
            throw;
        }

        if (!acquired)
        {
            ReleaseWaiter(processId, gate, permitHeld: false);
            return null;
        }

        return new Releaser(this, processId, gate);
    }

    private void ReleaseWaiter(int processId, Gate gate, bool permitHeld)
    {
        lock (_gates)
        {
            if (permitHeld)
            {
                gate.Semaphore.Release();
            }

            if (--gate.Waiters <= 0)
            {
                _gates.Remove(processId);
                gate.Semaphore.Dispose();
            }
        }
    }

    private static AttachConcurrencyLimiter CreateFromEnvironment()
    {
        var max = 1;
        if (int.TryParse(Environment.GetEnvironmentVariable(MaxPerProcessEnvironmentVariable), out var parsedMax) && parsedMax >= 1)
        {
            max = parsedMax;
        }

        var waitMs = 0;
        if (int.TryParse(Environment.GetEnvironmentVariable(AcquireTimeoutMsEnvironmentVariable), out var parsedWait) && parsedWait >= 0)
        {
            waitMs = parsedWait;
        }

        return new AttachConcurrencyLimiter(max, TimeSpan.FromMilliseconds(waitMs));
    }

    private sealed class Gate
    {
        public Gate(int maxPerProcess) => Semaphore = new SemaphoreSlim(maxPerProcess, maxPerProcess);

        public SemaphoreSlim Semaphore { get; }

        public int Waiters { get; set; }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly AttachConcurrencyLimiter _owner;
        private readonly int _processId;
        private Gate? _gate;

        public Releaser(AttachConcurrencyLimiter owner, int processId, Gate gate)
        {
            _owner = owner;
            _processId = processId;
            _gate = gate;
        }

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            if (gate is not null)
            {
                _owner.ReleaseWaiter(_processId, gate, permitHeld: true);
            }
        }
    }
}
