using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class PortableCaptureImportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnconfirmedParentIoRetainsFilesAndReservationsAcrossReceiptReconciliation(bool terminal)
    {
        if (!Linux) return;
        var store = Store();
        _ = store.InitializePortableRoot();
        var key = Key();
        var result = new PortableImportResult(key.Id, null, new string('a', 64), false, false, [], null);
        string archive;
        string receipt;
        byte[] before;
        using (var storage = PortableCaptureStorage.Begin(store, key, Owner, new string('b', 64),
            DateTimeOffset.UtcNow, new(result, [], terminal, ParentIo: PortableWorkerIdentity.Capture(Environment.ProcessId))))
        {
            storage.Reserve(1024 * 1024);
            archive = storage.ArchivePath;
            receipt = Receipt(key);
            File.WriteAllBytes(archive, [1, 2, 3]);
            before = File.ReadAllBytes(receipt);
            var failure = Assert.Throws<CaptureStoreException>(storage.CleanImport);
            Assert.Contains("ImportWorkerUnconfirmed", failure.Message);
            Assert.Equal(before, File.ReadAllBytes(receipt));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(archive));
        }
        var reconciliation = Assert.Throws<CaptureStoreException>(() =>
            PortableCaptureStorage.ReadImportResult(store, key, Owner, DateTimeOffset.UtcNow));
        Assert.Contains("ImportWorkerUnconfirmed", reconciliation.Message);
        Assert.Equal(before, File.ReadAllBytes(receipt));
        Assert.True(File.Exists(archive));
    }

    [Fact]
    public async Task ArchiveReservationPrecedesConsumptionAndDoesNotBypassLowerStoreCapacity()
    {
        if (!Linux) return;
        var store = new SqliteCaptureStore(new RootProvider(Path.Combine(_root, "small")),
            new() { MaxDatabaseBytes = 128 * 1024, MaxPackageBytes = 128 * 1024, MaxStoreBytes = 1024 * 1024 });
        var service = new PortableCaptureUseCases(store, static (_, _) => ValueTask.CompletedTask,
            importWorker: new(Path.Combine(AppContext.BaseDirectory, "capture-worker"),
                Path.Combine(AppContext.BaseDirectory, "runtimes/linux-x64/native/libe_sqlite3.so")));
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() => service.ImportAsync(
            new(Key(), 2 * 1024 * 1024, new string('0', 64)), new NoRead(), Owner, Allow));
        Assert.Equal(CaptureErrorCode.CapacityExceeded, error.Code);
        Assert.Contains("MaxStoreBytes", error.Message);
        Assert.Empty((await store.ListAsync(Owner)).Captures);
    }

    [Fact]
    public async Task PortableAndOrdinaryWriterSlotsAreShared()
    {
        if (!Linux) return;
        await using var first = await Store().CreateAsync(new("active one"), Owner);
        await using var second = await Store().CreateAsync(new("active two"), Owner);
        var bytes = Frozen();
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() => Service().ImportAsync(
            new(Key(), bytes.Length, Hash(bytes)), new MemoryStream(bytes), Owner, Allow));
        Assert.Equal(CaptureErrorCode.Busy, error.Code);
        Assert.Equal(2, (await Store().ListAsync(Owner)).Captures.Count);
    }

    [Fact]
    public async Task OneOwnerAndOneValidatorPreventConcurrentImportsWithoutReadingRejectedSources()
    {
        if (!Linux) return;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bytes = Frozen();
        var first = Service().ImportAsync(new(Key(), bytes.Length, Hash(bytes)), new MemoryStream(bytes), Owner,
            async (_, phase, _, token) =>
            {
                if (phase != PortableAuthorizationPhase.Prepare) return;
                reached.SetResult();
                await release.Task.WaitAsync(token);
            });
        try
        {
            await Task.WhenAny(reached.Task, first);
            if (!reached.Task.IsCompleted) await first;
            var owner = await Assert.ThrowsAsync<CaptureStoreException>(() => Service().ImportAsync(
                new(Key(), bytes.Length, Hash(bytes)), new NoRead(), Owner, Allow));
            Assert.Equal(CaptureErrorCode.Busy, owner.Code);
            var validator = await Assert.ThrowsAsync<CaptureStoreException>(() => Service().ImportAsync(
                new(Key(), bytes.Length, Hash(bytes)), new NoRead(), new("other-owner"), Allow));
            Assert.Equal(CaptureErrorCode.Busy, validator.Code);
        }
        finally { release.TrySetResult(); }
        Assert.True((await first).Complete);
    }

    [Fact]
    public async Task PlannedSealReconciliationDoesNotDuplicateAlreadyPublishedDirectories()
    {
        if (!Linux) return;
        var bytes = Frozen();
        var request = new CaptureImportRequest(Key(), bytes.Length, Hash(bytes));
        var completed = await Service().ImportAsync(request, new MemoryStream(bytes), Owner, Allow);
        var receiptPath = Receipt(request.Operation);
        var document = JsonNode.Parse(await File.ReadAllBytesAsync(receiptPath))!;
        document["import"]!["terminal"] = false;
        document["import"]!["result"]!["complete"] = false;
        foreach (var item in document["import"]!["result"]!["entries"]!.AsArray())
            item!["state"] = (int)PortableEntryState.Pending;
        await File.WriteAllTextAsync(receiptPath, document.ToJsonString());
        var recovered = await Service().GetImportResultAsync(request.Operation, Owner);
        Assert.True(recovered.Complete);
        Assert.Equal(JsonSerializer.Serialize(completed), JsonSerializer.Serialize(recovered));
        Assert.Equal(2, (await Store().ListAsync(Owner)).Captures.Count);
        Assert.Equal(JsonSerializer.Serialize(recovered), JsonSerializer.Serialize(
            await Service().ImportAsync(request, new NoRead(), Owner, Allow)));
    }

    [Fact]
    public async Task ConflictingPlannedSealIsNotAdoptedAndRemainsAccounted()
    {
        if (!Linux) return;
        var bytes = Frozen();
        var request = new CaptureImportRequest(Key(), bytes.Length, Hash(bytes));
        var completed = await Service().ImportAsync(request, new MemoryStream(bytes), Owner, Allow);
        var path = Receipt(request.Operation);
        var document = JsonNode.Parse(await File.ReadAllBytesAsync(path))!;
        document["import"]!["terminal"] = false;
        document["import"]!["result"]!["entries"]![0]!["state"] = (int)PortableEntryState.Pending;
        document["import"]!["plans"]![0]!["sealHash"] = new string('0', 64);
        await File.WriteAllTextAsync(path, document.ToJsonString());
        var before = await File.ReadAllBytesAsync(path);
        var failure = await Assert.ThrowsAsync<CaptureStoreException>(() => Service().GetImportResultAsync(request.Operation, Owner));
        Assert.Equal(CaptureErrorCode.StorageFailure, failure.Code);
        Assert.Contains("PublicationConflict", failure.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        foreach (var entry in completed.Entries)
            Assert.True(Directory.Exists(Path.Combine(_root, "one/captures", entry.Mapping!.LocalCaptureId)));
    }

    [Fact]
    public async Task LowerCaptureQuotaReturnsExplicitPartialOutcomeAndNumericLimit()
    {
        if (!Linux) return;
        var store = new SqliteCaptureStore(new RootProvider(Path.Combine(_root, "limited")), new() { MaxCaptures = 1 });
        var service = new PortableCaptureUseCases(store, static (_, _) => ValueTask.CompletedTask, importWorker:
            new(Path.Combine(AppContext.BaseDirectory, "capture-worker"),
                Path.Combine(AppContext.BaseDirectory, "runtimes/linux-x64/native/libe_sqlite3.so")));
        var bytes = Frozen();
        var result = await service.ImportAsync(new(Key(), bytes.Length, Hash(bytes)), new MemoryStream(bytes), Owner, Allow);
        Assert.False(result.Complete);
        Assert.Equal(PortableEntryState.Published, result.Entries[0].State);
        Assert.Equal(PortableEntryState.Failed, result.Entries[1].State);
        Assert.Equal("MaxCaptures", result.Failure!.Limit);
        Assert.Equal(2, result.Failure.Observed);
        Assert.Equal(1, result.Failure.Maximum);
        Assert.Single((await store.ListAsync(Owner)).Captures);
    }

    [Fact]
    public async Task SixteenIndependentEntriesFitTheSharedPrivateBudget()
    {
        if (!Linux) return;
        var originals = ReadArchive(Frozen());
        var index = JsonNode.Parse(originals[0].Bytes)!;
        var template = index["entries"]![0]!.DeepClone();
        var entries = new JsonArray();
        var members = new List<(string Name, byte[] Bytes)> { originals[0], originals[1] };
        for (var i = 0; i < 16; i++)
        {
            var entry = template.DeepClone();
            var id = Guid.NewGuid().ToString("N");
            entry["entryId"] = id;
            entries.Add(entry);
            foreach (var member in originals.Skip(2).Take(3))
                members.Add(($"entries/{id}/{member.Name[(member.Name.LastIndexOf('/') + 1)..]}", member.Bytes));
        }
        index["entries"] = entries;
        UpdateIndex(members, index);
        var bytes = Zip(members);
        var result = await Service().ImportAsync(new(Key(), bytes.Length, Hash(bytes)), new MemoryStream(bytes), Owner, Allow);
        Assert.True(result.Complete, JsonSerializer.Serialize(result));
        Assert.Equal(16, result.Entries.Count);
        Assert.Equal(16, result.Entries.Select(static entry => entry.Mapping!.LocalCaptureId).Distinct(StringComparer.Ordinal).Count());
    }

    private string Receipt(PortableOperationKey key) => Path.Combine(_root, "one/captures/.portable",
        PortableCaptureStorage.Digest(CapturePackage.Utf8.GetBytes(Owner.OwnerId + "\0" + key.Id)), "receipt.json");
}
