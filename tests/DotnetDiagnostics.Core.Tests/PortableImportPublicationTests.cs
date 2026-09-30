using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

[Collection("PortableExportResources")]
public sealed class PortableImportPublicationTests : IDisposable
{
    private sealed record RootProvider(string Root) : IArtifactRootProvider;
    private static readonly CaptureAccess Owner = new("publication-owner");
    private static readonly AuthorizePortableImport Allow = static (_, _, _, _) => ValueTask.CompletedTask;
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "publication-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public async Task DenialAndCancellationPersistExactPreOrPartialPublicationOutcomes(int deniedEntry, bool cancel)
    {
        using var fixture = await CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var calls = new List<(PortableAuthorizationPhase Phase, string? Entry)>();
        var publishCalls = 0;
        var failure = await Record.ExceptionAsync(() => fixture.RunAsync((entries, phase, entry, _) =>
        {
            Assert.Equal(3, entries.Count);
            calls.Add((phase, entry));
            var deny = phase == PortableAuthorizationPhase.Prepare ? deniedEntry == -1 : publishCalls++ == deniedEntry;
            if (deny)
            {
                if (cancel) cancellation.Cancel();
                else throw new CaptureStoreException(CaptureErrorCode.Forbidden, "Revoked: component policy denies publication.");
            }
            return ValueTask.CompletedTask;
        }, cancellation.Token));

        if (deniedEntry < 1)
        {
            if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure);
            else Assert.Equal(CaptureErrorCode.Forbidden, Assert.IsType<CaptureStoreException>(failure).Code);
        }
        else Assert.Null(failure);
        var result = fixture.Publication.Result;
        Assert.False(result.Complete);
        Assert.Equal(cancel, result.Cancelled);
        Assert.Equal(cancel ? CaptureErrorCode.Incomplete : CaptureErrorCode.Forbidden, result.Failure!.Code);
        Assert.Equal(cancel ? "Cancelled" : "Revoked", result.Failure.Reason);
        Assert.Equal(1, calls.Count(static call => call.Phase == PortableAuthorizationPhase.Prepare));
        Assert.Equal(deniedEntry + 1, publishCalls);
        for (var i = 0; i < 3; i++)
        {
            var state = i < deniedEntry ? PortableEntryState.Published :
                i == deniedEntry ? cancel ? PortableEntryState.Cancelled : PortableEntryState.Failed : PortableEntryState.NotAttempted;
            Assert.Equal(state, result.Entries[i].State);
            Assert.Equal(state == PortableEntryState.Published, result.Entries[i].Mapping is not null);
            Assert.Equal(state == PortableEntryState.Published, Directory.Exists(fixture.PublishedPath(i)));
            Assert.False(Directory.Exists(fixture.Destinations[i]));
        }
        if (deniedEntry >= 0)
            Assert.Equal(fixture.Mappings[deniedEntry].EntryId, result.Entries[deniedEntry].Failure!.EntryId);
        if (deniedEntry == 1)
        {
            using var reader = await fixture.Store.OpenAsync(result.Entries[0].Mapping!.LocalCaptureId, Owner);
            Assert.Single(reader.Query(new(reader.Info.Artifacts[0].ArtifactId)).Records);
        }
        await fixture.AssertReceiptAndRetryAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentOwnerIsEnforcedIndependentlyEvenWhenAllOwnersIsGranted(bool allOwners)
    {
        using var fixture = await CreateAsync(access: Owner with { AllOwners = allOwners }, fixtureOwner: new("producer-owner"));
        var publishCalls = 0;
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() => fixture.RunAsync((_, phase, _, _) =>
        {
            if (phase == PortableAuthorizationPhase.Publish) publishCalls++;
            return ValueTask.CompletedTask;
        }));
        Assert.Equal(CaptureErrorCode.Forbidden, error.Code);
        Assert.Equal(0, publishCalls);
        Assert.Empty((await fixture.Store.ListAsync(Owner)).Captures);
        Assert.All(fixture.Publication.Result.Entries, static entry => Assert.Null(entry.Mapping));
        await fixture.AssertReceiptAndRetryAsync();
    }

    [Fact]
    public async Task PublicationHoldsAdmissionAndRechecksCollisionAfterPolicyCallback()
    {
        using var fixture = await CreateAsync();
        var publishCalls = 0;
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() => fixture.RunAsync((_, phase, entry, _) =>
        {
            if (phase == PortableAuthorizationPhase.Publish)
            {
                publishCalls++;
                Assert.Equal(fixture.Mappings[0].EntryId, entry);
                var busy = Assert.Throws<CaptureStoreException>(() =>
                {
                    using var control = SqliteCaptureStore.PortableAdmission(fixture.Root);
                });
                Assert.Equal(CaptureErrorCode.Busy, busy.Code);
                File.WriteAllText(Path.Combine(fixture.Root, ".deleted-" + fixture.Infos[0].CaptureId), "purpose-created tombstone");
            }
            return ValueTask.CompletedTask;
        }));
        Assert.Equal(CaptureErrorCode.InvalidInput, error.Code);
        Assert.Contains("IdentityCollision", error.Message);
        Assert.Equal(1, publishCalls);
        Assert.False(Directory.Exists(fixture.PublishedPath(0)));
        await fixture.AssertReceiptAndRetryAsync();
    }

    [Fact]
    public async Task RealCaptureQuotaAfterFirstPublicationProducesPartialReceipt()
    {
        using var fixture = await CreateAsync(new() { MaxCaptures = 1 });
        var result = await fixture.RunAsync(Allow);
        Assert.False(result.Complete);
        Assert.Equal(PortableEntryState.Published, result.Entries[0].State);
        Assert.Equal(PortableEntryState.Failed, result.Entries[1].State);
        Assert.Equal(PortableEntryState.NotAttempted, result.Entries[2].State);
        Assert.Null(result.Entries[1].Mapping);
        Assert.Null(result.Entries[2].Mapping);
        Assert.Equal("MaxCaptures", result.Failure!.Limit);
        Assert.Equal(2, result.Failure.Observed);
        Assert.Equal(1, result.Failure.Maximum);
        Assert.Single((await fixture.Store.ListAsync(Owner)).Captures);
        await fixture.AssertReceiptAndRetryAsync();
    }

    [Fact]
    public async Task SuccessfulPublicationTransfersReservationsAndPersistsStableOwnerBoundMappings()
    {
        using var fixture = await CreateAsync();
        var result = await fixture.RunAsync(Allow);
        Assert.True(result.Complete);
        Assert.All(result.Entries, static entry => Assert.Equal(PortableEntryState.Published, entry.State));
        Assert.Equal(3, (await fixture.Store.ListAsync(Owner)).Captures.Count);
        await fixture.AssertReceiptAndRetryAsync();
    }

    private async Task<Fixture> CreateAsync(CaptureStoreOptions? options = null, CaptureAccess? access = null,
        CaptureAccess? fixtureOwner = null)
    {
        access ??= Owner;
        fixtureOwner ??= Owner;
        var producerRoot = Path.Combine(_root, "producer");
        var producer = new SqliteCaptureStore(new RootProvider(producerRoot));
        var infos = new CaptureInfo[3];
        for (var i = 0; i < infos.Length; i++)
        {
            // Ordinary Core writes trusted fixtures; no foreign SQLite or native-worker substitution.
            await using var writer = await producer.CreateAsync(new("trusted publication fixture"), fixtureOwner);
            var artifact = writer.AddArtifact("counters", "fixture");
            Assert.True(await writer.AppendAsync(artifact, new(Name: "known record")));
            infos[i] = await writer.CompleteAsync();
        }
        var store = new SqliteCaptureStore(new RootProvider(Path.Combine(_root, "destination")), options);
        var root = store.InitializePortableRoot();
        var key = new PortableOperationKey(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var mappings = infos.Select(info => new PortableEntryMapping(Guid.NewGuid().ToString("N"), "same label",
            Guid.NewGuid().ToString("N"), info.CaptureId,
            info.Artifacts.Select(static artifact => new PortableArtifactMapping(artifact.ArtifactId, artifact.ArtifactId, artifact.ArtifactId)).ToArray())).ToArray();
        var initial = new PortableImportResult(key.Id, Guid.NewGuid().ToString("N"), new string('a', 64), false, false,
            mappings.Select(static mapping => new PortableEntryResult(mapping.EntryId, PortableEntryState.Pending, null, null)).ToArray(), null);
        const string fingerprint = "trusted-component-fixture";
        var storage = PortableCaptureStorage.Begin(store, key, access, fingerprint, DateTimeOffset.UtcNow, new(initial, [], false));
        try
        {
            var budget = new PortableImportBudget(storage);
            var destinations = new string[3];
            var reservations = new long[3];
            var work = SafeArtifactPath.ResolveCaptureDirectory(storage.DirectoryPath, "work");
            for (var i = 0; i < infos.Length; i++)
            {
                var source = Path.Combine(producerRoot, "captures", infos[i].CaptureId);
                reservations[i] = CapturePackage.PackageBytes(source);
                budget.Reserve(reservations[i]);
                var entry = SafeArtifactPath.ResolveCaptureDirectory(work, i.ToString("D2", CultureInfo.InvariantCulture));
                destinations[i] = SafeArtifactPath.ResolveCaptureDirectory(entry, "destination");
                foreach (var path in Directory.EnumerateFiles(source))
                    File.Copy(path, Path.Combine(destinations[i], Path.GetFileName(path)));
            }
            return new(store, root, storage, budget, access, key, fingerprint, initial, infos, mappings, destinations, reservations);
        }
        catch
        {
            storage.Dispose();
            throw;
        }
    }

    private sealed class Fixture(SqliteCaptureStore store, string root, PortableCaptureStorage storage, PortableImportBudget budget,
        CaptureAccess access, PortableOperationKey key, string fingerprint, PortableImportResult initial,
        CaptureInfo[] infos, PortableEntryMapping[] mappings, string[] destinations, long[] reservations) : IDisposable
    {
        internal SqliteCaptureStore Store { get; } = store;
        internal string Root { get; } = root;
        internal CaptureInfo[] Infos { get; } = infos;
        internal PortableEntryMapping[] Mappings { get; } = mappings;
        internal string[] Destinations { get; } = destinations;
        internal PortableCaptureUseCases.ImportPublication Publication { get; } = new(store, root, storage, budget, access, initial);
        internal string PublishedPath(int index) => Path.Combine(Root, Infos[index].CaptureId);

        internal async Task<PortableImportResult> RunAsync(AuthorizePortableImport authorize, CancellationToken token = default)
        {
            var descriptors = Array.AsReadOnly(Infos.Select((info, i) =>
                new PortableImportEntry(Mappings[i].EntryId, Mappings[i].Label, info, CapturePackage.CurrentFormat)).ToArray());
            var index = -1;
            try
            {
                await Publication.PrepareAsync(descriptors, Mappings, authorize, token);
                for (index = 0; index < Infos.Length; index++)
                {
                    var before = storage.Receipt.ReservationBytes;
                    await Publication.PublishAsync(Destinations[index], Infos[index], index, reservations[index], descriptors, authorize, token);
                    Assert.Equal(before - reservations[index], storage.Receipt.ReservationBytes);
                    Assert.False(Directory.Exists(Destinations[index]));
                }
                return Publication.Complete();
            }
            catch (Exception error) when (error is CaptureStoreException or OperationCanceledException or IOException)
            {
                return Publication.Fail(error, index, token.IsCancellationRequested, ioOutstanding: false);
            }
        }

        internal async Task AssertReceiptAndRetryAsync()
        {
            var expected = JsonSerializer.Serialize(Publication.Result);
            Assert.Equal(PortableBounds.ReceiptReservation, storage.Receipt.ReservationBytes);
            storage.Dispose();
            var service = new PortableCaptureUseCases(Store, static (_, _) => ValueTask.CompletedTask);
            Assert.Equal(expected, JsonSerializer.Serialize(await service.GetImportResultAsync(key, access)));
            var denied = await Assert.ThrowsAsync<CaptureStoreException>(() =>
                service.GetImportResultAsync(key, new("another-owner", AllOwners: true)));
            Assert.Equal(CaptureErrorCode.NotFound, denied.Code);
            using (var retry = PortableCaptureStorage.Begin(Store, key, access, fingerprint, DateTimeOffset.UtcNow, new(initial, [], false)))
            {
                Assert.True(retry.Reused);
                Assert.True(retry.Receipt.Import!.Terminal);
                Assert.Equal(expected, JsonSerializer.Serialize(retry.Receipt.Import.Result));
            }
            var conflict = Assert.Throws<CaptureStoreException>(() =>
            {
                using var changed = PortableCaptureStorage.Begin(Store, key, access, "changed-fingerprint",
                    DateTimeOffset.UtcNow, new(initial, [], false));
            });
            Assert.Contains("OperationConflict", conflict.Message);
        }

        public void Dispose() => storage.Dispose();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
