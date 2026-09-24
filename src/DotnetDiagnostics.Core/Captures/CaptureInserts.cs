using Microsoft.Data.Sqlite;

namespace DotnetDiagnostics.Core.Captures;

internal sealed class CaptureInserts : IDisposable
{
    private readonly CaptureStoreOptions _options;
    private readonly Dictionary<string, long> _strings = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private long _cacheBytes;
    private readonly SqliteCommand _stringInsert, _stringSelect, _artifact, _record, _field, _snapshot;

    internal CaptureInserts(SqliteConnection connection, CaptureStoreOptions options)
    {
        _options = options;
        _stringInsert = Prepare(connection, "INSERT OR IGNORE INTO strings(value) VALUES($value);", "$value");
        _stringSelect = Prepare(connection, "SELECT id FROM strings WHERE value=$value;", "$value");
        _artifact = Prepare(connection, "INSERT OR IGNORE INTO artifacts(id,kind,name) VALUES($id,$kind,$name);", "$id", "$kind", "$name");
        _record = Prepare(connection, """
            INSERT INTO occurrences(artifact_id,timestamp_ticks,thread_id,category_id,name_id,numeric_value,duration_ns,unit_id)
            VALUES($artifact,$time,$thread,$category,$name,$value,$duration,$unit) RETURNING id;
            """, "$artifact", "$time", "$thread", "$category", "$name", "$value", "$duration", "$unit");
        _field = Prepare(connection, """
            INSERT INTO fields(record_id,ordinal,name_id,kind,string_id,int_value,double_value,bool_value,unit_id)
            VALUES($record,$ordinal,$name,$kind,$string,$int,$double,$bool,$unit);
            """, "$record", "$ordinal", "$name", "$kind", "$string", "$int", "$double", "$bool", "$unit");
        _snapshot = Prepare(connection, "INSERT INTO snapshots(artifact_id,version,json) VALUES($artifact,$version,$json);",
            "$artifact", "$version", "$json");
    }

    private static SqliteCommand Prepare(SqliteConnection connection, string sql, params string[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var name in parameters) command.Parameters.AddWithValue(name, DBNull.Value);
        command.Prepare();
        return command;
    }

    private static void Bind(SqliteCommand command, SqliteTransaction transaction, params object?[] values)
    {
        command.Transaction = transaction;
        for (var i = 0; i < values.Length; i++) command.Parameters[i].Value = values[i] ?? DBNull.Value;
    }

    internal void Artifact(CaptureArtifactInfo artifact, SqliteTransaction transaction)
    {
        Bind(_artifact, transaction, artifact.ArtifactId, artifact.Kind, artifact.Name);
        _artifact.ExecuteNonQuery();
    }

    private long? String(string? value, SqliteTransaction transaction)
    {
        if (value is null) return null;
        if (_strings.TryGetValue(value, out var found)) return found;
        Bind(_stringInsert, transaction, value);
        _stringInsert.ExecuteNonQuery();
        Bind(_stringSelect, transaction, value);
        var id = (long)_stringSelect.ExecuteScalar()!;
        var size = 64L + value.Length * 2L + CapturePackage.Utf8.GetByteCount(value);
        if (_options.StringCacheEntries > 0 && size <= _options.StringCacheBytes)
        {
            while (_strings.Count >= _options.StringCacheEntries || size > _options.StringCacheBytes - _cacheBytes)
            {
                var removed = _order.Dequeue();
                _strings.Remove(removed);
                _cacheBytes -= 64L + removed.Length * 2L + CapturePackage.Utf8.GetByteCount(removed);
            }
            _strings.Add(value, id);
            _order.Enqueue(value);
            _cacheBytes += size;
        }
        return id;
    }

    internal void Record(string artifactId, CaptureRecord record, SqliteTransaction transaction)
    {
        Bind(_record, transaction, artifactId, record.Timestamp?.UtcTicks, record.ThreadId,
            String(record.Category, transaction), String(record.Name, transaction), record.NumericValue,
            record.DurationNanoseconds, String(record.Unit, transaction));
        var recordId = (long)_record.ExecuteScalar()!;
        var ordinal = 0;
        foreach (var field in record.Fields!)
        {
            Bind(_field, transaction, recordId, ordinal++, String(field.Name, transaction), (int)field.Kind,
                String(field.StringValue, transaction), field.Int64Value, field.DoubleValue, field.BooleanValue,
                String(field.Unit, transaction));
            _field.ExecuteNonQuery();
        }
    }

    internal void Snapshot(string artifactId, CaptureSnapshot snapshot, SqliteTransaction transaction)
    {
        Bind(_snapshot, transaction, artifactId, snapshot.Version, snapshot.Utf8Json.ToArray());
        _snapshot.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _stringInsert.Dispose(); _stringSelect.Dispose(); _artifact.Dispose();
        _record.Dispose(); _field.Dispose(); _snapshot.Dispose();
    }
}
