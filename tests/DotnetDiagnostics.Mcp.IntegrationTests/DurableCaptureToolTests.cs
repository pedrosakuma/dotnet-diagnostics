using System.Text.Json.Nodes;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Bytes;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Resources;
using DotnetDiagnostics.Mcp.Security;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class DurableCaptureToolTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(
        ".validation", "durable-mcp-tests", Guid.NewGuid().ToString("N")));
    private static readonly IPrincipalAccessor Owner = Principal("owner-a", "read-counters", "module-bytes-read",
        "investigation-export", "delete-artifact");

    [Theory]
    [InlineData("exception-snapshot")]
    [InlineData("unreviewed-parent-kind")]
    public async Task ChildRecords_RequireTheCompletePackageAuthorizationPolicy(string siblingKind)
    {
        var context = Create();
        await using var writer = await context.Store.CreateAsync(new("composition-records"), new("owner-a"));
        var counters = writer.AddArtifact("counters", "counters");
        writer.AddArtifact(siblingKind, "parent-or-sibling");
        writer.TryAppend(counters, new(Name: "cpu-usage")).Should().BeTrue();
        var capture = await writer.CompleteAsync();

        var denied = await Records(context, Owner, capture.CaptureId, counters);
        denied.Error.Should().NotBeNull("a low-scope child stream must not bypass parent/sibling policies");
        var elevated = await Records(context, Principal("owner-a", "root"), capture.CaptureId, counters);
        if (siblingKind == "unreviewed-parent-kind") elevated.Error.Should().NotBeNull();
        else elevated.Error.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Records_DistinguishUnavailableSnapshotOnly_FromDeclaredZeroStream(bool declareStream)
    {
        var context = Create();
        var capture = (await CaptureCounters(context, declareStream)).Capture!;
        var artifact = capture.Artifacts.Single(item => item.Kind == "counters");
        var result = await Records(context, Owner, capture.CaptureId, artifact.ArtifactId);
        if (declareStream)
        {
            result.Error.Should().BeNull();
            ((CaptureRecordPage)result.Data!).Records.Should().BeEmpty();
            result.Capture!.Quality.SourceRejected.Should().BeNull("declaring availability does not invent known source loss");
        }
        else
        {
            result.Error!.Kind.Should().Be("CaptureStoreError");
            result.Error.Message.Should().Contain("no declared or retained record stream");
        }
    }

    [Fact]
    public async Task OversizedCollection_OmitsInlineDataButPreservesCaptureReference()
    {
        var context = Create();
        var result = await CaptureCounters(context, displayName: new string('x', DurableCaptureTools.MaximumResponseBytes / 2));
        result.Error!.Detail.Should().Be("CapacityExceeded");
        result.Data.Should().BeNull();
        result.Capture.Should().NotBeNull();
        result.Capture!.State.Should().Be(CaptureState.Sealed);
        result.Hints.Should().ContainSingle().Which.SuggestedArguments!["captureId"].Should().Be(result.Capture.CaptureId);
    }

    [Fact]
    public async Task Composition_EnforcesEveryChildScopeBeforeOpen_AndRechecksReusedHandlesAndDeletion()
    {
        var context = Create();
        var principal = Principal("owner-a", "root");
        var result = await DurableCaptureTools.CollectAsync(context.Tools, principal, true,
            "collect_batch", "batch", async ct =>
            {
                await DurableCaptureTools.ChildAsync(context.Tools, "counters", "counters", _ =>
                {
                    var snapshot = new CounterSnapshot(123, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), [], [], []);
                    var handle = context.Handles.Register(123, "counters", snapshot, TimeSpan.FromMinutes(10));
                    return Task.FromResult(DiagnosticResult.OkWithHandle(snapshot, "counters", handle.Id, handle.ExpiresAt));
                }, ct);
                await DurableCaptureTools.ChildAsync(context.Tools, "exceptions", "exceptions", _ =>
                {
                    var snapshot = new ExceptionSnapshot(123, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), 0, [], []);
                    var handle = context.Handles.Register(123, "exception-snapshot", snapshot, TimeSpan.FromMinutes(10));
                    return Task.FromResult(DiagnosticResult.OkWithHandle(snapshot, "exceptions", handle.Id, handle.ExpiresAt));
                }, ct);
                return DiagnosticResult.Ok(new object(), "batch");
            }, CancellationToken.None);
        result.Error.Should().BeNull();
        var capture = result.Capture!;
        var parent = capture.Artifacts.Single(artifact => artifact.Kind == "batch");
        (await Query(context, Principal("owner-a", "read-counters"), capture.CaptureId,
            parent.ArtifactId, view: "children")).Error!.Kind.Should().Be("Forbidden");
        (await Query(context, Principal("owner-b", "read-counters", "eventpipe"), capture.CaptureId,
            parent.ArtifactId, view: "children")).Error.Should().NotBeNull();

        var opened = await Query(context, principal, capture.CaptureId, parent.ArtifactId, view: "children");
        opened.Error.Should().BeNull();
        opened.Data.Should().BeOfType<DurableCaptureComposition>().Which.Children.Should().HaveCount(2);
        (await Query(context, Principal("owner-a", "read-counters"), handle: opened.Handle, view: "children"))
            .Error!.Kind.Should().Be("Forbidden");
        (await Query(context, principal, handle: opened.Handle, view: "object")).Error.Should().NotBeNull();
        (await context.Tools.LifecycleAsync(Owner, "delete", capture.CaptureId, 25, null, CancellationToken.None))
            .Error.Should().BeNull();
        (await Query(context, principal, handle: opened.Handle, view: "children")).Error.Should().NotBeNull();
    }

    [Fact]
    public async Task DefaultCollection_RemainsEphemeral_AndDoesNotRequireDurableServices()
    {
        var expected = DiagnosticResult.Ok(new object(), "ephemeral");
        var result = await DurableCaptureTools.CollectAsync(
            null, TestPrincipalAccessors.Anonymous, false, "collect_events", "counters",
            _ => Task.FromResult(expected), CancellationToken.None);

        result.Should().BeSameAs(expected);
        result.Capture.Should().BeNull();
        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public async Task MissingPrincipal_FailsClosedBeforeCollectorOrStorage()
    {
        var invoked = false;
        var result = await DurableCaptureTools.CollectAsync<object>(
            null, TestPrincipalAccessors.Anonymous, true, "collect_events", "counters",
            _ => { invoked = true; return Task.FromResult(DiagnosticResult.Ok(new object(), "unexpected")); },
            CancellationToken.None);

        result.Error!.Kind.Should().Be("InsufficientScope");
        invoked.Should().BeFalse();
        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public async Task Persist_ReopensAfterNewServices_AndEnforcesOwnerAndCurrentScopes()
    {
        var first = Create();
        var persisted = await CaptureCounters(first);
        persisted.Error.Should().BeNull();
        persisted.Capture.Should().NotBeNull();
        var artifact = persisted.Capture!.Artifacts.Single(item => item.Kind == "counters");

        var reopened = Create();
        var response = await Query(reopened, Owner, persisted.Capture.CaptureId, artifact.ArtifactId);
        response.Error.Should().BeNull();
        response.Handle.Should().NotBeNull().And.NotBe(persisted.Handle);
        response.Capture!.CaptureId.Should().Be(persisted.Capture.CaptureId);

        var otherOwner = await Query(reopened, Principal("owner-b", "read-counters"),
            persisted.Capture.CaptureId, artifact.ArtifactId);
        otherOwner.Error.Should().NotBeNull();
        var reduced = await Query(reopened, Principal("owner-a", "eventpipe"),
            persisted.Capture.CaptureId, artifact.ArtifactId);
        reduced.Error.Should().NotBeNull();
        var reuseByOtherOwner = await Query(reopened, Principal("owner-b", "read-counters"), handle: response.Handle);
        reuseByOtherOwner.Error.Should().NotBeNull();
        var reuseReduced = await Query(reopened, Principal("owner-a", "eventpipe"), handle: response.Handle);
        reuseReduced.Error.Should().NotBeNull();
    }

    [Theory]
    [InlineData("root")]
    [InlineData("*")]
    public async Task RootAuthority_IsCurrentAndCrossOwner(string scope)
    {
        var context = Create();
        var persisted = await CaptureCounters(context);
        var artifact = persisted.Capture!.Artifacts.Single(item => item.Kind == "counters");
        var result = await Query(context, Principal("other", scope),
            persisted.Capture.CaptureId, artifact.ArtifactId);
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task OwnershipKey_NotDisplayNameControlsAccess()
    {
        var context = Create();
        var persisted = await CaptureCounters(context);
        var artifact = persisted.Capture!.Artifacts.Single(item => item.Kind == "counters");
        var renamed = TestPrincipalAccessors.WithIdentity("renamed", "owner-a", "read-counters");
        (await Query(context, renamed, persisted.Capture.CaptureId, artifact.ArtifactId)).Error.Should().BeNull();
        var sameName = TestPrincipalAccessors.WithIdentity("same-name", "owner-b", "read-counters");
        (await Query(context, sameName, persisted.Capture.CaptureId, artifact.ArtifactId)).Error.Should().NotBeNull();
    }

    [Fact]
    public async Task DeletedCapture_InvalidatesReopenedAndOriginalHandles()
    {
        var context = Create();
        var persisted = await CaptureCounters(context);
        var artifact = persisted.Capture!.Artifacts.Single(item => item.Kind == "counters");
        var reopened = await Query(context, Owner, persisted.Capture.CaptureId, artifact.ArtifactId);
        var deletion = await context.Tools.LifecycleAsync(Owner, "delete", persisted.Capture.CaptureId,
            25, null, CancellationToken.None);
        deletion.Error.Should().BeNull();

        (await Query(context, Owner, handle: reopened.Handle)).Error.Should().NotBeNull();
        (await Query(context, Owner, handle: persisted.Handle)).Error.Should().NotBeNull();
    }

    [Fact]
    public async Task CaptureSelector_RejectsHandleAndLatestSelectors()
    {
        var context = Create();
        var result = await QuerySnapshotTool.QuerySnapshotCursorPaged(
            context.Handles, null!, new SensitiveDataRedactor(), new SensitiveValueGate(null),
            new SecurityOptions(), Owner, null!, null!,
            handle: "memory", captureId: "capture", artifactId: "artifact", durableCaptures: context.Tools);
        result.Error!.Kind.Should().Be("InvalidArgument");
        Directory.Exists(_root).Should().BeFalse();
    }

    [Theory]
    [InlineData("method-params-capture", "eventpipe", "sensitive-parameter-read")]
    [InlineData("heap-snapshot", "heap-read", "sensitive-heap-read")]
    [InlineData("event-source", "eventpipe", "eventsource-any")]
    public async Task Records_DoNotBypassSensitiveKindScopes(string kind, string primary, string modifier)
    {
        var context = Create();
        var (capture, artifact) = await WriteRecords(context, kind);
        var result = await Records(context, Principal("owner-a", primary, "ptrace"), capture, artifact);
        result.Error.Should().NotBeNull();
        var allowed = await Records(context, Principal("owner-a", primary, modifier, "ptrace"), capture, artifact);
        allowed.Error.Should().BeNull();
        ((CaptureRecordPage)allowed.Data!).Records.Should().ContainSingle();
    }

    [Fact]
    public async Task Records_ApplyTypedFiltersAndStableContinuation()
    {
        var context = Create();
        await using var writer = await context.Store.CreateAsync(new("rows"), new("owner-a"));
        var artifact = writer.AddArtifact("counters", "counters");
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 5; index++)
            writer.TryAppend(artifact, new(now, 7, "counter", "cpu", index)).Should().BeTrue();
        var capture = await writer.CompleteAsync();
        var first = await context.Tools.QueryRecordsAsync(Owner, capture.CaptureId, artifact,
            now.AddSeconds(-1), now.AddSeconds(1), 7, "counter", "cpu", 0, 2, CancellationToken.None);
        var page = (CaptureRecordPage)first.Data!;
        page.Records.Should().HaveCount(2);
        page.NextAfterRecordId.Should().NotBeNull();
        var next = await context.Tools.QueryRecordsAsync(Owner, capture.CaptureId, artifact,
            now.AddSeconds(-1), now.AddSeconds(1), 7, "counter", "cpu", page.NextAfterRecordId!.Value, 2, CancellationToken.None);
        ((CaptureRecordPage)next.Data!).Records[0].RecordId.Should().BeGreaterThan(page.Records[^1].RecordId);
    }

    [Fact]
    public async Task InterruptedQuery_DoesNotRecoverImplicitly()
    {
        var context = Create();
        var writer = await context.Store.CreateAsync(new("interrupted"), new("owner-a"));
        var artifact = writer.AddArtifact("counters", "counters");
        writer.TryAppend(artifact, new(Name: "cpu", NumericValue: 1)).Should().BeTrue();
        await writer.DisposeAsync();
        var failed = await Records(context, Owner, writer.Reference.CaptureId, artifact);
        failed.Error!.Kind.Should().Be("CaptureStoreError");
        failed.Hints.Should().Contain(item => item.Reason.Contains("recover", StringComparison.Ordinal));
        var before = await context.Store.ListAsync(new("owner-a"));
        before.Captures.Should().ContainSingle();

        var recovery = await context.Tools.LifecycleAsync(Owner, "recover", writer.Reference.CaptureId,
            25, null, CancellationToken.None);
        recovery.Error.Should().BeNull();
        recovery.Capture!.CaptureId.Should().NotBe(writer.Reference.CaptureId);
        recovery.Capture.DerivedFrom.Should().Be(writer.Reference.CaptureId);
        (await context.Store.ListAsync(new("owner-a"))).Captures.Should().HaveCount(2);
    }

    [Fact]
    public async Task Lifecycle_RequiresExplicitDeleteAndDoesNotLeakOtherOwners()
    {
        var context = Create();
        var persisted = await CaptureCounters(context);
        var rootWithoutDelete = Principal("root-owner", "root", "module-bytes-read");
        var denied = await context.Tools.LifecycleAsync(rootWithoutDelete, "delete", persisted.Capture!.CaptureId,
            25, null, CancellationToken.None);
        denied.Error!.Kind.Should().Be("InsufficientScope");
        var other = await context.Tools.LifecycleAsync(
            Principal("owner-b", "module-bytes-read", "investigation-export"), "list", null,
            25, null, CancellationToken.None);
        ((CaptureCatalogPage)other.Data!).Captures.Should().BeEmpty();
    }

    [Fact]
    public async Task DurableResource_DeniesRawSnapshotAccess()
    {
        var context = Create();
        var persisted = await CaptureCounters(context);
        var artifact = persisted.Capture!.Artifacts.Single(item => item.Kind == "counters");
        var reopened = await Query(context, Owner, persisted.Capture.CaptureId, artifact.ArtifactId);
        TraceSessionResources.ReadSession(context.Handles, reopened.Handle!, context.Tools)
            .Should().Be(DurableCaptureTools.ResourceDenial);
    }

    [Fact]
    public async Task RestoredLiveHeap_RejectsReattachmentViews_BeforeDispatcher()
    {
        var context = Create();
        var principal = Principal("owner-a", "root");
        var persisted = await DurableCaptureTools.CollectAsync(context.Tools, principal, true,
            "inspect_heap", "heap-snapshot", _ =>
            {
                var snapshot = new HeapSnapshotArtifact(
                    HeapSnapshotOrigin.Live, 123, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1),
                    new DumpRuntimeInfo("CoreCLR", "10.0.0", "X64", false, 0),
                    new DumpHeapSummary(100, 0, 0, 0, 0, 0, 100),
                    [new TypeStat("Example.Type", null, 2, 100, 100)],
                    [new TypeStat("Example.Type", null, 2, 100, 100)]);
                var handle = context.Handles.Register(123, "heap-snapshot", snapshot, TimeSpan.FromMinutes(10));
                return Task.FromResult(DiagnosticResult.OkWithHandle(snapshot, "heap", handle.Id, handle.ExpiresAt));
            }, CancellationToken.None);
        persisted.Error.Should().BeNull();
        var artifact = persisted.Capture!.Artifacts.Single(item => item.Kind == "heap-snapshot");
        var reopened = await Query(context, principal, persisted.Capture.CaptureId, artifact.ArtifactId, view: "top-types");
        reopened.Error.Should().BeNull();
        foreach (var view in new[] { "object", "gcroot", "objsize", "growth", "duplicate-strings" })
        {
            var denied = await Query(context, principal, handle: reopened.Handle, view: view);
            denied.Error.Should().NotBeNull("a historical heap must never reach the null live inspector");
        }
    }

    [Fact]
    public async Task SnapshotMaterialization_IsAuthorizedBeforeDecode()
    {
        var context = Create();
        var (capture, artifact) = await WriteRecords(context, "method-params-capture");
        var result = await Query(context, Principal("owner-a", "eventpipe"), capture, artifact);
        result.Error!.Kind.Should().Be("Forbidden");
        result.Error.Message.Should().Contain("sensitive-parameter-read");
        context.Handles.TryGetLatestByKind("method-params-capture").Should().BeNull();
    }

    [Theory]
    [InlineData(false, "CorruptPackage")]
    [InlineData(true, "UnsupportedFormat")]
    public async Task CorruptAndFuturePackages_ReturnStructuredReasons_WithoutRecovery(bool future, string reason)
    {
        var context = Create();
        var (capture, artifact) = await WriteRecords(context, "counters");
        var package = Path.Combine(_root, "captures", capture);
        if (future)
        {
            var manifest = Path.Combine(package, "manifest.json");
            var json = JsonNode.Parse(await File.ReadAllTextAsync(manifest))!;
            json["ReaderVersion"] = 999;
            await File.WriteAllTextAsync(manifest, json.ToJsonString());
        }
        else
        {
            await File.AppendAllTextAsync(Path.Combine(package, "capture.sqlite"), "invalid-tail");
        }
        var result = await Records(context, Owner, capture, artifact);
        result.Error!.Kind.Should().Be("CaptureStoreError");
        result.Error.Detail.Should().Be(reason);
        Directory.EnumerateDirectories(Path.Combine(_root, "captures"))
            .Count(path => Path.GetFileName(path).Length == 32).Should().Be(1);
    }

    [Fact]
    public async Task GenericGetBytes_CannotReadDeleteListOrRerootPrivatePackages()
    {
        var context = Create();
        var (capture, _) = await WriteRecords(context, "counters");
        var root = new TestRoot(_root);
        var lifecycle = new FileSystemArtifactLifecycle(root);
        var bytes = new FileSystemDumpByteSource(root);
        var relative = Path.Combine("captures", capture, "capture.sqlite");

        var listed = await GetBytesTool.GetBytes(null!, bytes, null!, Owner, lifecycle, "list");
        ((ArtifactListingEnvelope)listed.Data!).Count.Should().Be(0);
        var read = await GetBytesTool.GetBytes(null!, bytes, null!, Owner, lifecycle, "dump", dumpFilePath: relative);
        read.Error!.Kind.Should().Be("InvalidArtifactPath");
        var deleted = await GetBytesTool.GetBytes(null!, bytes, null!, Owner, lifecycle, "delete", artifactPath: relative);
        deleted.Error!.Kind.Should().Be("InvalidArtifactPath");

        var reroot = new TestRoot(Path.Combine(_root, "captures", capture));
        var rerootRead = await GetBytesTool.GetBytes(null!, new FileSystemDumpByteSource(reroot), null!, Owner,
            new FileSystemArtifactLifecycle(reroot), "dump", dumpFilePath: "capture.sqlite");
        rerootRead.Error!.Kind.Should().Be("InvalidArtifactPath");
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(_root, "alias.sqlite"), Path.Combine(_root, relative));
            var alias = await GetBytesTool.GetBytes(null!, bytes, null!, Owner, lifecycle, "dump", dumpFilePath: "alias.sqlite");
            alias.Error!.Kind.Should().Be("InvalidArtifactPath");
        }
    }

    [Fact]
    public void Bound_IncludesIndentedWrapperAndCaptureMetadata()
    {
        var result = DiagnosticResult.Ok<object>(new { Payload = new string('x', 1024 * 1024) }, "bounded");
        DurableCaptureTools.Bound(result).Error!.Detail.Should().Be("CapacityExceeded");
    }

    [Fact]
    public async Task RecordWireBudget_PreservesEveryContinuationAcrossEscapedPages()
    {
        var context = Create();
        await using var writer = await context.Store.CreateAsync(new("escaped"), new("owner-a"));
        var artifact = writer.AddArtifact("counters", "counters");
        for (var index = 0; index < 40; index++)
            writer.TryAppend(artifact, new(Name: "escaped",
                Fields: [new("text", CaptureFieldKind.Text, StringValue: new string('\n', 12_000))])).Should().BeTrue();
        var info = await writer.CompleteAsync();
        long after = 0;
        var ids = new HashSet<long>();
        var pages = 0;
        do
        {
            var result = await context.Tools.QueryRecordsAsync(Owner, info.CaptureId, artifact,
                null, null, null, null, null, after, 100, CancellationToken.None);
            result.Error.Should().BeNull();
            DurableCaptureTools.Bound(result).Should().BeSameAs(result);
            var page = (CaptureRecordPage)result.Data!;
            page.Records.Should().NotBeEmpty();
            foreach (var row in page.Records)
                ids.Add(row.RecordId).Should().BeTrue();
            pages++;
            if (page.NextAfterRecordId is not { } next)
                break;
            next.Should().BeGreaterThan(after);
            after = next;
            pages.Should().BeLessThan(40);
        } while (true);
        pages.Should().BeGreaterThan(1);
        ids.Should().HaveCount(40);
    }

    private Context Create()
    {
        var handles = new MemoryDiagnosticHandleStore();
        var options = new CaptureStoreOptions();
        var store = new SqliteCaptureStore(new TestRoot(_root), options);
        var useCases = new DurableCaptureUseCases(store, handles, options);
        return new(store, handles, new DurableCaptureTools(store, useCases));
    }

    private static Task<DiagnosticResult<CounterSnapshot>> CaptureCounters(Context context,
        bool declareStream = false, string? displayName = null)
        => DurableCaptureTools.CollectAsync(context.Tools, Owner, true, "collect_events", "counters", _ =>
        {
            if (declareStream)
                CaptureRecordingContext.Current!.ReportSourceLoss("EventPipe", null);
            var snapshot = new CounterSnapshot(123, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1),
                [new("System.Runtime", "cpu-usage", displayName ?? "CPU", 42, CounterKind.Mean)], [], []);
            var handle = context.Handles.Register(123, "counters", snapshot, TimeSpan.FromMinutes(10));
            return Task.FromResult(DiagnosticResult.OkWithHandle(snapshot, "counters", handle.Id, handle.ExpiresAt));
        }, CancellationToken.None);

    private static Task<DiagnosticResult<object>> Query(Context context, IPrincipalAccessor principal,
        string? capture = null, string? artifact = null, string? handle = null, string? view = "summary")
        => QuerySnapshotTool.QuerySnapshotCursorPaged(
            context.Handles, null!, new SensitiveDataRedactor(), new SensitiveValueGate(null),
            new SecurityOptions(), principal, null!, null!, handle: handle, view: view,
            captureId: capture, artifactId: artifact, durableCaptures: context.Tools);

    private static async Task<(string Capture, string Artifact)> WriteRecords(Context context, string kind)
    {
        await using var writer = await context.Store.CreateAsync(new("records"), new("owner-a"));
        var artifact = writer.AddArtifact(kind, kind);
        writer.TryAppend(artifact, new(Name: "secret", Fields: [new("value", CaptureFieldKind.Text, StringValue: "retained")]))
            .Should().BeTrue();
        var capture = await writer.CompleteAsync();
        return (capture.CaptureId, artifact);
    }

    private static Task<DiagnosticResult<object>> Records(Context context, IPrincipalAccessor principal,
        string capture, string artifact)
        => context.Tools.QueryRecordsAsync(principal, capture, artifact, null, null, null, null, null,
            0, 100, CancellationToken.None);

    private static IPrincipalAccessor Principal(string owner, params string[] scopes)
        => TestPrincipalAccessors.WithIdentity("same-name", owner, scopes);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed record Context(SqliteCaptureStore Store, MemoryDiagnosticHandleStore Handles, DurableCaptureTools Tools);
    private sealed record TestRoot(string RootPath) : IArtifactRootProvider
    {
        public string Root => RootPath;
    }
}
