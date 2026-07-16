namespace DotnetDiagnostics.Core.Internal;

internal static class EventPipeSessionControl
{
    // Diagnostics IPC session-control commands can overlap at the call site, but some Windows
    // runtimes strand one request when starts/stops arrive concurrently. Serialize only the
    // control exchange; active EventPipe sessions continue collecting in parallel.
    internal static SemaphoreSlim Gate { get; } = new(1, 1);
}
