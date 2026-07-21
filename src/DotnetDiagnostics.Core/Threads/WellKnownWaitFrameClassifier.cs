namespace DotnetDiagnostics.Core.Threads;

/// <summary>
/// Best-effort recognizer for managed frames whose self-time represents a thread parked in a
/// known wait/blocking primitive rather than actively burning CPU. Shared between
/// <c>collect_thread_snapshot</c> blocked-thread heuristics and CoreCLR EventPipe CPU-sample
/// leaf classification so both surfaces describe waits consistently.
/// </summary>
internal static class WellKnownWaitFrameClassifier
{
    private static readonly string[] MonitorEnterFrames =
    [
        "System.Threading.Monitor.ReliableEnter",
        "System.Threading.Monitor.Enter",
        "System.Threading.Monitor.TryEnter",
    ];

    private static readonly string[] WaitHandleFrames =
    [
        "System.Threading.WaitHandle.Wait",
        "System.Threading.WaitHandle.WaitOne",
        "System.Threading.WaitHandle.WaitAny",
        "System.Threading.WaitHandle.WaitAll",
    ];

    private static readonly string[] ResetEventFrames =
    [
        "System.Threading.ManualResetEvent.Wait",
        "System.Threading.ManualResetEventSlim.Wait",
        "System.Threading.AutoResetEvent.Wait",
        "System.Threading.EventWaitHandle.Wait",
    ];

    private static readonly string[] SemaphoreFrames =
    [
        "System.Threading.SemaphoreSlim.Wait(",
        "System.Threading.SemaphoreSlim.Wait()",
        "System.Threading.Semaphore.Wait(",
    ];

    private static readonly string[] TaskWaitFrames =
    [
        "System.Threading.Tasks.Task.Wait(",
        "System.Threading.Tasks.Task.Wait()",
        "System.Threading.Tasks.Task.WaitAll",
        "System.Threading.Tasks.Task.WaitAny",
        "System.Runtime.CompilerServices.TaskAwaiter.GetResult",
        "System.Runtime.CompilerServices.ValueTaskAwaiter.GetResult",
    ];

    private static readonly string[] ThreadSleepFrames = ["System.Threading.Thread.Sleep"];
    private static readonly string[] ThreadJoinFrames = ["System.Threading.Thread.Join"];
    private static readonly string[] LowLevelLifoSemaphoreFrames =
    [
        "System.Threading.LowLevelLifoSemaphore.WaitForSignal",
        "System.Threading.LowLevelLifoSemaphore.WaitNative",
        "System.Threading.LowLevelLifoSemaphore.Wait(",
        "System.Threading.LowLevelLifoSemaphore.Wait()",
    ];

    internal static WaitFrameMatch? Classify(string? frameDisplayName)
    {
        if (string.IsNullOrWhiteSpace(frameDisplayName))
        {
            return null;
        }

        var name = frameDisplayName!;

        return MatchAny(name, "ThreadPool worker idle wait", LowLevelLifoSemaphoreFrames)
            ?? Match(name, "System.Threading.PortableThreadPool+WorkerThread.WaitForWork", "ThreadPool worker idle wait")
            ?? Match(name, "System.Threading.LowLevelMonitor.Wait", "LowLevelMonitor.Wait")
            ?? Match(name, "System.Threading.Monitor.Wait", "Monitor.Wait")
            ?? MatchAny(name, "Monitor.Enter (contended)", MonitorEnterFrames)
            ?? MatchAny(name, "WaitHandle.Wait", WaitHandleFrames)
            ?? MatchAny(name, "ResetEvent.Wait", ResetEventFrames)
            ?? MatchAny(name, "Semaphore.Wait", SemaphoreFrames)
            ?? MatchAny(name, "Task.Wait (blocking)", TaskWaitFrames)
            ?? MatchTaskResult(name)
            ?? MatchAny(name, "Thread.Sleep", ThreadSleepFrames)
            ?? MatchAny(name, "Thread.Join", ThreadJoinFrames)
            ?? MatchSocketWait(name);
    }

    private static WaitFrameMatch? Match(string frameDisplayName, string needle, string reason)
        => Contains(frameDisplayName, needle) ? new WaitFrameMatch(reason) : null;

    private static WaitFrameMatch? MatchAny(string frameDisplayName, string reason, IReadOnlyList<string> needles)
        => needles.Any(needle => Contains(frameDisplayName, needle)) ? new WaitFrameMatch(reason) : null;

    private static WaitFrameMatch? MatchSocketWait(string frameDisplayName)
        => Contains(frameDisplayName, "System.Net.Sockets.Socket")
           && (Contains(frameDisplayName, "Receive")
               || Contains(frameDisplayName, "Accept")
               || Contains(frameDisplayName, "Poll"))
            ? new WaitFrameMatch("Socket I/O")
            : null;

    private static WaitFrameMatch? MatchTaskResult(string frameDisplayName)
        => (Contains(frameDisplayName, "System.Threading.Tasks.Task") || Contains(frameDisplayName, "System.Threading.Tasks.ValueTask"))
           && Contains(frameDisplayName, ".get_Result")
            ? new WaitFrameMatch("Task.Wait (blocking)")
            : null;
    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

internal sealed record WaitFrameMatch(string Reason);
