using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using Microsoft.Diagnostics.NETCore.Client;
using static DotnetDiagnosticsMcp.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnosticsMcp.Core.UseCases;

/// <summary>
/// Host-neutral process-inspection use cases (issue #285). These return the full
/// <see cref="DiagnosticResult{T}"/> envelope — summary, next-action hints, capability digest —
/// so both the MCP tool layer (<c>inspect_process</c>) and the future <c>diag</c> CLI (#283) can
/// share one behavior. They depend on Core abstractions only and own no transport assumptions.
/// </summary>
public static class ProcessInspectionUseCases
{
    /// <summary>
    /// Lists every .NET process on the local machine that exposes a Diagnostic IPC endpoint.
    /// Never touches the resolver and never sets <see cref="DiagnosticResult{T}.ResolvedProcess"/>.
    /// </summary>
    public static DiagnosticResult<IReadOnlyList<DotnetProcess>> ListProcesses(IProcessDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);

        var processes = discovery.ListProcesses();
        if (processes.Count == 0)
        {
            return DiagnosticResult.Ok(
                processes,
                "No attachable .NET processes found. If the target runs in a container, make sure the sidecar shares its PID namespace and runs as the same UID.",
                new NextActionHint("inspect_process", "Re-run once the target is up to confirm the runtime exposes a diagnostic endpoint."));
        }

        var preview = string.Join(", ", processes.Take(3).Select(p => $"{p.ProcessId}={p.ManagedEntrypointAssemblyName ?? "?"}"));
        return DiagnosticResult.Ok(
            processes,
            $"Found {processes.Count} .NET process(es): {preview}{(processes.Count > 3 ? ", …" : "")}.",
            new NextActionHint(
                "inspect_process",
                "Probe the target process to confirm which collectors are supported (CoreCLR vs NativeAOT).",
                new Dictionary<string, object?> { ["view"] = "capabilities", ["processId"] = processes[0].ProcessId }));
    }

    /// <summary>
    /// Returns metadata for a single .NET process, auto-resolving the lone visible candidate when
    /// <paramref name="processId"/> is omitted.
    /// </summary>
    public static async Task<DiagnosticResult<DotnetProcess>> GetProcessInfoAsync(
        IProcessDiscovery discovery,
        IProcessContextResolver resolver,
        int? processId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discovery);

        var resolved = await ResolveContextAsync<DotnetProcess>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;

        var process = discovery.TryGetProcess(resolved.ProcessId);
        if (process is null)
        {
            return DiagnosticResult.Fail<DotnetProcess>(
                $"No .NET process with id {resolved.ProcessId} exposes a diagnostic endpoint.",
                new DiagnosticError("ProcessNotFound", $"Process id {resolved.ProcessId} is not visible to the diagnostic IPC."),
                new NextActionHint("inspect_process", "List attachable .NET processes and pick a valid pid."));
        }

        var result = DiagnosticResult.Ok(
            process,
            $"Process {process.ProcessId} — {process.ManagedEntrypointAssemblyName ?? "<unknown>"} on .NET {process.RuntimeVersion} ({process.OperatingSystem}/{process.ProcessArchitecture}).",
            new NextActionHint("collect_events", "Cheap first signal: CPU/memory/GC/thread-pool sweep before any sampling.",
                new Dictionary<string, object?> { ["processId"] = process.ProcessId, ["durationSeconds"] = 5 }));
        return WithContext(result, resolved.Context);
    }

    /// <summary>
    /// Probes the target to determine which diagnostic collectors are supported (CoreCLR vs
    /// NativeAOT), auto-resolving the lone visible candidate when <paramref name="processId"/> is
    /// omitted.
    /// </summary>
    public static async Task<DiagnosticResult<DiagnosticCapabilities>> GetCapabilitiesAsync(
        ICapabilityDetector detector,
        IProcessContextResolver resolver,
        int? processId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detector);

        var resolved = await ResolveContextAsync<DiagnosticCapabilities>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;

        try
        {
            var caps = await detector.DetectAsync(resolved.ProcessId, cancellationToken).ConfigureAwait(false);
            var hint = caps.CanSampleCpu
                ? new NextActionHint("collect_events", "Cheap first signal: CPU/memory/GC/thread-pool. Run before reaching for sampling.",
                    new Dictionary<string, object?> { ["processId"] = resolved.ProcessId, ["durationSeconds"] = 5 })
                : new NextActionHint("collect_events", "NativeAOT: CPU sampling unavailable. Counters + EventSource + dumps still work.",
                    new Dictionary<string, object?> { ["processId"] = resolved.ProcessId, ["durationSeconds"] = 5 });

            var ok = DiagnosticResult.Ok(
                caps,
                $"Runtime: {caps.Runtime} {caps.RuntimeVersion}. CPU sampling: {caps.CanSampleCpu}, gcdump: {caps.CanCollectGcDump}. {caps.Notes}".TrimEnd(),
                hint);
            return WithContext(ok, resolved.Context);
        }
        catch (ServerNotAvailableException ex)
        {
            return DiagnosticResult.Fail<DiagnosticCapabilities>(
                $"Diagnostic socket for process {resolved.ProcessId} is not reachable.",
                new DiagnosticError("EndpointUnavailable", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process", "Re-list processes. Common cause: sidecar UID mismatch with target."));
        }
    }
}
