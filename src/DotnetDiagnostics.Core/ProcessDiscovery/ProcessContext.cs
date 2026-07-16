namespace DotnetDiagnostics.Core.ProcessDiscovery;

using DotnetDiagnostics.Core.Capabilities;

/// <summary>
/// Compact per-process digest attached to every successful diagnostic response so the LLM
/// can chain follow-up calls without re-running <c>inspect_process(view="list")</c> or
/// <c>inspect_process(view="capabilities")</c>. Cached for a short TTL — see
/// <see cref="IProcessContextResolver"/>.
/// </summary>
/// <param name="ProcessId">Resolved process id (after auto-resolve when the caller omitted it).</param>
/// <param name="Runtime">Runtime flavour as detected by <see cref="ICapabilityDetector"/> (e.g. CoreClr, NativeAot, Unknown).</param>
/// <param name="RuntimeVersion">CLR product version string when available.</param>
/// <param name="CanSampleCpu">True when CPU sampling is reachable (CoreCLR SampleProfiler, or NativeAOT with perf/ETW).</param>
/// <param name="CanCollectGcDump">True when ETW/EventPipe gcdump can be requested (CoreCLR only; withheld on NativeAOT because requesting a gcdump crashes .NET 10 AOT targets).</param>
/// <param name="AutoResolved">True when the caller omitted <c>processId</c> and the server resolved it from a single-match list.</param>
/// <param name="BindingSource">
/// Origin label introduced in Phase 2 of the central-orchestrator design (issue #20). Set to
/// <c>null</c> when the legacy resolver path was used (no session id known), or to one of
/// <c>"explicit"</c> (caller passed pid), <c>"session-binding"</c> (e.g. orchestrator-installed),
/// or <c>"local-auto"</c> (single-match local discovery). Purely informational — every existing
/// LLM hint keeps using <see cref="AutoResolved"/>.
/// </param>
public sealed record ProcessContext(
    int ProcessId,
    RuntimeFlavor Runtime,
    bool CanSampleCpu,
    bool CanCollectGcDump,
    bool AutoResolved,
    string? RuntimeVersion = null,
    string? BindingSource = null);
