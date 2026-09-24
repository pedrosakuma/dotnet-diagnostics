using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DotnetDiagnostics.Core.Captures;

/// <summary>A leased, immutable SQLite view. Dispose releases the package's cross-process reader lease.</summary>
public sealed class CaptureReader : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly FileStream? _lease;
    private readonly CaptureStoreOptions _options;
    private readonly object _gate = new();
    private bool _disposed;

    internal CaptureReader(SqliteConnection connection, FileStream? lease, CaptureInfo info, CaptureStoreOptions options)
    {
        _connection = connection;
        _lease = lease;
        Info = info;
        _options = options;
    }

    public CaptureInfo Info { get; }

    public CaptureRecordPage Query(CaptureRecordQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        CapturePackage.ValidateId(query.ArtifactId);
        if (query.PageSize < 1 || query.PageSize > _options.MaxQueryPageSize || query.AfterRecordId < 0 ||
            query.From > query.To)
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Invalid bounded record query.");
        CapturePackage.ValidateText(query.Category, 64 * 1024, nameof(query.Category));
        CapturePackage.ValidateText(query.Name, 64 * 1024, nameof(query.Name));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateArtifact(query.ArtifactId);
            try { return QueryCore(query); }
            catch (SqliteException ex) { throw CapturePackage.Translate(ex); }
            catch (Exception ex) when (ex is InvalidCastException or ArgumentOutOfRangeException or FormatException)
            {
                throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Occurrence scalar data is invalid.", ex);
            }
        }
    }

    private CaptureRecordPage QueryCore(CaptureRecordQuery query)
    {
        using var command = _connection.CreateCommand();
        var conditions = new List<string> { "o.artifact_id=$artifact", "o.id>$after" };
        command.Parameters.AddWithValue("$artifact", query.ArtifactId);
        command.Parameters.AddWithValue("$after", query.AfterRecordId);
        command.Parameters.AddWithValue("$limit", query.PageSize + 1);
        void Filter(string sql, string parameter, object? value)
        {
            if (value is null) return;
            conditions.Add(sql);
            command.Parameters.AddWithValue(parameter, value);
        }
        Filter("o.timestamp_ticks >= $from", "$from", query.From?.UtcTicks);
        Filter("o.timestamp_ticks <= $to", "$to", query.To?.UtcTicks);
        Filter("o.thread_id = $thread", "$thread", query.ThreadId);
        Filter("o.category_id = (SELECT id FROM strings WHERE value=$category)", "$category", query.Category);
        Filter("o.name_id = (SELECT id FROM strings WHERE value=$name)", "$name", query.Name);
        command.CommandText = $"""
            SELECT o.id,o.timestamp_ticks,o.thread_id,c.value,n.value,o.numeric_value,o.duration_ns,u.value
            FROM occurrences o
            LEFT JOIN strings c ON c.id=o.category_id
            LEFT JOIN strings n ON n.id=o.name_id
            LEFT JOIN strings u ON u.id=o.unit_id
            WHERE {string.Join(" AND ", conditions)} ORDER BY o.id LIMIT $limit;
            """;
        var entries = new List<CaptureRecordEntry>(query.PageSize);
        // Reserve the fixed page envelope, continuation ID, and accounting property. Entries are
        // measured with the fixed source-generated compact UTF-8 representation, including escapes.
        long accountedBytes = 128;
        var more = false;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (entries.Count == query.PageSize) { more = true; break; }
                var record = new CaptureRecord(
                    reader.IsDBNull(1) ? null : new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
                    Long(reader, 2), Text(reader, 3), Text(reader, 4),
                    reader.IsDBNull(5) ? null : reader.GetDouble(5), Long(reader, 6), Text(reader, 7));
                _ = ValidateDimensions(record);
                var id = reader.GetInt64(0);
                record = record with { Fields = ReadFields(id, record) };
                var entry = new CaptureRecordEntry(id, record);
                var entryBytes = JsonSerializer.SerializeToUtf8Bytes(entry, CaptureJsonContext.Default.CaptureRecordEntry).LongLength + 1;
                if (entryBytes > _options.MaxQueryPageBytes - accountedBytes)
                {
                    if (entries.Count == 0)
                        throw CapturePackage.Error(CaptureErrorCode.CapacityExceeded,
                            "One occurrence exceeds MaxQueryPageBytes; increase the configured query byte budget.");
                    more = true;
                    break;
                }
                accountedBytes += entryBytes;
                entries.Add(entry);
            }
        }
        return new(entries.AsReadOnly(), more ? entries[^1].RecordId : null, accountedBytes);
    }

    private System.Collections.ObjectModel.ReadOnlyCollection<CaptureField> ReadFields(long recordId, CaptureRecord record)
    {
        var bytes = ValidateDimensions(record);
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT n.value,f.kind,s.value,f.int_value,f.double_value,f.bool_value,u.value
            FROM fields f JOIN strings n ON n.id=f.name_id
            LEFT JOIN strings s ON s.id=f.string_id LEFT JOIN strings u ON u.id=f.unit_id
            WHERE f.record_id=$id ORDER BY f.ordinal LIMIT 65;
            """;
        command.Parameters.AddWithValue("$id", recordId);
        using var reader = command.ExecuteReader();
        var fields = new List<CaptureField>();
        while (reader.Read())
        {
            if (fields.Count == 64)
                throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Occurrence field bound exceeded.");
            var kind = (CaptureFieldKind)reader.GetInt32(1);
            if (!Enum.IsDefined(kind))
                throw CapturePackage.Error(CaptureErrorCode.UnsupportedFormat, "Unknown scalar field kind.");
            var field = new CaptureField(Text(reader, 0)!, kind, Text(reader, 2), Long(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetBoolean(5), Text(reader, 6));
            bytes += 64 + StringBytes(field.Name) + StringBytes(field.StringValue) + StringBytes(field.Unit);
            if (bytes > 64 * 1024 ||
                (kind == CaptureFieldKind.Text) != (field.StringValue is not null) ||
                (kind == CaptureFieldKind.SignedInteger) != field.Int64Value.HasValue ||
                (kind == CaptureFieldKind.FloatingPoint) != field.DoubleValue.HasValue ||
                (kind == CaptureFieldKind.Boolean) != field.BooleanValue.HasValue ||
                field.DoubleValue is { } value && !double.IsFinite(value) ||
                !reader.IsDBNull(5) && reader.GetInt64(5) is not (0 or 1))
                throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Occurrence field exceeds its frozen bound or scalar shape.");
            fields.Add(field);
        }
        return fields.AsReadOnly();
    }

    public CaptureSnapshot? ReadSnapshot(string artifactId)
    {
        CapturePackage.ValidateId(artifactId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateArtifact(artifactId);
            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    SELECT s.version,length(s.json),s.json,a.kind
                    FROM snapshots s JOIN artifacts a ON a.id=s.artifact_id WHERE s.artifact_id=$id;
                    """;
                command.Parameters.AddWithValue("$id", artifactId);
                using var reader = command.ExecuteReader();
                if (!reader.Read()) return null;
                if (reader.GetInt64(1) > _options.MaxSnapshotBytes || reader.GetInt32(0) < 1)
                    throw CapturePackage.Error(CaptureErrorCode.CapacityExceeded, "Snapshot exceeds the configured read bound or has no valid version.");
                return new(reader.GetInt32(0), (byte[])reader[2], reader.GetString(3));
            }
            catch (SqliteException ex) { throw CapturePackage.Translate(ex); }
        }
    }

    private void ValidateArtifact(string id)
    {
        if (!Info.Artifacts.Any(a => a.ArtifactId == id))
            throw CapturePackage.Error(CaptureErrorCode.NotFound, "Artifact is not present in this capture.");
    }

    private static long? Long(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static long ValidateDimensions(CaptureRecord record)
    {
        var bytes = 128L + StringBytes(record.Category) + StringBytes(record.Name) + StringBytes(record.Unit);
        if (bytes > 64 * 1024 || record.DurationNanoseconds < 0 ||
            record.NumericValue is { } numeric && !double.IsFinite(numeric))
            throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Occurrence exceeds frozen bounds or has invalid dimensions.");
        return bytes;
    }

    private static long StringBytes(string? text) =>
        text is null ? 0 : 24L + text.Length * 2L + CapturePackage.Utf8.GetByteCount(text);
    private static string? Text(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        if (reader.GetBytes(ordinal, 0, null, 0, 0) > 64 * 1024)
            throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "String exceeds the frozen record-format bound.");
        return reader.GetString(ordinal);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _connection.Dispose(); }
            finally { _lease?.Dispose(); }
        }
    }
}
