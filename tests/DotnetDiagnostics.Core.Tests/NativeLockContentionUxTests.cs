using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.NativeLockContention;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.UseCases;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class NativeLockContentionUxTests
{
    private const int Pid = 830201;

    [Fact]
    public void HasNativeSynchronizationEvidence_DetectsFutexBreakdown()
    {
        var snapshot = OffCpuSnapshotWith(
            new OffCpuStackHotspot(
                "pthread_mutex_lock",
                OffCpuMicros: 10_000,
                OccurrenceCount: 2,
                DominantState: "S",
                Stack: new[] { new OffCpuFrame("libc.so.6", "pthread_mutex_lock") },
                SyscallBreakdown: new[] { new OffCpuSyscallAttribution("futex", 2, 10_000) }));

        NativeLockContentionUx.HasNativeSynchronizationEvidence(snapshot).Should().BeTrue();
    }

    [Fact]
    public void HasNativeSynchronizationEvidence_ReturnsFalse_ForIoOnlyOffCpu()
    {
        var snapshot = OffCpuSnapshotWith(
            new OffCpuStackHotspot(
                "read",
                OffCpuMicros: 10_000,
                OccurrenceCount: 2,
                DominantState: "D",
                Stack: new[] { new OffCpuFrame("libc.so.6", "read") },
                SyscallBreakdown: new[] { new OffCpuSyscallAttribution("read", 2, 10_000) }));

        NativeLockContentionUx.HasNativeSynchronizationEvidence(snapshot).Should().BeFalse();
    }

    [Fact]
    public async Task CollectOffCpuSample_WithNativeSyncEvidenceAndCapability_SuggestsNativeLockContention()
    {
        var result = await SamplerUseCases.CollectOffCpuSample(
            new StubOffCpuSampler(OffCpuSnapshotWith(
                new OffCpuStackHotspot(
                    "pthread_mutex_lock",
                    OffCpuMicros: 10_000,
                    OccurrenceCount: 2,
                    DominantState: "S",
                    Stack: new[] { new OffCpuFrame("libc.so.6", "pthread_mutex_lock") },
                    SyscallBreakdown: new[] { new OffCpuSyscallAttribution("futex", 2, 10_000) }))),
            new MemoryDiagnosticHandleStore(),
            new FixedProcessContextResolver(canSampleNativeLockContention: true),
            new SymbolServerAllowlist(null),
            principalAllowsSymbolsRemote: false,
            processId: Pid,
            durationSeconds: 5);

        result.Error.Should().BeNull();
        var hint = result.Hints.Single(IsNativeLockContentionHint);
        hint.Priority.Should().Be(NextActionHintPriority.High);
        result.Hints[0].Should().BeSameAs(hint, "native synchronization evidence is the most specific follow-up");
    }

    [Fact]
    public async Task CollectOffCpuSample_WithNoNativeSyncEvidence_DoesNotSuggestNativeLockContention()
    {
        var result = await SamplerUseCases.CollectOffCpuSample(
            new StubOffCpuSampler(OffCpuSnapshotWith(
                new OffCpuStackHotspot(
                    "read",
                    OffCpuMicros: 10_000,
                    OccurrenceCount: 2,
                    DominantState: "D",
                    Stack: new[] { new OffCpuFrame("libc.so.6", "read") },
                    SyscallBreakdown: new[] { new OffCpuSyscallAttribution("read", 2, 10_000) }))),
            new MemoryDiagnosticHandleStore(),
            new FixedProcessContextResolver(canSampleNativeLockContention: true),
            new SymbolServerAllowlist(null),
            principalAllowsSymbolsRemote: false,
            processId: Pid,
            durationSeconds: 5);

        result.Error.Should().BeNull();
        result.Hints.Any(IsNativeLockContentionHint).Should().BeFalse();
    }

    [Fact]
    public async Task CollectOffCpuSample_WithNativeSyncEvidenceButNoCapability_DoesNotSuggestDeadEndNativeLockCommand()
    {
        var result = await SamplerUseCases.CollectOffCpuSample(
            new StubOffCpuSampler(OffCpuSnapshotWith(
                new OffCpuStackHotspot(
                    "pthread_mutex_lock",
                    OffCpuMicros: 10_000,
                    OccurrenceCount: 2,
                    DominantState: "S",
                    Stack: new[] { new OffCpuFrame("libc.so.6", "pthread_mutex_lock") },
                    SyscallBreakdown: new[] { new OffCpuSyscallAttribution("futex", 2, 10_000) }))),
            new MemoryDiagnosticHandleStore(),
            new FixedProcessContextResolver(canSampleNativeLockContention: false),
            new SymbolServerAllowlist(null),
            principalAllowsSymbolsRemote: false,
            processId: Pid,
            durationSeconds: 5);

        result.Error.Should().BeNull();
        result.Hints.Any(IsNativeLockContentionHint).Should().BeFalse();
        result.Hints.Should().Contain(h => h.Reason.Contains("native-lock-contention", StringComparison.Ordinal) &&
                                           (h.NextTool == "inspect_process" || h.NextTool == "query_snapshot"));
    }

    [Fact]
    public async Task CollectNativeLockContentionSample_SelectsFirstUsefulCallerOverUnknownAndMutexEntry()
    {
        var result = await SamplerUseCases.CollectNativeLockContentionSample(
            new StubNativeLockContentionSampler(
                new Hotspot(new SampledFrame("[unknown]", "[unknown]"), InclusiveSamples: 25, ExclusiveSamples: 1),
                new Hotspot(new SampledFrame("libc.so.6", "pthread_mutex_lock"), InclusiveSamples: 20, ExclusiveSamples: 2),
                new Hotspot(new SampledFrame("libnativecart.so", "cart_lock_bucket"), InclusiveSamples: 18, ExclusiveSamples: 4)),
            new MemoryDiagnosticHandleStore(),
            new FixedProcessContextResolver(canSampleNativeLockContention: true),
            processId: Pid,
            durationSeconds: 5);

        result.Error.Should().BeNull();
        result.Summary.Should().Contain("First useful caller: cart_lock_bucket");
        result.Summary.Should().Contain("Top sampled frame was [unknown]");
        result.Hints.Should().Contain(h => h.NextTool == "query_snapshot" &&
                                           h.Reason.Contains("unresolved or displaced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CollectNativeLockContentionSample_ClassifiesNativeLockSamples_AsActivityOnly()
    {
        var result = await SamplerUseCases.CollectNativeLockContentionSample(
            new StubNativeLockContentionSampler(
                new Hotspot(new SampledFrame("libnativecart.so", "cart_lock_bucket"), InclusiveSamples: 18, ExclusiveSamples: 4)),
            new MemoryDiagnosticHandleStore(),
            new FixedProcessContextResolver(canSampleNativeLockContention: true),
            processId: Pid,
            durationSeconds: 5);

        result.Error.Should().BeNull();
        result.Data!.ContentionEvidence.Should().NotBeNull();
        result.Data!.ContentionEvidence!.Level.Should().Be(NativeContentionEvidenceLevels.Activity);
        result.Summary.Should().Contain("Evidence level: activity");
        result.Summary.Should().NotContain("confirmed", because: "pthread mutex activity alone must never be reported as confirmed blocking");
    }

    [Fact]
    public async Task CollectNativeLockContentionSample_OmitsCallTreeHint_WhenInlineCallerIsUseful()
    {
        var result = await SamplerUseCases.CollectNativeLockContentionSample(
            new StubNativeLockContentionSampler(
                new Hotspot(new SampledFrame("libnativecart.so", "cart_lock_bucket"), InclusiveSamples: 18, ExclusiveSamples: 4),
                new Hotspot(new SampledFrame("libc.so.6", "pthread_mutex_lock"), InclusiveSamples: 10, ExclusiveSamples: 2)),
            new MemoryDiagnosticHandleStore(),
            new FixedProcessContextResolver(canSampleNativeLockContention: true),
            processId: Pid,
            durationSeconds: 5);

        result.Error.Should().BeNull();
        result.Summary.Should().Contain("First useful caller: cart_lock_bucket");
        result.Hints.Should().NotContain(h => h.NextTool == "query_snapshot" &&
                                             h.Reason.Contains("call tree", StringComparison.Ordinal));
        result.Hints.Any(IsOffCpuHint).Should().BeTrue();
    }

    [Fact]
    public async Task CollectNativeLockContentionSample_WhenOffCpuUnavailable_KeepsActivityOnlyAndSuggestsCapabilities()
    {
        var result = await SamplerUseCases.CollectNativeLockContentionSample(
            new StubNativeLockContentionSampler(
                new Hotspot(new SampledFrame("libnativecart.so", "cart_lock_bucket"), InclusiveSamples: 18, ExclusiveSamples: 4)),
            new MemoryDiagnosticHandleStore(),
            new FixedProcessContextResolver(canSampleNativeLockContention: true, canSampleOffCpu: false),
            processId: Pid,
            durationSeconds: 5);

        result.Error.Should().BeNull();
        result.Data!.ContentionEvidence!.Level.Should().Be(NativeContentionEvidenceLevels.Activity);
        result.Hints.Should().Contain(h => h.NextTool == "inspect_process" &&
                                           h.Reason.Contains("activity-only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CollectOffCpuSample_WithClosedFutexSpan_ClassifiesConfirmedBlocking()
    {
        var result = await SamplerUseCases.CollectOffCpuSample(
            new StubOffCpuSampler(OffCpuSnapshotWith(
                new OffCpuStackHotspot(
                    "pthread_mutex_lock",
                    OffCpuMicros: 25_000,
                    OccurrenceCount: 1,
                    DominantState: "S",
                    Stack: new[] { new OffCpuFrame("libc.so.6", "pthread_mutex_lock") },
                    SyscallBreakdown: new[] { new OffCpuSyscallAttribution("futex", 1, 25_000) },
                    NativeContentionEvidence: new NativeContentionEvidence(
                        NativeContentionEvidenceLevels.ConfirmedBlocking,
                        "closed futex",
                        NativeSyncSpanCount: 1,
                        ClosedNativeSyncSpanCount: 1,
                        NativeSyncOffCpuMicros: 25_000,
                        ClosedNativeSyncOffCpuMicros: 25_000)))),
            new MemoryDiagnosticHandleStore(),
            new FixedProcessContextResolver(canSampleNativeLockContention: true),
            new SymbolServerAllowlist(null),
            principalAllowsSymbolsRemote: false,
            processId: Pid,
            durationSeconds: 5);

        result.Error.Should().BeNull();
        result.Data!.NativeContentionEvidence!.Level.Should().Be(NativeContentionEvidenceLevels.ConfirmedBlocking);
        result.Summary.Should().Contain("Native sync blocking evidence: confirmed-blocking");
        result.Hints.Single(IsNativeLockContentionHint).Reason.Should().Contain("confirmed");
    }

    [Fact]
    public async Task CollectOffCpuSample_WithCensoredOnlyFutexSpan_ClassifiesProbableBlocking()
    {
        var result = await SamplerUseCases.CollectOffCpuSample(
            new StubOffCpuSampler(OffCpuSnapshotWith(
                new OffCpuStackHotspot(
                    "pthread_mutex_lock",
                    OffCpuMicros: 25_000,
                    OccurrenceCount: 1,
                    DominantState: "S",
                    Stack: new[] { new OffCpuFrame("libc.so.6", "pthread_mutex_lock") },
                    SyscallBreakdown: new[] { new OffCpuSyscallAttribution("futex", 1, 25_000) },
                    NativeContentionEvidence: new NativeContentionEvidence(
                        NativeContentionEvidenceLevels.ProbableBlocking,
                        "censored futex",
                        NativeSyncSpanCount: 1,
                        CensoredNativeSyncSpanCount: 1,
                        NativeSyncOffCpuMicros: 25_000,
                        CensoredNativeSyncOffCpuMicros: 25_000)))),
            new MemoryDiagnosticHandleStore(),
            new FixedProcessContextResolver(canSampleNativeLockContention: true),
            new SymbolServerAllowlist(null),
            principalAllowsSymbolsRemote: false,
            processId: Pid,
            durationSeconds: 5);

        result.Error.Should().BeNull();
        result.Data!.NativeContentionEvidence!.Level.Should().Be(NativeContentionEvidenceLevels.ProbableBlocking);
        result.Data.NativeContentionEvidence.ClosedNativeSyncSpanCount.Should().Be(0);
        result.Summary.Should().Contain("Native sync blocking evidence: probable-blocking");
    }

    [Fact]
    public async Task CollectOffCpuSample_WithAmbiguousNativeFrameButNoFutex_DoesNotSuggestNativeLockContention()
    {
        var result = await SamplerUseCases.CollectOffCpuSample(
            new StubOffCpuSampler(OffCpuSnapshotWith(
                new OffCpuStackHotspot(
                    "pthread_mutex_lock",
                    OffCpuMicros: 10_000,
                    OccurrenceCount: 2,
                    DominantState: "S",
                    Stack: new[] { new OffCpuFrame("libc.so.6", "pthread_mutex_lock") }))),
            new MemoryDiagnosticHandleStore(),
            new FixedProcessContextResolver(canSampleNativeLockContention: true),
            new SymbolServerAllowlist(null),
            principalAllowsSymbolsRemote: false,
            processId: Pid,
            durationSeconds: 5);

        result.Error.Should().BeNull();
        result.Data!.NativeContentionEvidence!.Level.Should().Be(NativeContentionEvidenceLevels.None);
        result.Data.NativeContentionEvidence.AmbiguousNativeSyncFrameSpanCount.Should().Be(2);
        result.Hints.Any(IsNativeLockContentionHint).Should().BeFalse();
    }

    [Fact]
    public async Task CollectOffCpuSample_WithPartialRawSyscallFallback_DoesNotPromoteNativeFrames()
    {
        var snapshot = OffCpuSnapshotWith(
            new OffCpuStackHotspot(
                "pthread_mutex_lock",
                OffCpuMicros: 10_000,
                OccurrenceCount: 1,
                DominantState: "S",
                Stack: new[] { new OffCpuFrame("libc.so.6", "pthread_mutex_lock") }))
            with
        {
            Notes =
            [
                "Syscall companion capture failed with exit code 86; base off-CPU stacks were returned without syscall labels.",
            ],
        };

        var result = await SamplerUseCases.CollectOffCpuSample(
            new StubOffCpuSampler(snapshot),
            new MemoryDiagnosticHandleStore(),
            new FixedProcessContextResolver(canSampleNativeLockContention: true),
            new SymbolServerAllowlist(null),
            principalAllowsSymbolsRemote: false,
            processId: Pid,
            durationSeconds: 5);

        result.Error.Should().BeNull();
        result.Data!.NativeContentionEvidence!.Level.Should().Be(NativeContentionEvidenceLevels.None);
        result.Data.NativeContentionEvidence.UncertaintyNotes.Should().Contain(n => n.Contains("failed with exit code 86", StringComparison.Ordinal));
        result.Hints.Any(IsNativeLockContentionHint).Should().BeFalse();
    }

    [Fact]
    public async Task CollectOffCpuSample_WithTruncatedFutexEvidence_DowngradesToProbable()
    {
        var snapshot = OffCpuSnapshotWith(
            new OffCpuStackHotspot(
                "pthread_mutex_lock",
                OffCpuMicros: 25_000,
                OccurrenceCount: 1,
                DominantState: "S",
                Stack: new[] { new OffCpuFrame("libc.so.6", "pthread_mutex_lock") },
                SyscallBreakdown: new[] { new OffCpuSyscallAttribution("futex", 1, 25_000) }))
            with
        {
            Notes =
            [
                "Syscall correlation hit the 500,000-interval cap; 10 syscall interval(s) were dropped.",
            ],
        };

        var result = await SamplerUseCases.CollectOffCpuSample(
            new StubOffCpuSampler(snapshot),
            new MemoryDiagnosticHandleStore(),
            new FixedProcessContextResolver(canSampleNativeLockContention: true),
            new SymbolServerAllowlist(null),
            principalAllowsSymbolsRemote: false,
            processId: Pid,
            durationSeconds: 5);

        result.Error.Should().BeNull();
        result.Data!.NativeContentionEvidence!.Level.Should().Be(NativeContentionEvidenceLevels.ProbableBlocking);
        result.Data.NativeContentionEvidence.ClosedNativeSyncSpanCount.Should().Be(0);
        result.Data.NativeContentionEvidence.NativeSyncSpanCount.Should().Be(1);
        result.Data.NativeContentionEvidence.UncertaintyNotes.Should().Contain(n => n.Contains("cap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CollectNativeLockContentionSample_TreatsPerfMapManagedFrameAsUsefulCaller()
    {
        var result = await SamplerUseCases.CollectNativeLockContentionSample(
            new StubNativeLockContentionSampler(
                new Hotspot(new SampledFrame("[unknown]", "[unknown]"), InclusiveSamples: 25, ExclusiveSamples: 1),
                new Hotspot(new SampledFrame("/tmp/perf-830.map", "BadCodeSample.Program.LockStorm"), InclusiveSamples: 18, ExclusiveSamples: 4)),
            new MemoryDiagnosticHandleStore(),
            new FixedProcessContextResolver(canSampleNativeLockContention: true),
            processId: Pid,
            durationSeconds: 5);

        result.Error.Should().BeNull();
        result.Summary.Should().Contain("First useful caller: BadCodeSample.Program.LockStorm");
        result.Summary.Should().Contain("Top sampled frame was [unknown]");
    }

    [Fact]
    public async Task CollectNativeLockContentionSample_SkipsUnknownMemfdFrame_ForNativeCaller()
    {
        var result = await SamplerUseCases.CollectNativeLockContentionSample(
            new StubNativeLockContentionSampler(
                new Hotspot(new SampledFrame("deleted)", "[unknown] (/memfd:doublemapper"), InclusiveSamples: 25, ExclusiveSamples: 1),
                new Hotspot(new SampledFrame("libnativecontention.so", "native_lock_hot_loop"), InclusiveSamples: 18, ExclusiveSamples: 4),
                new Hotspot(new SampledFrame("libnativecontention.so", "checkout_mutex_hot_path"), InclusiveSamples: 18, ExclusiveSamples: 4)),
            new MemoryDiagnosticHandleStore(),
            new FixedProcessContextResolver(canSampleNativeLockContention: true),
            processId: Pid,
            durationSeconds: 5);

        result.Error.Should().BeNull();
        result.Summary.Should().Contain("First useful caller: native_lock_hot_loop");
        result.Summary.Should().Contain("Top sampled frame was [unknown] (/memfd:doublemapper");
    }

    [Fact]
    public async Task CollectNativeLockContentionSample_PreservesHonestOutput_WhenNoUsefulCallerExists()
    {
        var result = await SamplerUseCases.CollectNativeLockContentionSample(
            new StubNativeLockContentionSampler(
                new Hotspot(new SampledFrame("[unknown]", "[unknown]"), InclusiveSamples: 25, ExclusiveSamples: 1),
                new Hotspot(new SampledFrame("libc.so.6", "pthread_mutex_lock"), InclusiveSamples: 20, ExclusiveSamples: 2)),
            new MemoryDiagnosticHandleStore(),
            new FixedProcessContextResolver(canSampleNativeLockContention: true),
            processId: Pid,
            durationSeconds: 5);

        result.Error.Should().BeNull();
        result.Summary.Should().Contain("Top sampled frame: [unknown]");
        result.Summary.Should().Contain("no clearer application/native caller surfaced inline");
        result.Hints.Should().Contain(h => h.NextTool == "query_snapshot");
    }

    [Fact]
    public async Task ProcessContextResolver_CarriesNativeLockCapabilityDigest()
    {
        var caps = new DiagnosticCapabilities(
            ProcessId: Pid,
            Runtime: RuntimeFlavor.CoreClr,
            RuntimeVersion: "10.0.0",
            CanReadEventCounters: true,
            CanSampleCpu: true,
            CanCollectGcDump: true,
            CanCollectExceptions: true,
            CanCollectHttpActivity: true,
            CanCollectCustomEventSource: true,
            CanCollectProcessDump: true,
            Notes: "")
        {
            CanSampleOffCpu = true,
            CanSampleNativeLockContention = true,
        };
        var resolver = new ProcessContextResolver(
            new StubDiscovery(new DotnetProcess(Pid, "/app", "linux", "x64", "10.0.0", "app")),
            new StubDetector(_ => caps));

        var result = await resolver.ResolveAsync(Pid, default);

        result.Error.Should().BeNull();
        result.Context!.CanSampleOffCpu.Should().BeTrue();
        result.Context.CanSampleNativeLockContention.Should().BeTrue();
    }

    // --- CorrelateBatchEvidence (issue #855: collect_batch native-lock + off-cpu correlation) ----

    private static NativeContentionEvidence ActivityEvidence(long sampledLockCallCount = 40)
        => new(
            NativeContentionEvidenceLevels.Activity,
            "sampled pthread mutex entry points are lock activity only; this sampler does not measure wait duration or prove blocking.",
            SampledLockCallCount: sampledLockCallCount,
            EvidenceSources: ["perf uprobes on pthread_mutex_lock/pthread_mutex_unlock"]);

    private static NativeContentionEvidence ConfirmedBlockingEvidence()
        => new(
            NativeContentionEvidenceLevels.ConfirmedBlocking,
            "closed futex span(s) confirm the thread blocked in the kernel.",
            NativeSyncSpanCount: 3,
            ClosedNativeSyncSpanCount: 3,
            NativeSyncOffCpuMicros: 45_000,
            ClosedNativeSyncOffCpuMicros: 45_000,
            EvidenceSources: ["perf sched_switch + raw_syscalls correlation"]);

    private static NativeContentionEvidence NoneEvidence()
        => new(NativeContentionEvidenceLevels.None, "No native synchronization off-CPU evidence observed in this window.");

    [Fact]
    public void CorrelateBatchEvidence_BothPresent_UsesOffCpuLevelAndAddsLockCallCount()
    {
        var lockEvidence = ActivityEvidence(sampledLockCallCount: 128);
        var offCpuEvidence = ConfirmedBlockingEvidence();

        var merged = NativeLockContentionUx.CorrelateBatchEvidence(lockEvidence, offCpuEvidence);

        merged.Level.Should().Be(NativeContentionEvidenceLevels.ConfirmedBlocking,
            "only off-CPU evidence can confirm blocking; native-lock activity never elevates the level");
        merged.SampledLockCallCount.Should().Be(128);
        merged.ClosedNativeSyncSpanCount.Should().Be(3);
        merged.ClosedNativeSyncOffCpuMicros.Should().Be(45_000);
        merged.Summary.Should().Contain("Correlated with 128 sampled native mutex-call(s)");
        merged.ConfidenceRationale.Should().Contain(note => note.Contains("does not itself raise or lower the level", StringComparison.Ordinal));
    }

    [Fact]
    public void CorrelateBatchEvidence_UncontendedWorkload_StaysActivityOnly_NotMislabeledAsBlocking()
    {
        var lockEvidence = ActivityEvidence(sampledLockCallCount: 500);
        var offCpuEvidence = NoneEvidence();

        var merged = NativeLockContentionUx.CorrelateBatchEvidence(lockEvidence, offCpuEvidence);

        merged.Level.Should().Be(NativeContentionEvidenceLevels.None,
            "heavy sampled lock activity alone, with no off-CPU blocking evidence, must never be reported as blocking");
        merged.SampledLockCallCount.Should().Be(500);
    }

    [Fact]
    public void CorrelateBatchEvidence_OnlyNativeLockRan_KeepsActivityLevelAndNotesMissingOffCpu()
    {
        var lockEvidence = ActivityEvidence(sampledLockCallCount: 64);

        var merged = NativeLockContentionUx.CorrelateBatchEvidence(lockEvidence, offCpuEvidence: null);

        merged.Level.Should().Be(NativeContentionEvidenceLevels.Activity);
        merged.SampledLockCallCount.Should().Be(64);
        merged.Summary.Should().Contain("off_cpu did not run");
    }

    [Fact]
    public void CorrelateBatchEvidence_OnlyOffCpuRan_KeepsItsOwnLevelAndNotesMissingNativeLock()
    {
        var offCpuEvidence = ConfirmedBlockingEvidence();

        var merged = NativeLockContentionUx.CorrelateBatchEvidence(lockEvidence: null, offCpuEvidence);

        merged.Level.Should().Be(NativeContentionEvidenceLevels.ConfirmedBlocking);
        merged.SampledLockCallCount.Should().Be(0);
        merged.Summary.Should().Contain("native-lock-contention did not run");
    }

    [Fact]
    public void CorrelateBatchEvidence_NeitherRan_ReturnsNoneLevel()
    {
        var merged = NativeLockContentionUx.CorrelateBatchEvidence(lockEvidence: null, offCpuEvidence: null);

        merged.Level.Should().Be(NativeContentionEvidenceLevels.None);
        merged.Summary.Should().Contain("Neither native-lock-contention nor off_cpu produced usable evidence");
    }

    private static OffCpuSnapshot OffCpuSnapshotWith(params OffCpuStackHotspot[] stacks)
        => new(
            ProcessId: Pid,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(5),
            TotalOffCpuMicros: stacks.Sum(s => s.OffCpuMicros),
            DistinctThreads: 1,
            TopBlockingStacks: stacks,
            SchedSwitches: stacks.Sum(s => s.OccurrenceCount),
            SymbolSource: "test");

    private static bool IsNativeLockContentionHint(NextActionHint hint)
        => HintKindEquals(hint, "native-lock-contention");

    private static bool IsOffCpuHint(NextActionHint hint)
        => HintKindEquals(hint, "off_cpu");

    private static bool HintKindEquals(NextActionHint hint, string expectedKind)
        => hint.NextTool == "collect_sample" &&
           hint.SuggestedArguments is not null &&
           hint.SuggestedArguments.TryGetValue("kind", out var kind) &&
           Equals(kind, expectedKind);

    private sealed class StubOffCpuSampler(OffCpuSnapshot snapshot) : IOffCpuSampler
    {
        public bool IsAvailable() => true;

        public Task<OffCpuSampleResult> SampleAsync(
            int processId,
            TimeSpan duration,
            int topN = 25,
            string? symbolPath = null,
            CancellationToken cancellationToken = default)
        {
            var summary = snapshot with { ProcessId = processId, Duration = duration };
            var artifact = new OffCpuSnapshotArtifact(
                processId,
                summary.StartedAt,
                duration,
                summary.TotalOffCpuMicros,
                summary.SchedSwitches,
                summary.TopBlockingStacks,
                Threads: Array.Empty<OffCpuThreadView>(),
                summary.SymbolSource);
            return Task.FromResult(new OffCpuSampleResult(summary, artifact));
        }
    }

    private sealed class StubNativeLockContentionSampler(params Hotspot[] hotspots) : INativeLockContentionSampler
    {
        public bool IsAvailable() => true;

        public Task<NativeLockContentionSampleResult> SampleAsync(
            int processId,
            TimeSpan duration,
            int topN = 25,
            long samplePeriod = 5000,
            CancellationToken cancellationToken = default)
        {
            var root = new CallTreeNode(
                new SampledFrame("root", "root"),
                InclusiveSamples: hotspots.Sum(h => h.InclusiveSamples),
                ExclusiveSamples: 0,
                Children: hotspots.Select(h => new CallTreeNode(
                    h.Frame,
                    h.InclusiveSamples,
                    h.ExclusiveSamples,
                    Children: Array.Empty<CallTreeNode>())).ToArray());
            var artifact = new CpuSampleTraceArtifact(
                processId,
                DateTimeOffset.UtcNow,
                duration,
                hotspots.Sum(h => h.InclusiveSamples),
                root);
            var summary = new NativeLockContentionSample(
                processId,
                DateTimeOffset.UtcNow,
                duration,
                hotspots.Sum(h => h.InclusiveSamples),
                hotspots,
                new[] { "pthread_mutex_lock", "pthread_mutex_unlock" },
                "/lib/x86_64-linux-gnu/libc.so.6",
                samplePeriod,
                "PdbResolved",
                Array.Empty<string>());
            return Task.FromResult(new NativeLockContentionSampleResult(summary, artifact));
        }
    }

    private sealed class FixedProcessContextResolver(bool canSampleNativeLockContention, bool canSampleOffCpu = true) : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken = default)
        {
            var pid = requestedProcessId.GetValueOrDefault(Pid);
            var context = new ProcessContext(
                pid,
                RuntimeFlavor.CoreClr,
                CanSampleCpu: true,
                CanCollectGcDump: true,
                AutoResolved: requestedProcessId is null,
                RuntimeVersion: "10.0.0",
                BindingSource: "test")
            {
                CanSampleOffCpu = canSampleOffCpu,
                CanSampleNativeLockContention = canSampleNativeLockContention,
            };
            return Task.FromResult(new ProcessContextResolution(context, Error: null));
        }
    }

    private sealed class StubDiscovery : IProcessDiscovery
    {
        private readonly IReadOnlyList<DotnetProcess> _processes;

        public StubDiscovery(params DotnetProcess[] processes) => _processes = processes;

        public IReadOnlyList<DotnetProcess> ListProcesses() => _processes;

        public DotnetProcess? TryGetProcess(int processId)
            => _processes.FirstOrDefault(p => p.ProcessId == processId);
    }

    private sealed class StubDetector(Func<int, DiagnosticCapabilities> factory) : ICapabilityDetector
    {
        public Task<DiagnosticCapabilities> DetectAsync(int processId, CancellationToken cancellationToken = default)
            => Task.FromResult(factory(processId));
    }
}
