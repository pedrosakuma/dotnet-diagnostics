using System.Collections.Immutable;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Evidence;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Unit coverage for the host-neutral <see cref="HeapSnapshotQueryDispatcher"/> shared by the MCP
/// server's <c>query_heap_snapshot</c> tool and the CLI <c>session</c> REPL (#300). Asserts the
/// projection views render from a walked snapshot, that the four capability-bound views are reported
/// as <c>ServerOnlyView</c>, and that argument / view validation matches the server preamble.
/// </summary>
public class HeapSnapshotQueryDispatcherTests
{
    private const string Handle = "heap-abc";

    [Theory]
    [InlineData("top-types")]
    [InlineData("retention-paths")]
    [InlineData("roots-by-kind")]
    [InlineData("finalizer-queue")]
    [InlineData("fragmentation")]
    [InlineData("static-fields")]
    [InlineData("delegate-targets")]
    [InlineData("gchandles")]
    [InlineData("async")]
    [InlineData("timers")]
    [InlineData("alc")]
    [InlineData("com-wrappers")]
    [InlineData("heap-integrity")]
    public void ProjectionViews_RenderResult(string view)
    {
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(Snapshot(), Handle, view, topN: 10, rankBy: "bytes", typeFullName: null);

        outcome.ServerOnlyView.Should().BeFalse();
        outcome.UnknownView.Should().BeFalse();
        outcome.Result.Should().NotBeNull();
        outcome.Result!.Error.Should().BeNull();
        outcome.Result.Data!.View.Should().Be(view);
    }

    [Fact]
    public void TopTypes_RanksByInstances_WhenRequested()
    {
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(Snapshot(), Handle, "top-types", topN: 10, rankBy: "instances", typeFullName: null);

        outcome.Result!.Data!.RankBy.Should().Be("instances");
        outcome.Result.Data.TopTypes.Should().NotBeEmpty();
    }

    [Fact]
    public void TopTypes_NullRankBy_DefaultsToBytes()
    {
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(Snapshot(), Handle, "top-types", topN: 10, rankBy: null, typeFullName: null);

        outcome.Result!.Error.Should().BeNull();
        outcome.Result.Data!.RankBy.Should().Be("bytes");
    }

    [Fact]
    public void TopTypes_ClrMdProducerShape_DoesNotFabricateQualityForProjection()
    {
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(
            Snapshot(),
            Handle,
            "top-types",
            topN: 1,
            rankBy: "bytes",
            typeFullName: null);

        outcome.Result!.Data!.TopTypes.Should().ContainSingle();
        outcome.Result.Data.Quality.Should().BeNull();
    }

    [Fact]
    public void TopTypes_PreservesQualityAndAddsResponseProjection()
    {
        var snapshot = Snapshot() with
        {
            Quality = new EvidenceQuality(
                EvidenceQuality.SchemaV1,
                [new EvidenceLimitation(EvidenceLimitationCategory.MechanismUnobservable, "eventpipe-loss", null, "unknown")],
                new EvidenceConclusionPolicy(
                    EvidenceConclusionSupport.Supported,
                    EvidenceConclusionSupport.Inconclusive,
                    EvidenceConclusionSupport.Inconclusive)),
            TopTypesByBytes =
            [
                new TypeStat("A", null, 3, 300, 50),
                new TypeStat("B", null, 2, 200, 33),
                new TypeStat("C", null, 1, 100, 17),
            ],
        };

        var outcome = HeapSnapshotQueryDispatcher.Dispatch(snapshot, Handle, "top-types", topN: 1, rankBy: "bytes", typeFullName: null);

        outcome.Result!.Data!.Quality!.Limitations.Should().Contain(l =>
            l.Category == EvidenceLimitationCategory.MechanismUnobservable && l.Scope == "eventpipe-loss");
        outcome.Result.Data.Quality.Limitations.Should().Contain(l =>
            l.Category == EvidenceLimitationCategory.OutputProjection
            && l.Scope == "query-top-types-bytes"
            && l.AffectedCount == 2);
    }

    [Fact]
    public void TopTypes_BadRankBy_ReturnsInvalidArgument()
    {
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(Snapshot(), Handle, "top-types", topN: 10, rankBy: "nonsense", typeFullName: null);

        outcome.Result.Should().NotBeNull();
        outcome.Result!.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public void View_IsNormalized_TrimAndCase()
    {
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(Snapshot(), Handle, "  TOP-TYPES  ", topN: 10, rankBy: "bytes", typeFullName: null);

        outcome.Result.Should().NotBeNull();
        outcome.Result!.Data!.View.Should().Be("top-types");
    }

    [Theory]
    [InlineData("object")]
    [InlineData("gcroot")]
    [InlineData("objsize")]
    [InlineData("duplicate-strings")]
    public void ServerOnlyViews_AreReportedNotRendered(string view)
    {
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(Snapshot(), Handle, view, topN: 10, rankBy: "bytes", typeFullName: null);

        outcome.ServerOnlyView.Should().BeTrue();
        outcome.UnknownView.Should().BeFalse();
        outcome.Result.Should().BeNull();
    }

    [Fact]
    public void UnknownView_IsReported()
    {
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(Snapshot(), Handle, "nope", topN: 10, rankBy: "bytes", typeFullName: null);

        outcome.UnknownView.Should().BeTrue();
        outcome.ServerOnlyView.Should().BeFalse();
        outcome.Result.Should().BeNull();
    }

    [Fact]
    public void TopNBelowOne_ReturnsInvalidArgument()
    {
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(Snapshot(), Handle, "top-types", topN: 0, rankBy: "bytes", typeFullName: null);

        outcome.Result.Should().NotBeNull();
        outcome.Result!.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public void ServerOnlyView_TakesPrecedenceOverTopNGuard()
    {
        // Server preamble validates topN before computing the view, so the dispatcher must NOT
        // pre-empt a server-only routing decision with its own topN guard.
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(Snapshot(), Handle, "object", topN: 0, rankBy: "bytes", typeFullName: null);

        outcome.ServerOnlyView.Should().BeTrue();
        outcome.Result.Should().BeNull();
    }

    [Fact]
    public void ProjectionViews_ExposesThirteenViews_WithoutServerOnly()
    {
        HeapSnapshotQueryDispatcher.ProjectionViews.Should().HaveCount(13);
        HeapSnapshotQueryDispatcher.ProjectionViews.Should().Contain("com-wrappers");
        HeapSnapshotQueryDispatcher.ProjectionViews.Should().Contain("heap-integrity");
        HeapSnapshotQueryDispatcher.ProjectionViews.Should().NotContain("object");
        HeapSnapshotQueryDispatcher.ProjectionViews.Should().NotContain("duplicate-strings");
    }

    [Fact]
    public void GcDumpClrMdOnlyView_IsExplicitlyUnavailableWithQuality()
    {
        var snapshot = Snapshot() with
        {
            Origin = HeapSnapshotOrigin.GcDump,
            RetentionPaths = null,
        };

        var outcome = HeapSnapshotQueryDispatcher.Dispatch(
            snapshot,
            Handle,
            "retention-paths",
            topN: 10,
            rankBy: null,
            typeFullName: null);

        outcome.Result!.Error!.Kind.Should().Be("ViewUnavailableForGcDump");
        outcome.Result.Data.Should().NotBeNull();
        outcome.Result.Data!.Quality.Should().NotBeNull();
        outcome.Result.Data.Quality!.Limitations.Should().Contain(
            limitation => limitation.Category == EvidenceLimitationCategory.LegacyUnknown);
    }

    [Fact]
    public void HeapIntegrity_NotCaptured_ReturnsViewNotCaptured()
    {
        var snapshot = Snapshot() with { HeapIntegrity = null };

        var outcome = HeapSnapshotQueryDispatcher.Dispatch(snapshot, Handle, "heap-integrity", topN: 10, rankBy: null, typeFullName: null);

        outcome.ServerOnlyView.Should().BeFalse();
        outcome.UnknownView.Should().BeFalse();
        outcome.Result!.Error!.Kind.Should().Be("ViewNotCaptured");
        outcome.Result.Hints.Should().ContainSingle(h => h.NextTool == "inspect_heap");
    }

    [Fact]
    public void HeapIntegrity_NotCaptured_ForLiveOrigin_DoesNotSuggestLiveRecapture()
    {
        var snapshot = Snapshot() with { HeapIntegrity = null, Origin = HeapSnapshotOrigin.Live };

        var outcome = HeapSnapshotQueryDispatcher.Dispatch(snapshot, Handle, "heap-integrity", topN: 10, rankBy: null, typeFullName: null);

        outcome.Result!.Error!.Kind.Should().Be("ViewNotCaptured");
        outcome.Result.Error.Message.Should().Contain("dump-only");
        outcome.Result.Hints.Single().SuggestedArguments.Should().BeNull();
    }

    [Fact]
    public void HeapIntegrity_ZeroCorruptions_RendersHealthySummary()
    {
        var snapshot = Snapshot() with { HeapIntegrity = new HeapIntegrityView(0, Array.Empty<HeapCorruptionStat>(), Array.Empty<string>()) };

        var outcome = HeapSnapshotQueryDispatcher.Dispatch(snapshot, Handle, "heap-integrity", topN: 10, rankBy: null, typeFullName: null);

        outcome.Result!.Error.Should().BeNull();
        outcome.Result.Data!.HeapIntegrity!.TotalCorruptions.Should().Be(0);
        outcome.Result.Summary.Should().Contain("zero corrupted objects");
    }

    [Fact]
    public void HeapIntegrity_WithCorruption_RendersSummary()
    {
        var corruption = new HeapCorruptionStat(0x1000, "MyApp.Leaked", 8, "InvalidMethodTable", 0, 0);
        var view = HeapIntegrityAggregation.Build(new[] { corruption }, totalObserved: 1);
        var snapshot = Snapshot() with { HeapIntegrity = view };

        var outcome = HeapSnapshotQueryDispatcher.Dispatch(snapshot, Handle, "heap-integrity", topN: 10, rankBy: null, typeFullName: null);

        outcome.Result!.Error.Should().BeNull();
        outcome.Result.Data!.HeapIntegrity!.TotalCorruptions.Should().Be(1);
        outcome.Result.Summary.Should().Contain("InvalidMethodTable").And.Contain("MyApp.Leaked");
    }

    [Fact]
    public void HeapIntegrity_IncompletePass_NeverRendersAsHealthy_EvenWithZeroObservedCorruptions()
    {
        // Regression (code review, #1119): a VerifyHeap() enumeration that fails before finding
        // any corruption must not be indistinguishable from an actually-clean heap.
        var view = HeapIntegrityAggregation.Build(
            Array.Empty<HeapCorruptionStat>(),
            totalObserved: 0,
            failureMessage: "ClrHeap.VerifyHeap() failed partway through (boom); verification did not complete.");
        var snapshot = Snapshot() with { HeapIntegrity = view };

        view.Completed.Should().BeFalse();

        var outcome = HeapSnapshotQueryDispatcher.Dispatch(snapshot, Handle, "heap-integrity", topN: 10, rankBy: null, typeFullName: null);

        outcome.Result!.Error.Should().BeNull();
        outcome.Result.Summary.Should().NotContain("passed ClrHeap.VerifyHeap()");
        outcome.Result.Summary.Should().Contain("did NOT complete");
        outcome.Result.Data!.HeapIntegrity!.Notes.Should().ContainSingle(n => n.Contains("failed partway through"));
    }

    [Fact]
    public void HeapIntegrityAggregation_Build_UnderCap_ReportsNoNotes()
    {
        var captured = Enumerable.Range(0, 10)
            .Select(i => new HeapCorruptionStat((ulong)i, "T", 0, "ObjectTooLarge", 0, 0))
            .ToArray();

        var view = HeapIntegrityAggregation.Build(captured, totalObserved: 10);

        view.TotalCorruptions.Should().Be(10);
        view.Corruptions.Should().HaveCount(10);
        view.Truncated.Should().BeFalse();
        view.Notes.Should().BeEmpty();
    }

    [Fact]
    public void HeapIntegrityAggregation_Build_OverCap_ReportsTruncationAndNotes()
    {
        const int cap = HeapIntegrityAggregation.MaxCapturedCorruptions;
        var captured = Enumerable.Range(0, cap)
            .Select(i => new HeapCorruptionStat((ulong)i, "T", 0, "ObjectTooLarge", 0, 0))
            .ToArray();
        const int totalObserved = cap + 137;

        var view = HeapIntegrityAggregation.Build(captured, totalObserved);

        view.TotalCorruptions.Should().Be(totalObserved);
        view.Corruptions.Should().HaveCount(cap);
        view.Truncated.Should().BeTrue();
        view.Notes.Should().ContainSingle();
        view.Notes[0].Should().Contain($"MaxCapturedCorruptions={cap}").And.Contain("137");
    }

    [Fact]
    public void HeapIntegrityAggregation_Build_WithFailureMessage_MarksIncompleteEvenWithZeroObserved()
    {
        var view = HeapIntegrityAggregation.Build(
            Array.Empty<HeapCorruptionStat>(),
            totalObserved: 0,
            failureMessage: "ClrHeap.VerifyHeap() failed partway through (boom).");

        view.Completed.Should().BeFalse();
        view.TotalCorruptions.Should().Be(0);
        view.Notes.Should().ContainSingle().Which.Should().Contain("failed partway through");
    }

    [Fact]
    public void HeapIntegrityAggregation_Build_WithFailureMessageAndCapOverflow_ReportsBothNotes()
    {
        const int cap = HeapIntegrityAggregation.MaxCapturedCorruptions;
        var captured = Enumerable.Range(0, cap)
            .Select(i => new HeapCorruptionStat((ulong)i, "T", 0, "ObjectTooLarge", 0, 0))
            .ToArray();

        var view = HeapIntegrityAggregation.Build(
            captured,
            totalObserved: cap + 5,
            failureMessage: "ClrHeap.VerifyHeap() failed partway through (boom).");

        view.Completed.Should().BeFalse();
        view.Notes.Should().HaveCount(2);
        view.Notes[0].Should().Contain("MaxCapturedCorruptions");
        view.Notes[1].Should().Contain("failed partway through");
    }

    private static HeapSnapshotArtifact Snapshot() => new(
        Origin: HeapSnapshotOrigin.Live,
        ProcessId: 123,
        CapturedAt: DateTimeOffset.UtcNow,
        WalkDuration: TimeSpan.FromMilliseconds(50),
        Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "X64", IsServerGC: false, HeapCount: 1),
        Heap: new DumpHeapSummary(1024, 0, 0, 1024, 0, 0, 1024),
        TopTypesByBytes: new[] { new TypeStat("System.String", "System.Private.CoreLib", 100, 4096, 40.0) },
        TopTypesByInstances: new[] { new TypeStat("System.Byte[]", "System.Private.CoreLib", 200, 2048, 20.0) })
    {
        RetentionPaths = new[] { new RetentionPath("System.String", 0x1000, new[] { new RetentionFrame("System.String", 0x1000) }, Truncated: false) },
        RootsByKind = new[] { new RootKindStat("StaticVar", 5, 5, 4096, 0, 0) },
        FinalizableObjectsByType = new[] { new FinalizableTypeStat("System.IO.FileStream", null, 3, 384) },
        Segments = new[] { new SegmentStat(0, "Gen2", "Gen2", 0x1000, 0x2000, 4096, 4096, 0, 2048, 2048, 10, 1) { FreePercent = 50.0 } },
        StaticFields = new[] { new StaticFieldStat("MyType", null, "Cache", 0x0A000001, 0x2000, "System.Collections.Generic.Dictionary`2", 4096, 1) },
        DelegateTargets = new[] { new DelegateTargetStat("Subscriber", "Publisher", "OnChanged", null, null, 7) },
        GcHandles = new GcHandlesView(2, ImmutableArray.Create(new GcHandleBucket("Strong", 2, 4096, ImmutableArray<GcHandleTypeStat>.Empty)), ImmutableArray<string>.Empty),
        AsyncOperations = new[] { new AsyncOperationStat("MyAsyncStateMachine", 0, "TaskAwaiter", 128) },
        Timers = new TaskTimerLeakView(1, 2, 1, [new TimerCallbackStat("System.Threading.TimerQueueTimer", null, "MyTimer", "Tick", null, 1)], [new TaskTypeStat("System.Threading.Tasks.Task", "System.Private.CoreLib", 2, 128)], [new TaskTypeStat("System.Threading.Tasks.TaskCompletionSource", "System.Private.CoreLib", 1, 64)], []),
        AssemblyLoadContexts = new AssemblyLoadContextLeakView(
            TotalContexts: 1,
            CollectibleContexts: 1,
            SuspectedLeakedCollectibleContexts: 1,
            Contexts:
            [
                new AssemblyLoadContextStat(
                    Address: 0x5000,
                    TypeFullName: "System.Runtime.Loader.AssemblyLoadContext",
                    Name: "Plugin",
                    IsCollectible: true,
                    IsDefault: false,
                    AssemblyCount: 1,
                    Assemblies: [new AssemblyLoadContextAssemblyStat("Plugin", "Plugin.dll", "/app/Plugin.dll", 3, 256)])
                {
                    SuspectedLeak = true,
                    RetentionTargetKind = "sample-object-from-alc",
                    RetentionTargetAddress = 0x6000,
                    RetentionTargetTypeFullName = "Plugin.Leaked",
                    RetentionPath = new RetentionPath("Plugin.Leaked", 0x6000, [new RetentionFrame("<root>", 0) { RootKind = "StaticVar" }, new RetentionFrame("Plugin.Leaked", 0x6000)], Truncated: false),
                },
            ],
            Notes: ["Retention hints are capped."]),
        HeapIntegrity = new HeapIntegrityView(0, Array.Empty<HeapCorruptionStat>(), Array.Empty<string>()),
    };
}
