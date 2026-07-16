using DotnetDiagnostics.Core.Capabilities;

namespace DotnetDiagnostics.Core.Preflight;

/// <summary>
/// Pure preflight-report assembler. Kept separate from <see cref="PreflightInspector"/> (which does
/// the real OS I/O) so unit tests can drive every branch with synthetic probe results — the same
/// internal-for-tests split <see cref="PtraceProbe.DetectLinux"/> uses.
/// </summary>
public static class PreflightChecks
{
    /// <summary>Tools that depend on a working ClrMD live attach (ptrace on Linux).</summary>
    internal static readonly string[] ClrMdAttachTools =
    {
        "collect_thread_snapshot",
        "inspect_heap(source=\"live\")",
        "capture_method_bytes",
        "get_bytes(kind=\"module\")",
    };

    private static readonly string[] AllTools = { "all tools" };
    private static readonly string[] OffCpuTools = { "collect_sample(kind=\"off_cpu\")" };
    private static readonly string[] NativeAllocTools = { "collect_sample(kind=\"native-alloc\")" };

    /// <summary>
    /// Builds a <see cref="PreflightReport"/> from already-gathered probe results. <paramref name="targetUid"/>
    /// is only consulted when <paramref name="processId"/> is non-null and <paramref name="selfUid"/> is known.
    /// </summary>
    public static PreflightReport Build(
        int? processId,
        bool isLinux,
        bool isWindows,
        bool isMacOs,
        PtraceProbeResult ptrace,
        PerfHostProbeResult perf,
        int? selfUid,
        int? targetUid)
    {
        var os = isLinux ? "linux" : isWindows ? "windows" : isMacOs ? "macos" : "other";
        var checks = new List<PreflightCheck>
        {
            BuildSocketUidCheck(processId, isLinux, selfUid, targetUid),
            BuildClrMdAttachCheck(ptrace),
            BuildOffCpuCheck(isLinux, perf),
            BuildNativeAllocCheck(isLinux, perf),
        };

        // Surface the most actionable findings first: Blocked, then Degraded, then Ok, then N/A.
        var ordered = checks
            .OrderBy(c => SeverityRank(c.Status))
            .ToList();

        // Worst applicable status wins. SeverityRank ranks Blocked lowest, so the smallest rank
        // among applicable checks is the worst; default to Ok when everything is N/A.
        var overall = ordered
            .Where(c => c.Status != PreflightStatus.NotApplicable)
            .OrderBy(c => SeverityRank(c.Status))
            .Select(c => c.Status)
            .DefaultIfEmpty(PreflightStatus.Ok)
            .First();

        return new PreflightReport(processId, os, overall, ordered);
    }

    private static int SeverityRank(PreflightStatus status) => status switch
    {
        PreflightStatus.Blocked => 0,
        PreflightStatus.Degraded => 1,
        PreflightStatus.Ok => 2,
        _ => 3,
    };

    private static PreflightCheck BuildSocketUidCheck(int? processId, bool isLinux, int? selfUid, int? targetUid)
    {
        const string id = "socket-uid";
        const string title = "Diagnostic IPC socket UID match";

        if (processId is null)
        {
            return new PreflightCheck(id, title, PreflightStatus.NotApplicable,
                "No target processId supplied — host-only diagnosis. Re-run with --pid/processId to verify the socket UID against a specific target.");
        }

        if (!isLinux)
        {
            return new PreflightCheck(id, title, PreflightStatus.NotApplicable,
                "UID matching is a Linux concept. On Windows/macOS the diagnostic transport does not gate on numeric UID.");
        }

        if (selfUid is null || targetUid is null)
        {
            return new PreflightCheck(id, title, PreflightStatus.Degraded,
                $"Could not read both UIDs (self={Format(selfUid)}, target={Format(targetUid)}) from /proc/*/status — the target pid may have exited or procfs is restricted for this UID. Cannot confirm the socket is reachable.",
                Remediation: "Confirm the target pid is alive and that the sidecar can read /proc/<pid>/status (same UID or CAP_SYS_PTRACE).");
        }

        if (selfUid.Value != targetUid.Value)
        {
            return new PreflightCheck(id, title, PreflightStatus.Blocked,
                $"Sidecar UID ({selfUid.Value}) differs from target UID ({targetUid.Value}). The diagnostic IPC socket /tmp/dotnet-diagnostic-{processId}-* is owned by the target UID, so EVERY tool (even EventPipe counters) will fail with ServerNotAvailableException: Permission denied.",
                Remediation: $"docker: run the sidecar with --user {targetUid.Value}. k8s: set the pod securityContext runAsUser={targetUid.Value} (plus matching runAsGroup + fsGroup). Local dev against a root target: --user 0.",
                AffectedTools: AllTools);
        }

        return new PreflightCheck(id, title, PreflightStatus.Ok,
            $"Sidecar and target share UID {selfUid.Value}; the diagnostic IPC socket is reachable.");
    }

    private static PreflightCheck BuildClrMdAttachCheck(PtraceProbeResult ptrace)
    {
        const string id = "clrmd-attach";
        const string title = "ClrMD live attach (ptrace)";

        if (ptrace.CanAttach)
        {
            return new PreflightCheck(id, title, PreflightStatus.Ok, ptrace.Reason);
        }

        // PtraceProbe.Reason already embeds the concrete remediation (cap-add / sysctl / dump
        // fallback). Surface it as the remediation too so the field is never empty for a blocker.
        return new PreflightCheck(id, title, PreflightStatus.Blocked,
            ptrace.Reason,
            Remediation: ExtractRemediation(ptrace.Reason),
            AffectedTools: ClrMdAttachTools);
    }

    private static PreflightCheck BuildOffCpuCheck(bool isLinux, PerfHostProbeResult perf)
    {
        const string id = "offcpu-perf";
        const string title = "Off-CPU sampling (perf sched_switch)";

        if (!isLinux)
        {
            return new PreflightCheck(id, title, PreflightStatus.NotApplicable,
                "Linux-only check. On Windows off-CPU sampling uses the NT Kernel Logger and requires an elevated diagnostics host (Administrators / SeSystemProfilePrivilege).");
        }

        if (!perf.PerfInstalled)
        {
            return new PreflightCheck(id, title, PreflightStatus.Degraded,
                "perf is not resolvable on PATH / linux-tools. Off-CPU sampling is unavailable; EventPipe-based tools (counters, cpu, gc, exceptions) are unaffected.",
                Remediation: "Install linux-tools (the default sidecar image already ships perf — avoid the -lean tag), or set PERF=/path/to/perf.",
                AffectedTools: OffCpuTools);
        }

        if (!perf.CanTraceSchedSwitch)
        {
            return new PreflightCheck(id, title, PreflightStatus.Degraded,
                $"perf is installed but sched_switch tracing is blocked (perf_event_paranoid={Format(perf.PerfEventParanoid)}, CAP_PERFMON={(perf.HasCapPerfmon ? "held" : "absent")}).",
                Remediation: "Grant CAP_PERFMON to the sidecar (docker: --cap-add PERFMON; k8s: capabilities.add: [\"PERFMON\"]) or relax the host (sudo sysctl -w kernel.perf_event_paranoid=-1).",
                AffectedTools: OffCpuTools);
        }

        return new PreflightCheck(id, title, PreflightStatus.Ok,
            "perf is installed and sched_switch tracing is permitted; off-CPU sampling is available.");
    }

    private static PreflightCheck BuildNativeAllocCheck(bool isLinux, PerfHostProbeResult perf)
    {
        const string id = "native-alloc";
        const string title = "Native allocation sampling (uprobe)";

        if (!isLinux)
        {
            return new PreflightCheck(id, title, PreflightStatus.NotApplicable,
                "Linux-only check.");
        }

        if (!perf.PerfInstalled)
        {
            return new PreflightCheck(id, title, PreflightStatus.Degraded,
                "perf is not resolvable on PATH / linux-tools, so the libc-allocator uprobe cannot be created.",
                Remediation: "Install linux-tools (or use the non -lean sidecar image).",
                AffectedTools: NativeAllocTools);
        }

        if (!perf.HasCapSysAdmin)
        {
            return new PreflightCheck(id, title, PreflightStatus.Degraded,
                "Creating a dynamic uprobe on the libc allocator writes to kernel tracefs, which requires CAP_SYS_ADMIN (strictly stronger than the CAP_PERFMON off-CPU needs).",
                Remediation: "Grant CAP_SYS_ADMIN to the sidecar (docker: --cap-add SYS_ADMIN; k8s: capabilities.add: [\"SYS_ADMIN\"]).",
                AffectedTools: NativeAllocTools);
        }

        return new PreflightCheck(id, title, PreflightStatus.Ok,
            "perf is installed and CAP_SYS_ADMIN is held; native allocation sampling is available.");
    }

    private static string Format(int? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<unknown>";

    /// <summary>
    /// PtraceProbe reason strings carry the remediation inline in parentheses
    /// (for example "… — same-UID peer attach is blocked. Grant the capability (…)."). Pull the
    /// trailing actionable sentence out so the <see cref="PreflightCheck.Remediation"/> field is
    /// focused; fall back to the whole reason when no separator is present.
    /// </summary>
    private static string ExtractRemediation(string reason)
    {
        var marker = reason.IndexOf(". ", StringComparison.Ordinal);
        if (marker >= 0 && marker + 2 < reason.Length)
        {
            return reason[(marker + 2)..];
        }

        return reason;
    }
}
