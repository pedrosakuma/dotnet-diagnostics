namespace DotnetDiagnosticsMcp.Core.Db;

public sealed record DbSnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long TotalCommands,
    IReadOnlyList<DbCommandAggregate> ByCommand,
    IReadOnlyList<DbNPlusOneIncident> NPlusOne,
    IReadOnlyList<DbConnectionPoolStats> ConnectionPool,
    IReadOnlyList<string> Notes);

public sealed record DbCommandAggregate(
    string CommandTextHash,
    string CommandTextSanitized,
    string ConnectionStringSanitized,
    IReadOnlyList<string> Providers,
    long Count,
    double TotalMs,
    double MaxMs,
    double P95Ms,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

public sealed record DbNPlusOneIncident(
    string ScopeId,
    string CommandTextHash,
    string CommandTextSanitized,
    string ConnectionStringSanitized,
    IReadOnlyList<string> Providers,
    int Count,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

public sealed record DbConnectionPoolStats(
    string Provider,
    double? LatestOpenConnections,
    double? MaxOpenConnections,
    double? LatestPooledConnections,
    double? MaxPooledConnections,
    int PoolExhaustedCount,
    IReadOnlyList<string> Notes);
