namespace DotnetDiagnosticsMcp.Core.Jobs;

/// <summary>Lifecycle phase of a background collection job.</summary>
public enum CollectionJobStatus
{
    Running,
    Completed,
    Failed,
    Canceled,
}

/// <summary>
/// State container for a single background collection job. Mutated by
/// <see cref="CollectionJobRunner"/> as the underlying work transitions states. Readers
/// snapshot via <see cref="Snapshot"/> so they observe a consistent view.
/// </summary>
public sealed class CollectionJob : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly object _gate = new();
    private CollectionJobStatus _status;
    private object? _result;
    private DiagnosticError? _error;
    private DateTimeOffset? _completedAt;
    private string? _handleId;

    internal CollectionJob(string kind, int processId, DateTimeOffset startedAt, CancellationTokenSource cts)
    {
        Kind = kind;
        ProcessId = processId;
        StartedAt = startedAt;
        _cts = cts;
        _status = CollectionJobStatus.Running;
    }

    public string Kind { get; }
    public int ProcessId { get; }
    public DateTimeOffset StartedAt { get; }

    internal void AttachHandle(string handleId)
    {
        lock (_gate)
        {
            _handleId = handleId;
        }
    }

    internal void MarkCompleted<T>(DiagnosticResult<T> result, DateTimeOffset completedAt)
    {
        lock (_gate)
        {
            if (_status != CollectionJobStatus.Running)
            {
                return;
            }

            _result = result;
            _completedAt = completedAt;
            _status = result.IsError ? CollectionJobStatus.Failed : CollectionJobStatus.Completed;
            if (result.IsError && result.Error is not null)
            {
                _error = result.Error;
            }
        }
    }

    internal void MarkFailed(Exception ex, DateTimeOffset completedAt)
    {
        lock (_gate)
        {
            if (_status != CollectionJobStatus.Running)
            {
                return;
            }

            _status = CollectionJobStatus.Failed;
            _completedAt = completedAt;
            _error = new DiagnosticError(
                Kind: ex.GetType().Name,
                Message: ex.Message,
                Detail: ex.StackTrace);
        }
    }

    internal void MarkCanceled(DateTimeOffset completedAt)
    {
        lock (_gate)
        {
            if (_status != CollectionJobStatus.Running)
            {
                return;
            }

            _status = CollectionJobStatus.Canceled;
            _completedAt = completedAt;
        }
    }

    internal void RequestCancel()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* job already finished */ }
    }

    /// <summary>Returns an immutable snapshot of the job's current state.</summary>
    public CollectionJobSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new CollectionJobSnapshot(
                Handle: _handleId ?? string.Empty,
                Kind: Kind,
                ProcessId: ProcessId,
                Status: _status,
                StartedAt: StartedAt,
                CompletedAt: _completedAt,
                Result: _result,
                Error: _error);
        }
    }

    public void Dispose()
    {
        try { _cts.Dispose(); } catch { /* best-effort */ }
    }
}

/// <summary>Immutable point-in-time view of a <see cref="CollectionJob"/>.</summary>
public sealed record CollectionJobSnapshot(
    string Handle,
    string Kind,
    int ProcessId,
    CollectionJobStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    object? Result,
    DiagnosticError? Error)
{
    public double ElapsedSeconds => ((CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt).TotalSeconds;
    public bool IsTerminal => Status != CollectionJobStatus.Running;
}
