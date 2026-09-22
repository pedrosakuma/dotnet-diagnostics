using System.Text.Json;
using DotnetDiagnostics.Core.Counters;
using Microsoft.Data.Sqlite;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Sqlite;

internal sealed class DurableSqliteReadonlyStore : IDurableCounterReadonlyStore
{
    private readonly SqliteConnection _connection;
    private readonly DurableCounterPipelineLimits _limits;
    private readonly DurableStorageQualityReport _quality;
    private readonly DurableStoragePackageManifest? _manifest;
    private bool _disposed;

    private DurableSqliteReadonlyStore(
        SqliteConnection connection,
        DurableCounterPipelineLimits limits,
        DurableStorageQualityReport quality,
        DurableStoragePackageManifest? manifest)
    {
        _connection = connection;
        _limits = limits;
        _quality = quality;
        _manifest = manifest;
    }

    internal static DurableSqliteReadonlyStore Open(
        string databasePath,
        DurableStoragePackageManifest? manifest,
        DurableCounterPipelineLimits limits,
        string expectedCaptureId,
        string expectedArtifactId,
        DurableStorageQualityReport? adapterQuality = null)
    {
        var immutableUri = new UriBuilder(new Uri(Path.GetFullPath(databasePath)))
        {
            Query = "immutable=1",
        }.Uri.AbsoluteUri;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = immutableUri,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        connection.Open();
        try
        {
            DurableSqliteDatabase.ExecuteNonQuery(connection, "PRAGMA query_only=ON;");
            DurableSqliteDatabase.ExecuteNonQuery(connection, "PRAGMA cache_size=-2048;");
            DurableSqliteDatabase.ExecuteNonQuery(connection, "PRAGMA mmap_size=0;");
            DurableSqliteDatabase.VerifyMetadata(connection, expectedCaptureId, expectedArtifactId);
            DurableSqliteDatabase.VerifyReadOnlySchema(connection);
            DurableSqliteDatabase.VerifyIntegrity(connection);
            var retained = DurableSqliteDatabase.CountRecords(connection);
            var quality = adapterQuality ?? new DurableStorageQualityReport(
                manifest?.FinalPipelineQuality,
                retained,
                manifest?.VolatileTailUnknown ?? false);
            if (quality.RetainedRecords != retained)
            {
                throw new DurableStorageExperimentException(
                    "SqliteRetainedCountMismatch",
                    "Candidate A quality retained count does not match the database.");
            }
            if (manifest is null)
            {
                DurableStorageQualityRules.Validate(quality);
            }
            else
            {
                DurableStorageQualityRules.Validate(quality, manifest);
            }
            return new DurableSqliteReadonlyStore(connection, limits, quality, manifest);
        }
        catch (SqliteException exception)
        {
            connection.Dispose();
            throw new DurableStorageExperimentException(
                "SqliteReadonlyOpenFailed",
                $"Candidate A could not validate the sealed SQLite database: {exception.SqliteErrorCode}.");
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public ValueTask<IReadOnlyList<DurableCounterSummaryRow>> SummaryAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT
                r.provider,
                r.name,
                (SELECT first.kind FROM records AS first
                 WHERE first.provider = r.provider AND first.name = r.name
                 ORDER BY first.sequence LIMIT 1),
                (SELECT first.unit FROM records AS first
                 WHERE first.provider = r.provider AND first.name = r.name
                 ORDER BY first.sequence LIMIT 1),
                COUNT(*),
                (SELECT first.value FROM records AS first
                 WHERE first.provider = r.provider AND first.name = r.name
                 ORDER BY first.sequence LIMIT 1),
                (SELECT last.value FROM records AS last
                 WHERE last.provider = r.provider AND last.name = r.name
                 ORDER BY last.sequence DESC LIMIT 1),
                MIN(r.value),
                MAX(r.value),
                (SELECT first_time.source_time_ticks FROM records AS first_time
                 WHERE first_time.provider = r.provider AND first_time.name = r.name
                   AND first_time.source_time_ticks IS NOT NULL
                 ORDER BY first_time.sequence LIMIT 1),
                (SELECT last_time.source_time_ticks FROM records AS last_time
                 WHERE last_time.provider = r.provider AND last_time.name = r.name
                   AND last_time.source_time_ticks IS NOT NULL
                 ORDER BY last_time.sequence DESC LIMIT 1),
                SUM(CASE WHEN r.coverage_gap = 2 THEN 1 ELSE 0 END),
                SUM(CASE WHEN r.coverage_gap = 0 THEN 1 ELSE 0 END)
            FROM records AS r
            GROUP BY r.provider, r.name
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", _limits.DistinctKeys + 1);
        using var reader = command.ExecuteReader();
        var rows = new List<DurableCounterSummaryRow>(_limits.DistinctKeys);
        while (reader.Read())
        {
            if (rows.Count == _limits.DistinctKeys)
            {
                throw new DurableStorageExperimentException(
                    "SqliteDistinctKeyLimit",
                    "Candidate A database contains more than the frozen distinct-key limit.");
            }
            rows.Add(new DurableCounterSummaryRow(
                reader.GetString(0),
                reader.GetString(1),
                (CounterKind)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                checked((int)reader.GetInt64(4)),
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.GetDouble(7),
                reader.GetDouble(8),
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10),
                checked((int)reader.GetInt64(11)),
                checked((int)reader.GetInt64(12))));
        }
        var ordered = rows
            .OrderBy(static row => row.Provider, StringComparer.Ordinal)
            .ThenBy(static row => row.Name, StringComparer.Ordinal)
            .ToArray();
        EnsureResultBound(ordered);
        return ValueTask.FromResult<IReadOnlyList<DurableCounterSummaryRow>>(ordered);
    }

    public ValueTask<DurableCounterSeriesPage> SeriesAsync(
        string provider,
        string name,
        long? afterSequence,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (afterSequence < 0)
        {
            throw new DurableCounterPipelineException("InvalidCursor", "The sequence cursor cannot be negative.");
        }
        if (pageSize is < 1 or > 100 || pageSize > _limits.PageRows)
        {
            throw new DurableCounterPipelineException("InvalidPageSize", "The requested page size is outside the limit.");
        }

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, provider, name, display_name, unit, value, kind,
                   interval_sec, interval_state, display_scale_ticks, display_scale_state,
                   source_time_ticks, clock_domain, clock_origin, coverage_gap, reset_state,
                   encoded_bytes
            FROM records
            WHERE provider = $provider COLLATE BINARY
              AND name = $name COLLATE BINARY
              AND ($after IS NULL OR sequence > $after)
            ORDER BY sequence
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$after", afterSequence.HasValue ? afterSequence.Value : DBNull.Value);
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        using var reader = command.ExecuteReader();
        var rows = new List<DurableCounterRecord>(pageSize + 1);
        long retainedBytes = 0;
        while (reader.Read())
        {
            var row = ReadRecord(reader);
            retainedBytes = checked(retainedBytes + row.EncodedBytes);
            if (retainedBytes > DurableSqliteLimits.ReaderBufferBytes)
            {
                throw new DurableStorageExperimentException(
                    "SqliteReaderBufferLimit",
                    "Candidate A query exceeded the frozen retained-reader buffer limit.");
            }
            rows.Add(row);
        }

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        var page = new DurableCounterSeriesPage(
            rows,
            hasMore && rows.Count > 0 ? rows[^1].Sequence : null,
            LimitExceeded: false);
        EnsureResultBound(page);
        return ValueTask.FromResult(page);
    }

    public ValueTask<DurableStorageQualityReport> QualityAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_manifest is null)
        {
            DurableStorageQualityRules.Validate(_quality);
        }
        else
        {
            DurableStorageQualityRules.Validate(_quality, _manifest);
        }
        EnsureResultBound(_quality);
        return ValueTask.FromResult(_quality);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _connection.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private static DurableCounterRecord ReadRecord(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetDouble(5),
            (CounterKind)reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetDouble(7),
            (CounterMetadataState)reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            (CounterMetadataState)reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetInt64(11),
            reader.GetString(12),
            reader.GetString(13),
            (CoverageGapState)reader.GetInt32(14),
            (CounterResetState)reader.GetInt32(15),
            reader.GetInt32(16));

    private void EnsureResultBound<T>(T result)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(result).Length > _limits.ResultBytes)
        {
            throw new DurableCounterPipelineException(
                "ResultLimitExceeded",
                "Candidate A typed query result exceeded the frozen byte limit.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal static class DurableSqliteRecovery
{
    internal static DurableStorageRecoveryResult Recover(
        DurableStorageRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        var sourceDatabase = DurableSqlitePackage.DatabasePath(Path.GetFullPath(request.SourcePackageRoot));
        if (!File.Exists(sourceDatabase))
        {
            throw new DurableStorageExperimentException(
                "SqliteRecoverySourceMissing",
                "Candidate A recovery source has no canonical database.");
        }

        DurableSqlitePackage.PrepareNewStagingRoot(request.RecoveryStagingRoot);
        var destinationDatabase = DurableSqlitePackage.DatabasePath(request.RecoveryStagingRoot);
        var sourceFiles = new[]
        {
            sourceDatabase,
            sourceDatabase + "-wal",
        }.Where(File.Exists).ToArray();
        var sourceBytes = sourceFiles.Sum(static path => new FileInfo(path).Length);
        if (sourceBytes < 1 || sourceBytes > DurableSqliteLimits.PackageBytes)
        {
            throw new DurableStorageExperimentException(
                "SqliteRecoverySourceLimit",
                "Candidate A recovery source exceeds the package byte limit.");
        }
        DurableSqlitePackage.EnsureRecoveryPeakFits(
            request.SourcePackageRoot,
            request.RecoveryStagingRoot,
            checked(sourceBytes + DurableSqliteLimits.ReaderBufferBytes));

        foreach (var source in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var suffix = source[sourceDatabase.Length..];
            CopyBounded(source, destinationDatabase + suffix, cancellationToken);
        }
        DurableSqlitePackage.EnsureRecoveryPeakFits(
            request.SourcePackageRoot,
            request.RecoveryStagingRoot,
            checked(new FileInfo(destinationDatabase).Length + DurableSqliteLimits.ReaderBufferBytes));

        using (var connection = DurableSqliteDatabase.OpenWritable(destinationDatabase))
        {
            DurableSqliteDatabase.ApplyAndVerifyP1(connection);
            DurableSqliteDatabase.VerifyMetadata(
                connection,
                request.SourceCaptureId,
                request.SourceArtifactId);
            DurableSqliteDatabase.VerifyIntegrity(connection);
            DurableSqliteDatabase.CreateQueryIndex(connection);
            DurableSqliteDatabase.UpdatePackageIdentity(
                connection,
                request.NewCaptureId,
                request.NewArtifactId);
            DurableSqliteDatabase.CheckpointForSeal(connection);
        }

        DurableSqlitePackage.DeleteEmptyAuxiliaryFiles(destinationDatabase);
        var member = DurableSqlitePackage.DescribeDatabase(request.RecoveryStagingRoot);
        return new DurableStorageRecoveryResult(
            request.NewCaptureId,
            request.NewArtifactId,
            request.SourceCaptureId,
            request.Reason,
            VolatileTailUnknown: true,
            RecoveredMembers: [member]);
    }

    private static void CopyBounded(string source, string destination, CancellationToken cancellationToken)
    {
        const int bufferSize = 1_048_576;
        using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.SequentialScan);
        using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.SequentialScan);
        var buffer = new byte[bufferSize];
        long copied = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer);
            if (read == 0)
            {
                break;
            }
            copied = checked(copied + read);
            if (copied > DurableSqliteLimits.PackageBytes)
            {
                throw new DurableStorageExperimentException(
                    "SqliteRecoverySourceLimit",
                    "Candidate A recovery copy exceeded the package byte limit.");
            }
            output.Write(buffer, 0, read);
        }
        output.Flush(flushToDisk: true);
    }

}
