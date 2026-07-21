using System.Globalization;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Preflight;
using DotnetDiagnostics.Core.ProcessDiscovery;
using Microsoft.Diagnostics.NETCore.Client;
using static DotnetDiagnostics.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>
/// Host-neutral process-inspection use cases (issue #285). These return the full
/// <see cref="DiagnosticResult{T}"/> envelope — summary, next-action hints, capability digest —
/// so both the MCP tool layer (<c>inspect_process</c>) and the future <c>diag</c> CLI (#283) can
/// share one behavior. They depend on Core abstractions only and own no transport assumptions.
/// </summary>
public static class ProcessInspectionUseCases
{
    /// <summary>
    /// Lists every .NET process on the local machine that exposes a Diagnostic IPC endpoint,
    /// optionally narrowed by a case-insensitive substring filter against
    /// <see cref="DotnetProcess.CommandLine"/> (issue #665 part B — disambiguates among several
    /// candidates spawned by a wrapper the caller doesn't control, e.g. several
    /// <c>testhost.exe</c> under <c>dotnet test</c>). Never touches the resolver and never sets
    /// <see cref="DiagnosticResult{T}.ResolvedProcess"/>.
    /// </summary>
    public static DiagnosticResult<IReadOnlyList<DotnetProcess>> ListProcesses(
        IProcessDiscovery discovery,
        string? commandLineContains = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);

        var processes = discovery.ListProcesses();
        if (!string.IsNullOrEmpty(commandLineContains))
        {
            processes = processes
                .Where(p => p.CommandLine.Contains(commandLineContains, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (processes.Count == 0)
        {
            var summary = string.IsNullOrEmpty(commandLineContains)
                ? "No attachable .NET processes found. If the target runs in a container, make sure the sidecar shares its PID namespace and runs as the same UID."
                : $"No .NET process found matching commandLineContains='{commandLineContains}'. If the target runs in a container, make sure the sidecar shares its PID namespace and runs as the same UID.";
            return DiagnosticResult.Ok(
                processes,
                summary,
                new NextActionHint("inspect_process", "Re-run once the target is up to confirm the runtime exposes a diagnostic endpoint."));
        }

        var preview = string.Join(", ", processes.Take(3).Select(p => $"{p.ProcessId}={p.ManagedEntrypointAssemblyName ?? "?"}"));
        var filterNote = string.IsNullOrEmpty(commandLineContains) ? string.Empty : $" matching commandLineContains='{commandLineContains}'";
        return DiagnosticResult.Ok(
            processes,
            $"Found {processes.Count} .NET process(es){filterNote}: {preview}{(processes.Count > 3 ? ", …" : "")}.",
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
                new Dictionary<string, object?>
                {
                    ["kind"] = "counters",
                    ["processId"] = process.ProcessId,
                    ["durationSeconds"] = 5,
                }));
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
                    new Dictionary<string, object?>
                    {
                        ["kind"] = "counters",
                        ["processId"] = resolved.ProcessId,
                        ["durationSeconds"] = 5,
                    })
                : new NextActionHint("collect_events", "NativeAOT: CPU sampling unavailable. Counters + EventSource + dumps still work.",
                    new Dictionary<string, object?>
                    {
                        ["kind"] = "counters",
                        ["processId"] = resolved.ProcessId,
                        ["durationSeconds"] = 5,
                    });

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

    /// <summary>
    /// Target-optional environment self-diagnosis (Phase 13 / G1). Reuses the host probes via
    /// <see cref="IPreflightInspector"/> and projects the report into a
    /// <see cref="DiagnosticResult{T}"/> with a one-line verdict summary and remediation hints.
    /// Never resolves through the diagnostic IPC and never fails: every finding is a check, so it
    /// works even when no .NET process is reachable (host-only diagnosis).
    /// </summary>
    public static DiagnosticResult<PreflightReport> Preflight(
        IPreflightInspector inspector,
        int? processId = null)
    {
        ArgumentNullException.ThrowIfNull(inspector);

        var report = inspector.Inspect(processId);
        var summary = SummarisePreflight(report);
        var hints = BuildPreflightHints(report);
        return DiagnosticResult.Ok(report, summary, [.. hints]);
    }

    private static string SummarisePreflight(PreflightReport report)
    {
        var blocked = report.Checks.Count(c => c.Status == PreflightStatus.Blocked);
        var degraded = report.Checks.Count(c => c.Status == PreflightStatus.Degraded);
        var scope = report.ProcessId is int pid
            ? $"pid {pid.ToString(CultureInfo.InvariantCulture)}"
            : "host-only (no target)";

        return report.Overall switch
        {
            PreflightStatus.Blocked =>
                $"Preflight: BLOCKED ({scope}) — {blocked} hard blocker(s){(degraded > 0 ? $", {degraded} degraded" : string.Empty)}. See remediation on each blocked check.",
            PreflightStatus.Degraded =>
                $"Preflight: DEGRADED ({scope}) — core diagnostics OK, {degraded} optional capability(ies) unavailable.",
            _ => $"Preflight: OK ({scope}) — the environment is ready for diagnostics.",
        };
    }

    private static List<NextActionHint> BuildPreflightHints(PreflightReport report)
    {
        var hints = new List<NextActionHint>();
        foreach (var check in report.Checks.Where(c =>
                     c.Status is PreflightStatus.Blocked or PreflightStatus.Degraded &&
                     c.Remediation is not null))
        {
            hints.Add(new NextActionHint(
                "fix-environment",
                $"[{check.Id}] {check.Remediation}",
                new Dictionary<string, object?>
                {
                    ["check"] = check.Id,
                    ["status"] = check.Status.ToString(),
                }));
        }

        return hints;
    }
}
