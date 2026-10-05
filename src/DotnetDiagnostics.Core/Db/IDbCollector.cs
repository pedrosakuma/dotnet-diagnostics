namespace DotnetDiagnostics.Core.Db;

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

/// <summary>Collects database events while publishing completed operations incrementally.</summary>
public interface IStreamingDbCollector
{
    Task<DbSnapshot> CollectStreamingAsync(
        int processId,
        TimeSpan duration,
        Action<DbObservation> onObservation,
        int intervalSeconds = 1,
        CancellationToken cancellationToken = default);
}

/// <summary>Base type for incremental database observations.</summary>
public abstract record DbObservation(DateTimeOffset Timestamp);

public sealed record DbCommandObservation(
    DateTimeOffset Timestamp,
    string Provider,
    string CommandTextHash,
    string CommandTextSanitized,
    string ConnectionStringSanitized,
    string ScopeId,
    DateTimeOffset? StartedAt,
    double? DurationMs,
    bool PairingCertain) : DbObservation(Timestamp);

public sealed record DbConnectionPoolObservation(
    DateTimeOffset Timestamp,
    string Provider,
    string CounterName,
    double Value) : DbObservation(Timestamp);
