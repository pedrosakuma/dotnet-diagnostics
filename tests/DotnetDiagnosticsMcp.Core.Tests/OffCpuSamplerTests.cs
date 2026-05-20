using DotnetDiagnosticsMcp.Core.OffCpu;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class RoutingOffCpuSamplerTests
{
    [Fact]
    public async Task OnNonLinux_NonWindows_Throws_NotSupportedException()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) return; // exercised on macOS or other only
        var router = new RoutingOffCpuSampler(new PerfSchedOffCpuSampler(), new EtwOffCpuSampler());
        router.IsAvailable().Should().BeFalse();

        var act = async () => await router.SampleAsync(processId: 1, TimeSpan.FromSeconds(1));
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task OnWindows_WithoutElevation_Throws_InvalidOperation_WithAdminHint()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Tests run unelevated in CI, so we expect the router to bail with an actionable message.
        // If a developer happens to run the suite elevated locally the sampler is available and the
        // call would proceed to ETW capture against pid=1, which would fail differently — skip
        // the assertion in that case rather than introduce flakiness.
        var sampler = new EtwOffCpuSampler();
        if (sampler.IsAvailable()) return;

        var router = new RoutingOffCpuSampler(new PerfSchedOffCpuSampler(), sampler);
        router.IsAvailable().Should().BeFalse();

        var act = async () => await router.SampleAsync(processId: 1, TimeSpan.FromSeconds(1));
        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("ETW");
        ex.Which.Message.Should().Contain("administrative", because: "the LLM needs the actionable elevation hint");
    }
}

public sealed class PerfSchedAggregateTests
{
    [Fact]
    public void GroupsByStackKeyAndRanksByTotalOffCpuMicros()
    {
        // Two spans on the same blocking stack and one on a different stack — the heavier stack
        // should win the top spot and the per-thread rollup should track per-TID totals.
        var futexStack = new List<OffCpuFrame>
        {
            // perf prints leaf→root: schedule() is the leaf (event fires in-kernel),
            // pthread_cond_wait() is the user-space root. Aggregate reverses internally.
            new("[kernel.kallsyms]", "schedule"),
            new("[kernel.kallsyms]", "futex_wait_queue"),
            new("libc.so.6", "pthread_cond_wait"),
        };
        var ioStack = new List<OffCpuFrame>
        {
            new("[kernel.kallsyms]", "schedule"),
            new("[kernel.kallsyms]", "io_schedule"),
        };

        var spans = new List<OffCpuSpan>
        {
            new(Tid: 1001, Comm: "worker-1", DurationMicros: 100_000, PrevState: "S", BlockingStack: futexStack),
            new(Tid: 1002, Comm: "worker-2", DurationMicros: 200_000, PrevState: "S", BlockingStack: futexStack),
            new(Tid: 1003, Comm: "worker-3", DurationMicros: 50_000,  PrevState: "D", BlockingStack: ioStack),
        };

        var result = PerfSchedOffCpuSampler.Aggregate(
            processId: 4242,
            startedAt: DateTimeOffset.UtcNow,
            duration: TimeSpan.FromSeconds(10),
            spans: spans,
            schedSwitches: 3,
            topN: 25);

        result.Summary.TotalOffCpuMicros.Should().Be(350_000);
        result.Summary.DistinctThreads.Should().Be(3);
        result.Summary.SchedSwitches.Should().Be(3);
        result.Summary.TopBlockingStacks.Should().HaveCount(2);
        result.Summary.TopBlockingStacks[0].OffCpuMicros.Should().Be(300_000, "futex stack aggregates 1001+1002");
        result.Summary.TopBlockingStacks[0].OccurrenceCount.Should().Be(2);
        result.Summary.TopBlockingStacks[0].DominantState.Should().Be("S");
        result.Summary.TopBlockingStacks[1].OffCpuMicros.Should().Be(50_000);
        result.Summary.TopBlockingStacks[1].DominantState.Should().Be("D");

        result.Artifact.Threads.Should().HaveCount(3);
        result.Artifact.Threads[0].Tid.Should().Be(1002, "worker-2 blocked the longest individually");
        result.Artifact.Threads[0].OffCpuMicros.Should().Be(200_000);
    }

    [Fact]
    public void Aggregate_PreservesPerFrame_MethodIdentity()
    {
        // Slice 2c Eixo B contract: frames that the backend already enriched with a
        // MethodIdentity (perf-map enrichment on Linux, TraceMethod on Windows) must
        // round-trip through the aggregator intact so dotnet-assembly-mcp can resolve
        // them without re-walking the trace.
        var identity = new DotnetDiagnosticsMcp.Core.Memory.MethodIdentity(
            MethodName: "Checkout",
            GenericArity: 0,
            ModuleName: "MyApp.dll",
            ModulePath: "/app/MyApp.dll",
            ModuleVersionId: Guid.NewGuid(),
            MetadataToken: 0x06000123,
            TypeFullName: "MyApp.OrderService");
        var stack = new List<OffCpuFrame>
        {
            new("[kernel.kallsyms]", "schedule"),
            new("libc.so.6", "pthread_cond_wait"),
            new("MyApp.dll", "MyApp.OrderService.Checkout", Identity: identity),
        };
        var spans = new List<OffCpuSpan>
        {
            new(Tid: 7, Comm: "w", DurationMicros: 1_000, PrevState: "S", BlockingStack: stack),
        };

        var result = PerfSchedOffCpuSampler.Aggregate(
            processId: 1, startedAt: DateTimeOffset.UtcNow, duration: TimeSpan.FromSeconds(1),
            spans: spans, schedSwitches: 1, topN: 5);

        var top = result.Summary.TopBlockingStacks.Single();
        // Aggregator reverses to root→leaf, so the managed frame (originally at index 2,
        // the user-space root) ends up at index 0.
        top.Stack[0].Identity.Should().BeSameAs(identity, "Identity payload must propagate unmodified");
        top.Stack[1].Identity.Should().BeNull("native libc frame stays Identity=null");
        top.Stack[2].Identity.Should().BeNull("kernel frame stays Identity=null");
    }
}

public sealed class PerfSchedScriptParserEnrichmentTests
{
    [Fact]
    public void ParseFrame_AttachesMethodIdentity_WhenSymbolMatchesPerfMapEntry()
    {
        // Synthetic perf-script output covering one OUT/IN pair around a managed user frame
        // whose symbol exactly matches the key the JitMapEmitter would have written into the
        // perf-map. This exercises the dict lookup path without needing a live JIT.
        const string script = @"swapper     0 [000] 100.000000: sched:sched_switch: prev_comm=worker prev_pid=4242 prev_prio=120 prev_state=S ==> next_comm=swapper next_pid=0 next_prio=120
        ffffffff8100abcd schedule+0x0 ([kernel.kallsyms])
        7fabc1234567 pthread_cond_wait+0x0 (libc.so.6)
        7fabc7654321 MyApp.OrderService.Checkout+0x0 (/app/MyApp.dll)

worker     4242 [000] 100.500000: sched:sched_switch: prev_comm=swapper prev_pid=0 prev_prio=120 prev_state=R ==> next_comm=worker next_pid=4242 next_prio=120

";
        var identity = new DotnetDiagnosticsMcp.Core.Memory.MethodIdentity(
            MethodName: "Checkout",
            GenericArity: 0,
            ModuleName: "MyApp.dll",
            ModulePath: "/app/MyApp.dll",
            ModuleVersionId: Guid.NewGuid(),
            MetadataToken: 0x06000123,
            TypeFullName: "MyApp.OrderService");
        var symbols = new Dictionary<string, DotnetDiagnosticsMcp.Core.Memory.MethodIdentity>(StringComparer.Ordinal)
        {
            ["MyApp.OrderService.Checkout"] = identity,
        };
        var tids = new HashSet<int> { 4242 };

        var (spans, _) = PerfSchedScriptParser.Parse(script, tids, flushPending: false, symbolToIdentity: symbols);

        spans.Should().HaveCount(1);
        var stack = spans[0].BlockingStack;
        stack.Should().HaveCount(3);
        var managed = stack.Single(f => f.Method == "MyApp.OrderService.Checkout");
        managed.Identity.Should().BeSameAs(identity, "the parser looks the symbol up in the perf-map dict and attaches the canonical handoff payload");
        stack.Where(f => f.Method != "MyApp.OrderService.Checkout")
             .Should().AllSatisfy(f => f.Identity.Should().BeNull("kernel and native frames are not in the perf-map"));
    }

    [Fact]
    public void ParseFrame_LeavesIdentityNull_WhenNoMapProvided()
    {
        const string script = @"swapper     0 [000] 100.000000: sched:sched_switch: prev_comm=worker prev_pid=4242 prev_prio=120 prev_state=S ==> next_comm=swapper next_pid=0 next_prio=120
        ffffffff8100abcd schedule+0x0 ([kernel.kallsyms])
        7fabc7654321 MyApp.OrderService.Checkout+0x0 (/app/MyApp.dll)

worker     4242 [000] 100.500000: sched:sched_switch: prev_comm=swapper prev_pid=0 prev_prio=120 prev_state=R ==> next_comm=worker next_pid=4242 next_prio=120

";
        var (spans, _) = PerfSchedScriptParser.Parse(script, new HashSet<int> { 4242 }, flushPending: false);
        spans.Should().HaveCount(1);
        spans[0].BlockingStack.Should().AllSatisfy(f => f.Identity.Should().BeNull());
    }
}
