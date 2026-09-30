using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Captures;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

[Collection("PortableExportResources")]
public sealed partial class PortableCaptureImportTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    public PortableCaptureImportTests(ITestOutputHelper output) => _output = output;

    private sealed record RootProvider(string Root) : IArtifactRootProvider;
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "portable-import-tests", Guid.NewGuid().ToString("N"));
    private static readonly CaptureAccess Owner = new("destination-owner");
    private static bool Linux => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    private static readonly AuthorizePortableImport Allow = static (_, _, _, _) => ValueTask.CompletedTask;
    private SqliteCaptureStore Store(string name = "one") => new(new RootProvider(Path.Combine(_root, name)));
    private PortableCaptureUseCases Service(string name = "one", PortableCaptureOptions? options = null) =>
        new(Store(name), static (_, _) => ValueTask.CompletedTask, options, importWorker: new(
            Path.Combine(AppContext.BaseDirectory, "capture-worker"),
            Path.Combine(AppContext.BaseDirectory, "runtimes/linux-x64/native/libe_sqlite3.so")));
    private static PortableOperationKey Key() => new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static byte[] Frozen()
    {
        var file = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../Fixtures/PortableImport/known-counters-v1-v2.ddcapture"));
        var bytes = File.ReadAllBytes(file);
        Assert.Equal("bbdc6584acb14da9b21fc2d5170252aab2cf307fbcda1f554a05b6f1938c9ea1", Hash(bytes));
        return bytes;
    }

    [Fact]
    public async Task IndependentV1V2_OfflineQueriesQualityOwnershipAndPermanentOrigin()
    {
        if (!Linux) return;
        var bytes = Frozen();
        var request = new CaptureImportRequest(Key(), bytes.Length, Hash(bytes));
        var phases = new List<PortableAuthorizationPhase>();
        var imported = await Service().ImportAsync(request, new MemoryStream(bytes, false), Owner,
            (entries, phase, _, _) =>
            {
                Assert.Equal(2, entries.Count);
                phases.Add(phase);
                return ValueTask.CompletedTask;
            });
        Assert.True(imported.Complete);
        Assert.Equal(2, imported.Entries.Count);
        Assert.Equal(new[] { PortableAuthorizationPhase.Prepare, PortableAuthorizationPhase.Publish, PortableAuthorizationPhase.Publish }, phases);
        Assert.NotEqual(imported.Entries[0].Mapping!.LocalCaptureId, imported.Entries[1].Mapping!.LocalCaptureId);
        for (var i = 0; i < 2; i++)
        {
            var entry = imported.Entries[i];
            Assert.Equal(PortableEntryState.Published, entry.State);
            Assert.Equal("../duplicate-label", entry.Mapping!.Label);
            using var reader = await Store().OpenAsync(entry.Mapping.LocalCaptureId, Owner);
            Assert.Equal(3, reader.Format.PackageVersion);
            Assert.Equal(3, reader.ExecutingReader.Version);
            Assert.Equal(Owner.OwnerId, reader.Info.OwnerId);
            Assert.True(reader.Info.Quality.UnknownTail);
            Assert.True(reader.Info.Quality.Interrupted);
            Assert.Null(reader.Info.Quality.SourceRejected);
            Assert.Equal(5, reader.Info.Quality.Offered);
            Assert.Equal(2, reader.Info.Quality.StorageRejected);
            Assert.Equal(i + 1, reader.Info.PortableSource!.Origin.Format.PackageVersion);
            Assert.Equal("original-owner", reader.Info.PortableSource.Origin.OwnerId);
            Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", reader.Info.PortableSource.Origin.CaptureId);
            var record = Assert.Single(reader.Query(new(Assert.Single(reader.Info.Artifacts).ArtifactId)).Records);
            Assert.Equal(41, record.RecordId);
            Assert.Equal("working-set", record.Record.Name);
            Assert.Equal(7, Assert.Single(record.Record.Fields!).Int64Value);
            var denied = await Assert.ThrowsAsync<CaptureStoreException>(() => Store().OpenAsync(reader.Info.CaptureId, new("original-owner")));
            Assert.Equal(CaptureErrorCode.Forbidden, denied.Code);
        }
        Assert.Equal(JsonSerializer.Serialize(imported),
            JsonSerializer.Serialize(await Service().GetImportResultAsync(request.Operation, Owner)));
        using var unread = new NoRead();
        Assert.Equal(JsonSerializer.Serialize(imported),
            JsonSerializer.Serialize(await Service().ImportAsync(request, unread, Owner, Allow)));
        Assert.Equal(Hash(bytes), Hash(Frozen()));
    }

    [Fact]
    public async Task ImportedV3ReexportsAndRepeatedEntriesRetainOriginWithFreshLocalIds()
    {
        if (!Linux) return;
        var bytes = Frozen();
        var first = await Service().ImportAsync(new(Key(), bytes.Length, Hash(bytes)), new MemoryStream(bytes), Owner, Allow);
        var capture = first.Entries[0].Mapping!.LocalCaptureId;
        using var exported = new MemoryStream();
        var transfer = await Service().ExportAsync(new(Key(), [new(capture, "same"), new(capture, "same")]), exported, Owner);
        exported.Position = 0;
        var second = await Service("two").ImportAsync(new(Key(), transfer.ArchiveBytes, transfer.ArchiveSha256), exported, Owner, Allow);
        Assert.True(second.Complete);
        using var original = await Store().OpenAsync(capture, Owner);
        foreach (var entry in second.Entries)
        {
            using var reader = await Store("two").OpenAsync(entry.Mapping!.LocalCaptureId, Owner);
            Assert.NotEqual(capture, reader.Info.CaptureId);
            Assert.Equal(capture, reader.Info.PortableSource!.ImmediateSource.CaptureId);
            Assert.Equal(original.Info.PortableSource!.OriginMemberHashes, reader.Info.PortableSource.OriginMemberHashes);
            Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", reader.Info.PortableSource.Origin.CaptureId);
            Assert.Equal(original.Info.Artifacts[0].ArtifactId, Assert.Single(entry.Mapping.Artifacts).EntryArtifactId);
            Assert.Equal("cccccccccccccccccccccccccccccccc", Assert.Single(entry.Mapping.Artifacts).OriginArtifactId);
        }
    }

    private sealed class NoRead : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Terminal retry must not consume source bytes.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
