namespace DotnetDiagnostics.Core.ProcessDiscovery;

/// <summary>
/// Metadata about a discovered .NET process exposing a Diagnostic IPC endpoint.
/// </summary>
public sealed record DotnetProcess(
    int ProcessId,
    string CommandLine,
    string OperatingSystem,
    string ProcessArchitecture,
    string RuntimeVersion,
    string? ManagedEntrypointAssemblyName);
