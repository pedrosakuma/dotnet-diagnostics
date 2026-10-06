using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Core.Logs;

public interface ILogCollector
{
    Task<LogSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        IReadOnlyList<string>? categories = null,
        LogLevel minLevel = LogLevel.Information,
        int maxEvents = 500,
        int maxMessageBytes = 4096,
        bool includeJsonPayload = false,
        CancellationToken cancellationToken = default);
}

/// <summary>Collects logs while publishing finalized entries incrementally.</summary>
public interface IStreamingLogCollector
{
    Task<LogSnapshot> CollectStreamingAsync(
        int processId,
        TimeSpan duration,
        Action<LogEntry> onObservation,
        IReadOnlyList<string>? categories = null,
        LogLevel minLevel = LogLevel.Information,
        int maxEvents = 500,
        int maxMessageBytes = 4096,
        bool includeJsonPayload = false,
        CancellationToken cancellationToken = default);
}
