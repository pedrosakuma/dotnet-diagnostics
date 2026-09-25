using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Captures;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

[Collection("PortableExportResources")]
public sealed class SqliteAdmissionTests(ITestOutputHelper output) : IDisposable
{
    private const string Artifact = "11111111111111111111111111111111";
    private const string V2Hash = "F587BB019255611C138AEF061CDCDE936AE04219DD28A4AB2DB5F6F551E1D887";
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "admission-tests", Guid.NewGuid().ToString("N"));
    private static bool Linux => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    private static string Fixtures => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Fixtures"));
    private string Database => Path.Combine(_root, "source ?#% \u00e9.sqlite");
    private static CaptureFormatVersions V2 => new(2, 1, 1, 1, 2, 2);
    private SqliteAdmissionRequest Request => new(
        Path.Combine(AppContext.BaseDirectory, "capture-worker"),
        Path.Combine(AppContext.BaseDirectory, "runtimes/linux-x64/native/libe_sqlite3.so"),
        _root, Database, V2, [new(Artifact, "synthetic", "Independent v2 scalar fixture")], 1);

    private void Staging()
    {
        Directory.CreateDirectory(_root);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private void FrozenV2()
    {
        Staging();
        var file = Path.Combine(Fixtures, "SqliteAdmission/v2.sqlite");
        Assert.Equal(V2Hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        File.Copy(file, Database);
    }

    // This is trusted generator source, not DDL extracted from a database or archive.
    private void CreateKnownSql(string mutation, Func<string, string>? transform = null)
    {
        Staging();
        Assert.False(File.Exists(Database));
        var generator = File.ReadAllText(Path.Combine(Fixtures, "SqliteAdmission/generate.py"));
        var sql = generator.Split("SQL = \"\"\"", StringSplitOptions.None)[1].Split("\"\"\"", StringSplitOptions.None)[0];
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Database, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=OFF;" + (transform is null ? sql : transform(sql)) + mutation;
        command.ExecuteNonQuery();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FrozenIndependentFormats_StreamProvisionalEvidenceWithoutHostSqliteReads(int version)
    {
        if (!Linux) return;
        SqliteAdmissionRequest request;
        if (version == 2) { FrozenV2(); request = Request; }
        else
        {
            Staging();
            var path = Path.Combine(Fixtures, "DurableCaptureV1/sealed-v1.zip");
            Assert.Equal("B2F22F08BB2BA46D54A6E157F45280281A96070A4BF56F47B3766579F59D7B7E",
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            using var archive = ZipFile.OpenRead(path);
            archive.GetEntry("capture.sqlite")!.ExtractToFile(Database);
            Assert.Equal("0CAED899C6DA15DD4722976325DD74E20E49000088C5DE0865BDFAE4C915C6E7",
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Database))));
            request = Request with { Format = new(1, 1, 1, 1, 1, 1), Persisted = 2,
                Artifacts = [new("e3c8b564ffec41f980015ed8f40867ca", "synthetic", "Synthetic v1 occurrences")] };
        }
        var before = SHA256.HashData(File.ReadAllBytes(Database));
        using var evidence = new MemoryStream();
        var result = await IsolatedCaptureWorker.AdmitSqliteAsync(request, evidence);
        Assert.Equal(version == 1 ? 2 : 1, result.TableRows[3]);
        Assert.Equal(result.WireBytes, evidence.Length);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(Database)));
        Assert.Single(Directory.GetFiles(_root));
        output.WriteLine($"v{version}: rows={string.Join(',', result.TableRows)} logical={result.LogicalBytes} " +
            $"wire={result.WireBytes} VM={result.VmInstructions} ABI={result.LandlockAbi} " +
            $"RSS={result.PeakObservedRss} gapMs={result.MaximumObservationGap.TotalMilliseconds:F3}");
    }

    [Fact]
    public async Task KnownProducerWhitespaceAndIdentifierQuotingRemainSupported()
    {
        if (!Linux) return;
        CreateKnownSql("", static sql => sql.Replace("CREATE TABLE format(", "CREATE  TABLE \"format\" (\n", StringComparison.Ordinal));
        await IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null);
    }

    [Theory]
    [InlineData("DROP INDEX ix_occurrence_name; CREATE INDEX ix_occurrence_name ON occurrences(name_id,artifact_id,id);", "Schema.Definition")]
    [InlineData("DROP INDEX ix_occurrence_name; CREATE UNIQUE INDEX ix_occurrence_name ON occurrences(artifact_id,name_id,id);", "Schema.Definition")]
    [InlineData("CREATE INDEX extra ON strings(value);", "Schema.Object")]
    [InlineData("CREATE VIEW extra AS SELECT 1;", "Schema.RootPage")]
    [InlineData("CREATE TRIGGER extra AFTER INSERT ON strings BEGIN SELECT 1; END;", "Schema.RootPage")]
    [InlineData("CREATE TABLE extra(value);", "Schema.Object")]
    [InlineData("ANALYZE;", "Schema.Object")]
    [InlineData("DELETE FROM format;", "Data.FormatMissing")]
    [InlineData("INSERT INTO format VALUES(2,1,1,1,2,2);", "Data.FormatDuplicate")]
    [InlineData("UPDATE format SET package=99;", "Format.Mismatch")]
    [InlineData("UPDATE occurrences SET name_id=999;", "Data.ForeignKey")]
    [InlineData("UPDATE fields SET ordinal=1;", "Data.FieldOrdinal")]
    [InlineData("UPDATE fields SET int_value=5;", "Data.ScalarSlots")]
    [InlineData("UPDATE fields SET kind=4,string_id=NULL,bool_value=2; DELETE FROM strings WHERE id=3;", "Data.ScalarSlots")]
    [InlineData("UPDATE occurrences SET numeric_value=9e999;", "Data.NonFinite")]
    [InlineData("UPDATE occurrences SET timestamp_ticks=-1;", "Data.Timestamp")]
    [InlineData("UPDATE occurrences SET duration_ns=-1;", "Data.Duration")]
    [InlineData("UPDATE strings SET value=CAST(x'ff' AS TEXT) WHERE id=3;", "Data.Utf8")]
    [InlineData("INSERT INTO strings VALUES(4,'unused');", "Data.UnusedString")]
    [InlineData("UPDATE artifacts SET name='different';", "Data.ArtifactDescriptor")]
    [InlineData("UPDATE occurrences SET thread_id=x'0102';", "Data.StorageClass")]
    [InlineData("UPDATE snapshots SET json=x'7b';", "Snapshot.JsonDepthOrSyntax")]
    public async Task InvalidSchemaOrScalarsFailOnlyInsideContainedChild(string mutation, string reason)
    {
        if (!Linux) return;
        CreateKnownSql(mutation);
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() => IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null));
        Assert.True(error.Code == CaptureErrorCode.CorruptPackage, error.Message);
        Assert.Contains(reason, error.Message);
    }

    [Theory]
    [InlineData("kind BETWEEN 0 AND 4", "kind BETWEEN 0 AND 5")]
    [InlineData("kind BETWEEN 0 AND 4", "kind BETWEEN 0 AND 4 AND 'a b' <> 'ab'")]
    [InlineData("kind BETWEEN 0 AND 4", "kind BETWEEN \"kind\" AND \"kind\"")]
    [InlineData("name TEXT NOT NULL", "name TEXT")]
    public async Task AlteredConstraintDefinitionsAreNotNormalizedIntoTheAllowlist(string original, string replacement)
    {
        if (!Linux) return;
        CreateKnownSql("", sql => sql.Replace(original, replacement, StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() => IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null));
        Assert.Contains("Schema.", error.Message);
    }

    [Theory]
    [InlineData("rows", 3)]
    [InlineData("total", 8)]
    [InlineData("snapshot", 2)]
    [InlineData("tokens", 2)]
    [InlineData("record", 318)]
    [InlineData("logical", 320)]
    [InlineData("fields", 1)]
    public async Task ConsumptionBoundaryAcceptsExactAndRejectsOneLess(string limit, int exact)
    {
        if (!Linux) return;
        FrozenV2();
        SqliteAdmissionLimits Limits(int maximum) => limit switch
        {
            "rows" => new() { RowsPerTable = maximum },
            "total" => new() { RowsPerCapture = maximum },
            "snapshot" => new() { Store = new() { MaxSnapshotBytes = maximum } },
            "record" => new() { Store = new() { MaxRecordBytes = maximum } },
            "logical" => new() { Store = new() { MaxLogicalBytes = maximum } },
            "fields" => new() { Store = new() { MaxFields = maximum } },
            _ => new() { TokensPerSnapshot = maximum }
        };
        await IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null, Limits(exact));
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null, Limits(exact - 1)));
        Assert.Equal(CaptureErrorCode.CapacityExceeded, error.Code);
    }

    [Fact]
    public async Task ShortStatementsAreChargedToTheCumulativeNativeVmBudget()
    {
        if (!Linux) return;
        FrozenV2();
        var result = await IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null);
        Assert.True(result.VmInstructions >= 1000);
        output.WriteLine($"Cumulative native VM instructions, including sub-callback statements: {result.VmInstructions}");
        await IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null,
            new() { VmInstructions = result.VmInstructions });
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null, new() { VmInstructions = result.VmInstructions - 1 }));
        Assert.Equal(CaptureErrorCode.CapacityExceeded, error.Code);
        Assert.Contains("VmInstructions", error.Message);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    public async Task NativeFieldCountUsesTheRealSixtyFourFieldCeiling(int fields)
    {
        if (!Linux) return;
        CreateKnownSql("DELETE FROM fields;" + string.Concat(Enumerable.Range(0, fields).Select(i =>
            FormattableString.Invariant($"INSERT INTO fields VALUES(1,{i},2,1,3,NULL,NULL,NULL,NULL);"))));
        if (fields == 64) await IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null);
        else
        {
            var error = await Assert.ThrowsAsync<CaptureStoreException>(() =>
                IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null));
            Assert.Equal(CaptureErrorCode.CapacityExceeded, error.Code);
            Assert.Contains("Limit.Fields", error.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeRecordBytesEnforceSixtyFourKiBAndOneBeyond(bool beyond)
    {
        if (!Linux) return;
        var text = new string('a', 21745) + (beyond ? "\u0800" : "\u00e9");
        CreateKnownSql("UPDATE strings SET value='" + text + "' WHERE id=3;");
        if (!beyond)
        {
            var result = await IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null);
            Assert.Equal(65536 + 2, result.LogicalBytes);
        }
        else
        {
            var error = await Assert.ThrowsAsync<CaptureStoreException>(() => IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null));
            Assert.Contains("Limit.RecordBytes", error.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotStreamingEnforcesEightMiBAndOneBeyond(bool beyond)
    {
        if (!Linux) return;
        var padding = 8 * 1024 * 1024 - 2 + (beyond ? 1 : 0);
        CreateKnownSql(FormattableString.Invariant($"UPDATE snapshots SET json=CAST('{{}}' || printf('%.*c',{padding},' ') AS BLOB);"));
        if (!beyond)
        {
            var result = await IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null);
            Assert.Equal(318 + 8 * 1024 * 1024, result.LogicalBytes);
            Assert.True(result.WireBytes > 8 * 1024 * 1024);
            output.WriteLine($"8 MiB snapshot: wire={result.WireBytes} RSS={result.PeakObservedRss} gapMs={result.MaximumObservationGap.TotalMilliseconds:F3}");
        }
        else
        {
            var error = await Assert.ThrowsAsync<CaptureStoreException>(() => IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null));
            Assert.Contains("Limit.SnapshotBytes", error.Message);
        }
    }

    [Fact]
    public async Task CorruptNativeBtreeCannotProduceEvidenceSuccess()
    {
        if (!Linux) return;
        FrozenV2();
        using (var file = new FileStream(Database, FileMode.Open, FileAccess.Write))
        { file.Position = 100; file.WriteByte(0); }
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() => IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null));
        Assert.Equal(CaptureErrorCode.CorruptPackage, error.Code);
        Assert.Contains("Sqlite", error.Message);
    }

    [Fact]
    public async Task UnsupportedFormatIsExplicitBeforeNativeOpen()
    {
        if (!Linux) return;
        FrozenV2();
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            IsolatedCaptureWorker.AdmitSqliteAsync(Request with { Format = new(99, 1, 1, 1, 99, 99) }, Stream.Null));
        Assert.Equal(CaptureErrorCode.UnsupportedFormat, error.Code);
        Assert.Contains("Format.Unsupported", error.Message);
    }

    [Theory]
    [InlineData("CREATE TABLE hidden(value INTEGER, derived INTEGER GENERATED ALWAYS AS (value+1));", "Schema.Object")]
    [InlineData("CREATE VIRTUAL TABLE hidden USING fts5(value);", "Schema.RootPage")]
    public async Task SchemaGeneratedColumnsAndVirtualTablesAreRejected(string definition, string reason)
    {
        if (!Linux) return;
        CreateKnownSql(definition);
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null));
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public async Task CheckpointedWalHeaderDoesNotGrantSidecarAccess()
    {
        if (!Linux) return;
        CreateKnownSql("PRAGMA journal_mode=WAL;");
        Assert.False(File.Exists(Database + "-wal"));
        await IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null);
    }

    [Fact]
    public async Task SnapshotDepthBeyondSixtyFourIsRejectedBeforeTypedMaterialization()
    {
        if (!Linux) return;
        var json = new string('[', 65) + "0" + new string(']', 65);
        CreateKnownSql("UPDATE snapshots SET json=CAST('" + json + "' AS BLOB);");
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() => IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null));
        Assert.Contains("Snapshot.JsonDepthOrSyntax", error.Message);
    }

    [Fact]
    public async Task ExportedSourceDatabaseIsAdmittedOfflineWithoutSourceMutation()
    {
        if (!Linux) return;
        Staging();
        var store = new SqliteCaptureStore(new FixtureRoot(Path.Combine(_root, "store")));
        await using var writer = await store.CreateAsync(new("offline export"), new("owner"));
        var artifact = writer.AddArtifact("counters", "counter");
        Assert.True(writer.TryAppend(artifact, new(Name: "metric", NumericValue: 1.5)));
        var capture = await writer.CompleteAsync();
        using var archive = new MemoryStream();
        var exporter = new PortableCaptureUseCases(store, static (_, _) => ValueTask.CompletedTask);
        await exporter.ExportAsync(new(new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow),
            [new(capture.CaptureId, "source")]), archive, new("owner"));
        archive.Position = 0;
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true))
            Assert.Single(zip.Entries, static e => e.FullName.EndsWith("/capture.sqlite", StringComparison.Ordinal)).ExtractToFile(Database);
        var result = await IsolatedCaptureWorker.AdmitSqliteAsync(Request with
            { Artifacts = capture.Artifacts, Persisted = capture.Quality.Persisted }, Stream.Null);
        Assert.Equal(1, result.TableRows[3]);
    }

    [Fact]
    public async Task OutputFailureLeavesOnlyProvisionalPrefixAndNoSuccessfulResult()
    {
        if (!Linux) return;
        FrozenV2();
        using var sink = new FailingSink();
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() => IsolatedCaptureWorker.AdmitSqliteAsync(Request, sink));
        Assert.Equal(CaptureErrorCode.StorageFailure, error.Code);
        Assert.IsType<IOException>(error.InnerException);
        Assert.True(sink.Bytes > 0);
    }

    [Fact]
    public async Task CancellationDuringFramedEvidenceCannotProduceAdmission()
    {
        if (!Linux) return;
        FrozenV2();
        using var cancellation = new CancellationTokenSource();
        using var sink = new CancellingSink(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            IsolatedCaptureWorker.AdmitSqliteAsync(Request, sink, cancellationToken: cancellation.Token));
    }

    private sealed record FixtureRoot(string Root) : IArtifactRootProvider;
    private sealed class FailingSink : MemoryStream
    {
        internal long Bytes { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Bytes > 0) throw new IOException("Purpose-created evidence output failure");
            Bytes += buffer.Length;
            return ValueTask.CompletedTask;
        }
    }
    private sealed class CancellingSink(CancellationTokenSource cancellation) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ManifestPopulationMismatchIsNotRepaired()
    {
        if (!Linux) return;
        FrozenV2();
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            IsolatedCaptureWorker.AdmitSqliteAsync(Request with { Persisted = 2 }, Stream.Null));
        Assert.Contains("Data.PersistedPopulation", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FixedHeaderRejectsGeometryOrTruncationWithoutAnyNativeOpen(bool truncate)
    {
        if (!Linux) return;
        FrozenV2();
        using (var file = new FileStream(Database, FileMode.Open, FileAccess.Write))
        {
            if (truncate) file.SetLength(99);
            else { file.Position = 56; file.Write([0, 0, 0, 2]); }
        }
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() => IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null));
        Assert.Contains("Header.", error.Message);
    }

    [Fact]
    public async Task WalSidecarIsRejectedNotSilentlyIgnored()
    {
        if (!Linux) return;
        FrozenV2();
        File.WriteAllBytes(Database + "-wal", []);
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() => IsolatedCaptureWorker.AdmitSqliteAsync(Request, Stream.Null));
        Assert.Contains("Header.ExternalJournal", error.Message);
    }

    [Fact]
    public async Task BoundedWireRejectsLengthBeforeEagerPayloadAllocation()
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 65537);
        using var input = new MemoryStream(bytes);
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            SqliteAdmissionWire.ReadAsync(input, Stream.Null, Request, new(), CancellationToken.None));
        Assert.Equal(CaptureErrorCode.CapacityExceeded, error.Code);
        Assert.Equal(4, input.Position);
    }

    [Fact]
    public async Task PartialChildFrameCannotCompleteAdmission()
    {
        using var input = new MemoryStream([3, 0, 0, 0, 1]);
        var error = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            SqliteAdmissionWire.ReadAsync(input, Stream.Null, Request, new(), CancellationToken.None));
        Assert.Contains("Wire.Truncated", error.Message);
    }

    [Theory]
    [InlineData(true, 100000)]
    [InlineData(true, 100001)]
    [InlineData(false, 100000)]
    [InlineData(false, 100001)]
    public void ExitReconciliationNeverResetsTheLastValidSampleDeadline(bool confirmed, long ticks)
    {
        var observation = new CaptureWorkerObservation(new());
        observation.Record(TimeSpan.Zero, 1000, TimeSpan.Zero);
        var now = TimeSpan.Zero;
        void Reconcile() => observation.WaitForConfirmedExit(() => now, milliseconds =>
        {
            Assert.Equal(10, milliseconds);
            now = TimeSpan.FromTicks(ticks);
            return confirmed;
        }, CancellationToken.None);
        if (confirmed && ticks == 100000) Reconcile();
        else
        {
            var error = Assert.Throws<CaptureStoreException>(Reconcile);
            Assert.Equal(ticks > 100000 ? CaptureErrorCode.CapacityExceeded : CaptureErrorCode.UnsupportedFormat, error.Code);
        }
        Assert.Equal(1000, observation.PeakRss);
    }

    [Fact]
    public void ZeroRssCanWaitBeyondOneMillisecondOnlyForConfirmedExitWithinTheOriginalDeadline()
    {
        var observation = new CaptureWorkerObservation(new());
        observation.Record(TimeSpan.FromMilliseconds(40), 1000, TimeSpan.Zero);
        var now = TimeSpan.FromMilliseconds(43);
        var calls = 0;
        observation.WaitForConfirmedExit(() => now, milliseconds =>
        {
            calls++;
            Assert.Equal(7, milliseconds);
            now = TimeSpan.FromMilliseconds(47);
            return true;
        }, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal(1000, observation.PeakRss);
        Assert.Equal(TimeSpan.FromMilliseconds(7), observation.MaximumGap);
        var error = Assert.Throws<CaptureStoreException>(() => observation.CheckGap(
            TimeSpan.FromMilliseconds(50) + TimeSpan.FromTicks(1)));
        Assert.Contains("WorkerObservationGap", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PendingZeroRssUsesOnlyTheRemainderFromTheLastValidSample(bool confirmAtDeadline)
    {
        var observation = new CaptureWorkerObservation(new());
        observation.Record(TimeSpan.FromMilliseconds(40), 1000, TimeSpan.Zero);
        var now = TimeSpan.FromMilliseconds(43.5);
        var calls = 0;
        void Reconcile() => observation.WaitForConfirmedExit(() => now, milliseconds =>
        {
            Assert.True(++calls <= 3);
            Assert.Equal(calls == 1 ? 6 : 0, milliseconds);
            now = TimeSpan.FromMilliseconds(calls == 1 ? 49.5 : calls == 2 ? 49.75 : 50);
            return calls == 3 && confirmAtDeadline;
        }, CancellationToken.None);
        if (confirmAtDeadline) Reconcile();
        else
        {
            var error = Assert.Throws<CaptureStoreException>(Reconcile);
            Assert.Equal(CaptureErrorCode.UnsupportedFormat, error.Code);
            Assert.Contains("WorkerObservationUnavailable", error.Message);
        }
        Assert.Equal(3, calls);
        Assert.Equal(TimeSpan.FromMilliseconds(10), observation.MaximumGap);
        Assert.Equal(1000, observation.PeakRss);
    }

    [Fact]
    public void ExitProbeFaultIsUnavailableNotExitEvidence()
    {
        var observation = new CaptureWorkerObservation(new());
        observation.Record(TimeSpan.Zero, 1000, TimeSpan.Zero);
        var now = TimeSpan.FromMilliseconds(2);
        var failure = new IOException("Purpose-created unavailable exit observation");
        var error = Assert.Throws<CaptureStoreException>(() => observation.WaitForConfirmedExit(() => now, _ =>
        {
            now = TimeSpan.FromMilliseconds(4);
            throw failure;
        }, CancellationToken.None));
        Assert.Equal(CaptureErrorCode.UnsupportedFormat, error.Code);
        Assert.Same(failure, error.InnerException);
        Assert.Equal(TimeSpan.FromMilliseconds(4), observation.MaximumGap);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAndWallDeadlinePreemptExitEvidenceEvenIfTheGapAlsoExpires(bool cancel)
    {
        var observation = new CaptureWorkerObservation(new() { WallTime = TimeSpan.FromMilliseconds(5) });
        observation.Record(TimeSpan.Zero, 1000, TimeSpan.Zero);
        var now = TimeSpan.FromMilliseconds(2);
        using var cancellation = new CancellationTokenSource();
        void Reconcile() => observation.WaitForConfirmedExit(() => now, milliseconds =>
        {
            Assert.Equal(3, milliseconds);
            now = TimeSpan.FromMilliseconds(11);
            if (cancel) cancellation.Cancel();
            return true;
        }, cancellation.Token);
        if (cancel) Assert.ThrowsAny<OperationCanceledException>(Reconcile);
        else
        {
            var error = Assert.Throws<CaptureStoreException>(Reconcile);
            Assert.Contains("WorkerWallTime", error.Message);
        }
    }

    [Fact]
    public void ExpiredObservationDeadlinePreemptsAnExitProbeFault()
    {
        var observation = new CaptureWorkerObservation(new());
        observation.Record(TimeSpan.Zero, 1000, TimeSpan.Zero);
        var now = TimeSpan.FromMilliseconds(2);
        var error = Assert.Throws<CaptureStoreException>(() => observation.WaitForConfirmedExit(() => now, _ =>
        {
            now = TimeSpan.FromMilliseconds(10) + TimeSpan.FromTicks(1);
            throw new IOException("Purpose-created late exit-observation failure");
        }, CancellationToken.None));
        Assert.Equal(CaptureErrorCode.CapacityExceeded, error.Code);
        Assert.Contains("WorkerObservationGap", error.Message);
    }

    [Fact]
    public void ZeroRssWithoutAnInitialValidSampleCannotBecomeSuccess()
    {
        var observation = new CaptureWorkerObservation(new());
        var calls = 0;
        var error = Assert.Throws<CaptureStoreException>(() => observation.WaitForConfirmedExit(
            static () => TimeSpan.Zero, _ => { calls++; return true; }, CancellationToken.None));
        Assert.Equal(CaptureErrorCode.UnsupportedFormat, error.Code);
        Assert.Equal(0, calls);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
