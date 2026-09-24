using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Captures;
using Microsoft.Data.Sqlite;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class DurableCaptureStoreTests : IDisposable
{
    private sealed record RootProvider(string Root) : IArtifactRootProvider;
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "capture-tests", Guid.NewGuid().ToString("N"));
    private static readonly CaptureAccess Owner = new("alice");
    private SqliteCaptureStore Store(CaptureStoreOptions? options = null) => new(new RootProvider(_root), options);
    private string Package(string id) => Path.Combine(_root, "captures", id);

    [Fact]
    public async Task DefaultNonUse_DoesNotCreateSqliteOrDirectories()
    {
        var store = Store();
        Assert.False(Directory.Exists(_root));
        Assert.Empty((await store.ListAsync(Owner)).Captures);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task ScalarsExactStringsMultipleArtifacts_IndexedQueriesAndImmutableReopen()
    {
        var store = Store(new CaptureStoreOptions { StringCacheEntries = 1, StringCacheBytes = 256 });
        await using var writer = await store.CreateAsync(new("capture", "group"), Owner);
        var first = writer.AddArtifact("events", "Exceptions");
        var second = writer.AddArtifact("counters", "Runtime");
        var timestamp = new DateTimeOffset(2026, 9, 24, 12, 1, 2, TimeSpan.FromHours(2));
        var fields = new[]
        {
            new CaptureField("message", CaptureFieldKind.Text, StringValue: "exact\u0000é🙂 text"),
            new CaptureField("large", CaptureFieldKind.SignedInteger, Int64Value: long.MaxValue, Unit: "bytes"),
            new CaptureField("fraction", CaptureFieldKind.FloatingPoint, DoubleValue: 0.125),
            new CaptureField("enabled", CaptureFieldKind.Boolean, BooleanValue: false),
            new CaptureField("missing", CaptureFieldKind.Null)
        };
        Assert.True(writer.TryAppend(first, new(timestamp, 42, "exceptions", "E", null, 23, "ns", fields)));
        fields[0] = new("mutated", CaptureFieldKind.Null);
        Assert.True(writer.TryAppend(first, new(timestamp.AddSeconds(1), 43, "gc", "GC", 0)));
        Assert.True(writer.TryAppend(second, new(Name: "working-set", NumericValue: 123, Unit: "bytes")));
        writer.SetSnapshot(first, 7, "{\"view\":\"compatibility\"}"u8.ToArray());
        writer.SetSourceRejected(0);
        var info = await writer.CompleteAsync();
        Assert.True(info.Quality.IsComplete);
        Assert.Equal(3, info.Quality.Persisted);
        Assert.NotEqual(info.CaptureId, first);
        var before = Hashes(Package(info.CaptureId));
        using (var reader = await Store().OpenAsync(info.CaptureId, Owner))
        {
            Assert.Equal(2, reader.Info.Artifacts.Count);
            var record = Assert.Single(reader.Query(new(first, From: timestamp, To: timestamp,
                ThreadId: 42, Category: "exceptions", Name: "E")).Records).Record;
            Assert.Equal(timestamp.ToUniversalTime(), record.Timestamp);
            Assert.Null(record.NumericValue);
            Assert.Equal("exact\u0000é🙂 text", record.Fields![0].StringValue);
            Assert.Equal(long.MaxValue, record.Fields[1].Int64Value);
            Assert.Equal("bytes", record.Fields[1].Unit);
            Assert.Equal(0.125, record.Fields[2].DoubleValue);
            Assert.False(record.Fields[3].BooleanValue);
            Assert.Equal(CaptureFieldKind.Null, record.Fields[4].Kind);
            var page = reader.Query(new(first, PageSize: 1));
            Assert.NotNull(page.NextAfterRecordId);
            var next = reader.Query(new(first, AfterRecordId: page.NextAfterRecordId.Value, PageSize: 1));
            Assert.Null(next.NextAfterRecordId);
            Assert.Equal(0, Assert.Single(next.Records).Record.NumericValue);
            var snapshot = reader.ReadSnapshot(first)!;
            Assert.Equal(7, snapshot.Version);
            Assert.Equal("{\"view\":\"compatibility\"}", Encoding.UTF8.GetString(snapshot.Utf8Json.Span));
            Assert.Null(reader.ReadSnapshot(second));
        }
        Assert.Equal(before, Hashes(Package(info.CaptureId)));
        Assert.DoesNotContain(before.Keys, static x => x.EndsWith("-wal", StringComparison.Ordinal) || x.EndsWith("-shm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActualDatabase_UsesWalFullAndNormalizedIndexes()
    {
        var options = new CaptureStoreOptions { MaxDatabaseBytes = 128 * 1024 };
        await using var writer = await Store(options).CreateAsync(new("pragmas"), Owner);
        var id = writer.AddArtifact("test", "test");
        Assert.True(writer.TryAppend(id, new(Name: "real-string")));
        await writer.CompleteAsync();
        using var connection = CapturePackage.Connect(Package(writer.Reference.CaptureId), immutable: true);
        Assert.Equal(2L, Scalar(connection, "PRAGMA synchronous;"));
        Assert.Equal(6L, Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='index' AND name LIKE 'ix_%';"));
        Assert.Equal("wal", writer.GetMetrics().JournalMode);
        Assert.Equal(2, writer.GetMetrics().Synchronous);
        Assert.Equal(options.MaxDatabaseBytes / 4096, writer.GetMetrics().DatabasePageLimit);
        Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM strings WHERE value='real-string';"));
        Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM occurrences;"));
        Assert.Equal("wal", ReadHeaderJournalMode(Package(writer.Reference.CaptureId)));
        Assert.True(new FileInfo(Path.Combine(Package(writer.Reference.CaptureId), "capture.sqlite")).Length <= options.MaxDatabaseBytes);
    }

    [Fact]
    public async Task ForeignOwnerCannotListOpenDeleteRecover_PrivilegeExplicit()
    {
        var store = Store();
        await using var writer = await store.CreateAsync(new("private"), Owner);
        await writer.CompleteAsync();
        var id = writer.Reference.CaptureId;
        var foreign = new CaptureAccess("bob");
        Assert.Empty((await store.ListAsync(foreign)).Captures);
        await Error(CaptureErrorCode.Forbidden, () => store.OpenAsync(id, foreign));
        await Error(CaptureErrorCode.Forbidden, () => store.DeleteAsync(id, foreign));
        await Error(CaptureErrorCode.Forbidden, () => store.RecoverAsync(id, foreign));
        using (var reader = await store.OpenAsync(id, foreign with { AllOwners = true }))
            Assert.Equal("alice", reader.Info.OwnerId);
        await store.DeleteAsync(id, foreign with { AllOwners = true });
        Assert.False(Directory.Exists(Package(id)));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("not-an-id")]
    public async Task InvalidIds_AreRejectedBeforeFilesystemAccess(string id)
    {
        var store = Store();
        await Error(CaptureErrorCode.InvalidInput, () => store.OpenAsync(id, Owner));
        await Error(CaptureErrorCode.InvalidInput, () => store.DeleteAsync(id, Owner));
        await Error(CaptureErrorCode.InvalidInput, () => store.RecoverAsync(id, Owner));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task MissingUnsealedFutureAndCorruptPackages_RejectWithoutRepair()
    {
        var store = Store();
        await Error(CaptureErrorCode.NotFound, () => store.OpenAsync(Guid.NewGuid().ToString("N"), Owner));
        var writer = await store.CreateAsync(new("interrupted"), Owner);
        var id = writer.Reference.CaptureId;
        await writer.DisposeAsync();
        await Error(CaptureErrorCode.Incomplete, () => store.OpenAsync(id, Owner));
        var before = Hashes(Package(id));
        await Error(CaptureErrorCode.Incomplete, () => store.OpenAsync(id, Owner));
        Assert.Equal(before, Hashes(Package(id)));

        await using var sealedWriter = await store.CreateAsync(new("sealed"), Owner);
        await sealedWriter.CompleteAsync();
        id = sealedWriter.Reference.CaptureId;
        var manifestPath = Path.Combine(Package(id), "manifest.json");
        var original = await File.ReadAllBytesAsync(manifestPath);
        var json = JsonNode.Parse(original)!;
        json["ReaderVersion"] = 99;
        await File.WriteAllTextAsync(manifestPath, json.ToJsonString());
        await Error(CaptureErrorCode.UnsupportedFormat, () => store.OpenAsync(id, Owner));
        await File.WriteAllBytesAsync(manifestPath, original);
        json = JsonNode.Parse(original)!;
        json.AsObject().Remove("SchemaVersion");
        await File.WriteAllTextAsync(manifestPath, json.ToJsonString());
        await Error(CaptureErrorCode.UnsupportedFormat, () => store.OpenAsync(id, Owner));
        await File.WriteAllBytesAsync(manifestPath, original);
        json = JsonNode.Parse(original)!;
        json["RequiredFeatures"] = new JsonArray("future-feature");
        await File.WriteAllTextAsync(manifestPath, json.ToJsonString());
        await Error(CaptureErrorCode.UnsupportedFormat, () => store.OpenAsync(id, Owner));
        await File.WriteAllBytesAsync(manifestPath, original);
        await File.AppendAllTextAsync(Path.Combine(Package(id), "capture.sqlite"), "corruption");
        await Error(CaptureErrorCode.CorruptPackage, () => store.OpenAsync(id, Owner));
        File.Delete(Path.Combine(Package(id), "capture.sqlite"));
        await Error(CaptureErrorCode.NotFound, () => store.OpenAsync(id, Owner));
    }

    [Fact]
    public async Task QueueRecordLogicalAndSnapshotBounds_AreExplicitNotTruncation()
    {
        var options = new CaptureStoreOptions
        {
            QueueRecords = 1, MaxBatchAge = TimeSpan.FromSeconds(1),
            MaxRecordBytes = 512, MaxSnapshotBytes = 16, MaxLogicalBytes = 512
        };
        await using var writer = await Store(options).CreateAsync(new("bounded"), Owner);
        var id = writer.AddArtifact("test", "test");
        Assert.False(writer.TryAppend(id, new(Name: new string('é', 1000))));
        Assert.False(writer.TryAppend(id, new(Fields: [new("bad", CaptureFieldKind.Null, Int64Value: 0)])));
        Assert.True(writer.TryAppend(id, new(Name: "accepted")));
        Assert.False(writer.TryAppend(id, new(Name: "queue-rejected")));
        Assert.Equal(CaptureErrorCode.CapacityExceeded,
            Assert.Throws<CaptureStoreException>(() => writer.SetSnapshot(id, 1, new byte[17])).Code);
        writer.SetSnapshot(id, 1, "{}"u8.ToArray());
        var result = await writer.CompleteAsync();
        Assert.True(result.Quality.IsIncomplete);
        Assert.Null(result.Quality.SourceRejected);
        Assert.Equal(2, result.Quality.RecordRejected);
        Assert.Equal(1, result.Quality.QueueRejected);
        Assert.Equal(1, result.Quality.Persisted);
        Assert.Equal(0, result.Quality.Pending);
        AssertConservation(writer.GetMetrics().Quality);
        using var reader = await Store().OpenAsync(result.CaptureId, Owner);
        Assert.Equal("accepted", Assert.Single(reader.Query(new(id)).Records).Record.Name);

        await using var limited = await Store(new CaptureStoreOptions { MaxLogicalBytes = 128 }).CreateAsync(new("logical"), Owner);
        var artifact = limited.AddArtifact("test", "test");
        Assert.False(limited.TryAppend(artifact, new(Name: "cannot-fit")));
        Assert.Equal(1, (await limited.CompleteAsync()).Quality.StorageRejected);
    }

    [Fact]
    public async Task WriterAndReaders_BlockDeleteAndRecoveryUntilDisposed()
    {
        var store = Store();
        var writer = await store.CreateAsync(new("leases"), Owner);
        var id = writer.Reference.CaptureId;
        await Error(CaptureErrorCode.Busy, () => store.DeleteAsync(id, Owner));
        await Error(CaptureErrorCode.Busy, () => store.RecoverAsync(id, Owner));
        await Error(CaptureErrorCode.Busy, () => store.OpenAsync(id, Owner));
        await writer.CompleteAsync();
        using (var first = await store.OpenAsync(id, Owner))
        using (var second = await Store().OpenAsync(id, Owner))
        {
            await Error(CaptureErrorCode.Busy, () => store.DeleteAsync(id, Owner));
            await Error(CaptureErrorCode.Busy, () => store.RecoverAsync(id, Owner));
        }
        await store.DeleteAsync(id, Owner);
        Assert.Empty((await store.ListAsync(Owner)).Captures);
        await writer.DisposeAsync();
    }

    [Fact]
    public async Task Recovery_NewCaptureAndArtifactIds_SourceBytesUnchanged()
    {
        var store = Store();
        var writer = await store.CreateAsync(new("interrupted", "session"), Owner);
        var artifact = writer.AddArtifact("events", "events");
        Assert.True(writer.TryAppend(artifact, new(Name: "committed", NumericValue: 2)));
        writer.SetSnapshot(artifact, 1, "{\"data\":2}"u8.ToArray());
        await writer.DisposeAsync();
        var original = writer.Reference.CaptureId;
        var before = Hashes(Package(original));
        var recovered = await store.RecoverAsync(original, Owner);
        Assert.Equal(before, Hashes(Package(original)));
        Assert.NotEqual(original, recovered.CaptureId);
        Assert.NotEqual(artifact, Assert.Single(recovered.Artifacts).ArtifactId);
        Assert.Equal(original, recovered.DerivedFrom);
        Assert.True(recovered.Quality.UnknownTail);
        Assert.True(recovered.Quality.IsIncomplete);
        Assert.NotNull(recovered.SourceHashes);
        Assert.Equal(before, recovered.SourceHashes!.OrderBy(static x => x.Key).ToDictionary());
        using var reader = await store.OpenAsync(recovered.CaptureId, Owner);
        Assert.Equal("committed", Assert.Single(reader.Query(new(recovered.Artifacts[0].ArtifactId)).Records).Record.Name);
        Assert.NotNull(reader.ReadSnapshot(recovered.Artifacts[0].ArtifactId));
    }

    [Fact]
    public async Task AdmissionLimitsAndCatalogPaging_AcrossNewStoreInstances()
    {
        var options = new CaptureStoreOptions { MaxActiveWriters = 1, MaxCaptures = 2 };
        var first = await Store(options).CreateAsync(new("one"), Owner);
        await Error(CaptureErrorCode.CapacityExceeded, () => Store(options).CreateAsync(new("busy"), Owner));
        await first.CompleteAsync();
        await using var second = await Store(options).CreateAsync(new("two"), Owner);
        await second.CompleteAsync();
        await Error(CaptureErrorCode.CapacityExceeded, () => Store(options).CreateAsync(new("three"), Owner));
        var page = await Store(options).ListAsync(Owner, 1);
        Assert.Single(page.Captures);
        var next = await Store(options).ListAsync(Owner, 1, page.NextAfterCaptureId);
        Assert.Single(next.Captures);
        Assert.Null(next.NextAfterCaptureId);
        Assert.NotEqual(page.Captures[0].CaptureId, next.Captures[0].CaptureId);
        await first.DisposeAsync();
    }

    [Fact]
    public async Task RealSqliteFull_FailsCompletionAndConservesAllOfferedPopulations()
    {
        var options = new CaptureStoreOptions { MaxDatabaseBytes = 128 * 1024, MaxBatchAge = TimeSpan.FromSeconds(1) };
        var writer = await Store(options).CreateAsync(new("full"), Owner);
        var artifact = writer.AddArtifact("events", "events");
        for (var i = 0; i < 100; i++)
            writer.TryAppend(artifact, new(Name: i + new string('x', 2000)));
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() => writer.CompleteAsync());
        Assert.Equal(CaptureErrorCode.CapacityExceeded, error.Code);
        var metrics = writer.GetMetrics();
        Assert.True(metrics.Quality.StorageRejected > 0);
        Assert.Equal(0, metrics.Quality.Persisted);
        Assert.Equal(0, metrics.Quality.Pending);
        Assert.Equal(0, metrics.QueueBytes);
        AssertConservation(metrics.Quality);
        Assert.False(File.Exists(Path.Combine(Package(writer.Reference.CaptureId), "seal.json")));
        await Assert.ThrowsAsync<CaptureStoreException>(async () => await writer.DisposeAsync());
        await Error(CaptureErrorCode.Incomplete, () => Store().OpenAsync(writer.Reference.CaptureId, Owner));
    }

    [Fact]
    public async Task CancelledCompletion_NeverSeals()
    {
        var writer = await Store().CreateAsync(new("cancel"), Owner);
        var artifact = writer.AddArtifact("test", "test");
        writer.TryAppend(artifact, new(Name: "offered"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = await Record.ExceptionAsync(() => writer.CompleteAsync(cancellation.Token));
        Assert.NotNull(failure);
        Assert.False(File.Exists(Path.Combine(Package(writer.Reference.CaptureId), "seal.json")));
        Assert.True(writer.GetMetrics().Quality.Interrupted);
        AssertConservation(writer.GetMetrics().Quality);
        await Record.ExceptionAsync(async () => await writer.DisposeAsync());
    }

    [Fact]
    public async Task SymlinkMembersAndRoots_AreRejected()
    {
        if (OperatingSystem.IsWindows()) return; // Creating symlinks requires a separate Windows privilege.
        var store = Store();
        await using var writer = await store.CreateAsync(new("links"), Owner);
        await writer.CompleteAsync();
        var id = writer.Reference.CaptureId;
        var db = Path.Combine(Package(id), "capture.sqlite");
        File.Move(db, Path.Combine(_root, "target.sqlite"));
        File.CreateSymbolicLink(db, Path.Combine(_root, "target.sqlite"));
        await Error(CaptureErrorCode.UnsafePath, () => store.OpenAsync(id, Owner));
        await Error(CaptureErrorCode.UnsafePath, () => store.DeleteAsync(id, Owner));
        await Error(CaptureErrorCode.UnsafePath, () => store.RecoverAsync(id, Owner));
        File.Delete(db);
        Directory.Delete(Package(id), recursive: true);
        Directory.Delete(Path.Combine(_root, "captures"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(_root, "captures"), _root);
        await Error(CaptureErrorCode.UnsafePath, () => store.CreateAsync(new("unsafe"), Owner));
        Directory.Delete(Path.Combine(_root, "captures"));
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string ReadHeaderJournalMode(string directory)
    {
        var bytes = File.ReadAllBytes(Path.Combine(directory, "capture.sqlite"));
        Assert.Equal(2, bytes[18]);
        Assert.Equal(2, bytes[19]);
        return "wal";
    }

    private static SortedDictionary<string, string> Hashes(string directory) => new(
        Directory.GetFiles(directory).ToDictionary(static file => Path.GetFileName(file)!,
            static file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))))!,
        StringComparer.Ordinal);

    private static void AssertConservation(CaptureQuality quality) =>
        Assert.Equal(quality.Offered, quality.Persisted + quality.RecordRejected +
            quality.QueueRejected + quality.StorageRejected + quality.Pending);

    private static async Task Error(CaptureErrorCode code, Func<Task> action) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<CaptureStoreException>(action)).Code);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
