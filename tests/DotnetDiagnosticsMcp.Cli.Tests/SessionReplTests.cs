using System.Text;
using DotnetDiagnosticsMcp.Cli;
using DotnetDiagnosticsMcp.Core.Collection;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.UseCases;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnosticsMcp.Cli.Tests;

/// <summary>
/// Coverage for the stateful <c>session</c> REPL (issue #300). These tests drive
/// <see cref="SessionRepl.RunAsync"/> directly with a <see cref="StringReader"/> feeding scripted
/// commands and <see cref="StringWriter"/> sinks, so they exercise the loop, cancellation, validation
/// gating and the handle-reuse <c>query</c> path without spawning a live target process. The shared
/// <see cref="IDiagnosticHandleStore"/> is seeded directly to simulate a prior <c>collect</c>.
/// </summary>
public sealed class SessionReplTests
{
    [Fact]
    public async Task Exit_ReturnsZero_AndPrintsBanner()
    {
        var (exit, stdout, stderr) = await RunReplAsync("exit\n");

        exit.Should().Be(0);
        stdout.Should().Contain("dotnet-diagnostics session");
        stdout.Should().Contain("diag>");
        stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task Quit_ReturnsZero()
    {
        var (exit, _, _) = await RunReplAsync("quit\n");
        exit.Should().Be(0);
    }

    [Fact]
    public async Task Eof_ReturnsZero()
    {
        // No input at all => immediate EOF => graceful exit.
        var (exit, _, _) = await RunReplAsync(string.Empty);
        exit.Should().Be(0);
    }

    [Fact]
    public async Task BlankLines_AreIgnored_AndLoopContinues()
    {
        var (exit, _, stderr) = await RunReplAsync("\n   \nexit\n");

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task Help_PrintsSessionCommandList()
    {
        var (exit, stdout, _) = await RunReplAsync("help\nexit\n");

        exit.Should().Be(0);
        stdout.Should().Contain("Session commands:");
        stdout.Should().Contain("query --handle");
    }

    [Fact]
    public async Task UnknownCommand_StaysInLoop()
    {
        var (exit, _, stderr) = await RunReplAsync("bogus-command\nexit\n");

        exit.Should().Be(0);
        stderr.Should().Contain("Unknown command 'bogus-command'");
    }

    [Fact]
    public async Task SessionCommand_InsideSession_IsRejected()
    {
        var (exit, _, stderr) = await RunReplAsync("session\nexit\n");

        exit.Should().Be(0);
        stderr.Should().Contain("Already in a session");
    }

    [Fact]
    public async Task ValidationError_StaysInLoop()
    {
        // `collect` without --kind fails validation before any attach/DI resolution.
        var (exit, _, stderr) = await RunReplAsync("collect\nexit\n");

        exit.Should().Be(0);
        stderr.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CommandHelp_InsideSession_ShowsFocusedHelp()
    {
        var (exit, stdout, _) = await RunReplAsync("dump --help\nexit\n");

        exit.Should().Be(0);
        stdout.Should().Contain("dump options:");
    }

    [Fact]
    public async Task Query_WithoutHandle_ReturnsInvalidArgument_StaysInLoop()
    {
        var (exit, stdout, _) = await RunReplAsync("query\nexit\n");

        exit.Should().Be(0);
        stdout.Should().Contain("--handle <id> is required");
        stdout.Should().Contain("InvalidArgument");
    }

    [Fact]
    public async Task Query_UnknownHandle_ReturnsNotFound()
    {
        var (exit, stdout, _) = await RunReplAsync("query --handle does-not-exist\nexit\n");

        exit.Should().Be(0);
        stdout.Should().Contain("unknown or expired");
        stdout.Should().Contain("NotFound");
    }

    [Fact]
    public async Task Query_ReusesCollectedCountersHandle_WithoutRecollecting()
    {
        var (services, store) = BuildServices();
        var handle = SeedCountersHandle(store);

        var (exit, stdout, stderr) = await RunReplAsync(
            $"query --handle {handle.Id} --view byProvider\nexit\n", services);

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        stdout.Should().Contain("query: counters");
        stdout.Should().Contain("byProvider");
        stdout.Should().Contain("System.Runtime");
    }

    [Fact]
    public async Task Query_UnknownView_ListsValidViews()
    {
        var (services, store) = BuildServices();
        var handle = SeedCountersHandle(store);

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --view nonsense\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("unknown view 'nonsense'");
        stdout.Should().Contain("byProvider");
    }

    [Fact]
    public async Task Query_ThreadSnapshotKindHandle_ReturnsNotSupportedInSession()
    {
        var (services, store) = BuildServices();
        // thread-snapshot drill-down routing still lives in the MCP server (it needs a live attach);
        // the dummy artifact is never touched because the empty-views check fires before dispatch.
        var handle = store.Register(Environment.ProcessId, "thread-snapshot", new object(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id}\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("not available in the session yet");
        stdout.Should().Contain("NotSupportedInSession");
    }

    [Fact]
    public async Task Query_HeapHandle_TopTypes_RendersFromSnapshot()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, HeapInspectionUseCases.HeapSnapshotKind, HeapSnapshot(), TimeSpan.FromMinutes(10));

        var (exit, stdout, stderr) = await RunReplAsync(
            $"query --handle {handle.Id} --view top-types\nexit\n", services);

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        stdout.Should().Contain("top-types");
        stdout.Should().Contain("System.String");
    }

    [Fact]
    public async Task Query_HeapHandle_DefaultsToTopTypes()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, HeapInspectionUseCases.HeapSnapshotKind, HeapSnapshot(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id}\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("top-types");
        stdout.Should().Contain("System.String");
    }

    [Fact]
    public async Task Query_HeapHandle_ServerOnlyView_ReturnsNotSupportedInSession()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, HeapInspectionUseCases.HeapSnapshotKind, HeapSnapshot(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --view objsize\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("not available in the session yet");
        stdout.Should().Contain("NotSupportedInSession");
        stdout.Should().Contain("live ClrMD attach");
    }

    [Fact]
    public async Task Query_HeapHandle_DuplicateStringsView_ExplainsSensitivePolicy()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, HeapInspectionUseCases.HeapSnapshotKind, HeapSnapshot(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --view duplicate-strings\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("NotSupportedInSession");
        stdout.Should().Contain("sensitive-value policy");
    }

    [Fact]
    public async Task Query_HeapHandle_UnknownView_ListsProjectionViews()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, HeapInspectionUseCases.HeapSnapshotKind, HeapSnapshot(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --view nonsense\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("unknown view 'nonsense'");
        stdout.Should().Contain("top-types");
        stdout.Should().Contain("gchandles");
    }

    [Fact]
    public async Task Query_CpuSampleHandle_CallTree_RendersFromTrace()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, "cpu-sample", CpuTrace(), TimeSpan.FromMinutes(10));

        var (exit, stdout, stderr) = await RunReplAsync(
            $"query --handle {handle.Id} --view call-tree\nexit\n", services);

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        stdout.Should().Contain("Root");
        stdout.Should().Contain("LeafA");
    }

    [Fact]
    public async Task Query_CpuSampleHandle_DefaultsToCallTree()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, "cpu-sample", CpuTrace(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id}\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("Root");
    }

    [Fact]
    public async Task Query_CpuSampleHandle_RootMethodFilter_ReRoots()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, "cpu-sample", CpuTrace(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --root-method-filter LeafA\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("LeafA");
    }

    [Fact]
    public async Task Query_AllocationSampleHandle_ResolvesWrappedTrace()
    {
        var (services, store) = BuildServices();
        var alloc = new AllocationSampleArtifact(
            new AllocationSample(Environment.ProcessId, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), 0, 0, Array.Empty<AllocatedType>(), Array.Empty<AllocatedType>()),
            CpuTrace());
        var handle = store.Register(Environment.ProcessId, "allocation-sample", alloc, TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id}\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("Root");
    }

    [Fact]
    public async Task Query_CpuSampleHandle_DiffView_ReturnsNotSupportedInSession()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, "cpu-sample", CpuTrace(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --view diff\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("NotSupportedInSession");
        stdout.Should().Contain("baseline");
    }

    [Fact]
    public async Task Query_CpuSampleHandle_UnknownView_ListsCallTree()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, "cpu-sample", CpuTrace(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --view nonsense\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("unknown view 'nonsense'");
        stdout.Should().Contain("call-tree");
    }

    [Fact]
    public async Task IdleCancellation_ReturnsOneThirty()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Pre-cancelled external token: the linked session CTS is already cancelled, so the loop never
        // reads a line and the graceful-cancel exit code is returned.
        var (exit, _, _) = await RunReplAsync("exit\n", ct: cts.Token);

        exit.Should().Be(130);
    }

    [Fact]
    public async Task Query_ActivitiesGcOverlayView_ReturnsNotSupportedInSession()
    {
        var (services, store) = BuildServices();
        // gc-overlay needs a correlated GC artifact the session can't supply; the dummy artifact is
        // never touched because the excluded-view check fires before dispatch.
        var handle = store.Register(Environment.ProcessId, CollectionHandleKinds.Activities, new object(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --view gc-overlay\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("not available in the session yet");
        stdout.Should().Contain("NotSupportedInSession");
    }

    [Fact]
    public void SessionViewsFor_ExcludesActivitiesGcOverlay_ButKeepsTheRest()
    {
        var all = CollectionQueryDispatcher.ViewsFor(CollectionHandleKinds.Activities);
        all.Should().Contain("gc-overlay", "the dispatcher itself still offers the correlated view");

        var sessionViews = CliCommands.SessionViewsFor(CollectionHandleKinds.Activities);

        sessionViews.Should().NotContain("gc-overlay");
        sessionViews.Should().Contain("summary");
        sessionViews.Should().Contain("bySource");
    }

    [Fact]
    public void SessionViewsFor_LeavesCountersViewsUntouched()
    {
        CliCommands.SessionViewsFor(CollectionHandleKinds.Counters)
            .Should().Equal("summary", "byProvider");
    }

    [Fact]
    public void SessionViewsFor_HeapKind_ReturnsProjectionViews()
    {
        var sessionViews = CliCommands.SessionViewsFor(HeapInspectionUseCases.HeapSnapshotKind);

        sessionViews.Should().Equal(HeapSnapshotQueryDispatcher.ProjectionViews);
        sessionViews.Should().Contain("top-types");
        // Server-only views never surface in the session-advertised list.
        sessionViews.Should().NotContain("object");
        sessionViews.Should().NotContain("duplicate-strings");
    }

    [Fact]
    public void SessionViewsFor_CpuSampleKinds_ReturnCallTree()
    {
        CliCommands.SessionViewsFor("cpu-sample").Should().Equal(CpuSampleQueryDispatcher.CallTreeView);
        CliCommands.SessionViewsFor("allocation-sample").Should().Equal(CpuSampleQueryDispatcher.CallTreeView);
        CliCommands.SessionViewsFor("native-alloc-sample").Should().Equal(CpuSampleQueryDispatcher.CallTreeView);
    }

    // --- Tokenizer --------------------------------------------------------------------------------

    [Fact]
    public void Tokenize_SplitsOnWhitespace()
    {
        SessionRepl.Tokenize("collect --kind gc --pid 1234")
            .Should().Equal("collect", "--kind", "gc", "--pid", "1234");
    }

    [Fact]
    public void Tokenize_HonoursDoubleQuotes_ForPathsWithSpaces()
    {
        SessionRepl.Tokenize("dump --out \"C:\\my dumps\" --confirm")
            .Should().Equal("dump", "--out", "C:\\my dumps", "--confirm");
    }

    [Fact]
    public void Tokenize_EmptyQuotedString_ProducesEmptyToken()
    {
        SessionRepl.Tokenize("get-bytes --asset \"\"")
            .Should().Equal("get-bytes", "--asset", string.Empty);
    }

    [Fact]
    public void Tokenize_CollapsesRepeatedWhitespace()
    {
        SessionRepl.Tokenize("  processes   ")
            .Should().Equal("processes");
    }

    // --- Helpers ----------------------------------------------------------------------------------

    private static (ServiceProvider Services, MemoryDiagnosticHandleStore Store) BuildServices()
    {
        var store = new MemoryDiagnosticHandleStore();
        var services = new ServiceCollection()
            .AddSingleton<IDiagnosticHandleStore>(store)
            .BuildServiceProvider();
        return (services, store);
    }

    private static DiagnosticHandle SeedCountersHandle(MemoryDiagnosticHandleStore store)
    {
        var snapshot = new CounterSnapshot(
            Environment.ProcessId,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(5),
            new[]
            {
                new CounterValue("System.Runtime", "cpu-usage", "CPU Usage", 12.5, CounterKind.Mean, "%"),
            },
            Array.Empty<MeterInstrumentValue>(),
            Array.Empty<string>());

        // Use the live test process id so the dead-PID sweep never evicts it mid-test.
        return store.Register(Environment.ProcessId, CollectionHandleKinds.Counters, snapshot, TimeSpan.FromMinutes(10));
    }

    private static HeapSnapshotArtifact HeapSnapshot() => new(
        Origin: HeapSnapshotOrigin.Live,
        ProcessId: Environment.ProcessId,
        CapturedAt: DateTimeOffset.UtcNow,
        WalkDuration: TimeSpan.FromMilliseconds(50),
        Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "X64", IsServerGC: false, HeapCount: 1),
        Heap: new DumpHeapSummary(1024, 0, 0, 1024, 0, 0, 1024),
        TopTypesByBytes: new[] { new TypeStat("System.String", "System.Private.CoreLib", 100, 4096, 40.0) },
        TopTypesByInstances: new[] { new TypeStat("System.String", "System.Private.CoreLib", 100, 4096, 40.0) });

    private static CpuSampleTraceArtifact CpuTrace()
    {
        var leafA = new CallTreeNode(new SampledFrame("App.dll", "LeafA"), 40, 40, Array.Empty<CallTreeNode>());
        var leafB = new CallTreeNode(new SampledFrame("App.dll", "LeafB"), 60, 60, Array.Empty<CallTreeNode>());
        var root = new CallTreeNode(new SampledFrame("App.dll", "Root"), 100, 0, new[] { leafA, leafB });
        return new CpuSampleTraceArtifact(Environment.ProcessId, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5), 100, root);
    }

    private static Task<(int Exit, string Stdout, string Stderr)> RunReplAsync(
        string input, CancellationToken ct = default)
    {
        var (services, _) = BuildServices();
        return RunReplAsync(input, services, ct);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunReplAsync(
        string input, IServiceProvider services, CancellationToken ct = default)
    {
        var defaultRoot = Path.Combine(Path.GetTempPath(), "session-repl-test-" + Guid.NewGuid().ToString("N"));
        var provider = new MutableArtifactRootProvider(defaultRoot);
        using var stdin = new StringReader(input);
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        try
        {
            var exit = await SessionRepl.RunAsync(services, provider, stdin, stdout, stderr, ct);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            if (Directory.Exists(defaultRoot))
            {
                Directory.Delete(defaultRoot, recursive: true);
            }
        }
    }
}
