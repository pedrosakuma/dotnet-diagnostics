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

/// <summary>Collects managed exceptions while publishing each observed event incrementally.</summary>
public interface IStreamingExceptionCollector
{
    Task<ExceptionSnapshot> CollectStreamingAsync(
        int processId,
        TimeSpan duration,
        Action<ManagedExceptionEvent> onObservation,
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

/// <summary>Guards a process while publishing exception observations incrementally.</summary>
public interface IStreamingCrashGuardCollector
{
    Task<CrashGuardSnapshot> CollectStreamingAsync(
        int processId,
        TimeSpan duration,
        Action<CrashGuardExceptionEvent> onObservation,
        int maxRecent = 100,
        CancellationToken cancellationToken = default);
}
