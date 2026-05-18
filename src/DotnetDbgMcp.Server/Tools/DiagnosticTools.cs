using System.ComponentModel;
using DotnetDbgMcp.Core.Capabilities;
using DotnetDbgMcp.Core.Counters;
using DotnetDbgMcp.Core.CpuSampling;
using DotnetDbgMcp.Core.ProcessDiscovery;
using ModelContextProtocol.Server;

namespace DotnetDbgMcp.Server.Tools;

/// <summary>
/// MCP tools that expose the dotnet-dbg-mcp Core diagnostic primitives.
/// Each tool delegates to a Core service resolved from the request scope.
/// </summary>
[McpServerToolType]
public sealed class DiagnosticTools
{
    [McpServerTool(
        Name = "list_dotnet_processes",
        Title = "List local .NET processes",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true)]
    [Description(
        "Lists all .NET processes on the local machine that expose a Diagnostic IPC endpoint. " +
        "Returns process id, runtime version, OS, architecture and the managed entrypoint assembly.")]
    public static IReadOnlyList<DotnetProcess> ListDotnetProcesses(IProcessDiscovery discovery)
        => discovery.ListProcesses();

    [McpServerTool(
        Name = "get_process_info",
        Title = "Get .NET process info",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true)]
    [Description(
        "Returns metadata for a single .NET process identified by its OS process id, " +
        "or null if the process is not running or does not expose a diagnostic endpoint.")]
    public static DotnetProcess? GetProcessInfo(
        IProcessDiscovery discovery,
        [Description("Operating system process id of the target .NET process.")] int processId)
        => discovery.TryGetProcess(processId);

    [McpServerTool(
        Name = "get_diagnostic_capabilities",
        Title = "Detect diagnostic capabilities",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false)]
    [Description(
        "Probes the target process to determine which diagnostic tools the server can use against it. " +
        "Detects CoreCLR vs NativeAOT (NativeAOT lacks CPU sampling and gcdump) and returns a capability matrix. " +
        "Takes up to ~2 seconds while probing the SampleProfiler provider.")]
    public static Task<DiagnosticCapabilities> GetDiagnosticCapabilities(
        ICapabilityDetector detector,
        [Description("Operating system process id of the target .NET process.")] int processId,
        CancellationToken cancellationToken)
        => detector.DetectAsync(processId, cancellationToken);

    [McpServerTool(
        Name = "snapshot_counters",
        Title = "Snapshot EventCounters",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false)]
    [Description(
        "Collects EventCounters from the target process over a fixed time window and returns the " +
        "latest value seen per counter. Default providers cover the .NET runtime, ASP.NET Core hosting " +
        "and Kestrel; pass a custom list to observe other EventSources.")]
    public static Task<CounterSnapshot> SnapshotCounters(
        ICounterCollector collector,
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 5.")] int durationSeconds = 5,
        [Description("Optional list of EventCounter provider names to subscribe to. " +
                     "If null/empty, defaults to System.Runtime, Microsoft.AspNetCore.Hosting and Microsoft-AspNetCore-Server-Kestrel.")]
        string[]? providers = null,
        [Description("Refresh interval (in seconds) requested from each provider. Defaults to 1.")] int intervalSeconds = 1,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "durationSeconds must be >= 1.");
        }

        if (intervalSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds), "intervalSeconds must be >= 1.");
        }

        return collector.CollectAsync(
            processId,
            TimeSpan.FromSeconds(durationSeconds),
            providers is { Length: > 0 } ? providers : null,
            intervalSeconds,
            cancellationToken);
    }

    [McpServerTool(
        Name = "collect_cpu_sample",
        Title = "Collect CPU sample",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false)]
    [Description(
        "Captures a CPU sample from the target process and returns the top-N hotspots aggregated by method. " +
        "Requires CoreCLR — NativeAOT processes do not implement the SampleProfiler EventSource. " +
        "Each hotspot reports both inclusive and exclusive sample counts.")]
    public static Task<CpuSample> CollectCpuSample(
        ICpuSampler sampler,
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("Duration of the sampling window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of hotspots to return. Must be >= 1. Defaults to 25.")] int topN = 25,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "durationSeconds must be >= 1.");
        }

        if (topN < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(topN), "topN must be >= 1.");
        }

        return sampler.SampleAsync(processId, TimeSpan.FromSeconds(durationSeconds), topN, cancellationToken);
    }
}
