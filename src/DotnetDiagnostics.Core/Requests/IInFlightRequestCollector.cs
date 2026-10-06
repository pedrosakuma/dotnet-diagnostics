namespace DotnetDiagnostics.Core.Requests;

/// <summary>
/// Enumerates the ASP.NET Core requests that are in-flight (started but not stopped) in a target
/// process over a fixed EventPipe window. Pure EventPipe — does not attach via <c>ptrace</c> — so it
/// is safe to run against a hung production process.
/// </summary>
public interface IInFlightRequestCollector
{
    Task<InFlightRequestSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        double longRunningThresholdMs = 1000,
        int maxRequests = 100,
        CancellationToken cancellationToken = default);
}

/// <summary>Tracks requests while publishing correlated start/stop events incrementally.</summary>
public interface IStreamingInFlightRequestCollector
{
    Task<InFlightRequestSnapshot> CollectStreamingAsync(
        int processId,
        TimeSpan duration,
        Action<InFlightRequestObservation> onObservation,
        double longRunningThresholdMs = 1000,
        int maxRequests = 100,
        CancellationToken cancellationToken = default);
}

/// <summary>A path-only ASP.NET request lifecycle event observed through EventPipe.</summary>
public sealed record InFlightRequestObservation(
    DateTimeOffset Timestamp,
    bool IsStart,
    string TraceId,
    string? SpanId,
    string? Method,
    string? Path,
    DateTimeOffset? StartedAt);
