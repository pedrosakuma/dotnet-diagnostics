namespace DotnetDiagnostics.Core.Requests;

/// <summary>
/// A single ASP.NET Core request that started during the collection window but had not completed
/// when the window closed — i.e. it is still in-flight. <see cref="ElapsedMs"/> is measured from the
/// request's <see cref="StartedAt"/> to the moment the window closed, so the oldest request is the
/// one most likely to be stuck.
/// </summary>
public sealed record InFlightRequest(
    string TraceId,
    string? SpanId,
    string Method,
    string Path,
    DateTimeOffset StartedAt,
    double ElapsedMs,
    bool IsLongRunning);

/// <summary>
/// Aggregated view of which ASP.NET Core requests are in-flight (started but not stopped) over a
/// fixed EventPipe window, derived from the <c>Microsoft.AspNetCore.Hosting HttpRequestIn</c>
/// Activity start/stop pairs surfaced through the <c>Microsoft-Diagnostics-DiagnosticSource</c>
/// provider. Pure EventPipe — no <c>ptrace</c> required — making it safe to run against a hung
/// production process to answer "what is the app doing right now?".
/// </summary>
public sealed record InFlightRequestSnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long RequestsStarted,
    long RequestsCompleted,
    int InFlightCount,
    int LongRunningCount,
    double LongRunningThresholdMs,
    double OldestElapsedMs,
    IReadOnlyList<InFlightRequest> Requests,
    IReadOnlyList<string> Notes);
