using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Core.Etw;

/// <summary>
/// Stops and disposes an ETW session that enabled kernel stack walking on a dedicated,
/// short-lived thread instead of the calling (usually thread-pool) thread.
/// </summary>
/// <remarks>
/// On Windows, the thread that stops a session with kernel stack walking enabled can be left
/// in a state where every later ETW sample of that thread carries no user-mode stack, in any
/// session, for the rest of the thread's lifetime (issue #1070 / PR #1080 investigation,
/// reproduced on Windows Server 2025 outside this codebase). A thread-pool thread would carry
/// that state into unrelated work, so a self-profiling capture — or any later capture of this
/// process — silently loses managed call stacks on it. Confining stop/dispose to a throwaway
/// thread means the affected thread exits with the capture.
/// </remarks>
internal static class EtwStackSessionStopper
{
    public static void StopAndDispose(TraceEventSession? session, ILogger logger, string description)
    {
        if (session is null)
        {
            return;
        }

        Exception? disposeFailure = null;
        var thread = new Thread(() =>
        {
            try { session.Stop(); }
            catch (Exception ex) { logger.LogDebug(ex, "{Session} stop failed (best effort).", description); }

            try { session.Dispose(); }
            catch (Exception ex) { disposeFailure = ex; }
        })
        {
            IsBackground = true,
            Name = "dotnet-diagnostics ETW session stop",
        };
        thread.Start();
        thread.Join();

        if (disposeFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposeFailure).Throw();
        }
    }
}
