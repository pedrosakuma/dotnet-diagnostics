using System.Security.Cryptography;
using System.Text.Json;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class PortableCaptureExportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleasedNonterminalUpload_ReconcilesToInterruptedWithoutResuming(bool queryBeforeRetry)
    {
        var (store, request, original) = PersistedReceivingUpload();
        var path = original.DirectoryPath;
        var persisted = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(path, "receipt.json")),
            PortableCaptureJson.Default.PortableExportReceipt)!;
        Assert.False(persisted.Import!.Terminal);
        Assert.Null(persisted.Import.Result.Failure);
        Assert.Equal(75, persisted.Transfer!.ReceivedBytes);
        Assert.Equal(75, new FileInfo(original.ArchivePath).Length);
        original.Dispose(); // All fixture I/O is closed; explicitly release only the process lease, not transfer cleanup.

        var service = new PortableCaptureUseCases(store, static (_, _) => ValueTask.CompletedTask, timeProvider: _clock);
        if (queryBeforeRetry)
            AssertInterrupted(await service.GetImportResultAsync(request.Operation, Owner));
        await using var retry = service.BeginUpload(request, Owner,
            static (_, _, _, _) => throw new InvalidOperationException("Interrupted upload must not be validated or published."));
        AssertInterrupted(retry.ExistingResult!);
        Assert.Same(retry.ExistingResult, await retry.CommitAsync());
        Assert.Equal(CaptureErrorCode.InvalidInput, (await Assert.ThrowsAsync<CaptureStoreException>(() =>
            retry.AppendAsync(75, new byte[25]))).Code);
        Assert.False(File.Exists(Path.Combine(path, "bundle.ddcapture")));
        Assert.Empty((await store.ListAsync(Owner)).Captures);
        await retry.DisposeAsync();
        AssertInterrupted(await service.GetImportResultAsync(request.Operation, Owner));
        Assert.Equal(CaptureErrorCode.NotFound, (await Assert.ThrowsAsync<CaptureStoreException>(() =>
            service.GetImportResultAsync(request.Operation, new("other", true)))).Code);
        var conflict = Assert.Throws<CaptureStoreException>(() => service.BeginUpload(
            request with { ArchiveBytes = 101 }, Owner, static (_, _, _, _) => ValueTask.CompletedTask));
        Assert.Equal(CaptureErrorCode.InvalidInput, conflict.Code);
        Assert.Contains("OperationConflict", conflict.Message);
    }

    [Fact]
    public async Task LeaseReleasedAfterCleanupProbe_ReconcilesAgainInsideReusedLease()
    {
        var (store, request, original) = PersistedReceivingUpload();
        var path = original.DirectoryPath;
        PortableCaptureTransfer retry;
        using (SqliteCaptureStore.PortableAdmission(store.PortableRoot()))
        {
            // Reproduce CleanupLocked's active-lease observation, then the owner exiting
            // before Begin's later reuse step. No GC/finalizer timing or native process is involved.
            Assert.Throws<IOException>(() => new FileStream(Path.Combine(path, ".lease"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            var receipt = original.Receipt;
            Assert.False(receipt.Import!.Terminal);
            original.Dispose();
            var reused = PortableCaptureStorage.ReuseUnderAdmission(store, path, receipt, import: true);
            Assert.True(reused.Receipt.Import!.Terminal);
            AssertInterrupted(reused.Receipt.Import.Result);
            Assert.Equal(PortableBounds.ReceiptReservation, reused.Receipt.ReservationBytes);
            Assert.False(File.Exists(reused.ArchivePath));
            retry = new PortableCaptureTransfer(reused, request,
                static _ => throw new InvalidOperationException("The stopped upload is terminal, not resumable."));
        }
        await using (retry)
        {
            AssertInterrupted(retry.ExistingResult!);
            Assert.Same(retry.ExistingResult, await retry.CommitAsync());
            Assert.Empty((await store.ListAsync(Owner)).Captures);
        }
    }

    private (SqliteCaptureStore Store, CaptureImportRequest Request, PortableCaptureStorage Storage) PersistedReceivingUpload()
    {
        var store = Store();
        _ = store.InitializePortableRoot();
        var request = new CaptureImportRequest(new(Guid.NewGuid().ToString("N"), _clock.Now), 100, new string('0', 64));
        var initial = new PortableImportResult(request.Operation.Id, null, request.ArchiveSha256, false, false, [], null);
        var fingerprint = PortableCaptureStorage.Digest(CapturePackage.Utf8.GetBytes(
            FormattableString.Invariant($"import/{request.ArchiveBytes}/{request.ArchiveSha256}")));
        var storage = PortableCaptureStorage.Begin(store, request.Operation, Owner, fingerprint, _clock.Now, new(initial, [], false));
        storage.Reserve(request.ArchiveBytes);
        using (var bytes = SafeArtifactPath.CreateRestrictedFile(storage.ArchivePath))
        {
            bytes.Write(new byte[75]);
            bytes.Flush(flushToDisk: true);
        }
        storage.SaveTransfer(upload: true, 75);
        return (store, request, storage);
    }

    private static void AssertInterrupted(PortableImportResult result)
    {
        Assert.NotNull(result);
        Assert.False(result.Complete);
        Assert.False(result.Cancelled);
        Assert.Null(result.BundleId);
        Assert.Empty(result.Entries);
        Assert.Equal(CaptureErrorCode.Incomplete, result.Failure!.Code);
        Assert.Equal("ImportInterrupted", result.Failure.Reason);
    }

    [Fact]
    public async Task TransferCallAdmissionSharesItsFiniteCounterAcrossStoreInstances()
    {
        var first = Exporter();
        first.AdmitTransferCall();
        Assert.False(Directory.Exists(_root));
        await CreateAsync();
        var second = Exporter();
        for (var i = 0; i < 100; i++)
            (i % 2 == 0 ? first : second).AdmitTransferCall();
        var excess = Assert.Throws<CaptureStoreException>(() => Exporter().AdmitTransferCall());
        Assert.Equal(CaptureErrorCode.Busy, excess.Code);
        Assert.Equal(16, new FileInfo(Path.Combine(_root, "captures", ".portable-calls")).Length);
        _clock.Now = _clock.Now.AddSeconds(1);
        second.AdmitTransferCall();
        await first.ExportAsync(Request(new CaptureExportSelection((await Store().ListAsync(Owner)).Captures[0].CaptureId, null)),
            Stream.Null, Owner);
    }

    [Fact]
    public async Task HeldExport_RandomReadsReuseOneArchiveAndHoldPortableSlotAndSourceLease()
    {
        var capture = await CreateAsync();
        var service = Exporter();
        var request = Request(new CaptureExportSelection(capture.CaptureId, "held"));
        await using var transfer = await service.PrepareExportAsync(request, Owner);
        Assert.NotNull(transfer.Export);
        var busy = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            Exporter().ExportAsync(Request(new CaptureExportSelection(capture.CaptureId, null)), Stream.Null, Owner));
        Assert.Equal(CaptureErrorCode.Busy, busy.Code);
        var bytes = new byte[checked((int)transfer.ArchiveBytes)];
        for (var offset = 0; offset < bytes.Length; offset += 4096)
            Assert.Equal(Math.Min(4096, bytes.Length - offset),
                await transfer.ReadAsync(offset, bytes.AsMemory(offset, Math.Min(4096, bytes.Length - offset))));
        Assert.Equal(transfer.ArchiveSha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var replay = new byte[Math.Min(24 * 1024, bytes.Length)];
        await transfer.ReadAsync(0, replay);
        Assert.Equal(bytes[..replay.Length], replay);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "captures", ".portable"), "bundle.ddcapture",
            SearchOption.AllDirectories));
        await transfer.DisposeAsync();
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "captures", ".portable"), "bundle.ddcapture",
            SearchOption.AllDirectories));
        await service.ExportAsync(Request(new CaptureExportSelection(capture.CaptureId, null)), Stream.Null, Owner);
    }

    [Fact]
    public async Task HeldExport_RechecksAuthorizationOnEveryRead_AndRetainsSlotUntilIoQuiesces()
    {
        var capture = await CreateAsync();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = false;
        var deny = false;
        var service = Exporter(authorize: async (_, _) =>
        {
            if (deny) throw new CaptureStoreException(CaptureErrorCode.Forbidden, "Revoked");
            if (block) { readStarted.SetResult(); await release.Task; }
        });
        var transfer = await service.PrepareExportAsync(Request(new CaptureExportSelection(capture.CaptureId, null)), Owner);
        block = true;
        var read = transfer.ReadAsync(0, new byte[1]);
        await readStarted.Task;
        var disposal = transfer.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        var busy = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            Exporter().ExportAsync(Request(new CaptureExportSelection(capture.CaptureId, null)), Stream.Null, Owner));
        Assert.Equal(CaptureErrorCode.Busy, busy.Code);
        release.SetResult();
        await read;
        await disposal;
        block = false;
        await using var next = await service.PrepareExportAsync(Request(new CaptureExportSelection(capture.CaptureId, null)), Owner);
        deny = true;
        Assert.Equal(CaptureErrorCode.Forbidden, (await Assert.ThrowsAsync<CaptureStoreException>(() =>
            next.ReadAsync(0, new byte[1]))).Code);
    }

    [Fact]
    public async Task Upload_ReservesBeforeReceiving_PersistsOffset_AndCancelsIntoDurableReceipt()
    {
        var bytes = new byte[100];
        var key = new PortableOperationKey(Guid.NewGuid().ToString("N"), _clock.Now);
        var request = new CaptureImportRequest(key, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        var service = Exporter();
        var upload = service.BeginUpload(request, Owner, static (_, _, _, _) => ValueTask.CompletedTask);
        Assert.Equal(0, upload.ReceivedBytes);
        var conflict = Assert.Throws<CaptureStoreException>(() =>
            Exporter().BeginUpload(request with { Operation = new(Guid.NewGuid().ToString("N"), _clock.Now) }, Owner,
                static (_, _, _, _) => ValueTask.CompletedTask));
        Assert.Equal(CaptureErrorCode.Busy, conflict.Code);
        await upload.AppendAsync(0, bytes.AsMemory(0, 25));
        Assert.Equal(25, upload.ReceivedBytes);
        Assert.Equal(CaptureErrorCode.InvalidInput, (await Assert.ThrowsAsync<CaptureStoreException>(() =>
            upload.AppendAsync(0, bytes))).Code);
        Assert.Equal(CaptureErrorCode.Incomplete, (await Assert.ThrowsAsync<CaptureStoreException>(() =>
            upload.CommitAsync())).Code);
        await upload.DisposeAsync();
        var result = await service.GetImportResultAsync(key, Owner);
        Assert.True(result.Cancelled);
        Assert.Empty(result.Entries);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "captures", ".portable"), "bundle.ddcapture",
            SearchOption.AllDirectories));
        await using var retry = service.BeginUpload(request, Owner, static (_, _, _, _) => ValueTask.CompletedTask);
        Assert.Equal(result.OperationId, retry.ExistingResult!.OperationId);
        Assert.Equal(result.Failure, retry.ExistingResult.Failure);
        Assert.True(retry.ExistingResult.Cancelled);
        Assert.Empty(retry.ExistingResult.Entries);
    }

    [Fact]
    public async Task Upload_HashMismatchCannotCommitOrRunWorker()
    {
        var request = new CaptureImportRequest(new(Guid.NewGuid().ToString("N"), _clock.Now), 1, new string('0', 64));
        var service = Exporter();
        await using var upload = service.BeginUpload(request, Owner, static (_, _, _, _) => ValueTask.CompletedTask);
        await upload.AppendAsync(0, new byte[] { 1 });
        Assert.Equal(CaptureErrorCode.CorruptPackage, (await Assert.ThrowsAsync<CaptureStoreException>(() =>
            upload.CommitAsync())).Code);
        await upload.DisposeAsync();
        var result = await service.GetImportResultAsync(request.Operation, Owner);
        Assert.False(result.Cancelled);
        Assert.Equal(CaptureErrorCode.CorruptPackage, result.Failure!.Code);
    }

    [Fact]
    public async Task Upload_TwoOwnersShareGlobalSlots_AndEntireDeclaredArchiveIsReservedBeforeFirstByte()
    {
        var request = new CaptureImportRequest(new(Guid.NewGuid().ToString("N"), _clock.Now),
            512 * 1024, new string('0', 64));
        var options = new CaptureStoreOptions
        {
            MaxDatabaseBytes = 1024 * 1024, MaxPackageBytes = 1024 * 1024, MaxStoreBytes = 4 * 1024 * 1024
        };
        var service = Exporter(storeOptions: options);
        await using var first = service.BeginUpload(request, Owner, static (_, _, _, _) => ValueTask.CompletedTask);
        await using var second = service.BeginUpload(request with { Operation = new(Guid.NewGuid().ToString("N"), _clock.Now) },
            new("bob"), static (_, _, _, _) => ValueTask.CompletedTask);
        var busy = Assert.Throws<CaptureStoreException>(() => service.BeginUpload(
            request with { Operation = new(Guid.NewGuid().ToString("N"), _clock.Now) }, new("carol"),
            static (_, _, _, _) => ValueTask.CompletedTask));
        Assert.Equal(CaptureErrorCode.Busy, busy.Code);
        await second.DisposeAsync();
        var full = Assert.Throws<CaptureStoreException>(() => service.BeginUpload(
            request with { Operation = new(Guid.NewGuid().ToString("N"), _clock.Now), ArchiveBytes = 4 * 1024 * 1024 },
            new("carol"), static (_, _, _, _) => ValueTask.CompletedTask));
        Assert.Equal(CaptureErrorCode.CapacityExceeded, full.Code);
        Assert.Equal(0, first.ReceivedBytes);
    }

    [Fact]
    public async Task Transfer_CleanupFailureKeepsLeaseAndCanBeRetriedAfterControlContention()
    {
        var capture = await CreateAsync();
        var service = Exporter();
        var transfer = await service.PrepareExportAsync(Request(new CaptureExportSelection(capture.CaptureId, null)), Owner);
        using (var control = new FileStream(Path.Combine(_root, "captures", ".admission"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<CaptureStoreException>(() => transfer.DisposeAsync().AsTask());
        var busy = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            service.ExportAsync(Request(new CaptureExportSelection(capture.CaptureId, null)), Stream.Null, Owner));
        Assert.Equal(CaptureErrorCode.Busy, busy.Code);
        await transfer.DisposeAsync();
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "captures", ".portable"), "bundle.ddcapture",
            SearchOption.AllDirectories));
    }
}
