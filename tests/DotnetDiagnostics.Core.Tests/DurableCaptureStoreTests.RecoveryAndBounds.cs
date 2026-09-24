using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using DotnetDiagnostics.Core.Captures;
using Microsoft.Data.Sqlite;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class DurableCaptureStoreTests
{
    [Fact]
    public async Task StoreMarker_IsPrivateExactAndRequired_ReadNeverCreatesOrRepairsIt()
    {
        var store = Store();
        await using var writer = await store.CreateAsync(new("marker"), Owner);
        var marker = Path.Combine(_root, "captures", ".capture-store");
        Assert.Equal("dotnet-diagnostics-captures/1", await File.ReadAllTextAsync(marker));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(marker));
        await writer.CompleteAsync();
        var before = Hashes(Package(writer.Reference.CaptureId));
        File.Delete(marker);
        await Error(CaptureErrorCode.NotFound, () => store.OpenAsync(writer.Reference.CaptureId, Owner));
        await Error(CaptureErrorCode.NotFound, () => store.ListAsync(Owner));
        Assert.False(File.Exists(marker));
        Assert.Equal(before, Hashes(Package(writer.Reference.CaptureId)));
        await File.WriteAllTextAsync(marker, "unknown-marker");
        await Error(CaptureErrorCode.UnsupportedFormat, () => store.OpenAsync(writer.Reference.CaptureId, Owner));
        await Error(CaptureErrorCode.UnsupportedFormat, () => store.CreateAsync(new("must-not-repair"), Owner));
        Assert.Equal("unknown-marker", await File.ReadAllTextAsync(marker));
    }

    [Fact]
    public async Task StoreMarkerInitializationLease_BlocksPackageAndControlCreation()
    {
        Directory.CreateDirectory(Path.Combine(_root, "captures"));
        var marker = Path.Combine(_root, "captures", ".capture-store");
        using (var initializing = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await Error(CaptureErrorCode.Busy, () => Store().CreateAsync(new("wait-for-marker"), Owner));
            Assert.Single(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "captures")));
            initializing.Write("dotnet-diagnostics-captures/1"u8);
            initializing.Flush(flushToDisk: true);
        }
        await using var writer = await Store().CreateAsync(new("initialized"), Owner);
        await writer.CompleteAsync();
    }

    [Fact]
    public async Task SealedState_DoesNotInventKnownZeroSourceLoss()
    {
        await using var writer = await Store().CreateAsync(new("unknown-source"), Owner);
        var capture = await writer.CompleteAsync();
        Assert.Equal(CaptureState.Sealed, capture.State);
        Assert.Null(capture.Quality.SourceRejected);
        Assert.False(capture.Quality.IsComplete);
        Assert.False(capture.Quality.IsIncomplete);
        using var reader = await Store().OpenAsync(capture.CaptureId, Owner);
        Assert.Null(reader.Info.Quality.SourceRejected);
    }

    [Fact]
    public async Task TombstoneHidesCapture_WithoutReleasingUnremovedPackageAdmission()
    {
        var options = new CaptureStoreOptions { MaxCaptures = 1 };
        await using var writer = await Store(options).CreateAsync(new("tombstoned"), Owner);
        await writer.CompleteAsync();
        var id = writer.Reference.CaptureId;
        File.WriteAllBytes(Path.Combine(_root, "captures", ".deleted-" + id), []);
        Assert.Empty((await Store(options).ListAsync(Owner)).Captures);
        await Error(CaptureErrorCode.Deleted, () => Store(options).OpenAsync(id, Owner));
        await Error(CaptureErrorCode.Deleted, () => Store(options).RecoverAsync(id, Owner));
        await Error(CaptureErrorCode.CapacityExceeded, () => Store(options).CreateAsync(new("still-full"), Owner));
        Assert.True(Directory.Exists(Package(id)));
    }

    [Fact]
    public async Task ReadonlyPackagePermissions_OpenWithoutWalShmOrEvidenceChanges()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var writer = await Store().CreateAsync(new("readonly"), Owner);
        var artifact = writer.AddArtifact("test", "test");
        Assert.True(writer.TryAppend(artifact, new(Name: "readonly-record")));
        await writer.CompleteAsync();
        var directory = Package(writer.Reference.CaptureId);
        var before = Hashes(directory);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            File.SetUnixFileMode(file, UnixFileMode.UserRead);
        }
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var reader = await Store().OpenAsync(writer.Reference.CaptureId, Owner);
            Assert.Single(reader.Query(new(artifact)).Records);
            Assert.Equal(before, Hashes(directory));
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var file in Directory.EnumerateFiles(directory))
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task RecoveryWithRetainedWal_ReplaysCommittedEvidenceOnly_WithoutSourceMutation()
    {
        var store = Store();
        var writer = await store.CreateAsync(new("wal-evidence"), Owner);
        var artifact = writer.AddArtifact("events", "events");
        Assert.True(writer.TryAppend(artifact, new(Name: "base")));
        await writer.DisposeAsync();
        var source = Package(writer.Reference.CaptureId);
        byte[] main;
        byte[] wal;
        using (var connection = CapturePackage.Connect(source, immutable: false))
        {
            CapturePackage.Execute(connection, "PRAGMA wal_autocheckpoint=0; PRAGMA synchronous=FULL;");
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO strings(value) VALUES('committed-wal');
                INSERT INTO occurrences(artifact_id,name_id) VALUES($artifact,last_insert_rowid());
                """;
            command.Parameters.AddWithValue("$artifact", artifact);
            command.ExecuteNonQuery();
            var committedWalLength = new FileInfo(Path.Combine(source, "capture.sqlite-wal")).Length;
            // This independently retained WAL fixture represents committed evidence plus an uncommitted tail.
            // It is not a power-loss or process-crash simulation.
            CapturePackage.Execute(connection, "PRAGMA cache_size=1; PRAGMA cache_spill=ON; BEGIN IMMEDIATE;");
            command.CommandText = """
                INSERT INTO strings(value) VALUES('uncommitted-tail');
                INSERT INTO occurrences(artifact_id,name_id) VALUES($artifact,last_insert_rowid());
                """;
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$payload", "");
            command.CommandText = "INSERT INTO strings(value) VALUES($payload);";
            for (var i = 0; i < 20; i++)
            {
                command.Parameters["$payload"].Value = new string('x', 4096) + i.ToString(CultureInfo.InvariantCulture);
                command.ExecuteNonQuery();
            }
            main = ReadShared(Path.Combine(source, "capture.sqlite"));
            wal = ReadShared(Path.Combine(source, "capture.sqlite-wal"));
            Assert.True(wal.Length > committedWalLength);
            CapturePackage.Execute(connection, "ROLLBACK;");
        }
        File.WriteAllBytes(Path.Combine(source, "capture.sqlite"), main);
        File.WriteAllBytes(Path.Combine(source, "capture.sqlite-wal"), wal);
        var before = Hashes(source);
        await Error(CaptureErrorCode.Incomplete, () => store.OpenAsync(writer.Reference.CaptureId, Owner));
        var recovered = await store.RecoverAsync(writer.Reference.CaptureId, Owner);
        Assert.Equal(before, Hashes(source));
        Assert.Equal(before["capture.sqlite-wal"], recovered.SourceHashes!["capture.sqlite-wal"]);
        using var reader = await store.OpenAsync(recovered.CaptureId, Owner);
        var records = reader.Query(new(recovered.Artifacts[0].ArtifactId)).Records;
        Assert.Collection(records,
            static entry => Assert.Equal("base", entry.Record.Name),
            static entry => Assert.Equal("committed-wal", entry.Record.Name));
        Assert.True(reader.Info.Quality.UnknownTail);
        Assert.Null(reader.Info.Quality.SourceRejected);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "captures"), ".recovery-*"));
    }

    [Fact]
    public async Task CrossProcessLease_ReaderPreventsDeletionAndRecovery()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        var store = Store();
        await using var writer = await store.CreateAsync(new("cross-process"), Owner);
        await writer.CompleteAsync();
        var id = writer.Reference.CaptureId;
        var leasePath = Path.Combine(Package(id), ".lease");
        var start = new ProcessStartInfo
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false
        };
        if (OperatingSystem.IsWindows())
        {
            start.FileName = "powershell.exe";
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$f=[System.IO.File]::Open($env:CAPTURE_LEASE,[System.IO.FileMode]::Open,[System.IO.FileAccess]::Read,[System.IO.FileShare]::Read); [Console]::WriteLine('ready'); [Console]::ReadLine() | Out-Null; $f.Dispose()");
            start.Environment["CAPTURE_LEASE"] = leasePath;
        }
        else
        {
            start.FileName = "/usr/bin/flock";
            foreach (var argument in new[] { "--shared", leasePath, "/bin/sh", "-c", "printf 'ready\\n'; read answer" })
                start.ArgumentList.Add(argument);
        }
        using var child = Process.Start(start)!;
        try
        {
            Assert.Equal("ready", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            using var reader = await Store().OpenAsync(id, Owner);
            await Error(CaptureErrorCode.Busy, () => Store().DeleteAsync(id, Owner));
            await Error(CaptureErrorCode.Busy, () => Store().RecoverAsync(id, Owner));
            await child.StandardInput.WriteLineAsync("release");
            await child.StandardInput.FlushAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, child.ExitCode);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }
        await store.DeleteAsync(id, Owner);
    }

    [Fact]
    public async Task SharedReaderLease_IsNotMistakenForWriterAndMultipleReadersCoexist()
    {
        var store = Store();
        await using var writer = await store.CreateAsync(new("readers"), Owner);
        await writer.CompleteAsync();
        using var first = await store.OpenAsync(writer.Reference.CaptureId, Owner);
        using var second = await Store().OpenAsync(writer.Reference.CaptureId, Owner);
        await using var anotherWriter = await Store().CreateAsync(new("separate"), Owner);
        await anotherWriter.CompleteAsync();
        Assert.Equal(first.Info.CaptureId, second.Info.CaptureId);
    }

    [Fact]
    public async Task QueueReservationIncludesInflightBatch_AndStringsAreChargedBeforeAdmission()
    {
        var options = new CaptureStoreOptions
        {
            QueueBytes = 512, QueueRecords = 32, MaxBatchAge = TimeSpan.FromSeconds(1)
        };
        await using var writer = await Store(options).CreateAsync(new("bytes"), Owner);
        var id = writer.AddArtifact("test", "test");
        Assert.True(writer.TryAppend(id, new(Name: new string('a', 60))));
        Assert.False(writer.TryAppend(id, new(Name: new string('b', 60))));
        var metrics = writer.GetMetrics();
        Assert.InRange(metrics.QueueBytes, 1, 512);
        Assert.Equal(1, metrics.QueueRecords);
        var info = await writer.CompleteAsync();
        Assert.Equal(1, info.Quality.QueueRejected);
        Assert.Equal(0, writer.GetMetrics().QueueBytes);
        AssertConservation(info.Quality);
    }

    [Fact]
    public async Task AvailableBacklog_FillsBatchesRatherThanCollapsingToSingletons()
    {
        await using var writer = await Store(new CaptureStoreOptions { MaxBatchAge = TimeSpan.FromSeconds(1) })
            .CreateAsync(new("batching"), Owner);
        var id = writer.AddArtifact("test", "test");
        for (var i = 0; i < 600; i++) writer.TryAppend(id, new(NumericValue: i));
        var info = await writer.CompleteAsync();
        var metrics = writer.GetMetrics();
        Assert.InRange(metrics.LargestBatch, 2, 256);
        Assert.True(metrics.Transactions < metrics.Quality.Persisted);
        Assert.Equal(metrics.Quality.Accepted, metrics.Quality.Persisted);
        AssertConservation(info.Quality);
    }

    [Fact]
    public async Task ConcurrentAdmissionAndShutdown_ConservePopulations()
    {
        var writer = await Store(new CaptureStoreOptions { QueueRecords = 32 }).CreateAsync(new("concurrency"), Owner);
        var artifact = writer.AddArtifact("test", "test");
        using var start = new ManualResetEventSlim();
        var producers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < 1000; i++)
            {
                try { writer.TryAppend(artifact, new(NumericValue: i)); }
                catch (CaptureStoreException ex) when (ex.Code == CaptureErrorCode.Closed) { break; }
            }
        })).ToArray();
        start.Set();
        var result = await writer.CompleteAsync();
        await Task.WhenAll(producers);
        AssertConservation(result.Quality);
        AssertConservation(writer.GetMetrics().Quality);
        Assert.Equal(result.Quality, writer.GetMetrics().Quality);
        Assert.Equal(0, result.Quality.Pending);
        await writer.DisposeAsync();
    }

    [Fact]
    public async Task PageSnapshotAndFieldBounds_AreEnforced()
    {
        await using var writer = await Store(new CaptureStoreOptions { MaxFields = 1, MaxSnapshotBytes = 8 })
            .CreateAsync(new("bounds"), Owner);
        var first = writer.AddArtifact("test", "first");
        var second = writer.AddArtifact("test", "second");
        Assert.False(writer.TryAppend(first, new(Fields: [new("a", CaptureFieldKind.Null), new("b", CaptureFieldKind.Null)])));
        Assert.False(writer.TryAppend(first, new(Name: "\ud800")));
        Assert.False(writer.TryAppend(first, new(NumericValue: double.NaN)));
        Assert.False(writer.TryAppend(first, new(DurationNanoseconds: -1)));
        writer.SetSnapshot(first, 1, "\"1234\""u8.ToArray());
        Assert.Throws<CaptureStoreException>(() => writer.SetSnapshot(second, 1, "\"12\""u8.ToArray()));
        Assert.Throws<CaptureStoreException>(() => writer.SetSnapshot(first, 1, "{}"u8.ToArray()));
        var info = await writer.CompleteAsync();
        Assert.Equal(4, info.Quality.RecordRejected);
        Assert.Equal(2, info.Quality.SnapshotRejected);
        using var reader = await Store().OpenAsync(info.CaptureId, Owner);
        Assert.Throws<CaptureStoreException>(() => reader.Query(new(first, PageSize: 1001)));
        Assert.Throws<CaptureStoreException>(() => reader.Query(new(first, AfterRecordId: -1)));
        await Error(CaptureErrorCode.InvalidInput, () => Store().ListAsync(Owner, 101));
    }

    [Fact]
    public async Task MissingIndexesAndUnknownSqliteVersions_AreRejectedBeforeQueries()
    {
        var store = Store();
        await using var writer = await store.CreateAsync(new("format"), Owner);
        await writer.CompleteAsync();
        var directory = Package(writer.Reference.CaptureId);
        using (var connection = CapturePackage.Connect(directory, immutable: false))
            CapturePackage.Execute(connection, "UPDATE format SET record_version=99;");
        ResealForMalformedFixture(directory);
        await Error(CaptureErrorCode.UnsupportedFormat, () => store.OpenAsync(writer.Reference.CaptureId, Owner));
        using (var connection = CapturePackage.Connect(directory, immutable: false))
            CapturePackage.Execute(connection, "UPDATE format SET record_version=1; DROP INDEX ix_occurrence_name;");
        ResealForMalformedFixture(directory);
        await Error(CaptureErrorCode.CorruptPackage, () => store.OpenAsync(writer.Reference.CaptureId, Owner));
    }

    [Fact]
    public async Task StoreAdmission_ReservesUnsealedPackagesNotJustCurrentBytes()
    {
        var options = new CaptureStoreOptions
        {
            MaxDatabaseBytes = 128 * 1024, MaxPackageBytes = 256 * 1024, MaxStoreBytes = 256 * 1024
        };
        var writer = await Store(options).CreateAsync(new("reserved"), Owner);
        await Error(CaptureErrorCode.CapacityExceeded, () => Store(options).CreateAsync(new("no-space"), Owner));
        await writer.DisposeAsync();
        await Error(CaptureErrorCode.CapacityExceeded, () => Store(options).CreateAsync(new("interrupted-still-reserved"), Owner));
        await Store(options).DeleteAsync(writer.Reference.CaptureId, Owner);
        await using var next = await Store(options).CreateAsync(new("released"), Owner);
        await next.CompleteAsync();
    }

    [Fact]
    public async Task PackageMonitorThreshold_FailsExplicitlyWithoutClaimingPhysicalContainment()
    {
        var options = new CaptureStoreOptions
        {
            MaxDatabaseBytes = 256 * 1024, MaxPackageBytes = 256 * 1024,
            MaxSnapshotBytes = 192 * 1024
        };
        var writer = await Store(options).CreateAsync(new("monitor"), Owner);
        var artifact = writer.AddArtifact("test", "test");
        writer.SetSnapshot(artifact, 1, System.Text.Encoding.UTF8.GetBytes("\"" + new string('x', 160 * 1024) + "\""));
        var failure = await Assert.ThrowsAsync<CaptureStoreException>(() => writer.CompleteAsync());
        Assert.Equal(CaptureErrorCode.CapacityExceeded, failure.Code);
        Assert.True(writer.GetMetrics().ObservedPackageBytes > options.MaxPackageBytes);
        Assert.False(File.Exists(Path.Combine(Package(writer.Reference.CaptureId), "seal.json")));
        Assert.True(writer.GetMetrics().Quality.Interrupted);
        await Assert.ThrowsAsync<CaptureStoreException>(async () => await writer.DisposeAsync());
    }

    private static byte[] ReadShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void ResealForMalformedFixture(string directory)
    {
        var seal = new CaptureSeal(CapturePackage.Hash(Path.Combine(directory, "manifest.json")),
            CapturePackage.Hash(Path.Combine(directory, "capture.sqlite")));
        CapturePackage.WriteJson(directory, "seal.json", seal);
    }
}
