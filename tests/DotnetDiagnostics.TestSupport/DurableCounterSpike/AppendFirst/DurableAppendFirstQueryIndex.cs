using Microsoft.Data.Sqlite;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.AppendFirst;

using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Tests.DurableCounterSpike;

/// <summary>
/// Decodes a frame-record payload back into a <see cref="DurableCounterRecord"/>.
/// The payload is the exact JSON bytes the shared pipeline wrote via
/// <c>Utf8JsonWriter</c> (see <c>DurableCounterPipelineSpike.OwnAndEncode</c>),
/// optionally padded with trailing ASCII spaces up to a requested encoded size.
/// <see cref="System.Text.Json.JsonSerializer"/> tolerates trailing JSON
/// whitespace after a complete root value, so the padding decodes cleanly.
/// The record's own <c>EncodedBytes</c> field, as originally serialized, is
/// stale (it was written before the pipeline's final padding decision), so it
/// is always overwritten here with the payload's true length.
/// </summary>
internal static class DurableAppendFirstRecordCodec
{
    internal static DurableCounterRecord Decode(byte[] payload)
    {
        var decoded = System.Text.Json.JsonSerializer.Deserialize<DurableCounterRecord>(payload)
            ?? throw new DurableStorageExperimentException("InvalidRecordPayload", "A frame record payload decoded to null.");
        return decoded with { EncodedBytes = payload.Length };
    }
}

/// <summary>
/// Derived SQLite query index over decoded <see cref="DurableCounterRecord"/> rows.
/// One instance either owns a writable connection (built incrementally as
/// finalized canonical frames are scanned, or during bounded recovery) or a strictly
/// read-only connection opened against an already-sealed, immutable database
/// file. All query methods run bounded, parameterized, typed projections in
/// SQL; no method materializes the whole table into managed memory.
/// </summary>
internal sealed class DurableAppendFirstQueryIndex : IDurableCounterReadonlyStore
{
    private const int ApplicationId = 0x44434232;
    private const int SchemaVersion = 1;
    private const string RecordColumns = """
        sequence, provider, name, displayName, unit, value, kind, intervalSec, intervalState,
        displayScaleTicks, displayScaleState, sourceTimeTicks, clockDomain, clockOrigin,
        coverageGap, resetState, encodedBytes
        """;
    private static readonly string[] ColumnTypes =
    [
        "INTEGER", "TEXT", "TEXT", "TEXT", "TEXT", "REAL", "TEXT", "REAL", "TEXT",
        "INTEGER", "TEXT", "INTEGER", "TEXT", "TEXT", "TEXT", "TEXT", "INTEGER",
    ];
    private const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS records (
            sequence INTEGER PRIMARY KEY,
            provider TEXT NOT NULL,
            name TEXT NOT NULL,
            displayName TEXT NOT NULL,
            unit TEXT NULL,
            value REAL NOT NULL,
            kind TEXT NOT NULL,
            intervalSec REAL NULL,
            intervalState TEXT NOT NULL,
            displayScaleTicks INTEGER NULL,
            displayScaleState TEXT NOT NULL,
            sourceTimeTicks INTEGER NULL,
            clockDomain TEXT NOT NULL,
            clockOrigin TEXT NOT NULL,
            coverageGap TEXT NOT NULL,
            resetState TEXT NOT NULL,
            encodedBytes INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_records_provider_name_sequence
            ON records(provider, name, sequence);
        """;

    private readonly SqliteConnection _connection;
    private readonly DurableCounterPipelineLimits _limits;
    private bool _writable;
    private DurableStorageQualityReport? _fixedQuality;
    private bool _disposed;

    private DurableAppendFirstQueryIndex(
        SqliteConnection connection,
        DurableCounterPipelineLimits limits,
        bool writable,
        DurableStorageQualityReport? fixedQuality = null)
    {
        _connection = connection;
        _limits = limits;
        _writable = writable;
        _fixedQuality = fixedQuality;
    }

    /// <summary>Creates a brand-new index for a bounded scan of committed canonical frames.</summary>
    internal static DurableAppendFirstQueryIndex CreateWritable(string databasePath, DurableCounterPipelineLimits limits)
    {
        if (File.Exists(databasePath))
        {
            throw new DurableStorageExperimentException("QueryIndexAlreadyExists", "A derived query index already exists at this path.");
        }
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ConnectionString);
        try
        {
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode = DELETE; PRAGMA synchronous = NORMAL; "
                    + "PRAGMA cache_size = -2048; PRAGMA mmap_size = 0; PRAGMA temp_store = MEMORY; "
                    + $"PRAGMA application_id = {ApplicationId}; PRAGMA user_version = {SchemaVersion};";
                pragma.ExecuteNonQuery();
            }
            using (var create = connection.CreateCommand())
            {
                create.CommandText = CreateTableSql;
                create.ExecuteNonQuery();
            }
            return new DurableAppendFirstQueryIndex(connection, limits, writable: true);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an already-built, sealed database strictly read-only. Never mutates or rebuilds
    /// anything. When <paramref name="fixedQuality"/> is supplied, <see cref="QualityAsync"/>
    /// returns that exact host-validated manifest snapshot instead of re-deriving it.
    /// </summary>
    internal static DurableAppendFirstQueryIndex OpenReadOnly(
        string databasePath, DurableCounterPipelineLimits limits, DurableStorageQualityReport? fixedQuality = null)
    {
        if (!File.Exists(databasePath))
        {
            throw new DurableStorageExperimentException("MissingQueryIndex", $"Derived query index '{databasePath}' does not exist.");
        }
        // Immutable opens can report DELETE even for a WAL-format database.
        using (var file = File.OpenRead(databasePath))
        {
            Span<byte> header = stackalloc byte[20];
            if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length
                || !header[..16].SequenceEqual("SQLite format 3\0"u8)
                || header[18] != 1 || header[19] != 1)
            {
                throw new DurableStorageExperimentException(
                    "QuerySchemaMismatch", "The query database must use the standalone rollback-journal file format.");
            }
        }
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = new UriBuilder(new Uri(Path.GetFullPath(databasePath))) { Query = "immutable=1" }.Uri.AbsoluteUri,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ConnectionString);
        try
        {
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA query_only = 1; PRAGMA cache_size = -2048; PRAGMA mmap_size = 0;";
                pragma.ExecuteNonQuery();
            }
            VerifySchema(connection);
            return new DurableAppendFirstQueryIndex(connection, limits, writable: false, fixedQuality);
        }
        catch (SqliteException exception)
        {
            connection.Dispose();
            throw new DurableStorageExperimentException(
                "QuerySchemaMismatch", $"The query database could not be validated ({exception.SqliteErrorCode}).");
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void VerifySchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA application_id;";
        var applicationId = (long)command.ExecuteScalar()!;
        command.CommandText = "PRAGMA user_version;";
        var version = (long)command.ExecuteScalar()!;
        command.CommandText = "PRAGMA journal_mode;";
        var journalMode = (string)command.ExecuteScalar()!;
        if (applicationId != ApplicationId || version != SchemaVersion
            || !string.Equals(journalMode, "delete", StringComparison.OrdinalIgnoreCase))
        {
            throw new DurableStorageExperimentException("QuerySchemaMismatch", "The query index identity or schema version is unsupported.");
        }

        command.CommandText = "PRAGMA table_info(records);";
        using (var reader = command.ExecuteReader())
        {
            var count = 0;
            while (reader.Read())
            {
                if (count >= ColumnTypes.Length
                    || reader.GetInt32(0) != count
                    || reader.GetString(1) != DurableStorageLogicalSchema.Columns[count].Name
                    || !string.Equals(reader.GetString(2), ColumnTypes[count], StringComparison.OrdinalIgnoreCase)
                    || reader.GetInt32(5) != (count == 0 ? 1 : 0)
                    || (count != 0 && (reader.GetInt32(3) == 0) != DurableStorageLogicalSchema.Columns[count].Nullable))
                {
                    throw new DurableStorageExperimentException("QuerySchemaMismatch", "The records table does not match the logical schema.");
                }
                count++;
            }
            if (count != ColumnTypes.Length)
            {
                throw new DurableStorageExperimentException("QuerySchemaMismatch", "The records table is incomplete or absent.");
            }
        }
        command.CommandText = "PRAGMA index_info(idx_records_provider_name_sequence);";
        using var indexReader = command.ExecuteReader();
        foreach (var name in new[] { "provider", "name", "sequence" })
        {
            if (!indexReader.Read() || indexReader.GetString(2) != name)
            {
                throw new DurableStorageExperimentException("QuerySchemaMismatch", "The required query index is incomplete or absent.");
            }
        }
        if (indexReader.Read())
        {
            throw new DurableStorageExperimentException("QuerySchemaMismatch", "The query index has an unexpected shape.");
        }
    }

    internal void VerifyRecord(DurableCounterRecord expected)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT {RecordColumns} FROM records WHERE sequence = $sequence;";
        command.Parameters.AddWithValue("$sequence", expected.Sequence);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || ReadRecord(reader) != expected)
        {
            throw new DurableStorageExperimentException("IndexRecordMismatch", "An indexed record differs from its canonical projection.");
        }
    }

    /// <summary>Inserts one already-committed batch of decoded records inside a single bounded transaction.</summary>
    internal void InsertBatch(IReadOnlyList<DurableCounterRecord> records)
    {
        if (!_writable)
        {
            throw new InvalidOperationException("This derived query index is read-only.");
        }
        if (records.Count == 0)
        {
            return;
        }
        using var transaction = _connection.BeginTransaction();
        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO records
                (sequence, provider, name, displayName, unit, value, kind, intervalSec, intervalState,
                 displayScaleTicks, displayScaleState, sourceTimeTicks, clockDomain, clockOrigin,
                 coverageGap, resetState, encodedBytes)
            VALUES
                ($sequence, $provider, $name, $displayName, $unit, $value, $kind, $intervalSec, $intervalState,
                 $displayScaleTicks, $displayScaleState, $sourceTimeTicks, $clockDomain, $clockOrigin,
                 $coverageGap, $resetState, $encodedBytes);
            """;
        var pSequence = insert.Parameters.Add("$sequence", SqliteType.Integer);
        var pProvider = insert.Parameters.Add("$provider", SqliteType.Text);
        var pName = insert.Parameters.Add("$name", SqliteType.Text);
        var pDisplayName = insert.Parameters.Add("$displayName", SqliteType.Text);
        var pUnit = insert.Parameters.Add("$unit", SqliteType.Text);
        var pValue = insert.Parameters.Add("$value", SqliteType.Real);
        var pKind = insert.Parameters.Add("$kind", SqliteType.Text);
        var pIntervalSec = insert.Parameters.Add("$intervalSec", SqliteType.Real);
        var pIntervalState = insert.Parameters.Add("$intervalState", SqliteType.Text);
        var pDisplayScaleTicks = insert.Parameters.Add("$displayScaleTicks", SqliteType.Integer);
        var pDisplayScaleState = insert.Parameters.Add("$displayScaleState", SqliteType.Text);
        var pSourceTimeTicks = insert.Parameters.Add("$sourceTimeTicks", SqliteType.Integer);
        var pClockDomain = insert.Parameters.Add("$clockDomain", SqliteType.Text);
        var pClockOrigin = insert.Parameters.Add("$clockOrigin", SqliteType.Text);
        var pCoverageGap = insert.Parameters.Add("$coverageGap", SqliteType.Text);
        var pResetState = insert.Parameters.Add("$resetState", SqliteType.Text);
        var pEncodedBytes = insert.Parameters.Add("$encodedBytes", SqliteType.Integer);
        insert.Prepare();

        foreach (var record in records)
        {
            pSequence.Value = record.Sequence;
            pProvider.Value = record.Provider;
            pName.Value = record.Name;
            pDisplayName.Value = record.DisplayName;
            pUnit.Value = (object?)record.Unit ?? DBNull.Value;
            pValue.Value = record.Value;
            pKind.Value = record.Kind.ToString();
            pIntervalSec.Value = (object?)record.IntervalSec ?? DBNull.Value;
            pIntervalState.Value = record.IntervalState.ToString();
            pDisplayScaleTicks.Value = (object?)record.DisplayScaleTicks ?? DBNull.Value;
            pDisplayScaleState.Value = record.DisplayScaleState.ToString();
            pSourceTimeTicks.Value = (object?)record.SourceTimeTicks ?? DBNull.Value;
            pClockDomain.Value = record.ClockDomain;
            pClockOrigin.Value = record.ClockOrigin;
            pCoverageGap.Value = record.CoverageGap.ToString();
            pResetState.Value = record.ResetState.ToString();
            pEncodedBytes.Value = record.EncodedBytes;
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Row count known independently of any accounting object (used for retained-record checks).</summary>
    internal long CountRows()
    {
        using var count = _connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM records;";
        return (long)count.ExecuteScalar()!;
    }

    /// <summary>Closes the connection cleanly (checkpoint/rollback-journal already avoids sidecar files).</summary>
    internal void CloseForSeal()
    {
        _connection.Close();
    }

    /// <summary>
    /// Transitions this writable index into the adapter's own post-seal live reader:
    /// stops accepting further writes (best-effort <c>PRAGMA query_only</c>) and fixes
    /// the exact terminal quality snapshot that <see cref="QualityAsync"/> must report
    /// from now on. Called only once, only after a full, reconciled index build.
    /// </summary>
    internal void SealForLiveReading(DurableStorageQualityReport terminalQuality)
    {
        if (!_writable)
        {
            throw new InvalidOperationException("This derived query index has already been sealed for live reading, or is already read-only.");
        }
        _writable = false;
        _fixedQuality = terminalQuality;
        using var pragma = _connection.CreateCommand();
        pragma.CommandText = "PRAGMA query_only = 1;";
        pragma.ExecuteNonQuery();
    }

    public ValueTask<IReadOnlyList<DurableCounterSummaryRow>> SummaryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = new List<DurableCounterSummaryRow>();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT r.provider, r.name,
                   (SELECT kind FROM records r2 WHERE r2.provider = r.provider AND r2.name = r.name
                        ORDER BY r2.sequence ASC LIMIT 1) AS firstKind,
                   (SELECT unit FROM records r2 WHERE r2.provider = r.provider AND r2.name = r.name
                        ORDER BY r2.sequence ASC LIMIT 1) AS firstUnit,
                   COUNT(*) AS retainedCount,
                   (SELECT value FROM records r2 WHERE r2.provider = r.provider AND r2.name = r.name
                        ORDER BY r2.sequence ASC LIMIT 1) AS firstValue,
                   (SELECT value FROM records r2 WHERE r2.provider = r.provider AND r2.name = r.name
                        ORDER BY r2.sequence DESC LIMIT 1) AS lastValue,
                   MIN(r.value) AS minValue,
                   MAX(r.value) AS maxValue,
                   (SELECT sourceTimeTicks FROM records r2 WHERE r2.provider = r.provider AND r2.name = r.name
                        AND sourceTimeTicks IS NOT NULL ORDER BY r2.sequence ASC LIMIT 1) AS firstSourceTimeTicks,
                   (SELECT sourceTimeTicks FROM records r2 WHERE r2.provider = r.provider AND r2.name = r.name
                        AND sourceTimeTicks IS NOT NULL ORDER BY r2.sequence DESC LIMIT 1) AS lastSourceTimeTicks,
                   SUM(CASE WHEN r.coverageGap = 'Gap' THEN 1 ELSE 0 END) AS gapCount,
                   SUM(CASE WHEN r.coverageGap = 'Unknown' THEN 1 ELSE 0 END) AS unknownCoverageCount
            FROM records r
            GROUP BY r.provider, r.name
            ORDER BY r.provider, r.name
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", _limits.DistinctKeys + 1);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Count >= _limits.DistinctKeys)
            {
                throw new DurableStorageExperimentException("DistinctKeysLimit", "The query index exceeds the shared distinct-key bound.");
            }
            rows.Add(new DurableCounterSummaryRow(
                reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<CounterKind>(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                (int)reader.GetInt64(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.GetDouble(7),
                reader.GetDouble(8),
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10),
                (int)reader.GetInt64(11),
                (int)reader.GetInt64(12)));
        }
        rows.Sort(static (left, right) =>
        {
            var provider = StringComparer.Ordinal.Compare(left.Provider, right.Provider);
            return provider != 0 ? provider : StringComparer.Ordinal.Compare(left.Name, right.Name);
        });
        EnsureResultBound(rows);
        return ValueTask.FromResult<IReadOnlyList<DurableCounterSummaryRow>>(rows);
    }

    public ValueTask<DurableCounterSeriesPage> SeriesAsync(
        string provider, string name, long? afterSequence, int pageSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (afterSequence < 0)
        {
            throw new DurableStorageExperimentException("InvalidCursor", "The sequence cursor cannot be negative.");
        }
        if (pageSize is < 1 or > 100 || pageSize > _limits.PageRows)
        {
            throw new DurableStorageExperimentException("InvalidPageSize", "The requested page size is outside the limit.");
        }

        var rows = new List<DurableCounterRecord>(pageSize);
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT {RecordColumns}
            FROM records
            WHERE provider = $provider AND name = $name
                AND ($after IS NULL OR sequence > $after)
            ORDER BY sequence
            LIMIT $pageSizePlusOne;
            """;
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$after", (object?)afterSequence ?? DBNull.Value);
        command.Parameters.AddWithValue("$pageSizePlusOne", pageSize + 1);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(ReadRecord(reader));
        }
        var hasMore = rows.Count > pageSize;
        var page = rows.Count > pageSize ? rows.GetRange(0, pageSize) : rows;
        var result = new DurableCounterSeriesPage(
            page,
            hasMore && page.Count > 0 ? page[^1].Sequence : null,
            LimitExceeded: false);
        EnsureResultBound(result);
        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// Reports the supplied final quality, or unknown accounting for an internal
    /// recovery reader without a terminal snapshot.
    /// </summary>
    public ValueTask<DurableStorageQualityReport> QualityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _fixedQuality is null
            ? new DurableStorageQualityReport(
                FinalPipelineQuality: null,
                RetainedRecords: CountRows(),
                VolatileTailUnknown: true)
            : _fixedQuality with { RetainedRecords = CountRows() };
        EnsureResultBound(result);
        return ValueTask.FromResult(result);
    }

    private static DurableCounterRecord ReadRecord(SqliteDataReader reader)
        => new(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetDouble(5),
            Enum.Parse<CounterKind>(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetDouble(7),
            Enum.Parse<CounterMetadataState>(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            Enum.Parse<CounterMetadataState>(reader.GetString(10)),
            reader.IsDBNull(11) ? null : reader.GetInt64(11),
            reader.GetString(12), reader.GetString(13),
            Enum.Parse<CoverageGapState>(reader.GetString(14)),
            Enum.Parse<CounterResetState>(reader.GetString(15)), reader.GetInt32(16));

    private void EnsureResultBound<T>(T result)
    {
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result).Length > _limits.ResultBytes)
        {
            throw new DurableStorageExperimentException("ResultLimitExceeded", "The typed query result exceeded its byte limit.");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;
        _connection.Dispose();
        return ValueTask.CompletedTask;
    }
}
