using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class PortableCaptureExportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompositionAndStreamWrappersRemainByteIdentical(bool wrapped)
    {
        await using var writer = await Store().CreateAsync(new("batch"), Owner);
        var parent = writer.AddArtifact("batch", "parent");
        var child = writer.AddArtifact("counters", "child");
        Assert.True(writer.TryAppend(child, new(Name: "value", NumericValue: 1)));
        var composition = new DurableCaptureComposition(
            [new(child, "counters", "child", parent, 1, 1, null, new Dictionary<string, long?>(), 0, null, false, false)]);
        var bytes = DurableCaptureCompositionCodec.Encode("batch", composition, 8 * 1024 * 1024);
        if (wrapped)
            bytes = DurableCaptureSnapshotMetadata.Encode("batch", 2, bytes,
                new(false, 0, 0, null, new Dictionary<string, long?>(), 0), 8 * 1024 * 1024);
        writer.SetSnapshot(parent, wrapped ? 3 : 2, bytes);
        var source = await writer.CompleteAsync();
        var before = SourceHashes(source);
        using var output = new MemoryStream();
        await Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), output, Owner);
        ValidateIndependentArchive(output.ToArray(), [source], [null]);
        Assert.Equal(before, SourceHashes(source));
    }

    [Fact]
    public async Task ExplicitRecoveryRemainsSealedButQualityIncomplete_WhenExported()
    {
        var source = await CreateAsync(snapshot: true);
        File.Delete(Path.Combine(Package(source.CaptureId), "seal.json"));
        var recovered = await Store().RecoverAsync(source.CaptureId, Owner);
        Assert.True(recovered.Quality.UnknownTail);
        Assert.All(recovered.Artifacts, static a => Assert.NotNull(a.SourceArtifactId));
        var before = SourceHashes(recovered);
        using var output = new MemoryStream();
        await Exporter().ExportAsync(Request(new CaptureExportSelection(recovered.CaptureId, "recovered")), output, Owner);
        ValidateIndependentArchive(output.ToArray(), [recovered], ["recovered"]);
        Assert.Equal(before, SourceHashes(recovered));
        Assert.False(recovered.Quality.IsComplete);
    }

    [Fact]
    public async Task MissingCompositionReferencesAreNotExported()
    {
        await using var writer = await Store().CreateAsync(new("bad composition"), Owner);
        var parent = writer.AddArtifact("batch", "parent");
        var missing = Guid.NewGuid().ToString("N");
        writer.SetSnapshot(parent, 2, DurableCaptureCompositionCodec.Encode("batch",
            new([new(missing, "counters", "missing", parent, 0, 0, null, new Dictionary<string, long?>(), 0, null, false, false)]),
            8 * 1024 * 1024));
        var source = await writer.CompleteAsync();
        await Error(CaptureErrorCode.UnsupportedFormat, () =>
            Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), Stream.Null, Owner));
    }

    [Fact]
    public async Task OutputFailureAfterFirstChunkLeavesOnlyAnUnpublishedPrefix()
    {
        var source = await CreateAsync(records: 2000);
        using var prefix = new MemoryStream();
        using var output = new SinkStream(async (bytes, token) =>
        {
            if (prefix.Length != 0) throw new IOException("second write failed");
            await prefix.WriteAsync(bytes, token);
        });
        await Error(CaptureErrorCode.StorageFailure, () =>
            Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), output, Owner));
        Assert.Equal(64 * 1024, prefix.Length);
        Assert.Throws<InvalidDataException>(() => new ZipArchive(new MemoryStream(prefix.ToArray()), ZipArchiveMode.Read));
    }

    [Fact]
    public async Task ImportReceiptLookupDoesNotRequireOrRunAWorker()
    {
        await Error(CaptureErrorCode.NotFound, () => Exporter().GetImportResultAsync(
            new(Guid.NewGuid().ToString("N"), _clock.Now), Owner));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task ConsumptionLimit_DoesNotTrustAStreamLength()
    {
        using var input = new LyingLengthStream(new byte[64 * 1024]);
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            PortableZip.MeasureAsync(input, 1024, CancellationToken.None));
        Assert.Equal(CaptureErrorCode.CapacityExceeded, error.Code);
        Assert.Contains("observed=1025", error.Message);
        Assert.Contains("maximum=1024", error.Message);
        Assert.Equal(1025, input.Position);
        using var exact = new LyingLengthStream(new byte[1024]);
        Assert.Equal(1024, (await PortableZip.MeasureAsync(exact, 1024, CancellationToken.None)).Bytes);
    }

    [Fact]
    public async Task PolicyDenialPrecedesEvenSourceDatabaseHashValidation()
    {
        var source = await CreateAsync();
        await File.WriteAllBytesAsync(Path.Combine(Package(source.CaptureId), "capture.sqlite"), [1, 2, 3]);
        await Error(CaptureErrorCode.Forbidden, () => Exporter(authorize: (_, _) =>
            throw new CaptureStoreException(CaptureErrorCode.Forbidden, "whole-artifact policy"))
            .ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), Stream.Null, Owner));
    }

    [Fact]
    public async Task InterruptedReceiptInitializationIsCleanedWithoutTouchingSources()
    {
        var source = await CreateAsync();
        var before = SourceHashes(source);
        var partial = Path.Combine(_root, "captures", ".portable", new string('a', 64));
        Directory.CreateDirectory(partial);
        await File.WriteAllBytesAsync(Path.Combine(partial, ".lease"), []);
        await File.WriteAllTextAsync(Path.Combine(partial, "receipt.pending"), "{\"ownerId\":");
        await Exporter().CleanupExpiredExportsAsync();
        Assert.False(Directory.Exists(partial));
        Assert.Equal(before, SourceHashes(source));
    }

    [Fact]
    public async Task StagingCreateFailureIsExplicit_AndDoesNotMutateSources()
    {
        var source = await CreateAsync();
        var before = SourceHashes(source);
        using var output = new MemoryStream();
        await Error(CaptureErrorCode.StorageFailure, () => Exporter(authorize: (_, _) =>
        {
            var operation = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "captures", ".portable")));
            File.WriteAllBytes(Path.Combine(operation, "bundle.pending"), [1]);
            return ValueTask.CompletedTask;
        }).ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), output, Owner));
        Assert.Equal(0, output.Length);
        Assert.Equal(before, SourceHashes(source));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "captures", ".portable"), "bundle.*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CleanupContentionDoesNotPretendToReleaseDiskReservation()
    {
        var source = await CreateAsync();
        FileStream? admission = null;
        using var output = new SinkStream((_, _) =>
        {
            admission = new FileStream(Path.Combine(_root, "captures", ".admission"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            throw new IOException("output failed while control lease is busy");
        });
        try
        {
            var error = await Assert.ThrowsAsync<CaptureStoreException>(() => Exporter().ExportAsync(
                Request(new CaptureExportSelection(source.CaptureId, null)), output, Owner));
            Assert.Equal(CaptureErrorCode.StorageFailure, error.Code);
            Assert.Contains("CleanupFailed", error.Message);
        }
        finally { admission?.Dispose(); }
        var root = Path.Combine(_root, "captures");
        var archive = Assert.Single(Directory.GetFiles(Path.Combine(root, ".portable"), "bundle.ddcapture", SearchOption.AllDirectories));
        Assert.True(PortableCaptureStorage.AccountedBytes(root) >=
            new FileInfo(archive).Length + PortableBounds.ReceiptReservation);
        _clock.Now = _clock.Now.AddMinutes(61);
        await Exporter().CleanupExpiredExportsAsync();
        Assert.False(File.Exists(archive));
    }

    [Fact]
    public async Task ChangingMemberLengthOrBytesCannotCompleteAStagedArchive()
    {
        byte[] original = [1, 2, 3];
        var hash = PortableZip.Measure(original);
        foreach (var changed in new[] { new byte[] { 1, 2, 3, 4 }, [1, 2], [1, 2, 4] })
        {
            using var output = new MemoryStream();
            var error = await Assert.ThrowsAsync<CaptureStoreException>(() => PortableZip.WriteAsync(output,
                [new("bundle.json", hash, () => new MemoryStream(changed))], new(), CancellationToken.None));
            Assert.Contains(error.Code, new[] { CaptureErrorCode.CapacityExceeded, CaptureErrorCode.CorruptPackage });
        }
    }

    [Theory]
    [InlineData("count")]
    [InlineData("directory")]
    [InlineData("comment")]
    [InlineData("zip64")]
    public async Task EocdPreflightRejectsBeforeReadingTheCentralDirectory(string corruption)
    {
        var source = await CreateAsync();
        using var output = new MemoryStream();
        await Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), output, Owner);
        var bytes = output.ToArray();
        var offset = bytes.Length - 22;
        switch (corruption)
        {
            case "count":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 8), 500);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 10), 500);
                break;
            case "directory":
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 12), uint.MaxValue);
                break;
            case "comment":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 20), 1);
                break;
            default:
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 8), ushort.MaxValue);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 10), ushort.MaxValue);
                break;
        }
        using var observed = new ReadCountingStream(bytes);
        await Assert.ThrowsAsync<CaptureStoreException>(() => PortableZip.InspectAsync(observed, new(), CancellationToken.None));
        Assert.Equal(22, observed.Consumed);
    }

    [Theory]
    [InlineData("method")]
    [InlineData("flags")]
    [InlineData("extra")]
    [InlineData("crc")]
    [InlineData("overlap")]
    [InlineData("reparse")]
    public async Task StrictArchiveHeaderAndIntegrityChecks(string corruption)
    {
        var source = await CreateAsync();
        using var output = new MemoryStream();
        await Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), output, Owner);
        var bytes = output.ToArray();
        var directory = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6));
        switch (corruption)
        {
            case "method": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(directory + 10), 8); break;
            case "flags": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(directory + 8), 0x808); break;
            case "extra": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(directory + 30), 1); break;
            case "overlap": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directory + 42), 1); break;
            case "reparse": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directory + 38), 0x400); break;
            default:
                var nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26));
                bytes[30 + nameBytes] ^= 1;
                break;
        }
        using var input = new MemoryStream(bytes);
        await Assert.ThrowsAsync<CaptureStoreException>(() => PortableZip.InspectAsync(input, new(), CancellationToken.None));
    }

    [Fact]
    public async Task StagedByteTamperingIsNotAValidRetry()
    {
        var source = await CreateAsync();
        var request = Request(new CaptureExportSelection(source.CaptureId, null));
        await Exporter().ExportAsync(request, Stream.Null, Owner);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(_root, "captures", ".portable"),
            "bundle.ddcapture", SearchOption.AllDirectories));
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[30 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26))] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);
        using var output = new MemoryStream();
        await Error(CaptureErrorCode.CorruptPackage, () => Exporter().ExportAsync(request, output, Owner));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task TwoOwnersMayExport_ThirdStoreSlotCannotQueue()
    {
        var source = await CreateAsync();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstOutput = new SinkStream(async (_, token) =>
        {
            firstEntered.TrySetResult();
            await releaseFirst.Task.WaitAsync(token);
        });
        using var secondOutput = new SinkStream(async (_, token) =>
        {
            secondEntered.TrySetResult();
            await releaseSecond.Task.WaitAsync(token);
        });
        var first = Exporter().ExportAsync(
            Request(new CaptureExportSelection(source.CaptureId, null)), firstOutput, Owner);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Exporter().ExportAsync(
            Request(new CaptureExportSelection(source.CaptureId, null)), secondOutput, new("bob", AllOwners: true));
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await Error(CaptureErrorCode.Busy, () => Exporter().ExportAsync(
                Request(new CaptureExportSelection(source.CaptureId, null)), Stream.Null, new("charlie", AllOwners: true)));
        }
        finally
        {
            releaseFirst.TrySetResult();
            try { await first; }
            finally { releaseSecond.TrySetResult(); }
        }
        await second;
    }

    [Fact]
    public async Task PreCancelledAndPolicyCancelledOperationsDoNotPublishOutput()
    {
        var source = await CreateAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Exporter().ExportAsync(
            Request(new CaptureExportSelection(source.CaptureId, null)), Stream.Null, Owner, cancelled.Token));
        Assert.False(Directory.Exists(Path.Combine(_root, "captures", ".portable")));
        using var during = new CancellationTokenSource();
        using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Exporter(authorize: (_, _) =>
        {
            during.Cancel();
            return ValueTask.CompletedTask;
        }).ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), output, Owner, during.Token));
        Assert.Equal(0, output.Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "captures", ".portable"), "bundle.*", SearchOption.AllDirectories));
        await Store().DeleteAsync(source.CaptureId, Owner);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CpuStackReferencesAreArtifactScopedAndMustResolve(bool definition)
    {
        await using var writer = await Store().CreateAsync(new("CPU"), Owner);
        var artifact = writer.AddArtifact("cpu-sample", "samples");
        if (definition)
            Assert.True(writer.TryAppend(artifact, new(Category: CpuReplayStackObservationWriter.DefinitionCategory, Name: "stack-key")));
        Assert.True(writer.TryAppend(artifact, new(Category: CpuReplayStackObservationWriter.SampleCategory, Name: "stack-key")));
        var source = await writer.CompleteAsync();
        var operation = () => Exporter().ExportAsync(
            Request(new CaptureExportSelection(source.CaptureId, null)), Stream.Null, Owner);
        if (definition) await operation();
        else await Error(CaptureErrorCode.CorruptPackage, operation);
    }

    [Fact]
    public async Task LargeRepeatedExportUsesBoundedWrites_NotAnArchiveSizedBuffer()
    {
        await using var writer = await Store().CreateAsync(new("large"), Owner);
        var artifact = writer.AddArtifact("counters", "large");
        for (var i = 0; i < 1200; i++)
            Assert.True(await writer.AppendAsync(artifact,
                new(Name: "record", Fields: [new("value", CaptureFieldKind.Text, StringValue: new string('x', 2000) + i)])));
        var source = await writer.CompleteAsync();
        var largestWrite = 0;
        long written = 0;
        using var output = new SinkStream((bytes, _) =>
        {
            largestWrite = Math.Max(largestWrite, bytes.Length);
            written += bytes.Length;
            return ValueTask.CompletedTask;
        });
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var result = await Exporter().ExportAsync(Request(Enumerable.Repeat(
            new CaptureExportSelection(source.CaptureId, null), 16).ToArray()), output, Owner);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        _output.WriteLine($"ArchiveBytes={written}; LargestWriteBytes={largestWrite}; CumulativeAllocatedBytes={allocated}.");
        Assert.True(written > 32L * 1024 * 1024, $"Archive must exceed the host retained-buffer cap: {written}.");
        Assert.Equal(result.ArchiveBytes, written);
        Assert.InRange(largestWrite, 1, 64 * 1024);
        // This counts cumulative allocations (including freed record pages), not just retained memory.
        Assert.True(allocated < written, $"Streaming export allocated {allocated} bytes for {written} archive bytes.");
    }

    [Fact]
    public async Task FrozenPreviousFixtureWithUnknownCodecIsRejected_OriginalFixtureUnchanged()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotnetDiagnostics.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var fixture = Path.Combine(directory.FullName, "tests", "DotnetDiagnostics.Core.Tests", "Fixtures", "DurableCaptureV1", "sealed-v1.zip");
        var before = Hash(await File.ReadAllBytesAsync(fixture));
        using var zip = ZipFile.OpenRead(fixture);
        using var manifest = JsonDocument.Parse(Read(zip.GetEntry("manifest.json")!));
        var info = manifest.RootElement.GetProperty("Info");
        var id = info.GetProperty("CaptureId").GetString()!;
        var owner = new CaptureAccess(info.GetProperty("OwnerId").GetString()!);
        Directory.CreateDirectory(Package(id));
        await File.WriteAllTextAsync(Path.Combine(_root, "captures", ".capture-store"), "dotnet-diagnostics-captures/1");
        ZipFile.ExtractToDirectory(fixture, Package(id));
        var packageBefore = SourceHashes(id);
        await Error(CaptureErrorCode.UnsupportedFormat, () =>
            Exporter().ExportAsync(Request(new CaptureExportSelection(id, null)), Stream.Null, owner));
        Assert.Equal(packageBefore, SourceHashes(id));
        Assert.Equal(before, Hash(await File.ReadAllBytesAsync(fixture)));
    }

    private sealed class LyingLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override long Length => 1;
    }
    private sealed class ReadCountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal int Consumed { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.Read(buffer.Span);
            Consumed += read;
            return ValueTask.FromResult(read);
        }
    }
}
