namespace DotnetDiagnostics.Core.ProcessDiscovery;

/// <summary>
/// Discovers .NET processes reachable via Diagnostic IPC (sockets on Unix, named pipes on Windows).
/// </summary>
public interface IProcessDiscovery
{
    /// <summary>Enumerates all .NET processes published on the local diagnostic transport.</summary>
    IReadOnlyList<DotnetProcess> ListProcesses();

    /// <summary>Returns metadata for a specific PID, or <c>null</c> if it does not expose a diagnostic endpoint.</summary>
    DotnetProcess? TryGetProcess(int processId);
}
