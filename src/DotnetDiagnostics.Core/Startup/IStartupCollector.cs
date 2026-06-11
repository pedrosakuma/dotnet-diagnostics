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
}
