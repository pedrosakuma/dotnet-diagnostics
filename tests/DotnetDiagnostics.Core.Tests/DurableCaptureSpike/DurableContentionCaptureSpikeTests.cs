using System.Security.Cryptography;
using System.Text.Json;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Drilldown;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.DurableCaptureSpike;

public sealed class DurableContentionCaptureSpikeTests : IDisposable
{
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 22, 1, 2, 3, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _workspace;
    private readonly string _root;

    public DurableContentionCaptureSpikeTests()
    {
        _workspace = Path.Combine(
            AppContext.BaseDirectory,
            "dc2-durable-contention-fixtures",
            Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_workspace, "authorized-root");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Fact]
    public void WriteMoveReopen_RoundTripsDtoAndIssuesFreshHandlesWithoutLivePid()
    {
        var store = CreateStore();
        var snapshot = RepresentativeSnapshot(processId: int.MaxValue);
        var package = store.Write(snapshot, Target(snapshot.ProcessId), StartedAt.AddDays(1));
        var movedLocator = store.MovePackage(package.RelativeLocator, $"moved-{Guid.NewGuid():N}");

        var firstHostHandles = new MemoryDiagnosticHandleStore();
        string firstHandle;
        using (var opened = store.Open(
            movedLocator,
            package.CaptureId,
            package.ArtifactId,
            firstHostHandles,
            TimeSpan.FromMinutes(1)))
        {
            opened.Snapshot.Should().BeEquivalentTo(snapshot);
            opened.Manifest.CaptureId.Should().Be(package.CaptureId);
            opened.Manifest.Artifact.ArtifactId.Should().Be(package.ArtifactId);
            opened.Manifest.Target.Should().BeEquivalentTo(Target(snapshot.ProcessId));
            opened.Handle.Origin.Should().Be(HandleOrigin.Imported);
            opened.Handle.ProcessId.Should().Be(int.MaxValue);
            firstHandle = opened.Handle.Id;
        }

        firstHostHandles.Invalidate(firstHandle).Should().BeTrue("the original temporary handle can expire independently");
        var restartedStore = CreateStore();
        var restartedHostHandles = new MemoryDiagnosticHandleStore();
        using var reopened = restartedStore.Open(
            movedLocator,
            package.CaptureId,
            package.ArtifactId,
            restartedHostHandles,
            TimeSpan.FromMinutes(1));

        reopened.Handle.Id.Should().NotBe(firstHandle);
        reopened.Snapshot.Should().BeEquivalentTo(snapshot);
        reopened.Manifest.CaptureId.Should().Be(package.CaptureId);
        reopened.Manifest.Artifact.ArtifactId.Should().Be(package.ArtifactId);
        restartedHostHandles.TryGet<ContentionSnapshot>(reopened.Handle.Id)
            .Should().BeEquivalentTo(snapshot);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("byCallSite")]
    [InlineData("byOwner")]
    public void Reopen_PreservesExistingTypedQueryViews(string view)
    {
        var store = CreateStore();
        var snapshot = RepresentativeSnapshot();
        var package = store.Write(snapshot, Target(snapshot.ProcessId), StartedAt.AddDays(1));
        using var opened = store.Open(
            package.RelativeLocator,
            package.CaptureId,
            package.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));

        foreach (var topN in new[] { 1, 10 })
        {
            var expected = CollectionQueryDispatcher.Dispatch(
                CollectionHandleKinds.ContentionSnapshot,
                view,
                snapshot,
                topN);
            var actual = CollectionQueryDispatcher.Dispatch(
                CollectionHandleKinds.ContentionSnapshot,
                view,
                opened.Snapshot,
                topN);

            actual.Should().BeEquivalentTo(expected);
        }
    }

    [Fact]
    public void Reopen_RejectsUnsupportedViewWithoutExposingRawEvents()
    {
        var store = CreateStore();
        var snapshot = RepresentativeSnapshot();
        var package = store.Write(snapshot, Target(snapshot.ProcessId), StartedAt.AddDays(1));
        using var opened = store.Open(
            package.RelativeLocator,
            package.CaptureId,
            package.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));

        var outcome = CollectionQueryDispatcher.Dispatch(
            CollectionHandleKinds.ContentionSnapshot,
            "events",
            opened.Snapshot,
            topN: 10);

        outcome.Result.Should().BeNull();
        outcome.UnknownView.Should().Be("events");
        outcome.AllowedViews.Should().Equal("summary", "byCallSite", "byOwner");
    }

    [Fact]
    public void ZeroLockId_RemainsRetainedButDistinctMonitorCountIsOnlyTheRecordedLowerBound()
    {
        var snapshot = RepresentativeSnapshot() with
        {
            DistinctMonitors = 0,
            Events =
            [
                Event(1, lockId: 0, ownerThreadId: null, method: "Zero.Id"),
            ],
            Notes = Array.Empty<string>(),
        };
        var store = CreateStore();
        var package = store.Write(snapshot, Target(snapshot.ProcessId), StartedAt.AddDays(1));
        using var opened = store.Open(
            package.RelativeLocator,
            package.CaptureId,
            package.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));

        opened.Snapshot.Events.Should().ContainSingle().Which.LockId.Should().Be(0);
        opened.Snapshot.DistinctMonitors.Should().Be(0);
        opened.Snapshot.Notes.Should().BeEmpty("zero lock IDs are excluded without an overflow note");

        var callSites = CollectionQueryDispatcher.Dispatch(
            CollectionHandleKinds.ContentionSnapshot,
            "byCallSite",
            opened.Snapshot,
            topN: 10).Result!.Payload.Should().BeOfType<ContentionByCallSiteView>().Subject;
        callSites.CallSites.Should().ContainSingle().Which.DistinctMonitors.Should().Be(0);
    }

    [Fact]
    public void EmptyAndCappedArtifacts_PreserveOnlyRecordedSemantics()
    {
        var store = CreateStore();
        var empty = RepresentativeSnapshot() with
        {
            TotalEvents = 0,
            DistinctMonitors = 0,
            TotalContentionDuration = TimeSpan.Zero,
            P50ContentionDuration = TimeSpan.Zero,
            P95ContentionDuration = TimeSpan.Zero,
            MaxContentionDuration = TimeSpan.Zero,
            Events = Array.Empty<ContentionEventSample>(),
            Notes = ["No contention events were observed during the collection window."],
        };
        var emptyPackage = store.Write(empty, Target(empty.ProcessId), StartedAt.AddDays(1));
        using (var opened = store.Open(
            emptyPackage.RelativeLocator,
            emptyPackage.CaptureId,
            emptyPackage.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1)))
        {
            opened.Snapshot.Should().BeEquivalentTo(empty);
            opened.Snapshot.Notes.Should().ContainSingle()
                .Which.Should().Be("No contention events were observed during the collection window.");
        }

        var events = Enumerable.Range(1, 200)
            .Select(ordinal => Event(ordinal, (ulong)ordinal, ordinal, $"Fixture.Method{ordinal}"))
            .ToArray();
        var capped = RepresentativeSnapshot() with
        {
            TotalEvents = 250,
            DistinctMonitors = 200,
            Events = events,
            Notes =
            [
                "Retained the 200 longest contention event(s) after reaching the in-memory cap of 200; 50 shorter event(s) were dropped.",
                "Distinct monitor count is a lower bound after the monitor tracking cap was reached.",
                "Contention duration percentiles are exact up to the bounded exact capacity and approximate afterward.",
            ],
        };
        var cappedPackage = store.Write(capped, Target(capped.ProcessId), StartedAt.AddDays(1));
        using var reopened = store.Open(
            cappedPackage.RelativeLocator,
            cappedPackage.CaptureId,
            cappedPackage.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));
        reopened.Snapshot.Should().BeEquivalentTo(capped);
    }

    [Fact]
    public void Reopen_IsReadOnlyAndRejectsIdentityVersionIntegrityAndSealFailures()
    {
        var store = CreateStore();
        var package = store.Write(RepresentativeSnapshot(), Target(4242), StartedAt.AddDays(1));
        var before = FileStamps(package.AbsolutePath);

        using (store.Open(
            package.RelativeLocator,
            package.CaptureId,
            package.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1)))
        {
        }
        FileStamps(package.AbsolutePath).Should().BeEquivalentTo(before);

        Action wrongIdentity = () => store.Open(
            package.RelativeLocator,
            "capture-wrong",
            package.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));
        wrongIdentity.Should().Throw<DurableSpikeException>().Which.Code.Should().Be("IdentityMismatch");

        RewriteManifestAndSeal(package.AbsolutePath, manifest => manifest with
        {
            Artifact = manifest.Artifact with { RepresentationVersion = "contention-snapshot/999" },
        });
        Action unsupportedVersion = () => store.Open(
            package.RelativeLocator,
            package.CaptureId,
            package.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));
        unsupportedVersion.Should().Throw<DurableSpikeException>()
            .Which.Code.Should().Be("UnsupportedRepresentationVersion");

        var contractPackage = store.Write(RepresentativeSnapshot(), Target(4242), StartedAt.AddDays(1));
        RewriteManifestAndSeal(contractPackage.AbsolutePath, manifest => manifest with
        {
            ContractVersion = "dc-spike/999",
        });
        Action unsupportedContract = () => store.Open(
            contractPackage.RelativeLocator,
            contractPackage.CaptureId,
            contractPackage.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));
        unsupportedContract.Should().Throw<DurableSpikeException>()
            .Which.Code.Should().Be("UnsupportedContractVersion");

        var kindPackage = store.Write(RepresentativeSnapshot(), Target(4242), StartedAt.AddDays(1));
        RewriteManifestAndSeal(kindPackage.AbsolutePath, manifest => manifest with
        {
            Artifact = manifest.Artifact with { RepresentationKind = "unsupported-kind" },
        });
        Action unsupportedKind = () => store.Open(
            kindPackage.RelativeLocator,
            kindPackage.CaptureId,
            kindPackage.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));
        unsupportedKind.Should().Throw<DurableSpikeException>()
            .Which.Code.Should().Be("UnsupportedArtifactKind");

        var unsealedManifestMutation = store.Write(
            RepresentativeSnapshot(),
            Target(4242),
            StartedAt.AddDays(1));
        var manifestPath = Path.Combine(unsealedManifestMutation.AbsolutePath, "manifest.json");
        File.AppendAllText(manifestPath, " ");
        Action changedManifest = () => store.Open(
            unsealedManifestMutation.RelativeLocator,
            unsealedManifestMutation.CaptureId,
            unsealedManifestMutation.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));
        changedManifest.Should().Throw<DurableSpikeException>().Which.Code.Should().Be("IntegrityMismatch");

        var integrityPackage = store.Write(RepresentativeSnapshot(), Target(4242), StartedAt.AddDays(1));
        File.AppendAllText(RepresentationPath(integrityPackage.AbsolutePath, integrityPackage.ArtifactId), " ");
        Action corrupt = () => store.Open(
            integrityPackage.RelativeLocator,
            integrityPackage.CaptureId,
            integrityPackage.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));
        corrupt.Should().Throw<DurableSpikeException>().Which.Code.Should().Be("IntegrityMismatch");

        var missingMember = store.Write(RepresentativeSnapshot(), Target(4242), StartedAt.AddDays(1));
        File.Delete(RepresentationPath(missingMember.AbsolutePath, missingMember.ArtifactId));
        Action missing = () => store.Open(
            missingMember.RelativeLocator,
            missingMember.CaptureId,
            missingMember.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));
        missing.Should().Throw<DurableSpikeException>().Which.Code.Should().Be("MissingPackageMember");

        var unsealed = store.Write(
            RepresentativeSnapshot(),
            Target(4242),
            StartedAt.AddDays(1),
            new DurableSpikeWriteOptions(PublishSeal: false));
        Action missingSeal = () => store.Open(
            unsealed.RelativeLocator,
            unsealed.CaptureId,
            unsealed.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));
        missingSeal.Should().Throw<DurableSpikeException>().Which.Code.Should().Be("RecoveryRequired");
    }

    [Fact]
    public void ExplicitRecovery_PublishesNewIdsAndUnknownTailWithoutMutatingSource()
    {
        var store = CreateStore();
        var interrupted = store.Write(
            RepresentativeSnapshot(),
            Target(4242),
            StartedAt.AddDays(1),
            new DurableSpikeWriteOptions(PublishSeal: false));
        var before = FileStamps(interrupted.AbsolutePath);

        var recovered = store.Recover(
            interrupted.RelativeLocator,
            interrupted.CaptureId,
            interrupted.ArtifactId,
            StartedAt.AddDays(2));

        recovered.CaptureId.Should().NotBe(interrupted.CaptureId);
        recovered.ArtifactId.Should().NotBe(interrupted.ArtifactId);
        FileStamps(interrupted.AbsolutePath).Should().BeEquivalentTo(before);

        using var opened = store.Open(
            recovered.RelativeLocator,
            recovered.CaptureId,
            recovered.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));
        opened.Manifest.DerivedFromCaptureId.Should().Be(interrupted.CaptureId);
        opened.Manifest.RecoveryTailUnknown.Should().BeTrue();
        opened.Manifest.RecoveryReason.Should().Be("explicit-recovery-of-unsealed-package");
        opened.Manifest.DerivationReaderVersion.Should().Be(DurableContentionCaptureSpikeStore.ReaderVersion);
        opened.Manifest.Artifact.StructuredQualityState.Should().Be("legacy-unknown");
    }

    [Fact]
    public void AuthorizationDenial_HappensBeforeRepresentationMaterialization()
    {
        var writer = CreateStore();
        var package = writer.Write(RepresentativeSnapshot(), Target(4242), StartedAt.AddDays(1));
        var denied = CreateStore(_ => false);

        Action open = () => denied.Open(
            package.RelativeLocator,
            package.CaptureId,
            package.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));

        open.Should().Throw<DurableSpikeException>().Which.Code.Should().Be("AuthorizationDenied");
        denied.RepresentationMaterializationCount.Should().Be(0);
    }

    [Fact]
    public void AuthorizationPolicy_IsRequiredForRecoveryAndDeletion()
    {
        var writer = CreateStore();
        var sealedPackage = writer.Write(RepresentativeSnapshot(), Target(4242), StartedAt.AddDays(1));
        var interrupted = writer.Write(
            RepresentativeSnapshot(),
            Target(4242),
            StartedAt.AddDays(1),
            new DurableSpikeWriteOptions(PublishSeal: false));
        var denied = CreateStore(_ => false);

        Action recover = () => denied.Recover(
            interrupted.RelativeLocator,
            interrupted.CaptureId,
            interrupted.ArtifactId,
            StartedAt.AddDays(2));
        recover.Should().Throw<DurableSpikeException>().Which.Code.Should().Be("AuthorizationDenied");

        Action delete = () => denied.Delete(sealedPackage.RelativeLocator, sealedPackage.CaptureId);
        delete.Should().Throw<DurableSpikeException>().Which.Code.Should().Be("AuthorizationDenied");
        Directory.Exists(sealedPackage.AbsolutePath).Should().BeTrue();
        Directory.Exists(interrupted.AbsolutePath).Should().BeTrue();
    }

    [Fact]
    public void WriteLimits_RejectCountsStringsNotesAndEstimatedBytesBeforePublishing()
    {
        var tiny = CreateStore(limits: DefaultLimits() with
        {
            MaxEvents = 1,
            MaxNotes = 1,
            MaxStringChars = 8,
            MaxEstimatedCopyBytes = 300,
        });

        Action eventCount = () => tiny.Write(
            RepresentativeSnapshot(),
            Target(4242),
            StartedAt.AddDays(1));
        eventCount.Should().Throw<DurableSpikeException>().Which.Code.Should().Be("EventCountLimitExceeded");

        var oneEvent = RepresentativeSnapshot() with
        {
            Events = [Event(1, 1, 2, "123456789")],
            Notes = Array.Empty<string>(),
        };
        Action longString = () => tiny.Write(oneEvent, Target(4242), StartedAt.AddDays(1));
        longString.Should().Throw<DurableSpikeException>().Which.Code.Should().Be("StringLimitExceeded");

        var tooManyNotes = oneEvent with
        {
            Events = [Event(1, 1, 2, "short")],
            Notes = ["a", "b"],
        };
        Action noteCount = () => tiny.Write(tooManyNotes, Target(4242), StartedAt.AddDays(1));
        noteCount.Should().Throw<DurableSpikeException>().Which.Code.Should().Be("NoteCountLimitExceeded");

        var estimatedStore = CreateStore(limits: DefaultLimits() with
        {
            MaxEvents = 1,
            MaxNotes = 1,
            MaxStringChars = 100,
            MaxEstimatedCopyBytes = 300,
        });
        var estimated = oneEvent with
        {
            Events = [Event(1, 1, 2, "short")],
            Notes = ["12345678"],
        };
        Action estimatedBytes = () => estimatedStore.Write(estimated, Target(4242), StartedAt.AddDays(1));
        estimatedBytes.Should().Throw<DurableSpikeException>()
            .Which.Code.Should().Be("EstimatedCopyLimitExceeded");

        Directory.EnumerateDirectories(_root).Should().BeEmpty("failed validation occurs before package publication");
    }

    [Fact]
    public void ReadLimits_RejectOversizedFilesAndFileCountsBeforeRepresentationMaterialization()
    {
        var writer = CreateStore();
        var representationLimited = writer.Write(
            RepresentativeSnapshot(),
            Target(4242),
            StartedAt.AddDays(1));
        var tightRepresentationReader = CreateStore(limits: DefaultLimits() with
        {
            MaxRepresentationBytes = 32,
            MaxPackageBytes = 1_000_000,
        });

        Action representationRead = () => tightRepresentationReader.Open(
            representationLimited.RelativeLocator,
            representationLimited.CaptureId,
            representationLimited.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));
        representationRead.Should().Throw<DurableSpikeException>().Which.Code.Should().Be("InputLimitExceeded");
        tightRepresentationReader.RepresentationMaterializationCount.Should().Be(0);

        var manifestLimited = writer.Write(RepresentativeSnapshot(), Target(4242), StartedAt.AddDays(1));
        var tightManifestReader = CreateStore(limits: DefaultLimits() with
        {
            MaxManifestBytes = 32,
            MaxPackageBytes = 1_000_000,
        });
        Action manifestRead = () => tightManifestReader.Open(
            manifestLimited.RelativeLocator,
            manifestLimited.CaptureId,
            manifestLimited.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));
        manifestRead.Should().Throw<DurableSpikeException>().Which.Code.Should().Be("InputLimitExceeded");
        tightManifestReader.RepresentationMaterializationCount.Should().Be(0);

        var packageLimited = writer.Write(RepresentativeSnapshot(), Target(4242), StartedAt.AddDays(1));
        var tightPackageReader = CreateStore(limits: DefaultLimits() with { MaxPackageBytes = 32 });
        Action packageRead = () => tightPackageReader.Open(
            packageLimited.RelativeLocator,
            packageLimited.CaptureId,
            packageLimited.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));
        packageRead.Should().Throw<DurableSpikeException>().Which.Code.Should().Be("PackageLimitExceeded");
        tightPackageReader.RepresentationMaterializationCount.Should().Be(0);

        var fileCountLimited = writer.Write(RepresentativeSnapshot(), Target(4242), StartedAt.AddDays(1));
        File.WriteAllText(Path.Combine(fileCountLimited.AbsolutePath, "unexpected.txt"), "extra");
        Action tooManyFiles = () => writer.Open(
            fileCountLimited.RelativeLocator,
            fileCountLimited.CaptureId,
            fileCountLimited.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));
        tooManyFiles.Should().Throw<DurableSpikeException>()
            .Which.Code.Should().Be("PackageFileCountLimitExceeded");
    }

    [Fact]
    public void Accounting_RecordsEstimatedCopyActualBatchAndDiskBoundaries()
    {
        var store = CreateStore();
        var package = store.Write(RepresentativeSnapshot(), Target(4242), StartedAt.AddDays(1));
        using var opened = store.Open(
            package.RelativeLocator,
            package.CaptureId,
            package.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));

        opened.Manifest.Accounting.EstimatedCopyBytes.Should().BePositive();
        opened.Manifest.Accounting.ActualRepresentationBytes.Should().Be(
            new FileInfo(RepresentationPath(package.AbsolutePath, package.ArtifactId)).Length);
        opened.Manifest.Accounting.ActiveBatchBytes.Should().Be(
            opened.Manifest.Accounting.ActualRepresentationBytes);
        Directory.EnumerateFiles(package.AbsolutePath, "*", SearchOption.AllDirectories)
            .Sum(static file => new FileInfo(file).Length)
            .Should().BeLessThanOrEqualTo(DefaultLimits().MaxPackageBytes);
    }

    [Fact]
    public void ReadLease_BlocksWholePackageDeleteUntilReleased()
    {
        var store = CreateStore();
        var package = store.Write(RepresentativeSnapshot(), Target(4242), StartedAt.AddDays(1));
        var opened = store.Open(
            package.RelativeLocator,
            package.CaptureId,
            package.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));

        Action deleteWhileRead = () => store.Delete(package.RelativeLocator, package.CaptureId);
        deleteWhileRead.Should().Throw<DurableSpikeException>().Which.Code.Should().Be("LeaseConflict");

        opened.Dispose();
        store.Delete(package.RelativeLocator, package.CaptureId);
        Directory.Exists(package.AbsolutePath).Should().BeFalse();
    }

    [Fact]
    public async Task WriterTimeout_RetainsExclusiveOwnershipUntilWriterActuallyStops()
    {
        var store = CreateStore();
        var package = store.Write(
            RepresentativeSnapshot(),
            Target(4242),
            StartedAt.AddDays(1),
            new DurableSpikeWriteOptions(PublishSeal: false));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var timed = await store.StartTimedWriterAsync(
            package.RelativeLocator,
            async () =>
            {
                started.SetResult();
                await release.Task.ConfigureAwait(false);
            },
            TimeSpan.FromMilliseconds(20));
        await started.Task;

        timed.CompletedWithinTimeout.Should().BeFalse();
        Action readWhileWriterContinues = () => store.Open(
            package.RelativeLocator,
            package.CaptureId,
            package.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));
        readWhileWriterContinues.Should().Throw<DurableSpikeException>()
            .Which.Code.Should().Be("LeaseConflict");
        Action recoverWhileWriterContinues = () => store.Recover(
            package.RelativeLocator,
            package.CaptureId,
            package.ArtifactId,
            StartedAt.AddDays(2));
        recoverWhileWriterContinues.Should().Throw<DurableSpikeException>()
            .Which.Code.Should().Be("LeaseConflict");
        Action deleteWhileWriterContinues = () => store.Delete(package.RelativeLocator, package.CaptureId);
        deleteWhileWriterContinues.Should().Throw<DurableSpikeException>()
            .Which.Code.Should().Be("LeaseConflict");
        var cleanupWhileWriterContinues = store.Cleanup(new DurableSpikeCleanupPolicy(
            DateTimeOffset.UtcNow,
            MaxAge: null,
            MaxPackages: 0,
            MaxTotalBytes: 0));
        cleanupWhileWriterContinues.SkippedLeasedCaptureIds.Should().Contain(package.CaptureId);

        release.SetResult();
        await timed.Completion;
        store.Delete(package.RelativeLocator, package.CaptureId);
        Directory.Exists(package.AbsolutePath).Should().BeFalse();
    }

    [Fact]
    public void Cleanup_EnforcesHistoricalCountAndBytesButSkipsActiveReaderLease()
    {
        var store = CreateStore();
        var first = store.Write(RepresentativeSnapshot(), Target(1), StartedAt.AddDays(1));
        var second = store.Write(RepresentativeSnapshot(), Target(2), StartedAt.AddDays(1));
        var third = store.Write(RepresentativeSnapshot(), Target(3), StartedAt.AddDays(1));
        var reader = store.Open(
            first.RelativeLocator,
            first.CaptureId,
            first.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));

        var cleanup = store.Cleanup(new DurableSpikeCleanupPolicy(
            DateTimeOffset.UtcNow,
            MaxAge: null,
            MaxPackages: 1,
            MaxTotalBytes: long.MaxValue));

        cleanup.SkippedLeasedCaptureIds.Should().Contain(first.CaptureId);
        cleanup.DeletedCaptureIds.Should().Contain(second.CaptureId).And.Contain(third.CaptureId);
        Directory.Exists(first.AbsolutePath).Should().BeTrue();

        reader.Dispose();
        var finalCleanup = store.Cleanup(new DurableSpikeCleanupPolicy(
            DateTimeOffset.UtcNow,
            MaxAge: null,
            MaxPackages: 0,
            MaxTotalBytes: 0));
        finalCleanup.DeletedCaptureIds.Should().Contain(first.CaptureId);
        finalCleanup.RemainingBytes.Should().Be(0);
    }

    [Fact]
    public void Cleanup_EnforcesHistoricalByteAndScanLimits()
    {
        var store = CreateStore();
        var first = store.Write(RepresentativeSnapshot(), Target(1), StartedAt.AddDays(1));
        var second = store.Write(RepresentativeSnapshot(), Target(2), StartedAt.AddDays(1));
        var totalBytes = PackageBytes(first.AbsolutePath) + PackageBytes(second.AbsolutePath);

        var cleanup = store.Cleanup(new DurableSpikeCleanupPolicy(
            DateTimeOffset.UtcNow,
            MaxAge: null,
            MaxPackages: int.MaxValue,
            MaxTotalBytes: totalBytes - 1));
        cleanup.DeletedCaptureIds.Should().ContainSingle();
        cleanup.RemainingBytes.Should().BeLessThan(totalBytes);

        var scanLimitedStore = CreateStore(limits: DefaultLimits() with { MaxHistoricalPackagesToScan = 0 });
        Action scan = () => scanLimitedStore.Cleanup(new DurableSpikeCleanupPolicy(
            DateTimeOffset.UtcNow,
            MaxAge: null,
            MaxPackages: int.MaxValue,
            MaxTotalBytes: long.MaxValue));
        scan.Should().Throw<DurableSpikeException>().Which.Code.Should().Be("HistoryScanLimitExceeded");
    }

    [Fact]
    public void Cleanup_PositiveMaxAgeExpiresOldPackageButPreservesFreshPackage()
    {
        var store = CreateStore();
        var old = store.Write(RepresentativeSnapshot(), Target(1), StartedAt.AddDays(1));
        var fresh = store.Write(RepresentativeSnapshot(), Target(2), StartedAt.AddDays(1));
        var now = DateTimeOffset.UtcNow;
        RewriteManifestAndSeal(old.AbsolutePath, manifest => manifest with { CreatedAt = now.AddHours(-2) });

        var cleanup = store.Cleanup(new DurableSpikeCleanupPolicy(
            NowUtc: now,
            MaxAge: TimeSpan.FromHours(1),
            MaxPackages: int.MaxValue,
            MaxTotalBytes: long.MaxValue));

        cleanup.DeletedCaptureIds.Should().Equal(old.CaptureId);
        Directory.Exists(old.AbsolutePath).Should().BeFalse();
        Directory.Exists(fresh.AbsolutePath).Should().BeTrue();
    }

    [LinuxOnlyFact]
    public void Cleanup_RootSymlinkAliasUsesSameLeaseKeyAsOpen()
    {
        var actualRoot = Path.Combine(_workspace, "actual-root");
        Directory.CreateDirectory(actualRoot);
        var aliasRoot = Path.Combine(_workspace, "root-alias");
        Directory.CreateSymbolicLink(aliasRoot, actualRoot);
        var store = new DurableContentionCaptureSpikeStore(aliasRoot, DefaultLimits());
        var package = store.Write(RepresentativeSnapshot(), Target(4242), StartedAt.AddDays(1));
        var opened = store.Open(
            package.RelativeLocator,
            package.CaptureId,
            package.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));

        var cleanup = store.Cleanup(new DurableSpikeCleanupPolicy(
            DateTimeOffset.UtcNow,
            MaxAge: null,
            MaxPackages: 0,
            MaxTotalBytes: 0));

        cleanup.SkippedLeasedCaptureIds.Should().Contain(package.CaptureId);
        Directory.Exists(package.AbsolutePath).Should().BeTrue();
        opened.Dispose();
        store.Delete(package.RelativeLocator, package.CaptureId);
    }

    [WindowsOnlyFact]
    public void Cleanup_CaseVariantLocatorUsesSameLeaseKeyAsEnumeration()
    {
        var store = CreateStore();
        var package = store.Write(RepresentativeSnapshot(), Target(4242), StartedAt.AddDays(1));
        var opened = store.Open(
            package.RelativeLocator.ToUpperInvariant(),
            package.CaptureId,
            package.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));

        var cleanup = store.Cleanup(new DurableSpikeCleanupPolicy(
            DateTimeOffset.UtcNow,
            MaxAge: null,
            MaxPackages: 0,
            MaxTotalBytes: 0));

        cleanup.SkippedLeasedCaptureIds.Should().Contain(package.CaptureId);
        Directory.Exists(package.AbsolutePath).Should().BeTrue();
        opened.Dispose();
        store.Delete(package.RelativeLocator, package.CaptureId);
    }

    [Fact]
    public void TraversalAndAbsoluteLocatorsAreRejected()
    {
        var store = CreateStore();
        var handles = new MemoryDiagnosticHandleStore();

        Action traversal = () => store.Open(
            "../outside",
            "capture",
            "artifact",
            handles,
            TimeSpan.FromMinutes(1));
        traversal.Should().Throw<DurableSpikeException>().Which.Code.Should().Be("InvalidLocator");

        Action absolute = () => store.Open(
            Path.GetFullPath(_workspace),
            "capture",
            "artifact",
            handles,
            TimeSpan.FromMinutes(1));
        absolute.Should().Throw<DurableSpikeException>().Which.Code.Should().Be("InvalidLocator");
    }

    [LinuxOnlyFact]
    public void SymlinkEscapeIsRejected()
    {
        var outsideRoot = Path.Combine(_workspace, "outside-root");
        Directory.CreateDirectory(outsideRoot);
        var outsideStore = new DurableContentionCaptureSpikeStore(outsideRoot, DefaultLimits());
        var outside = outsideStore.Write(RepresentativeSnapshot(), Target(4242), StartedAt.AddDays(1));
        var link = Path.Combine(_root, "escape-link");
        Directory.CreateSymbolicLink(link, outside.AbsolutePath);

        var store = CreateStore();
        Action open = () => store.Open(
            "escape-link",
            outside.CaptureId,
            outside.ArtifactId,
            new MemoryDiagnosticHandleStore(),
            TimeSpan.FromMinutes(1));

        open.Should().Throw<Exception>()
            .Where(exception => exception is DotnetDiagnostics.Core.Artifacts.ArtifactPathException);

        var movable = store.Write(RepresentativeSnapshot(), Target(4242), StartedAt.AddDays(1));
        Action move = () => store.MovePackage(movable.RelativeLocator, "escape-link/moved-package");
        move.Should().Throw<Exception>()
            .Where(exception => exception is DotnetDiagnostics.Core.Artifacts.ArtifactPathException);
        Directory.Exists(movable.AbsolutePath).Should().BeTrue();
    }

    private DurableContentionCaptureSpikeStore CreateStore(
        Func<DurableSpikeAccessRequest, bool>? authorize = null,
        DurableSpikeLimits? limits = null)
        => new(_root, limits ?? DefaultLimits(), authorize);

    private static DurableSpikeLimits DefaultLimits() => new(
        MaxEvents: 200,
        MaxNotes: 32,
        MaxStringChars: 4_096,
        MaxEstimatedCopyBytes: 512_000,
        MaxRepresentationBytes: 1_000_000,
        MaxManifestBytes: 64_000,
        MaxSealBytes: 16_000,
        MaxPackageBytes: 2_000_000,
        MaxPackageFiles: 3,
        MaxHistoricalPackagesToScan: 32);

    private static DurableSpikeTarget Target(int processId) => new(
        ProcessId: processId,
        ProcessStartedAt: StartedAt.AddMinutes(-5),
        Runtime: "CoreClr",
        RuntimeVersion: "10.0.0",
        OperatingSystem: "fixture-os",
        ProcessArchitecture: "x64",
        ManagedEntrypointAssembly: "Fixture.App",
        CommandLineDigest: "sha256:fixture",
        HostBinding: "fixture-host");

    private static ContentionSnapshot RepresentativeSnapshot(int processId = 4242) => new(
        ProcessId: processId,
        StartedAt: StartedAt,
        Duration: TimeSpan.FromSeconds(10),
        TotalEvents: 5,
        DistinctMonitors: 3,
        TotalContentionDuration: TimeSpan.FromMilliseconds(37),
        P50ContentionDuration: TimeSpan.FromMilliseconds(5),
        P95ContentionDuration: TimeSpan.FromMilliseconds(20),
        MaxContentionDuration: TimeSpan.FromMilliseconds(21),
        Events:
        [
            Event(1, 0x10, 7, "Fixture.Worker.Run"),
            Event(2, 0x20, null, "Fixture.Cache.Get"),
        ],
        Notes:
        [
            "Retained the 2 longest contention event(s) after reaching the in-memory cap of 200; 3 shorter event(s) were dropped.",
            "Contention duration percentiles are exact up to the bounded exact capacity and approximate afterward.",
        ]);

    private static ContentionEventSample Event(
        int ordinal,
        ulong lockId,
        int? ownerThreadId,
        string method)
    {
        var start = StartedAt.AddMilliseconds(ordinal * 10);
        var duration = TimeSpan.FromMilliseconds(ordinal + 1);
        return new ContentionEventSample(
            StartedAt: start,
            StoppedAt: start + duration,
            Duration: duration,
            ContendingThreadId: ordinal + 100,
            OwnerManagedThreadId: ownerThreadId,
            LockId: lockId,
            AssociatedObjectId: (ulong)(ordinal + 1_000),
            CallSiteMethod: method,
            CallSiteModule: "Fixture.Module");
    }

    private static string RepresentationPath(string packagePath, string artifactId)
        => Path.Combine(packagePath, "representations", $"{artifactId}.json");

    private static IReadOnlyDictionary<string, FileStamp> FileStamps(string packagePath)
        => Directory.EnumerateFiles(packagePath, "*", SearchOption.AllDirectories)
            .ToDictionary(
                file => Path.GetRelativePath(packagePath, file),
                file => new FileStamp(
                    new FileInfo(file).Length,
                    File.GetLastWriteTimeUtc(file),
                    HashFile(file)),
                StringComparer.Ordinal);

    private static void RewriteManifestAndSeal(
        string packagePath,
        Func<DurableSpikeManifest, DurableSpikeManifest> rewrite)
    {
        var manifestPath = Path.Combine(packagePath, "manifest.json");
        var manifest = JsonSerializer.Deserialize<DurableSpikeManifest>(
            File.ReadAllBytes(manifestPath),
            JsonOptions)
            ?? throw new InvalidOperationException("Fixture manifest was empty.");
        File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(rewrite(manifest), JsonOptions));

        var sealPath = Path.Combine(packagePath, "seal.json");
        var seal = JsonSerializer.Deserialize<DurableSpikeSeal>(
            File.ReadAllBytes(sealPath),
            JsonOptions)
            ?? throw new InvalidOperationException("Fixture seal was empty.");
        seal = seal with
        {
            ManifestLength = new FileInfo(manifestPath).Length,
            ManifestSha256 = HashFile(manifestPath),
        };
        File.WriteAllBytes(sealPath, JsonSerializer.SerializeToUtf8Bytes(seal, JsonOptions));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static long PackageBytes(string packagePath)
        => Directory.EnumerateFiles(packagePath, "*", SearchOption.AllDirectories)
            .Sum(static file => new FileInfo(file).Length);

    private sealed record FileStamp(long Length, DateTime LastWriteUtc, string Sha256);
}
