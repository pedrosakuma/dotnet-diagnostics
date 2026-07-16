using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Preflight;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

public sealed class PreflightChecksTests
{
    private static PtraceProbeResult AttachOk() =>
        new(CanAttach: true, Reason: "Linux: ptrace_scope=0; attach allowed.") { HasCapSysPtrace = false, PtraceScope = 0 };

    private static PtraceProbeResult AttachBlocked() =>
        new(CanAttach: false, Reason: "Linux: kernel.yama.ptrace_scope=1 and sidecar lacks CAP_SYS_PTRACE — same-UID peer attach is blocked. Grant the capability (container: --cap-add SYS_PTRACE) or relax the host (sudo sysctl -w kernel.yama.ptrace_scope=0).")
        { HasCapSysPtrace = false, PtraceScope = 1 };

    private static PerfHostProbeResult PerfFull() =>
        new(PerfInstalled: true, HasCapPerfmon: true, HasCapSysAdmin: true, PerfEventParanoid: -1, CanTraceSchedSwitch: true);

    private static PerfHostProbeResult PerfParanoid() =>
        new(PerfInstalled: true, HasCapPerfmon: false, HasCapSysAdmin: false, PerfEventParanoid: 2, CanTraceSchedSwitch: false);

    private static PreflightCheck Find(PreflightReport report, string id) =>
        Assert.Single(report.Checks, c => c.Id == id);

    [Fact]
    public void HostOnly_all_green_reports_ok_and_no_blocker()
    {
        var report = PreflightChecks.Build(
            processId: null,
            isLinux: true, isWindows: false, isMacOs: false,
            ptrace: AttachOk(), perf: PerfFull(),
            selfUid: 1000, targetUid: null);

        Assert.Equal(PreflightStatus.Ok, report.Overall);
        Assert.False(report.HasBlocker);
        Assert.Equal("linux", report.Os);
        // Socket-UID is N/A without a target.
        Assert.Equal(PreflightStatus.NotApplicable, Find(report, "socket-uid").Status);
        Assert.Equal(PreflightStatus.Ok, Find(report, "clrmd-attach").Status);
        Assert.Equal(PreflightStatus.Ok, Find(report, "offcpu-perf").Status);
    }

    [Fact]
    public void Ptrace_blocked_is_a_hard_blocker_with_remediation_and_affected_tools()
    {
        var report = PreflightChecks.Build(
            processId: null,
            isLinux: true, isWindows: false, isMacOs: false,
            ptrace: AttachBlocked(), perf: PerfFull(),
            selfUid: 1000, targetUid: null);

        Assert.Equal(PreflightStatus.Blocked, report.Overall);
        Assert.True(report.HasBlocker);

        var attach = Find(report, "clrmd-attach");
        Assert.Equal(PreflightStatus.Blocked, attach.Status);
        Assert.False(string.IsNullOrWhiteSpace(attach.Remediation));
        Assert.Contains("capture_method_bytes", attach.AffectedTools!);
        Assert.Contains("get_bytes(kind=\"module\")", attach.AffectedTools!);
        Assert.Contains("collect_sample(kind=\"cpu\", resolveMethodInstantiations=true)", attach.AffectedTools!);
        Assert.DoesNotContain("collect_process_dump", attach.AffectedTools!);
        Assert.DoesNotContain("inspect_heap(source=\"dump\")", attach.AffectedTools!);
        // Most-severe check is surfaced first.
        Assert.Equal("clrmd-attach", report.Checks[0].Id);
    }

    [Fact]
    public void Uid_mismatch_against_target_blocks_everything()
    {
        var report = PreflightChecks.Build(
            processId: 4242,
            isLinux: true, isWindows: false, isMacOs: false,
            ptrace: AttachOk(), perf: PerfFull(),
            selfUid: 1000, targetUid: 0);

        Assert.Equal(PreflightStatus.Blocked, report.Overall);
        var socket = Find(report, "socket-uid");
        Assert.Equal(PreflightStatus.Blocked, socket.Status);
        Assert.Contains("0", socket.Remediation!); // suggests --user <targetUid>
        Assert.Equal(new[] { "all tools" }, socket.AffectedTools);
    }

    [Fact]
    public void Uid_match_against_target_is_ok()
    {
        var report = PreflightChecks.Build(
            processId: 4242,
            isLinux: true, isWindows: false, isMacOs: false,
            ptrace: AttachOk(), perf: PerfFull(),
            selfUid: 1000, targetUid: 1000);

        Assert.Equal(PreflightStatus.Ok, Find(report, "socket-uid").Status);
        Assert.False(report.HasBlocker);
    }

    [Fact]
    public void Unreadable_uids_with_target_degrade_socket_check()
    {
        var report = PreflightChecks.Build(
            processId: 4242,
            isLinux: true, isWindows: false, isMacOs: false,
            ptrace: AttachOk(), perf: PerfFull(),
            selfUid: 1000, targetUid: null);

        Assert.Equal(PreflightStatus.Degraded, Find(report, "socket-uid").Status);
    }

    [Fact]
    public void Perf_paranoid_degrades_offcpu_and_native_alloc_but_not_blocked()
    {
        var report = PreflightChecks.Build(
            processId: null,
            isLinux: true, isWindows: false, isMacOs: false,
            ptrace: AttachOk(), perf: PerfParanoid(),
            selfUid: 1000, targetUid: null);

        Assert.Equal(PreflightStatus.Degraded, report.Overall);
        Assert.False(report.HasBlocker);
        Assert.Equal(PreflightStatus.Degraded, Find(report, "offcpu-perf").Status);
        Assert.Equal(PreflightStatus.Degraded, Find(report, "native-alloc").Status);
    }

    [Fact]
    public void NonLinux_marks_linux_only_checks_not_applicable()
    {
        var report = PreflightChecks.Build(
            processId: 4242,
            isLinux: false, isWindows: true, isMacOs: false,
            ptrace: new PtraceProbeResult(CanAttach: true, Reason: "Windows: attach allowed."),
            perf: new PerfHostProbeResult(false, false, false, null, false),
            selfUid: null, targetUid: null);

        Assert.Equal("windows", report.Os);
        Assert.Equal(PreflightStatus.NotApplicable, Find(report, "socket-uid").Status);
        Assert.Equal(PreflightStatus.NotApplicable, Find(report, "offcpu-perf").Status);
        Assert.Equal(PreflightStatus.NotApplicable, Find(report, "native-alloc").Status);
        Assert.Equal(PreflightStatus.Ok, Find(report, "clrmd-attach").Status);
        Assert.Equal(PreflightStatus.Ok, report.Overall);
    }
}
