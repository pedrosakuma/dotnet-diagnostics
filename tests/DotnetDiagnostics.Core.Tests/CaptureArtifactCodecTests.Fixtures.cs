using System.Collections.Immutable;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuEfficiency;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Evidence;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Kestrel;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.MethodParameters;
using DotnetDiagnostics.Core.NativeLockContention;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Requests;
using DotnetDiagnostics.Core.Startup;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.Threads;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class CaptureArtifactCodecTests
{
    public static IEnumerable<object[]> Snapshots()
    {
        yield return ["counters", Counters()];
        yield return ["exception-snapshot", new ExceptionSnapshot(42, At, Window, 3,
            [new("System.Exception", 3)], [new(At.AddTicks(7), "System.Exception", Rich, "0x80131500", 9)])];
        var exception = new CrashGuardExceptionEvent(At, "System.Exception", Rich, "0x80131500", 9, "Thrown", false, [Rich, "Main"]);
        yield return ["crash-guard-snapshot", new CrashGuardSnapshot(42, At, Window, false, null, false, 1,
            [new("System.Exception", 1)], [exception], exception, [Rich])];
        yield return ["gc-events", new GcSummary(42, At, Window, 1, TimeSpan.FromTicks(7001), TimeSpan.FromTicks(7001),
            [new(2, 1)], [new(At, 2, "AllocLarge", "Background", TimeSpan.FromTicks(7001))],
            Suspension: new("no-detected-loss", At, At + Window, At.AddMinutes(-1), "complete",
                TimeSpan.FromTicks(123), TimeSpan.FromTicks(123), 1, 0,
                [new(At.AddTicks(7), At.AddTicks(130), 1, 9, 1, 123, TimeSpan.FromTicks(3))],
                new Dictionary<string, long>()))];
        yield return ["gc-datas", new GcDatasSnapshot(42, At, Window,
            [new(At, 123, 1234567, 1234, 7, 8, 9876543, 65536)], [], [], new(2, 1, 9))];
        yield return ["event-source", new EventSourceCapture(42, Rich, At, Window, 7,
            [new(At.AddTicks(1), Rich, "Event", "Warning", new Dictionary<string, string> { [Rich] = Rich })])];
        yield return ["event-catalog", new EventCatalogSnapshot(42, At, Window, [Rich], 7, 1,
            [new(Rich, "Event", "Warning", 7)], 2, [new(At, Rich, "Event", "Warning")])];
        yield return ["activities", new ActivityCapture(42, null, At, Window, 1, 1,
            [new(Rich, "GET /漢字", "id", null, "0123456789abcdef0123456789abcdef", "0123456789abcdef", null,
                At, At.AddTicks(55), TimeSpan.FromTicks(55), new Dictionary<string, string> { [Rich] = Rich })],
            [], [])];
        yield return ["log-snapshot", new LogSnapshot(42, null, "Information", At, Window, 1, 0, 0, 0, 0, 1, 0,
            [new(Rich, 1, 1, 1)], [new(At, "Error", Rich, 9, null, Rich, "System.Exception", Rich, null)],
            true, [Rich])];
        yield return ["jit-snapshot", new JitSnapshot(42, At, Window, 1, 1, 1, new(0, 1, 0, 0, 0),
            0, 1, 1, 1, 100, null, Rich,
            [new("Demo", "Run", "(Int32)", Rich, 12.5, 1, "OptimizedTier1OSR", 0, 1, 0, 1, 1, true)], [Rich])];
        yield return ["threadpool-snapshot", new ThreadPoolEventSnapshot(42, At, Window,
            [new(At, 7, ThreadPoolEvidence.RuntimeObserved)], [new(At, 2, ThreadPoolEvidence.RuntimeObserved)],
            [new(At, "Starvation", 4, 7, 12.5, ThreadPoolEvidence.RuntimeObserved)], [new(Rich, 4)], new(1, 32767, 1, 1000),
            7, 3, [Rich], Quality: EvidenceQuality.LegacyUnknown)];
        yield return ["contention-snapshot", new ContentionSnapshot(42, At, Window, 1, 1, Window, Window, Window, Window,
            [new(At, At + Window, Window, 9, 17, 101, 5001, Rich, "Demo")], [Rich])];
        yield return ["db-snapshot", new DbSnapshot(42, At, Window, 3,
            [new("hash", Rich, "Data Source=example;******", ["SqlClient"], 3, 12.5, 7.5, 5, At, At + Window)],
            [], [], [Rich])];
        yield return ["kestrel-snapshot", Kestrel()];
        yield return ["networking-snapshot", NetworkingCorrelationContractFixture.Create()];
        yield return ["in-flight-requests", new InFlightRequestSnapshot(42, At, Window, 7, 3, 1, 1, 1000, 1500,
            [new(Rich, null, "GET", "/漢字", At, 1500, true)], [Rich])];
        yield return ["startup-snapshot", new StartupSnapshot(42, At, Window, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0,
            Window, [new(At, "AssemblyLoad", Rich, 17)], [new(Rich, 1, At, At)],
            [new(At, "ModuleLoad", "Demo.dll", "/unavailable/Demo.dll", 19, 17)], [new("Demo.dll", 1, At, At)],
            [new(At, "ServiceResolved", 23, Rich, null, null, null, null, null, null, null, null, null)],
            [new(At, "loader", "AssemblyLoad", Rich)], true, [Rich])];
        yield return ["cpu-sample", Cpu()];
        yield return ["allocation-sample", new AllocationSampleArtifact(new(42, At, Window, 7, 8192,
            [new("System.String", 8192, 7, HeapKind.Small)], [new("System.String", 8192, 7, HeapKind.Small)]), Cpu())];
        yield return ["native-alloc-sample", Cpu()];
        yield return ["native-lock-contention-sample", new NativeLockContentionArtifact(
            new(42, At, Window, 7, [new(new(Rich, "LeafA"), 7, 4)], ["pthread_mutex_lock"], "/missing/libc.so", 31, "unknown", [Rich]), Cpu())];
        yield return ["off-cpu-snapshot", new OffCpuSnapshotArtifact(42, At, Window, 1234567, 7,
            [new("LeafA", 1234567, 7, "S", [new(Rich, "LeafA", Identity()) { InstructionPointer = 0xff0011223344 }],
                [new("futex", 7, 1234567)])], [new(9, Rich, 1234567, 7, "LeafA")], "unknown", 1, 321, [Rich])];
        yield return ["cpu-efficiency-sample", new CpuEfficiencySample(42, At, Window, "perf-stat",
            Instructions: 123456789, Cycles: 987654321, InstructionsPerCycle: 0.125, CacheMisses: null, Notes: [Rich])];
        yield return ["method-params-capture", MethodParameters()];
        yield return ["thread-snapshot", Threads()];
        yield return ["heap-snapshot", Heap()];
        yield return ["requests-now", new RequestsNowSnapshot(42, At, Window,
            [new(Rich, "/漢字", "GET", 12.345, 9, [Rich, "Main"])]) { Notes = [Rich] }];
    }

    private static CounterSnapshot Counters() => new(42, At, Window,
        [new("System.Runtime", "cpu-usage", Rich, 12.5, CounterKind.Mean, "%"),
            new(Rich, "duration", Rich, -0.125, CounterKind.Sum, "ms") { IntervalSec = 0.125, DisplayRateTimeScale = TimeSpan.FromTicks(1001) }],
        [new(Rich, "orders", null, "Counter", new Dictionary<string, string?> { [Rich] = null, ["value"] = Rich }, 7, 2, null)],
        [Rich]);

    private static MethodIdentity Identity() => new("LeafA", 1, "Demo.dll", "/unavailable/Demo.dll",
        Guid.Parse("12ae9cb6-1ffc-4aa8-b690-001122334455"), 0x06000017, "Demo.Handler`1")
    {
        GenericTypeArguments = new(["System.String"], ["System.Int32"]),
        ClosedSignature = Rich,
        Source = new("/unavailable/漢字.cs", 17, "https://example.invalid/line#L17", 22),
    };

    private static CpuSampleTraceArtifact Cpu()
    {
        var identity = Identity();
        var root = new CallTreeNode(new("Demo", "Root"), 7, 0,
            [new(new(Rich, "LeafA"), 4, 4, [], identity) { SelfSamples = new(0, 1, 3) },
                new(new("Demo", "LeafB"), 3, 3, []) { SelfSamples = new(0, 0, 3) }], identity);
        return new(42, At, Window, 7, root,
            new Dictionary<SymbolRef, SourceLocation> { [new(Rich, "LeafA")] = identity.Source! },
            new Dictionary<SymbolRef, MethodIdentity> { [new(Rich, "LeafA")] = identity },
            TracePath: "/unavailable/opt-in-trace.nettrace")
        {
            Evidence = CpuSampleEvidence.EventPipeSampleProfiler,
            SelfSamples = new(0, 1, 6),
            Notes = [Rich],
        };
    }

    private static ThreadSnapshotArtifact Threads() => new(ThreadSnapshotOrigin.Live, 42, At, Window, "CoreCLR", "10.0.0",
        [new(1, 9, 0x1234, "WaitSleepJoin", true, true, false, false, true, 1, null, Rich,
            [new("ManagedMethod", Rich, "Demo.Handler", "Demo.dll", 0xff0011223344, 0xff0055667788, Identity())
                { AddressKind = "managed", Rva = 17 }])
        {
            IsLikelyBlocked = true,
            IsContendedLockOwner = true,
            IsLockWaiter = true,
            IsDeadlockCandidate = true,
            InferredWaitReason = "Monitor",
        }], [])
    {
        Warnings = [Rich],
        ProcessStartedAtUtc = At.AddMinutes(-1),
        Source = "clrmd-thread-walk",
    };

    private static HeapSnapshotArtifact Heap() => new(HeapSnapshotOrigin.Live, 42, At, Window,
        new("CoreCLR", "10.0.0", "X64", false, 1), new(1024, 0, 0, 1024, 0, 0, 1024),
        [new("System.String", null, 7, 1024, 100)], [new("System.String", null, 7, 1024, 100)])
    {
        Quality = EvidenceQuality.LegacyUnknown,
        Warnings = [Rich],
        DumpFilePath = "/unavailable/provenance-only.dmp",
        RetentionPaths = [new("System.String", 0x1000, [new("<root>", 0) { RootKind = "StaticVar" }, new(Rich, 0x1000)], false)],
        RootsByKind = [new("StaticVar", 7, 7, 1024, 0, 0)],
        FinalizableObjectsByType = [new("System.IO.FileStream", null, 3, 384)],
        Segments = [new(0, "Gen2", "Gen2", 0x1000, 0x2000, 4096, 4096, 0, 2048, 2048, 10, 1) { FreePercent = 50 }],
        StaticFields = [new("Demo", null, "Cache", 0x0a000001, 0x2000, Rich, 4096, 1)],
        DelegateTargets = [new("Subscriber", "Publisher", "OnChanged", null, null, 7)],
        GcHandles = new(2, ImmutableArray.Create(new GcHandleBucket("Strong", 2, 4096, [])), []),
        AsyncOperations = [new("MyAsyncStateMachine", 0, "TaskAwaiter", 128)],
        Timers = new(1, 2, 1, [new("System.Threading.TimerQueueTimer", null, "MyTimer", "Tick", null, 1)],
            [new("System.Threading.Tasks.Task", null, 2, 128)], [new("System.Threading.Tasks.TaskCompletionSource", null, 1, 64)], [Rich]),
        AssemblyLoadContexts = new(0, 0, 0, [], [Rich]),
    };

    private static MethodParameterCaptureArtifact MethodParameters()
    {
        var method = new ResolvedMethodIdentity("Demo.dll", "12ae9cb6-1ffc-4aa8-b690-001122334455", "Demo.Handler", "Run", 0, 0x06000017, ["System.String"]);
        return new(42, At, Window, "CoreCLR", "10.0.0", [new("Demo.dll", "Demo.Handler", "Run")], [method],
            10, 1, 1, 2, 1, 1, true, true, "max_events_reached",
            [new(1, At.AddTicks(7), method, [new("input", "System.String", Rich, true, true) { Notes = [Rich] }])]);
    }

    private static KestrelSnapshot Kestrel() => new(42, At, Window, 7, 3, 1, 9, 4, 3, 2, 1, 2, 4,
        Window, Window, Window, Window, Window, Window, Window, Window, Window,
        [new("connection-queue-length", Rich, 3, null)], [new(At, "connection-queue-length", 3)],
        [new("GET", "/漢字", "HTTP/2", 4, Window, Window, Window)], ["TLS1.3"], "{\"raw\":\"漢字\"}", [Rich]);
}
