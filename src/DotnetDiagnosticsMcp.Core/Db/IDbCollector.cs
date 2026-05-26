namespace DotnetDiagnosticsMcp.Core.Db;

/// <summary>
/// Collects curated database command and pool diagnostics from a target process over a fixed
/// EventPipe window.
/// </summary>
public interface IDbCollector
{
    Task<DbSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        int intervalSeconds = 1,
        CancellationToken cancellationToken = default);
}
