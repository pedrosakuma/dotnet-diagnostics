using System.Security.Cryptography;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class PortableCaptureExportTests
{
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
        await using var upload = Exporter().BeginUpload(request, Owner, static (_, _, _, _) => ValueTask.CompletedTask);
        await upload.AppendAsync(0, new byte[] { 1 });
        Assert.Equal(CaptureErrorCode.CorruptPackage, (await Assert.ThrowsAsync<CaptureStoreException>(() =>
            upload.CommitAsync())).Code);
    }
}
