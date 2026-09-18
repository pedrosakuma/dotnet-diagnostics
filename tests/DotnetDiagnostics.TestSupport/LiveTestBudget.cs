namespace DotnetDiagnostics.TestSupport;

/// <summary>A work deadline shared with startup, followed by a separate bounded owned-task cleanup.</summary>
public sealed class LiveTestBudget : IAsyncDisposable
{
    private readonly CancellationTokenSource _work;
    private readonly CancellationTokenSource _operation;
    private readonly TimeSpan _cleanupTimeout;
    private readonly LiveSampleEvidence _evidence;
    private readonly List<Task> _owned = new(2);
    private Task? _cleanup;

    public LiveTestBudget(TimeSpan workTimeout, TimeSpan cleanupTimeout, LiveSampleEvidence evidence)
    {
        _work = new(workTimeout);
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_work.Token);
        _cleanupTimeout = cleanupTimeout;
        _evidence = evidence;
        _evidence.Mark($"work-budget-start timeout={workTimeout} cleanupReserve={cleanupTimeout}");
    }

    public CancellationToken Token => _operation.Token;
    public CancellationToken WorkToken => _work.Token;
    public void Own(Task task)
    {
        if (_owned.Count == 2) throw new InvalidOperationException("At most two owned collection tasks may be retained.");
        _owned.Add(task);
    }
    public Task CancelAsync()
    {
        _evidence.Mark("cancellation-request");
        return _operation.CancelAsync().WaitAsync(_work.Token);
    }
    public ValueTask DisposeAsync() => new(_cleanup ??= CleanupAsync());

    private async Task CleanupAsync()
    {
        _evidence.Mark("collection-cleanup-enter");
        using var cleanup = new CancellationTokenSource(_cleanupTimeout);
        try
        {
            await _operation.CancelAsync().WaitAsync(cleanup.Token).ConfigureAwait(false);
            try { await Task.WhenAll(_owned).WaitAsync(cleanup.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cleanup.IsCancellationRequested &&
                _owned.All(task => task.IsCompletedSuccessfully || task.IsCanceled))
            {
                // Cancellation of our operation is expected; exhaustion of cleanup is not.
            }
            _evidence.Mark("collection-cleanup-exit");
        }
        catch (Exception error)
        {
            _evidence.Error("collection-cleanup", error);
            _evidence.Mark($"collection-cleanup-failed states={string.Join(",", _owned.Select(task => task.Status))}");
            throw new InvalidOperationException($"Owned collection cleanup failed.\n{_evidence.Describe()}", error);
        }
        finally
        {
            _operation.Dispose();
            _work.Dispose();
        }
    }
}
