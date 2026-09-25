using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class DurableCaptureUseCasesTests
{
    [Fact]
    public async Task ConcurrentSameKindSamePidChildrenKeepExactRoutesReferencesAndSourceQuality()
    {
        var service = Service();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await service.CaptureAsync("batch", "batch", Owner, async _ =>
        {
            var first = service.RunChildAsync("counters", "first", async _ =>
            {
                var sink = CaptureRecordingContext.Current!;
                firstStarted.SetResult();
                await secondStarted.Task;
                Assert.Same(sink, CaptureRecordingContext.Current);
                Assert.True(sink.TryAppend(new("first", At, 42, "first", [])));
                sink.ReportSourceLoss("same-session-name", 2);
                var handle = _handles.RegisterWithMetadata(42, "counters", Snapshot, TimeSpan.FromMinutes(1),
                    producingTool: "collect_events");
                return DiagnosticResult.OkWithHandle(Snapshot, "first", handle.Id, handle.ExpiresAt);
            });
            await firstStarted.Task;
            var second = service.RunChildAsync("counters", "second", async _ =>
            {
                secondStarted.SetResult();
                await Task.Yield();
                var sink = CaptureRecordingContext.Current!;
                Assert.True(sink.TryAppend(new("second", At, 42, "second", [])));
                sink.ReportSourceLoss("same-session-name", 3);
                var handle = _handles.RegisterWithMetadata(42, "counters", Snapshot, TimeSpan.FromMinutes(1),
                    producingTool: "collect_events");
                return DiagnosticResult.OkWithHandle(Snapshot, "second", handle.Id, handle.ExpiresAt);
            });
            return DiagnosticResult.Ok(await Task.WhenAll(first, second), "batch complete");
        });

        Assert.False(result.IsError, result.Error?.Message);
        var info = result.Capture!;
        Assert.Equal(CaptureState.Sealed, info.State);
        Assert.Equal(3, info.Artifacts.Count);
        Assert.Equal(2, info.Quality.Offered);
        Assert.Equal(5, info.Quality.SourceRejected);
        var root = info.Artifacts.Single(a => a.Name == "batch");
        var opened = await service.OpenAsync(info.CaptureId, root.ArtifactId, Owner);
        Assert.Equal("capture-group", opened.Handle.Kind);
        Assert.Equal(["children"], opened.SupportedViews);
        Assert.Equal(["children"], await service.DescribeArtifactViewsAsync(info.CaptureId, root.ArtifactId, Owner));
        await service.AuthorizeViewAsync(opened.Handle.Id, "children", Owner);
        Assert.False(opened.RecordStreamAvailable);
        await Assert.ThrowsAsync<CaptureStoreException>(() =>
            service.QueryRecordsAsync(info.CaptureId, new(root.ArtifactId), Owner));
        var composition = Assert.IsType<DurableCaptureComposition>(opened.Composition);
        Assert.Equal(2, composition.Children.Count);
        foreach (var child in composition.Children)
        {
            Assert.Equal(root.ArtifactId, child.ParentArtifactId);
            Assert.True(child.SnapshotAvailable);
            Assert.Equal(1, child.Offered);
            Assert.Equal(1, child.Accepted);
            Assert.Equal(child.Name == "first" ? 2 : 3, child.SourceRejected);
            Assert.Equal(child.SourceRejected, child.Sources["same-session-name"]);
            var row = Assert.Single((await service.QueryRecordsAsync(info.CaptureId, new(child.ArtifactId), Owner)).Records);
            Assert.Equal(child.Name, row.Record.Category);
            var original = result.Data!.Single(r => r.Summary == child.Name);
            Assert.Equal(child.ArtifactId, service.LookupBinding(original.Handle!)!.ArtifactId);
            Assert.NotNull(await service.OpenAsync(info.CaptureId, child.ArtifactId, Owner));
        }
        Assert.Null(CaptureRecordingContext.Current);
    }

    [Fact]
    public async Task DelayedRegistrationReentersExactChildWithoutKindOrPidGuessing()
    {
        var service = Service();
        var result = await service.CaptureAsync("pair", "gc-activities", Owner, _ =>
        {
            var first = CaptureRecordingContext.CreateChild("counters", "first")!;
            var second = CaptureRecordingContext.CreateChild("counters", "second")!;
            using (CaptureRecordingContext.Enter(first))
                Assert.True(CaptureRecordingContext.Current!.TryAppend(new("first", At, 42, null, [])));
            using (CaptureRecordingContext.Enter(second))
                Assert.True(CaptureRecordingContext.Current!.TryAppend(new("second", At, 42, null, [])));
            using (CaptureRecordingContext.Enter(second))
                _handles.RegisterWithMetadata(42, "counters", Snapshot, TimeSpan.FromMinutes(1));
            using (CaptureRecordingContext.Enter(first))
                _handles.RegisterWithMetadata(42, "counters", Snapshot, TimeSpan.FromMinutes(1));
            first.ReportSourceLoss("source", 0);
            // Absence on the second child must not become zero because the first reported zero.
            return Task.FromResult(DiagnosticResult.Ok(new object(), "pair complete"));
        });
        Assert.False(result.IsError, result.Error?.Message);
        Assert.Null(result.Capture!.Quality.SourceRejected);
        var root = result.Capture.Artifacts.Single(a => a.Name == "pair");
        var composition = (await service.OpenAsync(result.Capture.CaptureId, root.ArtifactId, Owner)).Composition!;
        Assert.Equal(0, composition.Children.Single(c => c.Name == "first").SourceRejected);
        Assert.Null(composition.Children.Single(c => c.Name == "second").SourceRejected);
        foreach (var child in composition.Children)
        {
            var row = Assert.Single((await service.QueryRecordsAsync(result.Capture.CaptureId,
                new(child.ArtifactId), Owner)).Records);
            Assert.Equal(child.Name, row.Record.Category);
        }
    }

    [Fact]
    public async Task ChildResultFallbackAndNestedGroupsUseExplicitTypedSnapshots()
    {
        var service = Service();
        var result = await service.CaptureAsync("outer", "sweep", Owner, async _ =>
        {
            var child = await service.RunChildAsync("batch", "inner", async _ =>
            {
                var leaf = await service.RunChildAsync("counters", "leaf",
                    _ => Task.FromResult(DiagnosticResult.Ok(Snapshot, "leaf")));
                return DiagnosticResult.Ok(leaf, "inner");
            });
            return DiagnosticResult.Ok(child, "outer");
        });
        Assert.False(result.IsError, result.Error?.Message);
        var info = result.Capture!;
        var outer = await service.OpenAsync(info.CaptureId, info.Artifacts.Single(a => a.Name == "outer").ArtifactId, Owner);
        var inner = await service.OpenAsync(info.CaptureId, info.Artifacts.Single(a => a.Name == "inner").ArtifactId, Owner);
        Assert.Equal(2, outer.Composition!.Children.Count);
        Assert.Single(inner.Composition!.Children);
        var leaf = await service.OpenAsync(info.CaptureId, info.Artifacts.Single(a => a.Name == "leaf").ArtifactId, Owner);
        Assert.Null(leaf.Composition);
        Assert.IsType<CounterSnapshot>(_handles.TryGetWithKind(leaf.Handle.Id)!.Value.Artifact);
    }

    [Fact]
    public async Task StructuredChildFailureRetainsTypedPartialAndExplicitMetadataWithoutSealingSuccess()
    {
        var service = Service();
        var result = await service.CaptureAsync("failed-batch", "batch", Owner, async _ =>
        {
            var child = await service.RunChildAsync("counters", "failed-child", _ =>
            {
                CaptureRecordingContext.Current!.TryAppend(new("partial", At, null, null, []));
                return Task.FromResult(DiagnosticResult.Fail<CounterSnapshot>("partial",
                    new("TargetExited", "original child failure")) with { Data = Snapshot, Cancelled = true });
            });
            return DiagnosticResult.Ok(child, "partial batch");
        });
        Assert.Equal("CaptureChildIncomplete", result.Error!.Kind);
        Assert.Equal(CaptureState.Interrupted, result.Capture!.State);
        var recovered = await service.RecoverAsync(result.Capture.CaptureId, Owner);
        var childInfo = recovered.Artifacts.Single(a => a.Name == "failed-child");
        Assert.NotNull(await service.OpenAsync(recovered.CaptureId, childInfo.ArtifactId, Owner));
        var recoveredRoot = recovered.Artifacts.Single(a => a.Name == "failed-batch");
        using var reader = await Store().OpenAsync(recovered.CaptureId, Owner);
        var wrapper = DurableCaptureSnapshotMetadata.Decode("batch",
            reader.ReadSnapshot(recoveredRoot.ArtifactId)!, new CaptureStoreOptions().MaxSnapshotBytes).Snapshot;
        var originalRoot = result.Capture.Artifacts.Single(a => a.Name == "failed-batch");
        var composition = DurableCaptureCompositionCodec.Decode("batch", originalRoot.ArtifactId,
            wrapper, result.Capture, new());
        var metadata = Assert.Single(composition.Children);
        Assert.Equal("TargetExited", metadata.Error!.Kind);
        Assert.True(metadata.Cancelled);
        Assert.True(metadata.SnapshotAvailable);
        var reopened = await service.OpenAsync(recovered.CaptureId, recoveredRoot.ArtifactId, Owner);
        var currentChild = Assert.Single(reopened.Composition!.Children);
        Assert.Equal(childInfo.ArtifactId, currentChild.ArtifactId);
        Assert.Equal(recoveredRoot.ArtifactId, currentChild.ParentArtifactId);
        Assert.Equal("TargetExited", currentChild.Error!.Kind);
        Assert.Equal(metadata.ArtifactId, childInfo.SourceArtifactId);
        Assert.NotEqual(metadata.ArtifactId, currentChild.ArtifactId);
    }

    [Fact]
    public async Task SourceNameBudgetIsExplicitInChildMetadataAndAggregateRemainsUnknown()
    {
        var service = Service();
        var result = await service.CaptureAsync("batch", "batch", Owner, async _ =>
        {
            await service.RunChildAsync("counters", "child", _ =>
            {
                for (var i = 0; i < 65; i++)
                    CaptureRecordingContext.Current!.ReportSourceLoss(
                        "source-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
                return Task.FromResult(DiagnosticResult.Ok(Snapshot, "child"));
            });
            return DiagnosticResult.Ok(new object(), "batch");
        });
        Assert.False(result.IsError);
        Assert.Null(result.Capture!.Quality.SourceRejected);
        var root = result.Capture.Artifacts.Single(a => a.Name == "batch");
        var composition = (await service.OpenAsync(result.Capture.CaptureId, root.ArtifactId, Owner)).Composition!;
        var child = Assert.Single(composition.Children);
        Assert.Equal(64, child.Sources.Count);
        Assert.Equal(1, child.SourceReportsRejected);
        Assert.Null(child.SourceRejected);
    }

    [Fact]
    public async Task ChildHelperOutsideRecordingIsAnEphemeralPassThrough()
    {
        var original = DiagnosticResult.Ok(Snapshot, "ephemeral");
        var result = await Service().RunChildAsync("counters", "no capture", _ =>
        {
            Assert.Null(CaptureRecordingContext.Current);
            return Task.FromResult(original);
        });
        Assert.Same(original, result);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task CompositionRepresentationIsDetectedEvenWhenKindAliasesAnOrdinarySnapshot()
    {
        var service = Service();
        var result = await service.CaptureAsync("group", "counters", Owner, async _ =>
        {
            await service.RunChildAsync("counters", "child", _ =>
                Task.FromResult(DiagnosticResult.Ok(Snapshot, "child")));
            return DiagnosticResult.Ok(new object(), "group");
        });
        var info = result.Capture!;
        Assert.False(result.IsError, result.Error?.Message);
        var root = info.Artifacts.Single(a => a.Name == "group");
        Assert.Equal("counters", root.Kind);
        Assert.Equal(["children"], await service.DescribeArtifactViewsAsync(info.CaptureId, root.ArtifactId, Owner));
        var opened = await service.OpenAsync(info.CaptureId, root.ArtifactId, Owner);
        Assert.Equal("capture-group", opened.Handle.Kind);
        await service.AuthorizeViewAsync(opened.Handle.Id, "children", Owner);
        await Assert.ThrowsAsync<CaptureStoreException>(() =>
            service.AuthorizeViewAsync(opened.Handle.Id, "children", new("bob")));
        await Assert.ThrowsAsync<CaptureStoreException>(() =>
            service.AuthorizeViewAsync(opened.Handle.Id, "summary", Owner));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrownChildRestoresParentScopeAndReportsFailureEvenWhenParentCatches(bool cancel)
    {
        var service = Service();
        var result = await service.CaptureAsync("batch", "batch", Owner, async _ =>
        {
            var parent = CaptureRecordingContext.Current;
            await Assert.ThrowsAnyAsync<Exception>(() => service.RunChildAsync<CounterSnapshot>(
                "counters", "failed", _ => cancel
                    ? throw new OperationCanceledException() : throw new InvalidOperationException("failed child")));
            Assert.Same(parent, CaptureRecordingContext.Current);
            return DiagnosticResult.Ok(new object(), "parent caught failure");
        });
        Assert.Equal("CaptureChildIncomplete", result.Error!.Kind);
        Assert.Equal(CaptureState.Interrupted, result.Capture!.State);
        Assert.Null(CaptureRecordingContext.Current);
    }

    [Fact]
    public void CompositionCodecRejectsForeignDuplicateCyclicAndOversizedReferences()
    {
        var root = new CaptureArtifactInfo(Guid.NewGuid().ToString("N"), "batch", "root");
        var artifact = new CaptureArtifactInfo(Guid.NewGuid().ToString("N"), "counters", "child");
        var child = new DurableCaptureChild(artifact.ArtifactId, artifact.Kind, artifact.Name,
            root.ArtifactId, 1, 1, null, new Dictionary<string, long?> { ["unknown"] = null },
            0, null, false, true);
        var info = new CaptureInfo(Guid.NewGuid().ToString("N"), Owner.OwnerId, "batch", null,
            At, CaptureState.Sealed, [root, artifact], new());
        var options = new CaptureStoreOptions();
        var valid = new DurableCaptureComposition([child]);
        var bytes = DurableCaptureCompositionCodec.Encode("batch", valid, options.MaxSnapshotBytes);
        var snapshot = new CaptureSnapshot(DurableCaptureCompositionCodec.SnapshotVersion, bytes);
        Assert.Single(DurableCaptureCompositionCodec.Decode("batch", root.ArtifactId, snapshot, info, options).Children);
        foreach (var invalid in new[]
        {
            new DurableCaptureComposition([child, child]),
            new DurableCaptureComposition([child with { ParentArtifactId = child.ArtifactId }]),
            new DurableCaptureComposition([child with { ArtifactId = Guid.NewGuid().ToString("N") }]),
            new DurableCaptureComposition([child with { Accepted = 2 }]),
        })
        {
            var malformed = new CaptureSnapshot(DurableCaptureCompositionCodec.SnapshotVersion,
                DurableCaptureCompositionCodec.Encode("batch", invalid, options.MaxSnapshotBytes));
            Assert.Throws<InvalidDataException>(() =>
                DurableCaptureCompositionCodec.Decode("batch", root.ArtifactId, malformed, info, options));
        }
        Assert.Throws<InvalidDataException>(() => DurableCaptureCompositionCodec.Decode(
            "batch", root.ArtifactId, snapshot, info, options with { MaxSnapshotBytes = bytes.Length - 1 }));
        var duplicateJson = System.Text.Encoding.UTF8.GetBytes(
            System.Text.Encoding.UTF8.GetString(bytes).Replace("\"compositionVersion\":1,",
                "\"compositionVersion\":1,\"compositionVersion\":1,", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => DurableCaptureCompositionCodec.Decode("batch", root.ArtifactId,
            new(DurableCaptureCompositionCodec.SnapshotVersion, duplicateJson), info, options));
    }

    [Fact]
    public void CompositionResolvesOnlyExplicitRecoveryAliasesAndRejectsAmbiguousAliases()
    {
        var oldRoot = Guid.NewGuid().ToString("N");
        var oldChild = Guid.NewGuid().ToString("N");
        var root = new CaptureArtifactInfo(Guid.NewGuid().ToString("N"), "batch", "root", SourceArtifactId: oldRoot);
        var child = new CaptureArtifactInfo(Guid.NewGuid().ToString("N"), "counters", "child", SourceArtifactId: oldChild);
        var info = new CaptureInfo(Guid.NewGuid().ToString("N"), Owner.OwnerId, "batch", null,
            At, CaptureState.Sealed, [root, child], new());
        var composition = new DurableCaptureComposition([new(oldChild, "counters", "child", oldRoot,
            0, 0, null, new Dictionary<string, long?>(), 0, null, false, true)]);
        var bytes = DurableCaptureCompositionCodec.Encode("batch", composition, new CaptureStoreOptions().MaxSnapshotBytes);
        var snapshot = new CaptureSnapshot(DurableCaptureCompositionCodec.SnapshotVersion, bytes);
        var decoded = DurableCaptureCompositionCodec.Decode("batch", root.ArtifactId, snapshot, info, new());
        Assert.Equal(child.ArtifactId, Assert.Single(decoded.Children).ArtifactId);
        Assert.Equal(root.ArtifactId, decoded.Children[0].ParentArtifactId);
        var missingAlias = info with { Artifacts = [root, child with { SourceArtifactId = null }] };
        Assert.Throws<InvalidDataException>(() =>
            DurableCaptureCompositionCodec.Decode("batch", root.ArtifactId, snapshot, missingAlias, new()));
        var ambiguous = info with
        {
            Artifacts = [root, child, child with { ArtifactId = Guid.NewGuid().ToString("N") }],
        };
        Assert.Throws<InvalidDataException>(() =>
            DurableCaptureCompositionCodec.Decode("batch", root.ArtifactId, snapshot, ambiguous, new()));
    }
}
