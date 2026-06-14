namespace DotnetDiagnostics.Core.Exceptions;

/// <summary>
/// Captures managed exceptions thrown by the target process over a fixed time window
/// via the <c>Microsoft-Windows-DotNETRuntime</c> EventPipe provider (Exception keyword).
/// </summary>
public interface IExceptionCollector
{
    Task<ExceptionSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        int maxRecent = 100,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Captures the runtime exception stream and crash-adjacent signals for a process that may terminate
/// during the collection window.
/// </summary>
public interface ICrashGuardCollector
{
    Task<CrashGuardSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        int maxRecent = 100,
        CancellationToken cancellationToken = default);
}
