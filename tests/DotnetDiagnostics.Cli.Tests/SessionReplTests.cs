using System.Text;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.UseCases;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli.Tests;

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
    public async Task Query_UnsupportedKindHandle_ReturnsNotSupportedInSession()
    {
        var (services, store) = BuildServices();
        // A handle kind with no session drill-down routing (heap/cpu/thread/off-cpu and the collection
        // kinds are all handled) falls through to the defensive NotSupportedInSession guard. The dummy
        // artifact is never touched because the empty-views check fires before any dispatch.
        var handle = store.Register(Environment.ProcessId, "process-dump", new object(), TimeSpan.FromMinutes(10));

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
    public async Task Query_CpuSampleHandle_TopMethodsView_RanksByExclusive()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, "cpu-sample", CpuTrace(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --view top-methods --top 1\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("LeafB"); // 60 exclusive beats LeafA's 40
    }

    [Fact]
    public async Task Query_CpuSampleHandle_HotPathView_FollowsDominantChild()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, "cpu-sample", CpuTrace(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --view hot-path\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("LeafB");
    }

    [Fact]
    public async Task Query_CpuSampleHandle_CallerCalleeView_ResolvesFocusMethod()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, "cpu-sample", CpuTrace(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --view caller-callee --root-method-filter LeafA\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("LeafA");
        stdout.Should().Contain("Root"); // the caller
    }

    [Fact]
    public async Task Query_ThreadSnapshotHandle_DefaultsToTopBlocked()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, "thread-snapshot", ThreadSnapshot(), TimeSpan.FromMinutes(10));

        var (exit, stdout, stderr) = await RunReplAsync(
            $"query --handle {handle.Id}\nexit\n", services);

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        stdout.Should().Contain("top-blocked");
    }

    [Fact]
    public async Task Query_ThreadSnapshotHandle_StackView_RequiresThreadId()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, "thread-snapshot", ThreadSnapshot(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --view stack\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("InvalidArgument");
        stdout.Should().Contain("threadId");
    }

    [Fact]
    public async Task Query_ThreadSnapshotHandle_StackView_RendersSelectedThread()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, "thread-snapshot", ThreadSnapshot(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --view stack --thread-id 1\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("\"stack\"");
        stdout.Should().Contain("GroupA.Leaf");
    }

    [Fact]
    public async Task Query_ThreadSnapshotHandle_UnknownView_ListsValidViews()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, "thread-snapshot", ThreadSnapshot(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --view nonsense\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("InvalidArgument");
        stdout.Should().Contain("threads-summary");
    }

    [Fact]
    public async Task Query_OffCpuHandle_DefaultsToTopStacks()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, "off-cpu-snapshot", OffCpuSnapshot(), TimeSpan.FromMinutes(10));

        var (exit, stdout, stderr) = await RunReplAsync(
            $"query --handle {handle.Id}\nexit\n", services);

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        stdout.Should().Contain("topStacks");
        stdout.Should().Contain("LeafA");
    }

    [Fact]
    public async Task Query_OffCpuHandle_StackView_ReturnsRequestedRank()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, "off-cpu-snapshot", OffCpuSnapshot(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --view stack --stack-rank 2\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("LeafB");
    }

    [Fact]
    public async Task Query_OffCpuHandle_UnknownView_ListsValidViews()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, "off-cpu-snapshot", OffCpuSnapshot(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --view nonsense\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("unknown view 'nonsense'");
        stdout.Should().Contain("byThread");
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
    public async Task Query_GcHandle_ByGenerationView_RoutesThroughSession()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, CollectionHandleKinds.GcEvents, GcSummaryArtifact(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --view byGeneration\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("view=byGeneration");
        stdout.Should().Contain("background");
    }

    [Fact]
    public async Task Query_GcHandle_LongestPausesView_RanksByPause()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, CollectionHandleKinds.GcEvents, GcSummaryArtifact(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --view longestPauses --top-types 1\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("view=longestPauses");
    }

    [Fact]
    public void SessionViewsFor_GcEvents_IncludesNewDrilldownViews()
    {
        var sessionViews = CliCommands.SessionViewsFor(CollectionHandleKinds.GcEvents);

        sessionViews.Should().Contain(new[] { "summary", "events", "pauseHistogram", "timeline", "longestPauses", "byGeneration" });
    }

    [Fact]
    public async Task Query_EventCatalogHandle_CatalogView_RoutesThroughSession()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, CollectionHandleKinds.EventCatalog, EventCatalogArtifact(), TimeSpan.FromMinutes(10));

        var (exit, stdout, stderr) = await RunReplAsync(
            $"query --handle {handle.Id} --view catalog --provider-filter AspNet --root-method-filter Start\nexit\n", services);

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        stdout.Should().Contain("RequestStart");
        stdout.Should().NotContain("GcStart");
    }

    [Fact]
    public async Task Query_EventCatalogHandle_ByProviderView_RoutesThroughSession()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, CollectionHandleKinds.EventCatalog, EventCatalogArtifact(), TimeSpan.FromMinutes(10));

        var (exit, stdout, stderr) = await RunReplAsync(
            $"query --handle {handle.Id} --view byProvider\nexit\n", services);

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        stdout.Should().Contain("Busiest: Microsoft.AspNetCore.Hosting");
        stdout.Should().Contain("Microsoft-Windows-DotNETRuntime");
    }

    [Fact]
    public void SessionViewsFor_EventCatalog_ReturnsDedicatedViews()
    {
        CliCommands.SessionViewsFor(CollectionHandleKinds.EventCatalog)
            .Should().Equal(EventCatalogQueryDispatcher.SessionViews);
    }

    [Fact]
    public async Task Query_GcDatasHandle_DefaultsToOverview()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, CollectionHandleKinds.GcDatas, GcDatasArtifact(), TimeSpan.FromMinutes(10));

        var (exit, stdout, stderr) = await RunReplAsync(
            $"query --handle {handle.Id}\nexit\n", services);

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        stdout.Should().Contain("\"heapCountChanges\"");
    }

    [Fact]
    public async Task Query_GcDatasHandle_TuningChangesOnly_RoutesThroughSession()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, CollectionHandleKinds.GcDatas, GcDatasArtifact(), TimeSpan.FromMinutes(10));

        var (exit, stdout, stderr) = await RunReplAsync(
            $"query --handle {handle.Id} --view tuning --changes-only\nexit\n", services);

        exit.Should().Be(0);
        stderr.Should().BeEmpty();
        stdout.Should().Contain("\"changesOnly\": true");
    }

    [Fact]
    public async Task Query_GcDatasHandle_UnknownView_ListsValidViews()
    {
        var (services, store) = BuildServices();
        var handle = store.Register(Environment.ProcessId, CollectionHandleKinds.GcDatas, GcDatasArtifact(), TimeSpan.FromMinutes(10));

        var (exit, stdout, _) = await RunReplAsync(
            $"query --handle {handle.Id} --view bogus\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("overview").And.Contain("tuning");
    }

    [Fact]
    public void SessionViewsFor_GcDatas_ReturnsDedicatedViews()
    {
        CliCommands.SessionViewsFor(CollectionHandleKinds.GcDatas)
            .Should().Equal(Core.Gc.GcDatasQueryDispatcher.SessionViews);
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
    public void SessionViewsFor_CpuSampleKinds_ReturnAnalyticsViews()
    {
        CliCommands.SessionViewsFor("cpu-sample").Should().Equal(CpuSampleQueryDispatcher.SessionViews);
        CliCommands.SessionViewsFor("allocation-sample").Should().Equal(CpuSampleQueryDispatcher.SessionViews);
        CliCommands.SessionViewsFor("native-alloc-sample").Should().Equal(CpuSampleQueryDispatcher.SessionViews);

        var views = CliCommands.SessionViewsFor("cpu-sample");
        views.Should().Contain(CpuSampleQueryDispatcher.CallTreeView);
        views.Should().Contain("top-methods");
        views.Should().Contain("by-module");
        views.Should().Contain("by-namespace");
        views.Should().Contain("hot-path");
        views.Should().Contain("caller-callee");
    }

    [Fact]
    public void SessionViewsFor_ThreadSnapshotKind_ReturnsAllEightViews()
    {
        CliCommands.SessionViewsFor("thread-snapshot")
            .Should().Equal(ThreadSnapshotQueryDispatcher.SessionViews);
    }

    [Fact]
    public void SessionViewsFor_OffCpuKind_ReturnsThreeViews()
    {
        CliCommands.SessionViewsFor("off-cpu-snapshot")
            .Should().Equal(OffCpuQueryDispatcher.SessionViews);
    }

    // --- Session-target binding (strand C) --------------------------------------------------------

    [Fact]
    public async Task Target_WhenUnbound_ReportsNoBinding()
    {
        var (exit, stdout, stderr) = await RunReplAsync("target\nexit\n");

        exit.Should().Be(0);
        stdout.Should().Contain("No target bound");
        stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task Target_BindsPid_ConfirmsAndPromptReflectsIt()
    {
        var (exit, stdout, stderr) = await RunReplAsync("target 1234\nexit\n");

        exit.Should().Be(0);
        stdout.Should().Contain("Target bound to pid 1234");
        stdout.Should().Contain("diag(pid 1234)>");
        stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task Target_PidFlagForm_BindsPid()
    {
        var (exit, stdout, _) = await RunReplAsync("target --pid 1234\nexit\n");

        exit.Should().Be(0);
        stdout.Should().Contain("Target bound to pid 1234");
    }

    [Fact]
    public async Task Target_ShowsBinding_AfterBind()
    {
        var (exit, stdout, _) = await RunReplAsync("target 4321\ntarget\nexit\n");

        exit.Should().Be(0);
        stdout.Should().Contain("Target bound to pid 4321.");
    }

    [Fact]
    public async Task Target_Clear_UnbindsAndRestoresPrompt()
    {
        var (exit, stdout, _) = await RunReplAsync("target 1234\ntarget clear\nexit\n");

        exit.Should().Be(0);
        stdout.Should().Contain("Target cleared.");
        // After clearing, the final idle prompt must revert to the bare prompt (no bound pid).
        stdout.Should().EndWith("diag> ");
    }

    [Theory]
    [InlineData("target 0")]
    [InlineData("target -1")]
    [InlineData("target abc")]
    [InlineData("target 1234 5678")]
    [InlineData("target --pid abc")]
    [InlineData("target --pid")]
    [InlineData("target --pid clear")]
    public async Task Target_InvalidArgument_PrintsUsageError(string command)
    {
        var (exit, _, stderr) = await RunReplAsync(command + "\nexit\n");

        exit.Should().Be(0);
        stderr.Should().Contain("Usage: target <pid>");
    }

    [Fact]
    public async Task Use_IsAnAliasForTarget()
    {
        var (exit, stdout, _) = await RunReplAsync("use 777\nexit\n");

        exit.Should().Be(0);
        stdout.Should().Contain("Target bound to pid 777");
    }

    [Fact]
    public async Task BoundTarget_IsInheritedByLiveTargetCommand_WhenPidOmitted()
    {
        var (services, resolver) = BuildCapabilityServices();

        var (exit, stdout, _) = await RunReplAsync("target 4321\ncapabilities\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().Contain("using bound target pid 4321");
        resolver.LastRequestedPid.Should().Be(4321);
    }

    [Fact]
    public async Task ExplicitPid_OverridesBoundTarget_AndSuppressesNote()
    {
        var (services, resolver) = BuildCapabilityServices();

        var (exit, stdout, _) = await RunReplAsync("target 4321\ncapabilities --pid 9999\nexit\n", services);

        exit.Should().Be(0);
        stdout.Should().NotContain("using bound target");
        resolver.LastRequestedPid.Should().Be(9999);
    }

    [Theory]
    [InlineData("capabilities", true)]
    [InlineData("collect --kind gc", true)]
    [InlineData("dump --confirm", true)]
    [InlineData("inspect-heap", true)]
    [InlineData("inspect-heap --source live", true)]
    [InlineData("inspect-heap --source dump --dump-file x.dmp", false)]
    [InlineData("get-bytes --kind module --mvid abc --asset pe", true)]
    [InlineData("get-bytes --kind dump --dump-file x.dmp", false)]
    [InlineData("processes", false)]
    [InlineData("query --handle x --view y", false)]
    public void ShouldInheritTarget_GatesPerCommand(string command, bool expected)
    {
        var options = CliOptions.Parse(SessionRepl.Tokenize(command), out var error);
        error.Should().BeNull();

        SessionRepl.ShouldInheritTarget(options!).Should().Be(expected);
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

    /// <summary>
    /// Builds a service provider wired with a <see cref="RecordingProcessContextResolver"/> so a
    /// <c>capabilities</c> command runs to a clean (failure) envelope without a live target while
    /// capturing the process id it was asked to resolve — proving the session-bound target reached
    /// the use case (issue #300, strand C).
    /// </summary>
    private static (ServiceProvider Services, RecordingProcessContextResolver Resolver) BuildCapabilityServices()
    {
        var resolver = new RecordingProcessContextResolver();
        var services = new ServiceCollection()
            .AddSingleton<IDiagnosticHandleStore>(new MemoryDiagnosticHandleStore())
            .AddSingleton<IProcessContextResolver>(resolver)
            .AddSingleton<ICapabilityDetector>(new ThrowingCapabilityDetector())
            .BuildServiceProvider();
        return (services, resolver);
    }

    /// <summary>
    /// Captures the requested pid and always returns a structured resolution failure, so the
    /// capabilities use case short-circuits before touching the (unused) detector.
    /// </summary>
    private sealed class RecordingProcessContextResolver : IProcessContextResolver
    {
        public int? LastRequestedPid { get; private set; }

        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken)
        {
            LastRequestedPid = requestedProcessId;
            return Task.FromResult(new ProcessContextResolution(
                Context: null,
                Error: new DiagnosticError("NotFound", "no process (test stub)"),
                Candidates: null));
        }
    }

    private sealed class ThrowingCapabilityDetector : ICapabilityDetector
    {
        public Task<DiagnosticCapabilities> DetectAsync(int processId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("detector should not be reached on the failure-resolution path");
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

    private static Core.Gc.GcSummary GcSummaryArtifact()
    {
        var at = DateTimeOffset.UtcNow;
        var events = new List<Core.Gc.GcEvent>
        {
            new(at.AddMilliseconds(0), 0, "AllocSmall", "NonConcurrentGC", TimeSpan.FromMilliseconds(2)),
            new(at.AddMilliseconds(10), 2, "AllocSmall", "BackgroundGC", TimeSpan.FromMilliseconds(50)),
            new(at.AddMilliseconds(30), 2, "AllocLarge", "NonConcurrentGC", TimeSpan.FromMilliseconds(100)),
        };
        return new Core.Gc.GcSummary(
            Environment.ProcessId, at, TimeSpan.FromSeconds(5), events.Count,
            TimeSpan.FromMilliseconds(152), TimeSpan.FromMilliseconds(100),
            new List<Core.Gc.GenerationStats> { new(0, 1), new(2, 2) },
            events);
    }

    private static Core.Gc.GcDatasSnapshot GcDatasArtifact()
    {
        var at = DateTimeOffset.UtcNow;
        var samples = new List<Core.Gc.DatasSampleEvent>
        {
            new(at.AddMilliseconds(0), 1, 1000, 100, 0, 0, 2UL * 1024 * 1024, 1024 * 1024),
            new(at.AddMilliseconds(20), 2, 1000, 120, 0, 0, 2UL * 1024 * 1024, 1024 * 1024),
        };
        var tuning = new List<Core.Gc.DatasTuningEvent>
        {
            new(at.AddMilliseconds(0), 4, 16, 1, 1, 1000, 1.0f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            new(at.AddMilliseconds(20), 8, 16, 1, 2, 1000, 1.5f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        };
        var gen2 = new List<Core.Gc.DatasFullGcTuningEvent>
        {
            new(at.AddMilliseconds(10), 8, 1, 1.0f, 0, 0, 0, 0, 0, 0, 0),
        };
        return new Core.Gc.GcDatasSnapshot(
            Environment.ProcessId, at, TimeSpan.FromSeconds(15),
            samples, tuning, gen2, new Core.Gc.DatasParseStats(0, 0, 0));
    }

    private static EventCatalogSnapshot EventCatalogArtifact()
    {
        var at = DateTimeOffset.UtcNow;
        var catalog = new List<EventCatalogEntry>
        {
            new("Microsoft.AspNetCore.Hosting", "RequestStart", "Informational", 5),
            new("Microsoft.AspNetCore.Hosting", "RequestStop", "Informational", 3),
            new("Microsoft-Windows-DotNETRuntime", "GcStart", "Informational", 2),
        };
        var sample = new List<CatalogEventOccurrence>
        {
            new(at.AddMilliseconds(1), "Microsoft.AspNetCore.Hosting", "RequestStart", "Informational"),
            new(at.AddMilliseconds(2), "Microsoft-Windows-DotNETRuntime", "GcStart", "Informational"),
        };

        return new EventCatalogSnapshot(
            Environment.ProcessId,
            at,
            TimeSpan.FromSeconds(5),
            new[] { "Microsoft.AspNetCore.Hosting", "Microsoft-Windows-DotNETRuntime" },
            10,
            catalog.Count,
            catalog,
            10,
            sample);
    }

    private static ThreadSnapshotArtifact ThreadSnapshot()
    {
        var frames = new[]
        {
            new ManagedStackFrame("ManagedMethod", "GroupA.Leaf", "GroupA.Type", "App.dll", 0x1000, 0x2000),
            new ManagedStackFrame("ManagedMethod", "GroupA.Root", "GroupA.Type", "App.dll", 0x1010, 0x2010),
        };
        var thread = new ManagedThread(
            ManagedThreadId: 1,
            OSThreadId: 10_001,
            Address: 1,
            State: "Wait",
            IsAlive: true,
            IsBackground: false,
            IsFinalizer: false,
            IsGc: false,
            IsThreadpoolWorker: true,
            LockCount: 0,
            CurrentExceptionType: null,
            TopFrameMethod: frames[0].DisplayName,
            Frames: frames)
        {
            IsLikelyBlocked = true,
            InferredWaitReason = "Monitor.Wait",
        };
        return new ThreadSnapshotArtifact(
            Origin: ThreadSnapshotOrigin.Live,
            ProcessId: Environment.ProcessId,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(25),
            RuntimeName: "CoreClr",
            RuntimeVersion: "10.0.0",
            Threads: new[] { thread },
            Locks: Array.Empty<MonitorLockState>())
        {
            Source = "clrmd-thread-walk",
        };
    }

    private static OffCpuSnapshotArtifact OffCpuSnapshot()
    {
        var stackA = new OffCpuStackHotspot("LeafA", 1200, 3, "Sleeping",
            new[] { new OffCpuFrame("App.dll", "LeafA"), new OffCpuFrame("App.dll", "RootA") });
        var stackB = new OffCpuStackHotspot("LeafB", 800, 2, "Waiting",
            new[] { new OffCpuFrame("App.dll", "LeafB"), new OffCpuFrame("App.dll", "RootB") });
        return new OffCpuSnapshotArtifact(
            ProcessId: Environment.ProcessId,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(5),
            TotalOffCpuMicros: 2000,
            SchedSwitches: 5,
            Stacks: new[] { stackA, stackB },
            Threads: new[] { new OffCpuThreadView(101, "worker-1", 1200, 3, "LeafA") },
            SymbolSource: "user+kernel");
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
