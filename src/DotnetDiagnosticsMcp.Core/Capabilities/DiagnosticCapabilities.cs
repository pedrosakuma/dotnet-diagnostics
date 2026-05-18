namespace DotnetDiagnosticsMcp.Core.Capabilities;

/// <summary>
/// Runtime flavor detected for a target process.
/// </summary>
public enum RuntimeFlavor
{
    Unknown,
    CoreClr,
    NativeAot,
}

/// <summary>
/// Matrix describing what kinds of diagnostic data the MCP server can collect from a given process.
/// </summary>
public sealed record DiagnosticCapabilities(
    int ProcessId,
    RuntimeFlavor Runtime,
    string RuntimeVersion,
    bool CanReadEventCounters,
    bool CanSampleCpu,
    bool CanCollectGcDump,
    bool CanCollectExceptions,
    bool CanCollectHttpActivity,
    bool CanCollectCustomEventSource,
    bool CanCollectProcessDump,
    string Notes);
