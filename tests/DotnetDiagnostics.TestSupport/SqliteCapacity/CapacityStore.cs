using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DotnetDiagnostics.TestSupport.SqliteCapacity;

// Deliberately separate from candidate A: no counter adapter, identity, or limit changes.
internal sealed class CapacityStore : IDisposable
{
    private readonly SqliteConnection? _connection;
    private readonly SqliteCommand? _insertRecord;
    private readonly SqliteCommand? _insertFrame;
    private readonly SqliteCommand? _insertStack;
    private readonly SqliteCommand? _insertAttribute;
    private readonly CapacityResult _result;
    private readonly SortedSet<long> _stacks = [];
    private readonly IncrementalHash _records = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly IncrementalHash _attributes = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly string _database;
    private long _start;
    private readonly long _deadline;

    internal CapacityStore(string database, CapacityResult result, long start, long deadline)
    {
        _database = database;
        _result = result;
        _start = start;
        _deadline = deadline;
        if (result.ProducerOnly)
            return;
        if (File.Exists(database))
            throw new InvalidOperationException("database-already-exists");
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 1
        }.ToString());
        try
        {
            _connection.Open();
            Execute("PRAGMA page_size=4096; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;" +
                "PRAGMA foreign_keys=ON; PRAGMA cache_size=-2048; PRAGMA temp_store=MEMORY;" +
                "PRAGMA wal_autocheckpoint=256; PRAGMA journal_size_limit=8388608; PRAGMA max_page_count=32768;");
            foreach (var name in new[] { "page_size", "journal_mode", "synchronous", "foreign_keys", "cache_size",
                         "temp_store", "wal_autocheckpoint", "journal_size_limit", "max_page_count" })
                result.Pragmas[name] = Convert.ToString(Scalar("PRAGMA " + name), System.Globalization.CultureInfo.InvariantCulture)!;
            var expected = new[] { "4096", "wal", "2", "1", "-2048", "2", "256", "8388608", "32768" };
            if (!result.Pragmas.Values.SequenceEqual(expected))
                throw new InvalidOperationException("actual-pragma-mismatch");
            result.SqliteVersion = (string)Scalar("SELECT sqlite_version()");
            result.ProviderVersion = typeof(SqliteConnection).Assembly.GetName().Version!.ToString();
            Execute(CapacityProtocol.Schema);
            _insertRecord = Prepare("INSERT INTO records VALUES($a,$b,$c,$d,$e,$f,$g,$h,$i)", 9);
            _insertFrame = Prepare("INSERT INTO frames VALUES($a,$b)", 2);
            _insertStack = Prepare("INSERT INTO stack_frames VALUES($a,$b,$c)", 3);
            _insertAttribute = Prepare("INSERT INTO attributes VALUES($a,$b,$c)", 3);
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    internal void StartAcquisition(long timestamp) => _start = timestamp;

    internal static bool CanAddStack(int currentStacks, long currentBytes, long additionalBytes)
        => currentStacks < CapacityProtocol.StackLimit
            && (currentStacks + 1L) * 4 <= CapacityProtocol.FrameLimit
            && additionalBytes <= CapacityProtocol.DictionaryBytes - currentBytes;

    internal void Commit(IReadOnlyList<CapacityRecord> batch)
    {
        if (batch.Count > CapacityProtocol.BatchRecords ||
            batch.Count * CapacityProtocol.RecordReservationBytes > CapacityProtocol.BatchBytes)
            throw new InvalidOperationException("batch-cap");
        CheckDeadline();
        var accepted = new List<CapacityRecord>(CapacityProtocol.BatchRecords);
        var newStacks = new SortedSet<long>();
        long recordBytes = 0, dictionaryBytes = 0;
        foreach (var record in batch)
        {
            var novel = record.Stack >= 0 && !_stacks.Contains(record.Stack) && !newStacks.Contains(record.Stack);
            var additional = novel ? StackLogicalBytes(record.Stack) : 0;
            if (novel && !CanAddStack(_stacks.Count + newStacks.Count,
                _result.DictionaryLogicalBytes + dictionaryBytes, additional))
            {
                _result.DictionaryRejected++;
                continue;
            }

            if (_result.RetainedLogicalRecordBytes + recordBytes + record.LogicalBytes
                + _result.DictionaryLogicalBytes + dictionaryBytes + additional > CapacityProtocol.LogicalBytes)
            {
                _result.LogicalCapRejected++;
                continue;
            }
            accepted.Add(record);
            recordBytes += record.LogicalBytes;
            if (novel)
            {
                newStacks.Add(record.Stack);
                dictionaryBytes += additional;
            }
        }
        if (accepted.Count == 0)
            return;

        if (_connection is not null)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var command in new[] { _insertRecord, _insertFrame, _insertStack, _insertAttribute })
                command!.Transaction = transaction;
            foreach (var stack in newStacks)
                for (var ordinal = 0; ordinal < 4; ordinal++)
                {
                    var frame = stack * 4 + ordinal;
                    Insert(_insertFrame!, frame, CapacityProtocol.FrameName(frame));
                    Insert(_insertStack!, stack, ordinal, frame);
                }
            foreach (var record in accepted)
            {
                CheckDeadline();
                InsertRecord(record);
                if (record.Profile == (long)CapacityProfile.Activity)
                    for (var key = 0; key < 4; key++)
                        Insert(_insertAttribute!, record.Sequence, key, CapacityProtocol.AttributeValue(record.Sequence, key));
            }
            var commitStart = Stopwatch.GetTimestamp();
            transaction.Commit();
            _result.CommitLatency.AddTicks(Stopwatch.GetTimestamp() - commitStart);
        }

        // Membership advances only after a successful transaction; failed/uncertain tails remain unknown.
        var now = Stopwatch.GetTimestamp();
        foreach (var record in accepted)
        {
            record.AppendTo(_records, _attributes);
            if (record.Nominal >= _result.QueryLow && record.Nominal < _result.QueryHigh)
            {
                _result.QueryOracle[0]++;
                if (record.Key == 7) _result.QueryOracle[1]++;
                if (record.Thread == 3) _result.QueryOracle[2]++;
                if (record.Stack == _result.QueryStack) _result.QueryOracle[3]++;
                if (record.Trace == _result.QueryTrace) _result.QueryOracle[4]++;
            }
            if (!_result.ProducerOnly)
                _result.OfferedToCommit.AddTicks(now - _start - record.Offered);
        }
        _stacks.UnionWith(newStacks);
        _result.DictionaryLogicalBytes += dictionaryBytes;
        _result.RetainedLogicalRecordBytes += recordBytes;
        if (_result.ProducerOnly)
            _result.ControlConsumed += accepted.Count;
        else
        {
            _result.Committed += accepted.Count;
            _result.SqlRows += accepted.Count + newStacks.Count * 8L
                + accepted.Count(static r => r.Profile == (long)CapacityProfile.Activity) * 4L;
            _result.CommittedPerSecond[Math.Min(60, (now - _start) / Stopwatch.Frequency)] += accepted.Count;
            SampleFiles();
        }
    }

    internal void Seal()
    {
        CheckDeadline();
        var started = Stopwatch.GetTimestamp();
        _result.RecordHash = CapacityHash.Finish(_records);
        _result.AttributeHash = CapacityHash.Finish(_attributes);
        using var dictionary = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var stack in _stacks)
            for (var ordinal = 0; ordinal < 4; ordinal++)
            {
                var frame = stack * 4 + ordinal;
                CapacityHash.Append(dictionary, stack, ordinal, frame);
                var name = Encoding.UTF8.GetBytes(CapacityProtocol.FrameName(frame));
                CapacityHash.Append(dictionary, name.Length);
                dictionary.AppendData(name);
            }
        _result.DictionaryHash = CapacityHash.Finish(dictionary);
        if (_connection is not null)
        {
            _result.DatabaseBytesBeforeIndexes = Convert.ToInt64(Scalar("PRAGMA page_count"), System.Globalization.CultureInfo.InvariantCulture) * 4096;
            var indexStart = Stopwatch.GetTimestamp();
            Execute(CapacityProtocol.Indexes);
            _result.IndexTicks = Stopwatch.GetTimestamp() - indexStart;
            _result.IndexPageBytes = Convert.ToInt64(Scalar("PRAGMA page_count"), System.Globalization.CultureInfo.InvariantCulture) * 4096
                - _result.DatabaseBytesBeforeIndexes;
            SampleFiles();
            CheckDeadline();
            var checkpointStart = Stopwatch.GetTimestamp();
            using var checkpoint = _connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using (var reader = checkpoint.ExecuteReader())
                if (!reader.Read() || reader.GetInt64(0) != 0 || reader.GetInt64(1) != 0 || reader.GetInt64(2) != 0)
                    throw new InvalidOperationException("checkpoint-incomplete");
            _result.CheckpointTicks = Stopwatch.GetTimestamp() - checkpointStart;
            _connection.Close();
            _result.DatabaseBytes = new FileInfo(_database).Length;
            SampleFiles();
        }
        _result.FinalizationTicks = Stopwatch.GetTimestamp() - started;
    }

    private static long StackLogicalBytes(long stack)
    {
        long bytes = 0;
        for (var ordinal = 0; ordinal < 4; ordinal++)
            bytes += 24 + 8 + Encoding.UTF8.GetByteCount(CapacityProtocol.FrameName(stack * 4 + ordinal));
        return bytes;
    }

    private void SampleFiles()
    {
        var db = new FileInfo(_database).Length;
        var wal = File.Exists(_database + "-wal") ? new FileInfo(_database + "-wal").Length : 0;
        var shm = File.Exists(_database + "-shm") ? new FileInfo(_database + "-shm").Length : 0;
        _result.PeakSampledPackageBytes = Math.Max(_result.PeakSampledPackageBytes, db + wal + shm);
        _result.PeakSampledWalBytes = Math.Max(_result.PeakSampledWalBytes, wal);
        if (db + wal + shm > CapacityProtocol.PackageBytes)
            throw new InvalidOperationException("sampled-package-cap");
    }

    private void CheckDeadline()
    {
        if (Stopwatch.GetTimestamp() >= _deadline)
            throw new TimeoutException("worker-deadline");
    }

    private void Execute(string sql)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object Scalar(string sql)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }

    private SqliteCommand Prepare(string sql, int count)
    {
        var command = _connection!.CreateCommand();
        command.CommandText = sql;
        for (var index = 0; index < count; index++)
            command.Parameters.AddWithValue("$" + (char)('a' + index), 0L);
        command.Prepare();
        return command;
    }

    private void InsertRecord(CapacityRecord record)
    {
        var columns = record.Columns();
        for (var index = 0; index < columns.Length; index++)
            _insertRecord!.Parameters[index].Value = columns[index];
        _insertRecord!.ExecuteNonQuery();
    }

    private static void Insert(SqliteCommand command, params object[] values)
    {
        for (var index = 0; index < values.Length; index++)
            command.Parameters[index].Value = values[index];
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _insertRecord?.Dispose();
        _insertFrame?.Dispose();
        _insertStack?.Dispose();
        _insertAttribute?.Dispose();
        _connection?.Dispose();
        _records.Dispose();
        _attributes.Dispose();
    }
}

internal sealed class CapacityVerification
{
    public bool Equal { get; set; }
    public long ReopenTicks { get; set; }
    public long TimeToFirstQueryTicks { get; set; }
    public long TraversalTicks { get; set; }
    public string[] QueryPlans { get; set; } = new string[5];
    public CapacityHistogram QueryLatencies { get; set; } = new();
    public CapacityHistogram[] QueryLatenciesByShape { get; set; } = [new(), new(), new(), new(), new()];
    public string DatabaseSha256 { get; set; } = "";
}

internal static class CapacityReadonly
{
    internal static CapacityVerification Verify(string database, CapacityResult expected, long deadline)
    {
        if (expected.ProducerOnly)
            throw new InvalidOperationException("control-has-no-database");
        if (expected.ProtocolHash != CapacityProtocol.Hash)
            throw new InvalidOperationException("protocol-mismatch");
        var beforeFiles = Directory.GetFiles(Path.GetDirectoryName(database)!).Order(StringComparer.Ordinal).ToArray();
        var before = FileDigest(database);
        var result = new CapacityVerification { DatabaseSha256 = before };
        var started = Stopwatch.GetTimestamp();
        // immutable=1 forbids even WAL/SHM creation; use only after writer exit and verified checkpoint.
        var uri = new Uri(database).AbsoluteUri + "?immutable=1";
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = uri, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false }.ToString()))
        {
            connection.Open();
            result.ReopenTicks = Stopwatch.GetTimestamp() - started;
            for (var query = 0; query < CapacityProtocol.Queries.Length; query++)
            {
                using var command = connection.CreateCommand();
                command.Parameters.AddWithValue("$lo", expected.QueryLow);
                command.Parameters.AddWithValue("$hi", expected.QueryHigh);
                if (query >= 3)
                    command.Parameters.AddWithValue("$group", query == 3 ? expected.QueryStack : expected.QueryTrace);
                command.CommandText = CapacityProtocol.Queries[query];
                for (var repetition = 0; repetition < 5; repetition++)
                {
                    CheckDeadline(deadline);
                    var queryStart = Stopwatch.GetTimestamp();
                    var count = (long)command.ExecuteScalar()!;
                    var elapsed = Stopwatch.GetTimestamp() - queryStart;
                    result.QueryLatencies.AddTicks(elapsed);
                    result.QueryLatenciesByShape[query].AddTicks(elapsed);
                    if (query == 0 && repetition == 0)
                        result.TimeToFirstQueryTicks = Stopwatch.GetTimestamp() - started;
                    if (count != expected.QueryOracle[query])
                        throw new InvalidOperationException("query-oracle-mismatch");
                }
                command.CommandText = "EXPLAIN QUERY PLAN " + CapacityProtocol.Queries[query];
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException("missing-query-plan");
                var plan = reader.GetString(3);
                if (!plan.StartsWith("SEARCH records USING COVERING INDEX " + CapacityProtocol.QueryIndexes[query] + " (", StringComparison.Ordinal)
                    || reader.Read())
                    throw new InvalidOperationException("unexpected-query-plan");
                result.QueryPlans[query] = plan;
            }
            var traversal = Stopwatch.GetTimestamp();
            var (recordHash, records) = HashRows(connection,
                "SELECT seq,nominal,offered,key_id,thread_id,stack_id,trace_id,value,profile FROM records ORDER BY seq", 9, deadline);
            var (attributeHash, attributes) = HashRows(connection,
                "SELECT seq,key_id,value FROM attributes ORDER BY seq,key_id", 3, deadline);
            using var dictionary = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stacks = connection.CreateCommand();
            stacks.CommandText = "SELECT s.stack_id,s.ordinal,s.frame_id,f.name FROM stack_frames s JOIN frames f ON f.id=s.frame_id ORDER BY s.stack_id,s.ordinal";
            long stackRows = 0;
            using (var reader = stacks.ExecuteReader())
                while (reader.Read())
                {
                    CheckDeadline(deadline);
                    CapacityHash.Append(dictionary, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
                    var name = Encoding.UTF8.GetBytes(reader.GetString(3));
                    CapacityHash.Append(dictionary, name.Length);
                    dictionary.AppendData(name);
                    stackRows++;
                }
            using var countFrames = connection.CreateCommand();
            countFrames.CommandText = "SELECT count(*) FROM frames";
            var frames = (long)countFrames.ExecuteScalar()!;
            result.Equal = recordHash == expected.RecordHash && attributeHash == expected.AttributeHash
                && CapacityHash.Finish(dictionary) == expected.DictionaryHash && records == expected.Committed
                && records + attributes + stackRows + frames == expected.SqlRows && frames == stackRows;
            result.TraversalTicks = Stopwatch.GetTimestamp() - traversal;
        }
        if (FileDigest(database) != before ||
            !Directory.GetFiles(Path.GetDirectoryName(database)!).Order(StringComparer.Ordinal).SequenceEqual(beforeFiles))
            throw new InvalidOperationException("readonly-mutated-artifacts");
        if (!result.Equal)
            throw new InvalidOperationException("streaming-oracle-mismatch");
        return result;
    }

    private static (string Hash, long Count) HashRows(SqliteConnection connection, string sql, int columns, long deadline)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        long count = 0;
        while (reader.Read())
        {
            CheckDeadline(deadline);
            for (var column = 0; column < columns; column++)
                CapacityHash.Append(hash, reader.GetInt64(column));
            count++;
        }
        return (CapacityHash.Finish(hash), count);
    }

    private static string FileDigest(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    private static void CheckDeadline(long deadline)
    {
        if (Stopwatch.GetTimestamp() >= deadline)
            throw new TimeoutException("query-deadline");
    }
}
