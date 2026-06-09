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
