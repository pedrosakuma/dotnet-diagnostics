using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Counters;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

[Collection("PortableExportResources")]
public sealed partial class PortableCaptureExportTests : IDisposable
{
    private sealed record RootProvider(string Root) : IArtifactRootProvider;
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [CollectionDefinition("PortableExportResources", DisableParallelization = true)]
    public sealed class PortableExportResourceGroup;

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "portable-tests", Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly ITestOutputHelper _output;
    public PortableCaptureExportTests(ITestOutputHelper output) => _output = output;
    private static readonly CaptureAccess Owner = new("alice");
    private static readonly string[] ExpectedFeatures = ["independent-captures-v1", "stored-zip-v1", "index-sha256-v1"];
    private SqliteCaptureStore Store(CaptureStoreOptions? options = null) => new(new RootProvider(_root), options);
    private string Package(string id) => Path.Combine(_root, "captures", id);
    private PortableCaptureUseCases Exporter(PortableCaptureOptions? options = null,
        AuthorizePortableExport? authorize = null, CaptureStoreOptions? storeOptions = null) =>
        new(Store(storeOptions), authorize ?? (static (_, _) => ValueTask.CompletedTask), options, _clock);
    private CaptureExportRequest Request(params CaptureExportSelection[] entries) =>
        new(new(Guid.NewGuid().ToString("N"), _clock.Now), entries);

    private async Task<CaptureInfo> CreateAsync(bool snapshot = false, CaptureAccess? owner = null, int records = 2)
    {
        await using var writer = await Store().CreateAsync(new("immutable source", "group"), owner ?? Owner);
        var artifact = writer.AddArtifact("counters", "runtime");
        for (var i = 0; i < records; i++)
            Assert.True(await writer.AppendAsync(artifact, new(
                Timestamp: DateTimeOffset.UnixEpoch, ThreadId: 42, Category: "counter", Name: "working-set",
                Fields: [new("value", CaptureFieldKind.SignedInteger, Int64Value: long.MaxValue, Unit: "bytes"),
                    new("text", CaptureFieldKind.Text, StringValue: "exact\0é"), new("null", CaptureFieldKind.Null)])));
        if (snapshot)
            writer.SetSnapshot(artifact, 1, CaptureArtifactCodec.Encode("counters",
                new CounterSnapshot(42, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(6), [], [], []), 8 * 1024 * 1024));
        writer.SetSourceRejected(null);
        return await writer.CompleteAsync();
    }

    [Fact]
    public async Task Construction_IsInert_AndImportCannotReadAnArbitraryStream()
    {
        var service = Exporter();
        Assert.False(Directory.Exists(_root));
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() => service.ImportAsync(
            new(new(Guid.NewGuid().ToString("N"), _clock.Now), 5, new string('0', 64)),
            new ThrowOnReadStream(), Owner, static (_, _, _, _) => ValueTask.CompletedTask));
        Assert.Equal(CaptureErrorCode.UnsupportedFormat, error.Code);
        Assert.Contains("ImportWorkerUnavailable", error.Message);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task MultipleCaptures_DuplicateSourcesAndLabels_IndependentStoredZipContractAndImmutableBytes()
    {
        var first = await CreateAsync(snapshot: true);
        var second = await CreateAsync();
        var before = SourceHashes(first, second);
        var calls = new List<string>();
        var service = Exporter(authorize: (capture, _) =>
        {
            Assert.Single(capture.Artifacts);
            calls.Add(capture.CaptureId);
            return ValueTask.CompletedTask;
        });
        using var output = new MemoryStream();
        var result = await service.ExportAsync(Request(new CaptureExportSelection(first.CaptureId, "../friendly"),
            new(second.CaptureId, "../friendly"), new(first.CaptureId, null)), output, Owner);
        Assert.True(output.CanWrite);
        Assert.Equal(output.Length, result.ArchiveBytes);
        Assert.Equal(Hash(output.ToArray()), result.ArchiveSha256);
        Assert.Equal(4, calls.Count);
        ValidateIndependentArchive(output.ToArray(), [first, second, first], ["../friendly", "../friendly", null]);
        Assert.Equal(before, SourceHashes(first, second));
        Assert.Equal(CaptureState.Sealed, first.State);
    }

    [Fact]
    public async Task SixteenEntriesSucceed_SeventeenthFailsBeforeWriting()
    {
        var source = await CreateAsync();
        var selection = new CaptureExportSelection(source.CaptureId, "repeat");
        using var output = new MemoryStream();
        await Exporter().ExportAsync(Request(Enumerable.Repeat(selection, 16).ToArray()), output, Owner);
        using var zip = new ZipArchive(new MemoryStream(output.ToArray()), ZipArchiveMode.Read);
        Assert.Equal(50, zip.Entries.Count);
        using var denied = new MemoryStream();
        await Error(CaptureErrorCode.CapacityExceeded, () =>
            Exporter().ExportAsync(Request(Enumerable.Repeat(selection, 17).ToArray()), denied, Owner));
        Assert.Equal(0, denied.Length);
    }

    [Fact]
    public async Task ChangedLabelChangesIndexIntegrity_NotSource()
    {
        var source = await CreateAsync();
        var before = SourceHashes(source);
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        await Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, "a")), first, Owner);
        await Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, "b")), second, Owner);
        Assert.NotEqual(IndexHash(first), IndexHash(second));
        Assert.Equal(before, SourceHashes(source));
    }

    [Theory]
    [InlineData("../source")]
    [InlineData("/tmp/source.sqlite")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task ArbitrarySourceSelectorsAreRejected(string id)
    {
        using var output = new MemoryStream();
        await Error(CaptureErrorCode.InvalidInput, () => Exporter().ExportAsync(Request(new CaptureExportSelection(id, null)), output, Owner));
        Assert.Equal(0, output.Length);
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("nul\0label")]
    public async Task ControlCharacterLabelsAreRejected(string label)
    {
        var source = await CreateAsync();
        using var output = new MemoryStream();
        await Error(CaptureErrorCode.InvalidInput, () => Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, label)), output, Owner));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task LabelUtf8Budget_IsExact()
    {
        var source = await CreateAsync();
        using var output = new MemoryStream();
        await Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, new string('é', 128))), output, Owner);
        await Error(CaptureErrorCode.InvalidInput, () =>
            Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, new string('é', 129))), Stream.Null, Owner));
    }

    [Fact]
    public async Task OwnershipAndEveryArtifactPolicy_FailWithoutOutput()
    {
        var first = await CreateAsync();
        var foreign = await CreateAsync(owner: new("bob"));
        using var output = new MemoryStream();
        await Error(CaptureErrorCode.Forbidden, () =>
            Exporter().ExportAsync(Request(new CaptureExportSelection(first.CaptureId, null), new(foreign.CaptureId, null)), output, Owner));
        Assert.Equal(0, output.Length);
        await Error(CaptureErrorCode.Forbidden, () => Exporter(authorize: (capture, _) =>
        {
            Assert.NotEmpty(capture.Artifacts);
            throw new CaptureStoreException(CaptureErrorCode.Forbidden, "artifact policy denied");
        }).ExportAsync(Request(new CaptureExportSelection(first.CaptureId, null)), output, Owner));
        Assert.Equal(0, output.Length);
        await Exporter().ExportAsync(Request(new CaptureExportSelection(foreign.CaptureId, null)), output, Owner with { AllOwners = true });
        Assert.True(output.Length > 0);
    }

    [Fact]
    public async Task PolicyIsRecheckedBeforeDestinationBytes()
    {
        var source = await CreateAsync();
        var calls = 0;
        using var output = new MemoryStream();
        await Error(CaptureErrorCode.Forbidden, () => Exporter(authorize: (_, _) =>
        {
            if (++calls == 2) throw new CaptureStoreException(CaptureErrorCode.Forbidden, "revoked");
            return ValueTask.CompletedTask;
        }).ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), output, Owner));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task InterruptedCaptureIsNotRecovered()
    {
        var store = Store();
        string id;
        await using (var writer = await store.CreateAsync(new("interrupted"), Owner))
        {
            id = writer.Reference.CaptureId;
            writer.AddArtifact("counters", "empty");
        }
        var before = SourceHashes(id);
        await Error(CaptureErrorCode.Incomplete, () => Exporter().ExportAsync(Request(new CaptureExportSelection(id, null)), Stream.Null, Owner));
        Assert.Equal(before, SourceHashes(id));
        Assert.Single((await store.ListAsync(Owner)).Captures);
    }

    [Theory]
    [InlineData("capture.sqlite-wal", CaptureErrorCode.CorruptPackage)]
    [InlineData("manifest.json.pending", CaptureErrorCode.CorruptPackage)]
    [InlineData("native.nettrace", CaptureErrorCode.UnsafePath)]
    [InlineData("target.dmp", CaptureErrorCode.UnsafePath)]
    public async Task UnapprovedMembersAreNotSilentlyOmitted(string member, CaptureErrorCode expected)
    {
        var source = await CreateAsync();
        await File.WriteAllBytesAsync(Path.Combine(Package(source.CaptureId), member), [1]);
        using var output = new MemoryStream();
        await Error(expected, () => Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), output, Owner));
        Assert.Equal(0, output.Length);
        Assert.True(File.Exists(Path.Combine(Package(source.CaptureId), member)));
    }

    [Fact]
    public async Task UnknownSnapshotCodecIsExplicitlyUnsupported()
    {
        await using var writer = await Store().CreateAsync(new("unknown"), Owner);
        var artifact = writer.AddArtifact("counters", "counter");
        writer.SetSnapshot(artifact, 99, "{}"u8.ToArray());
        var source = await writer.CompleteAsync();
        await Error(CaptureErrorCode.UnsupportedFormat, () =>
            Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), Stream.Null, Owner));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SupportedSourceDescriptors_AreNotRelabeled(int version)
    {
        var source = await CreateAsync(snapshot: true);
        if (version == 1)
        {
            // A schema-1 compatibility case, not a claim to regenerate the independently frozen old-writer fixture.
            var path = Package(source.CaptureId);
            using (var connection = CapturePackage.Connect(path, immutable: false))
            {
                CapturePackage.Execute(connection, "UPDATE format SET package=1,writer_version=1,reader_version=1;");
                CapturePackage.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
            var manifest = CapturePackage.ReadManifest(path, source.CaptureId);
            CapturePackage.WriteJson(path, CapturePackage.Manifest, manifest with
            {
                PackageVersion = 1, WriterVersion = 1, ReaderVersion = 1, RequiredFeatures = ["normalized-scalars-v1"]
            });
            CapturePackage.WriteJson(path, CapturePackage.Seal,
                new CaptureSeal(CapturePackage.Hash(Path.Combine(path, CapturePackage.Manifest)),
                    CapturePackage.Hash(Path.Combine(path, CapturePackage.Database))));
        }
        var before = SourceHashes(source);
        using var output = new MemoryStream();
        await Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), output, Owner);
        using var index = ReadIndex(output);
        var format = index.RootElement.GetProperty("entries")[0].GetProperty("format");
        Assert.Equal(version, format.GetProperty("packageVersion").GetInt32());
        Assert.Equal(version, format.GetProperty("requiredReaderVersion").GetInt32());
        Assert.Equal(1, format.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(before, SourceHashes(source));
    }

    [Fact]
    public async Task FutureDescriptorIsRejectedWithoutRewriting()
    {
        var source = await CreateAsync();
        var path = Package(source.CaptureId);
        var manifest = CapturePackage.ReadManifest(path, source.CaptureId);
        CapturePackage.WriteJson(path, CapturePackage.Manifest, manifest with { PackageVersion = 99 });
        var before = SourceHashes(source);
        await Error(CaptureErrorCode.UnsupportedFormat, () =>
            Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), Stream.Null, Owner));
        Assert.Equal(before, SourceHashes(source));
    }

    [Fact]
    public async Task ExactArchiveAndUncompressedBudgets_RejectOneByteBelow()
    {
        var source = await CreateAsync();
        using var reference = new MemoryStream();
        var result = await Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), reference, Owner);
        using var zip = new ZipArchive(new MemoryStream(reference.ToArray()), ZipArchiveMode.Read);
        var content = zip.Entries.Sum(static e => e.Length);
        await Exporter(new() { MaxArchiveBytes = result.ArchiveBytes, MaxUncompressedBytes = content })
            .ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), Stream.Null, Owner);
        await Error(CaptureErrorCode.CapacityExceeded, () => Exporter(new() { MaxArchiveBytes = result.ArchiveBytes - 1 })
            .ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), Stream.Null, Owner));
        await Error(CaptureErrorCode.CapacityExceeded, () => Exporter(new() { MaxUncompressedBytes = content - 1 })
            .ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), Stream.Null, Owner));
    }

    [Theory]
    [InlineData("index")]
    [InlineData("rows")]
    [InlineData("tokens")]
    public async Task IndependentWorkAndMetadataBudgetsAreEnforced(string budget)
    {
        var source = await CreateAsync(snapshot: true);
        var options = budget switch
        {
            "index" => new PortableCaptureOptions { MaxIndexBytes = 64 },
            "rows" => new PortableCaptureOptions { MaxRowsPerTable = 1 },
            _ => new PortableCaptureOptions { MaxTokensPerSnapshot = 1 }
        };
        using var output = new MemoryStream();
        await Error(CaptureErrorCode.CapacityExceeded, () => Exporter(options).ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), output, Owner));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task RetryReusesExactBytesAndIds_ConflictingRequestFails()
    {
        var source = await CreateAsync();
        var request = Request(new CaptureExportSelection(source.CaptureId, "label"));
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        var a = await Exporter().ExportAsync(request, first, Owner);
        var b = await Exporter().ExportAsync(request, second, Owner);
        Assert.Equal(a, b);
        Assert.Equal(first.ToArray(), second.ToArray());
        await Error(CaptureErrorCode.InvalidInput, () => Exporter().ExportAsync(
            request with { Entries = [new(source.CaptureId, "changed")] }, Stream.Null, Owner));
    }

    [Fact]
    public async Task ExpiryCleanup_RemovesBytesButKeepsRetryReceipt_ThenRemovesReceipt()
    {
        var source = await CreateAsync();
        var request = Request(new CaptureExportSelection(source.CaptureId, null));
        await Exporter().ExportAsync(request, Stream.Null, Owner);
        _clock.Now = _clock.Now.AddMinutes(6);
        await Exporter().CleanupExpiredExportsAsync();
        var portable = Path.Combine(_root, "captures", ".portable");
        Assert.Empty(Directory.GetFiles(portable, "*.ddcapture", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(portable, "receipt.json", SearchOption.AllDirectories));
        await Error(CaptureErrorCode.InvalidInput, () => Exporter().ExportAsync(request, Stream.Null, Owner));
        _clock.Now = _clock.Now.AddHours(24);
        await Exporter().CleanupExpiredExportsAsync();
        Assert.Empty(Directory.GetDirectories(portable));
        await Error(CaptureErrorCode.InvalidInput, () => Exporter().ExportAsync(request, Stream.Null, Owner));
    }

    [Fact]
    public async Task OutputFailureKeepsBoundedRetryBytes_AndNeverReportsSuccess()
    {
        var source = await CreateAsync();
        var before = SourceHashes(source);
        var request = Request(new CaptureExportSelection(source.CaptureId, null));
        using var broken = new SinkStream((_, _) => throw new IOException("output failed"));
        await Error(CaptureErrorCode.StorageFailure, () => Exporter().ExportAsync(request, broken, Owner));
        using var retried = new MemoryStream();
        await Exporter().ExportAsync(request, retried, Owner);
        Assert.True(retried.Length > 0);
        Assert.Equal(before, SourceHashes(source));
    }

    [Fact]
    public async Task CancellationDuringOutput_ReleasesLeasesAndRemovesStaging()
    {
        var source = await CreateAsync();
        using var cts = new CancellationTokenSource();
        using var output = new SinkStream((_, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), output, Owner, cts.Token));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "captures", ".portable"), "bundle.*", SearchOption.AllDirectories));
        await Store().DeleteAsync(source.CaptureId, Owner);
    }

    [Fact]
    public async Task DeadlineIsNotSuccessfulCancellation()
    {
        var source = await CreateAsync();
        await Error(CaptureErrorCode.CapacityExceeded, () => Exporter(new() { OperationTimeout = TimeSpan.FromMilliseconds(20) },
            async (_, token) => await Task.Delay(TimeSpan.FromSeconds(5), token))
            .ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), Stream.Null, Owner));
    }

    [Fact]
    public async Task LeasesBlockDeletionThroughOutput_AndSameOwnerConcurrencyIsBounded()
    {
        var source = await CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var output = new SinkStream(async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        var running = Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), output, Owner);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await Error(CaptureErrorCode.Busy, () => Store().DeleteAsync(source.CaptureId, Owner));
            await Error(CaptureErrorCode.Busy, () =>
                Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), Stream.Null, Owner));
        }
        finally { release.TrySetResult(); }
        await running;
        await Store().DeleteAsync(source.CaptureId, Owner);
        await Error(CaptureErrorCode.NotFound, () => Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), Stream.Null, Owner));
    }

    [Fact]
    public async Task StagingReservationsParticipateInUnchangedStoreQuota()
    {
        var source = await CreateAsync();
        await Exporter().ExportAsync(Request(new CaptureExportSelection(source.CaptureId, null)), Stream.Null, Owner);
        var constrained = Store(new()
        {
            MaxDatabaseBytes = 512 * 1024, MaxPackageBytes = 512 * 1024, MaxStoreBytes = 1024 * 1024
        });
        await Error(CaptureErrorCode.CapacityExceeded, () => constrained.CreateAsync(new("another capture"), Owner));
        _clock.Now = _clock.Now.AddHours(25);
        await Exporter().CleanupExpiredExportsAsync();
        await using var writer = await constrained.CreateAsync(new("after expiry"), Owner);
        await writer.CompleteAsync();
    }

    private void ValidateIndependentArchive(byte[] bytes, CaptureInfo[] sources, string?[] labels)
    {
        var end = bytes.AsSpan(bytes.Length - 22);
        Assert.Equal(0x06054b50U, BinaryPrimitives.ReadUInt32LittleEndian(end));
        Assert.Equal(2 + 3 * sources.Length, BinaryPrimitives.ReadUInt16LittleEndian(end[10..]));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(end[20..]));
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Equal(2 + 3 * sources.Length, zip.Entries.Count);
        var indexBytes = Read(zip.Entries[0]);
        using var index = JsonDocument.Parse(indexBytes);
        using var seal = JsonDocument.Parse(Read(zip.Entries[1]));
        Assert.Equal(1, index.RootElement.GetProperty("archiveVersion").GetInt32());
        Assert.Equal(1, index.RootElement.GetProperty("requiredArchiveReaderVersion").GetInt32());
        Assert.Equal(ExpectedFeatures,
            index.RootElement.GetProperty("requiredFeatures").EnumerateArray().Select(static x => x.GetString()));
        Assert.Equal(indexBytes.Length, seal.RootElement.GetProperty("indexBytes").GetInt64());
        Assert.Equal(Hash(indexBytes), seal.RootElement.GetProperty("indexSha256").GetString());
        var entries = index.RootElement.GetProperty("entries");
        var ids = new HashSet<string>();
        for (var i = 0; i < sources.Length; i++)
        {
            var entry = entries[i];
            var id = entry.GetProperty("entryId").GetString()!;
            Assert.True(Guid.TryParseExact(id, "N", out _));
            Assert.True(ids.Add(id));
            Assert.Equal(sources[i].CaptureId, entry.GetProperty("sourceCaptureId").GetString());
            Assert.Equal(labels[i], entry.GetProperty("label").GetString());
            var manifest = entry.GetProperty("members");
            for (var j = 0; j < 3; j++)
            {
                var name = new[] { "manifest.json", "capture.sqlite", "seal.json" }[j];
                var member = zip.Entries[2 + i * 3 + j];
                Assert.Equal($"entries/{id}/{name}", member.FullName);
                var content = Read(member);
                Assert.Equal(File.ReadAllBytes(Path.Combine(Package(sources[i].CaptureId), name)), content);
                Assert.Equal(content.Length, manifest[j].GetProperty("bytes").GetInt64());
                Assert.Equal(Hash(content), manifest[j].GetProperty("sha256").GetString());
            }
        }
        var offset = 0;
        foreach (var member in zip.Entries)
        {
            var header = bytes.AsSpan(offset, 30);
            Assert.Equal(0x04034b50U, BinaryPrimitives.ReadUInt32LittleEndian(header));
            Assert.Equal(0x800, BinaryPrimitives.ReadUInt16LittleEndian(header[6..]));
            Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(header[8..]));
            Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(header[28..]));
            Assert.Equal(member.Length, member.CompressedLength);
            Assert.Equal(IndependentCrc(Read(member)), BinaryPrimitives.ReadUInt32LittleEndian(header[14..]));
            offset += 30 + BinaryPrimitives.ReadUInt16LittleEndian(header[26..]) + (int)member.Length;
        }
        Assert.Equal((uint)offset, BinaryPrimitives.ReadUInt32LittleEndian(end[16..]));
    }

    private SortedDictionary<string, string> SourceHashes(params CaptureInfo[] sources) =>
        SourceHashes(sources.Select(static s => s.CaptureId).ToArray());
    private SortedDictionary<string, string> SourceHashes(params string[] ids)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in ids)
            foreach (var path in Directory.GetFiles(Package(id)))
                result.Add(id + "/" + Path.GetFileName(path), Hash(File.ReadAllBytes(path)));
        return result;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var result = new MemoryStream();
        stream.CopyTo(result);
        return result.ToArray();
    }
    private static JsonDocument ReadIndex(MemoryStream archive)
    {
        using var zip = new ZipArchive(new MemoryStream(archive.ToArray()), ZipArchiveMode.Read);
        return JsonDocument.Parse(Read(zip.GetEntry("bundle.json")!));
    }
    private static string IndexHash(MemoryStream archive)
    {
        using var zip = new ZipArchive(new MemoryStream(archive.ToArray()), ZipArchiveMode.Read);
        using var seal = JsonDocument.Parse(Read(zip.GetEntry("bundle.seal.json")!));
        return seal.RootElement.GetProperty("indexSha256").GetString()!;
    }
    private static uint IndependentCrc(byte[] bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++) crc = (crc & 1) == 0 ? crc >> 1 : 0xedb88320 ^ (crc >> 1);
        }
        return ~crc;
    }
    private static async Task Error(CaptureErrorCode code, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<CaptureStoreException>(action);
        Assert.Equal(code, exception.Code);
    }
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class ThrowOnReadStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Input must not be read.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Input must not be read.");
    }
    private sealed class SinkStream(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => write(buffer, cancellationToken);
    }
}
