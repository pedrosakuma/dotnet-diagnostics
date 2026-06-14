using DotnetDiagnostics.Core.Drilldown;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Mcp.Hosting;

/// <summary>
/// Background loop that prunes drill-down handles whose target processes have exited. Keeps the
/// in-memory store from leaking artifacts when the LLM forgets to clean up, and avoids handing
/// the model a handle whose process is gone (it would otherwise time out only on TTL).
/// </summary>
/// <remarks>
/// The actual sweep is the host-neutral <see cref="DeadProcessHandleEvictor"/> in Core (shared with
/// the CLI <c>session</c> REPL, issue #300); this hosted service only supplies the hosting lifetime
/// and wires the evictor's progress/error callbacks to the server logger.
/// </remarks>
public sealed class HandleEvictionBackgroundService : BackgroundService
{
    private readonly DeadProcessHandleEvictor _evictor;
    private readonly ILogger<HandleEvictionBackgroundService> _logger;
    private readonly TimeSpan _interval;

    public HandleEvictionBackgroundService(
        IDiagnosticHandleStore store,
        ILogger<HandleEvictionBackgroundService>? logger = null,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _evictor = new DeadProcessHandleEvictor(store);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HandleEvictionBackgroundService>.Instance;
        _interval = interval ?? TimeSpan.FromSeconds(5);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => _evictor.RunAsync(
            _interval,
            onEvicted: (pid, count) =>
                _logger.LogInformation("Invalidated {Count} handle(s) for exited process {Pid}.", count, pid),
            onError: ex =>
                _logger.LogWarning(ex, "Handle eviction sweep failed; will retry on the next tick."),
            cancellationToken: stoppingToken);

    /// <summary>Runs a single eviction sweep synchronously. Retained for tests and ad-hoc triggers.</summary>
    public int EvictDeadProcesses()
        => _evictor.EvictDeadProcesses(
            onEvicted: (pid, count) =>
                _logger.LogInformation("Invalidated {Count} handle(s) for exited process {Pid}.", count, pid));
}
