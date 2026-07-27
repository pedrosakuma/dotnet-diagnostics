using DotnetDiagnostics.Core.Launch;

namespace DotnetDiagnostics.Core.Startup;

/// <summary>
/// Collects startup-related loader and DependencyInjection EventPipe activity from a target process.
/// </summary>
public interface IStartupCollector
{
    Task<StartupSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// True cold-start capture (issue #446): arms the EventPipe session on a <b>suspended</b> reverse-
    /// connected target and only then resumes it, so non-replayed DependencyInjection call-site
    /// activity emitted during startup is recorded (plus loader events when the runtime emits them).
    /// Requires
    /// <see cref="SuspendedColdStartLauncher"/> to have launched the target; callers include the CLI
    /// and the gated stdio MCP startup-launch path.
    /// </summary>
    Task<StartupSnapshot> CollectColdStartAsync(
        SuspendedTarget target,
        TimeSpan duration,
        CancellationToken cancellationToken = default);
}
