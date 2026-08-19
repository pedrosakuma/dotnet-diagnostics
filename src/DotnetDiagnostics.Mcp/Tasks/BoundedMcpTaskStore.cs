using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.Mcp.Tasks;

internal sealed class BoundedMcpTaskStore : IMcpTaskStore, IDisposable
{
    private readonly IMcpTaskStore _inner;
    private readonly int _maxTrackedTasks;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _trackedTasks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _createGate = new(1, 1);

    public BoundedMcpTaskStore(IMcpTaskStore inner, int maxTrackedTasks)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTrackedTasks);
        _inner = inner;
        _maxTrackedTasks = maxTrackedTasks;
    }

    public async Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken)
    {
        await _createGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CleanupExpiredTrackedTasks();
            if (_trackedTasks.Count >= _maxTrackedTasks)
            {
                throw new InvalidOperationException(
                    $"Too many MCP tasks are already retained on the server. Wait for an existing task to expire before starting another (limit: {_maxTrackedTasks}).");
            }

            var created = await _inner.CreateTaskAsync(cancellationToken).ConfigureAwait(false);
            _trackedTasks[created.TaskId] = ComputeExpiresAt(created);
            return created;
        }
        finally
        {
            _createGate.Release();
        }
    }

    public async Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        var task = await _inner.GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            _trackedTasks.TryRemove(taskId, out _);
            return null;
        }

        _trackedTasks[taskId] = ComputeExpiresAt(task);
        return task;
    }

    public async Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken)
        => await _inner.SetCompletedAsync(taskId, result, cancellationToken).ConfigureAwait(false);

    public async Task SetFailedAsync(string taskId, JsonElement error, CancellationToken cancellationToken)
        => await _inner.SetFailedAsync(taskId, error, cancellationToken).ConfigureAwait(false);

    public async Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken)
        => await _inner.SetCancelledAsync(taskId, cancellationToken).ConfigureAwait(false);

    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived
    {
        add => _inner.InputResponseReceived += value;
        remove => _inner.InputResponseReceived -= value;
    }

    public Task ResolveInputRequestsAsync(
        string taskId,
        IDictionary<string, InputResponse> responses,
        CancellationToken cancellationToken)
        => _inner.ResolveInputRequestsAsync(taskId, responses, cancellationToken);

    public Task SetInputRequestsAsync(
        string taskId,
        IDictionary<string, InputRequest> requests,
        CancellationToken cancellationToken)
        => _inner.SetInputRequestsAsync(taskId, requests, cancellationToken);

    public void Dispose()
        => _createGate.Dispose();

    private void CleanupExpiredTrackedTasks()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var task in _trackedTasks)
        {
            if (task.Value <= now)
            {
                _trackedTasks.TryRemove(task.Key, out _);
            }
        }
    }

    private static DateTimeOffset ComputeExpiresAt(McpTaskInfo task)
        => task.TimeToLive is { } ttl ? task.CreatedAt + ttl : DateTimeOffset.MaxValue;
}
