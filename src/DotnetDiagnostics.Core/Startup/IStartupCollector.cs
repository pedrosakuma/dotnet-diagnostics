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
    /// connected target and only then resumes it, so pre-attach events (static ctors, DI build,
    /// module-init exceptions, startup timings) are recorded. CLI-only — requires
    /// <see cref="SuspendedColdStartLauncher"/> to have launched the target.
    /// </summary>
    Task<StartupSnapshot> CollectColdStartAsync(
        SuspendedTarget target,
        TimeSpan duration,
        CancellationToken cancellationToken = default);
}
