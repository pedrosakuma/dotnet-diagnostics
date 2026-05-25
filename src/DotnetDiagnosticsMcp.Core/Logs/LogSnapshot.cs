namespace DotnetDiagnosticsMcp.Core.Logs;

public sealed record LogSnapshot(
    int ProcessId,
    IReadOnlyList<string>? CategoryFilters,
    string MinimumLevel,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long TotalEvents,
    long EventsByLevelTrace,
    long EventsByLevelDebug,
    long EventsByLevelInformation,
    long EventsByLevelWarning,
    long EventsByLevelError,
    long EventsByLevelCritical,
    IReadOnlyList<LogCategoryGroup> ByCategory,
    IReadOnlyList<LogEntry> Recent,
    bool Truncated,
    IReadOnlyList<string> Notes);

public sealed record LogCategoryGroup(string Category, long Count, long ErrorCount, long WarningCount);

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    int EventId,
    string? EventName,
    string Message,
    string? ExceptionType,
    string? ExceptionMessage,
    IReadOnlyDictionary<string, string>? Scopes);
