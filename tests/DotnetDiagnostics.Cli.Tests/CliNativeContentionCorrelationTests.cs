using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.NativeLockContention;
using DotnetDiagnostics.Core.OffCpu;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

/// <summary>
/// Coverage for the session-scoped cross-collector native-contention correlation (issue #855):
/// reuses the host-neutral <see cref="NativeLockContentionUx.CorrelateBatchEvidence"/> the MCP
/// <c>collect_batch</c> tool calls (<c>CollectBatchSalientEvidence.ApplyNativeContentionEvidence</c>)
/// to print the same correlation once both a <c>native-lock-contention-sample</c> and an
/// <c>off-cpu-snapshot</c> handle exist for the session's target pid — without a new multi-kind
/// <c>collect</c> verb.
/// </summary>
public sealed class CliNativeContentionCorrelationTests
{
    private const int Pid = 85501;

    [Fact]
    public void TryBuild_BothHandlesPresent_MergesEvidenceWithoutElevatingLevel()
    {
        var store = new MemoryDiagnosticHandleStore();
        store.Register(Pid, CliNativeContentionCorrelation.NativeLockContentionKind, LockArtifact(sampledLockCalls: 128), TimeSpan.FromMinutes(10));
        store.Register(Pid, CliNativeContentionCorrelation.OffCpuKind, OffCpuArtifact(ConfirmedBlockingEvidence()), TimeSpan.FromMinutes(10));

        var evidence = CliNativeContentionCorrelation.TryBuild(store, Pid);

        evidence.Should().NotBeNull();
        evidence!.Level.Should().Be(NativeContentionEvidenceLevels.ConfirmedBlocking);
        evidence.SampledLockCallCount.Should().Be(128);
    }

    [Fact]
    public void TryBuild_UncontendedWorkload_StaysActivityOnly()
    {
        var store = new MemoryDiagnosticHandleStore();
        store.Register(Pid, CliNativeContentionCorrelation.NativeLockContentionKind, LockArtifact(sampledLockCalls: 900), TimeSpan.FromMinutes(10));
        store.Register(Pid, CliNativeContentionCorrelation.OffCpuKind, OffCpuArtifact(NoneEvidence()), TimeSpan.FromMinutes(10));

        var evidence = CliNativeContentionCorrelation.TryBuild(store, Pid);

        evidence.Should().NotBeNull();
        evidence!.Level.Should().Be(
            NativeContentionEvidenceLevels.None,
            "heavy sampled mutex-call activity alone must never be mislabeled as blocking");
    }

    [Fact]
    public void TryBuild_OnlyNativeLockHandlePresent_ReturnsActivityOnlyEvidence()
    {
        var store = new MemoryDiagnosticHandleStore();
        store.Register(Pid, CliNativeContentionCorrelation.NativeLockContentionKind, LockArtifact(sampledLockCalls: 5), TimeSpan.FromMinutes(10));

        var evidence = CliNativeContentionCorrelation.TryBuild(store, Pid);

        evidence.Should().NotBeNull("a single successful collector must still surface its own evidence");
        evidence!.Level.Should().Be(NativeContentionEvidenceLevels.Activity);
    }

    [Fact]
    public void TryBuild_NeitherHandlePresent_ReturnsNull()
    {
        var store = new MemoryDiagnosticHandleStore();

        CliNativeContentionCorrelation.TryBuild(store, Pid).Should().BeNull();
    }

    [Fact]
    public void TryBuild_OffCpuHandleFromDifferentPid_ReturnsOnlyNativeLockPidEvidence()
    {
        var store = new MemoryDiagnosticHandleStore();
        store.Register(Pid, CliNativeContentionCorrelation.NativeLockContentionKind, LockArtifact(sampledLockCalls: 5), TimeSpan.FromMinutes(10));
        store.Register(Pid + 1, CliNativeContentionCorrelation.OffCpuKind, OffCpuArtifact(ConfirmedBlockingEvidence()), TimeSpan.FromMinutes(10));

        var evidence = CliNativeContentionCorrelation.TryBuild(store, Pid);

        evidence.Should().NotBeNull("the off-cpu handle belongs to a different pid and must not be cross-attributed");
        evidence!.Level.Should().Be(
            NativeContentionEvidenceLevels.Activity,
            "only this pid's own native-lock evidence should be used; the other pid's confirmed-blocking evidence must not leak in");
    }

    [Fact]
    public async Task SessionRepl_TryPrintNativeContentionCorrelationAsync_BothPresent_PrintsEvidence()
    {
        var store = new MemoryDiagnosticHandleStore();
        store.Register(Pid, CliNativeContentionCorrelation.NativeLockContentionKind, LockArtifact(sampledLockCalls: 42), TimeSpan.FromMinutes(10));
        var offCpuHandle = store.Register(Pid, CliNativeContentionCorrelation.OffCpuKind, OffCpuArtifact(ConfirmedBlockingEvidence()), TimeSpan.FromMinutes(10));
        var stdout = new StringWriter();

        await SessionRepl.TryPrintNativeContentionCorrelationAsync(store, offCpuHandle.Id, Pid, stdout);

        stdout.ToString().Should().Contain("native-contention evidence");
    }

    [Fact]
    public async Task SessionRepl_TryPrintNativeContentionCorrelationAsync_UnrelatedHandleKind_PrintsNothing()
    {
        var store = new MemoryDiagnosticHandleStore();
        store.Register(Pid, CliNativeContentionCorrelation.NativeLockContentionKind, LockArtifact(sampledLockCalls: 5), TimeSpan.FromMinutes(10));
        var unrelatedHandle = store.Register(Pid, "process-dump", new object(), TimeSpan.FromMinutes(10));
        var stdout = new StringWriter();

        await SessionRepl.TryPrintNativeContentionCorrelationAsync(store, unrelatedHandle.Id, Pid, stdout);

        stdout.ToString().Should().BeEmpty();
    }

    private static NativeContentionEvidence ConfirmedBlockingEvidence()
        => new(
            NativeContentionEvidenceLevels.ConfirmedBlocking,
            "closed futex span(s) confirm the thread blocked in the kernel.",
            NativeSyncSpanCount: 3,
            ClosedNativeSyncSpanCount: 3,
            NativeSyncOffCpuMicros: 45_000,
            ClosedNativeSyncOffCpuMicros: 45_000);

    private static NativeContentionEvidence NoneEvidence()
        => new(NativeContentionEvidenceLevels.None, "No native synchronization off-CPU evidence observed in this window.");

    private static NativeLockContentionArtifact LockArtifact(long sampledLockCalls)
    {
        var trace = new CpuSampleTraceArtifact(
            Pid,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(5),
            sampledLockCalls,
            new CallTreeNode(new SampledFrame("libc.so.6", "pthread_mutex_lock"), sampledLockCalls, sampledLockCalls, Array.Empty<CallTreeNode>()));
        var sample = new NativeLockContentionSample(
            Pid,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(5),
            sampledLockCalls,
            TopContendedCallSites: [],
            ProbedFunctions: ["pthread_mutex_lock", "pthread_mutex_unlock"],
            LibcPath: "/lib/x86_64-linux-gnu/libc.so.6",
            SamplePeriod: 5000,
            SymbolSource: "PdbResolved",
            ContentionEvidence: new NativeContentionEvidence(
                NativeContentionEvidenceLevels.Activity,
                "sampled pthread mutex entry points are lock activity only.",
                SampledLockCallCount: sampledLockCalls));
        return new NativeLockContentionArtifact(sample, trace);
    }

    private static OffCpuSnapshotArtifact OffCpuArtifact(NativeContentionEvidence evidence)
        => new(
            Pid,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(5),
            TotalOffCpuMicros: 45_000,
            SchedSwitches: 10,
            Stacks: [],
            Threads: [],
            SymbolSource: "PdbResolved",
            NativeContentionEvidence: evidence);
}
