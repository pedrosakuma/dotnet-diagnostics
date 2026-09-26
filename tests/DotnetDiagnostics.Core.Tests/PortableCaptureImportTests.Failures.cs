using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetDiagnostics.Core.Captures;
using Microsoft.Data.Sqlite;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class PortableCaptureImportTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void AcceptedQualityExcludesPreAdmissionRejectionsButAllowsBothStorageRejectionPaths(long accepted, bool valid)
    {
        var manifest = JsonSerializer.Deserialize(ReadArchive(Frozen())[5].Bytes, CaptureJsonContext.Default.CaptureManifest)!;
        manifest = manifest with { Info = manifest.Info with { Quality = manifest.Info.Quality with { Accepted = accepted } } };
        if (valid) _ = CapturePackage.ValidateManifest(manifest, manifest.Info.CaptureId);
        else
        {
            var error = Assert.Throws<CaptureStoreException>(() => CapturePackage.ValidateManifest(manifest, manifest.Info.CaptureId));
            Assert.Equal(CaptureErrorCode.CorruptPackage, error.Code);
        }
    }

    [Theory]
    [InlineData("traversal")]
    [InlineData("duplicate")]
    [InlineData("native")]
    [InlineData("wal")]
    [InlineData("trailing")]
    [InlineData("truncated")]
    [InlineData("descriptor")]
    [InlineData("membership")]
    [InlineData("index-label")]
    [InlineData("future")]
    public async Task InvalidArchiveCannotPublishAnyEntry(string mutation)
    {
        if (!Linux) return;
        var members = ReadArchive(Frozen());
        switch (mutation)
        {
            case "traversal": members[2] = ("../" + members[2].Name, members[2].Bytes); break;
            case "duplicate": members[3] = (members[2].Name, members[3].Bytes); break;
            case "native": members[3] = (members[3].Name.Replace("capture.sqlite", "native.nettrace", StringComparison.Ordinal), members[3].Bytes); break;
            case "wal": members[3] = (members[3].Name + "-wal", members[3].Bytes); break;
            case "membership":
                var index = JsonNode.Parse(members[0].Bytes)!;
                index["entries"]![0]!["members"]![1]!["bytes"] = 3;
                UpdateIndex(members, index);
                break;
            case "index-label":
                var label = JsonNode.Parse(members[0].Bytes)!;
                label["entries"]![0]!["label"] = "modified";
                members[0] = (members[0].Name, Encoding.UTF8.GetBytes(label.ToJsonString()));
                break;
            case "future":
                var future = JsonNode.Parse(members[0].Bytes)!;
                future["requiredArchiveReaderVersion"] = 99;
                UpdateIndex(members, future);
                break;
        }
        var bytes = Zip(members, mutation == "descriptor" ? (ushort)0x808 : (ushort)0x800);
        if (mutation == "trailing") bytes = [.. bytes, 0];
        if (mutation == "truncated") bytes = bytes[..^1];
        var calls = 0;
        await Assert.ThrowsAsync<CaptureStoreException>(() => Service().ImportAsync(
            new(Key(), bytes.Length, Hash(bytes)), new MemoryStream(bytes), Owner,
            (_, _, _, _) => { calls++; return ValueTask.CompletedTask; }));
        Assert.Equal(0, calls);
        Assert.Empty((await Store().ListAsync(Owner)).Captures);
    }

    [Theory]
    [InlineData("CREATE VIEW unexpected AS SELECT 1;")]
    [InlineData("CREATE TRIGGER unexpected AFTER INSERT ON strings BEGIN SELECT 1; END;")]
    [InlineData("DROP INDEX ix_occurrence_name; CREATE INDEX ix_occurrence_name ON occurrences(name_id,artifact_id,id);")]
    [InlineData("UPDATE fields SET ordinal=1;")]
    [InlineData("UPDATE fields SET int_value=NULL;")]
    [InlineData("UPDATE occurrences SET name_id=999;")]
    [InlineData("UPDATE strings SET value=CAST(x'ff' AS TEXT) WHERE id=2;")]
    [InlineData("INSERT INTO snapshots VALUES('cccccccccccccccccccccccccccccccc',1,CAST('{\"kind\":\"counters\",\"kind\":\"counters\",\"snapshot\":{}}' AS BLOB));")]
    [InlineData("INSERT INTO snapshots VALUES('cccccccccccccccccccccccccccccccc',99,CAST('{}' AS BLOB));")]
    public async Task InvalidSecondSqliteOrSnapshotRejectsTheWholePreparedBundle(string mutation)
    {
        if (!Linux) return;
        var members = ReadArchive(Frozen());
        members[6] = (members[6].Name, CreateKnownSql(mutation));
        ResealEntry(members, 1);
        var bytes = Zip(members);
        var prepared = false;
        await Assert.ThrowsAsync<CaptureStoreException>(() => Service().ImportAsync(
            new(Key(), bytes.Length, Hash(bytes)), new MemoryStream(bytes), Owner,
            (_, _, _, _) => { prepared = true; return ValueTask.CompletedTask; }));
        Assert.False(prepared);
        Assert.Empty((await Store().ListAsync(Owner)).Captures);
    }

    [Theory]
    [InlineData("overflow")]
    [InlineData("negative")]
    [InlineData("persisted")]
    public async Task QualityCannotBeNormalizedOrOverflowedIntoCleanEvidence(string mutation)
    {
        if (!Linux) return;
        var members = ReadArchive(Frozen());
        var manifest = JsonNode.Parse(members[5].Bytes)!;
        var quality = manifest["Info"]!["Quality"]!;
        if (mutation == "negative") quality["SourceRejected"] = -1;
        else if (mutation == "persisted") quality["Persisted"] = 2;
        else
        {
            quality["Offered"] = 1;
            quality["Accepted"] = 1;
            quality["RecordRejected"] = long.MaxValue;
            quality["QueueRejected"] = long.MaxValue;
            quality["StorageRejected"] = 2;
        }
        members[5] = (members[5].Name, Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        ResealEntry(members, 1);
        var bytes = Zip(members);
        await Assert.ThrowsAsync<CaptureStoreException>(() => Service().ImportAsync(
            new(Key(), bytes.Length, Hash(bytes)), new MemoryStream(bytes), Owner, Allow));
        Assert.Empty((await Store().ListAsync(Owner)).Captures);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AuthorizationAndCancellationPreserveTruthfulPartialPublication(bool afterFirst, bool cancel)
    {
        if (!Linux) return;
        using var diagnostics = new WatchdogFailureOutput(_output);
        var bytes = Frozen();
        var request = new CaptureImportRequest(Key(), bytes.Length, Hash(bytes));
        using var cancelled = new CancellationTokenSource();
        var publications = 0;
        async Task<PortableImportResult> Import() => await Service().ImportAsync(request, new MemoryStream(bytes), Owner,
            (_, phase, _, _) =>
            {
                var deny = afterFirst ? phase == PortableAuthorizationPhase.Publish && ++publications == 2 :
                    phase == PortableAuthorizationPhase.Prepare;
                if (deny)
                {
                    if (cancel) cancelled.Cancel();
                    else throw new CaptureStoreException(CaptureErrorCode.Forbidden, "Revoked: current policy denies this entry.");
                }
                return ValueTask.CompletedTask;
            }, cancelled.Token);
        if (!afterFirst)
        {
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Import);
            else Assert.Equal(CaptureErrorCode.Forbidden, (await Assert.ThrowsAsync<CaptureStoreException>(Import)).Code);
            Assert.Empty((await Store().ListAsync(Owner)).Captures);
        }
        else
        {
            var result = await Import();
            Assert.False(result.Complete);
            Assert.Equal(cancel, result.Cancelled);
            Assert.Equal(PortableEntryState.Published, result.Entries[0].State);
            Assert.Equal(cancel ? PortableEntryState.Cancelled : PortableEntryState.Failed, result.Entries[1].State);
            Assert.Null(result.Entries[1].Mapping);
            Assert.Single((await Store().ListAsync(Owner)).Captures);
            Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(
                await Service().GetImportResultAsync(request.Operation, Owner)));
            Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(
                await Service().ImportAsync(request, new NoRead(), Owner, Allow)));
        }
    }

    private sealed class WatchdogFailureOutput : IDisposable
    {
        private readonly Xunit.Abstractions.ITestOutputHelper _output;
        private CaptureStoreException? _first;

        internal WatchdogFailureOutput(Xunit.Abstractions.ITestOutputHelper output)
        {
            _output = output;
            AppDomain.CurrentDomain.FirstChanceException += Observe;
        }

        private void Observe(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            // Keep one enriched failure even when the importer returns a partial result.
            // No stack capture, payload inspection, clock read or output on the worker thread.
            if (args.Exception is CaptureStoreException error && error.Data.Contains("WorkerGapTicks"))
                Interlocked.CompareExchange(ref _first, error, null);
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.FirstChanceException -= Observe;
            var error = Volatile.Read(ref _first);
            if (error is null) return;
            var fields = error.Data.Cast<System.Collections.DictionaryEntry>()
                .Where(static pair => pair.Key is string key && key.StartsWith("Worker", StringComparison.Ordinal))
                .Take(12).ToDictionary(static pair => (string)pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            _output.WriteLine("First worker gap diagnostics: " + JsonSerializer.Serialize(fields));
        }
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(3, false)]
    public async Task ActualTableRowCeilingAndOneBeyondAreEnforced(int limit, bool success)
    {
        if (!Linux) return;
        var bytes = Frozen();
        var task = Service(options: new() { MaxRowsPerTable = limit }).ImportAsync(
            new(Key(), bytes.Length, Hash(bytes)), new MemoryStream(bytes), Owner, Allow);
        if (success) Assert.True((await task).Complete);
        else
        {
            var error = await Assert.ThrowsAsync<CaptureStoreException>(() => task);
            Assert.Equal(CaptureErrorCode.CapacityExceeded, error.Code);
            Assert.Empty((await Store().ListAsync(Owner)).Captures);
        }
    }

    [Fact]
    public async Task StatusAndRetryAreOwnerBoundAndInputFingerprintConflicts()
    {
        if (!Linux) return;
        var bytes = Frozen();
        var request = new CaptureImportRequest(Key(), bytes.Length, Hash(bytes));
        Assert.True((await Service().ImportAsync(request, new MemoryStream(bytes), Owner, Allow)).Complete);
        var conflict = await Assert.ThrowsAsync<CaptureStoreException>(() => Service().ImportAsync(
            request with { ArchiveSha256 = new string('0', 64) }, new NoRead(), Owner, Allow));
        Assert.Contains("OperationConflict", conflict.Message);
        var forbidden = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            Service().GetImportResultAsync(request.Operation, new("other", AllOwners: true)));
        Assert.Equal(CaptureErrorCode.NotFound, forbidden.Code);
        Assert.Equal(2, (await Store().ListAsync(Owner)).Captures.Count);
    }

    private byte[] CreateKnownSql(string mutation)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".sqlite");
        var generator = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../Fixtures/PortableImport/generate.py")));
        var sql = generator.Split("SQL = \"\"\"", StringSplitOptions.None)[1].Split("\"\"\"", StringSplitOptions.None)[0];
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=OFF;" + sql + "INSERT INTO format VALUES(2,1,1,1,2,2);" + mutation;
            command.ExecuteNonQuery();
        }
        return File.ReadAllBytes(path);
    }

    private static List<(string Name, byte[] Bytes)> ReadArchive(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        return zip.Entries.Select(entry =>
        {
            using var source = entry.Open();
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            return (entry.FullName, buffer.ToArray());
        }).ToList();
    }

    private static void ResealEntry(List<(string Name, byte[] Bytes)> members, int entry)
    {
        var start = 2 + entry * 3;
        members[start + 2] = (members[start + 2].Name, JsonSerializer.SerializeToUtf8Bytes(new
        {
            ManifestHash = Hash(members[start].Bytes).ToUpperInvariant(),
            DatabaseHash = Hash(members[start + 1].Bytes).ToUpperInvariant()
        }));
        var index = JsonNode.Parse(members[0].Bytes)!;
        for (var i = 0; i < 3; i++)
        {
            var descriptor = index["entries"]![entry]!["members"]![i]!;
            descriptor["bytes"] = members[start + i].Bytes.Length;
            descriptor["sha256"] = Hash(members[start + i].Bytes);
        }
        UpdateIndex(members, index);
    }

    private static void UpdateIndex(List<(string Name, byte[] Bytes)> members, JsonNode index)
    {
        members[0] = (members[0].Name, Encoding.UTF8.GetBytes(index.ToJsonString()));
        members[1] = (members[1].Name, JsonSerializer.SerializeToUtf8Bytes(new
        { archiveVersion = 1, indexBytes = members[0].Bytes.Length, indexSha256 = Hash(members[0].Bytes) }));
    }

    private static byte[] Zip(List<(string Name, byte[] Bytes)> members, ushort flags = 0x800)
    {
        using var data = new MemoryStream();
        using var directory = new MemoryStream();
        using var writer = new BinaryWriter(data, Encoding.UTF8, true);
        using var central = new BinaryWriter(directory, Encoding.UTF8, true);
        foreach (var member in members)
        {
            var name = Encoding.ASCII.GetBytes(member.Name);
            var offset = checked((uint)data.Length);
            uint crc = uint.MaxValue;
            foreach (var b in member.Bytes)
            {
                crc ^= b;
                for (var bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
            }
            crc = ~crc;
            writer.Write(0x04034b50U); writer.Write((ushort)20); writer.Write(flags);
            writer.Write((ushort)0); writer.Write(0U); writer.Write(crc);
            writer.Write(member.Bytes.Length); writer.Write(member.Bytes.Length);
            writer.Write((ushort)name.Length); writer.Write((ushort)0); writer.Write(name); writer.Write(member.Bytes);
            central.Write(0x02014b50U); central.Write((ushort)20); central.Write((ushort)20); central.Write(flags);
            central.Write((ushort)0); central.Write(0U); central.Write(crc);
            central.Write(member.Bytes.Length); central.Write(member.Bytes.Length);
            central.Write((ushort)name.Length); central.Write((ushort)0); central.Write((ushort)0);
            central.Write((ushort)0); central.Write((ushort)0); central.Write(0U); central.Write(offset); central.Write(name);
        }
        var directoryStart = checked((uint)data.Length);
        writer.Write(directory.ToArray());
        writer.Write(0x06054b50U); writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write((ushort)members.Count); writer.Write((ushort)members.Count);
        writer.Write(checked((uint)directory.Length)); writer.Write(directoryStart); writer.Write((ushort)0);
        return data.ToArray();
    }
}
