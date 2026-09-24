using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DotnetDiagnostics.TestSupport.SqliteCapacity;
using Microsoft.Data.Sqlite;

namespace DotnetDiagnostics.Core.Tests;

public sealed class SqliteCapacityTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "capacity-components", Guid.NewGuid().ToString("N"));

    public SqliteCapacityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void StructuredRowsQueriesAndStreamingOraclePreserveLossGaps(int profileValue)
    {
        var profile = (CapacityProfile)profileValue;
        var path = Path.Combine(_root, "records.sqlite");
        var result = Result(profile);
        long[] sequences = [0, 1, 3, 7, 8, 15, 16, 24, 31, 64, 128, 131];
        var records = sequences.Select(seq => CapacityProtocol.Generate(profile, seq, seq, seq + 2)).ToArray();
        using (var store = Store(path, result))
        {
            store.Commit(records[..5]);
            store.Commit(records[5..]);
            store.Seal();
        }
        Assert.Equal(12, result.Committed);
        Assert.Equal("wal", result.Pragmas["journal_mode"]);
        Assert.Equal("2", result.Pragmas["synchronous"]);
        Assert.Equal(12, result.QueryOracle[0]);
        Assert.Equal(1, result.QueryOracle[1]);
        Assert.Equal(2, result.QueryOracle[2]);
        Assert.Equal(profile switch { CapacityProfile.RepeatedStacks => 2, CapacityProfile.NovelStacks => 1, _ => 0 }, result.QueryOracle[3]);
        Assert.Equal(profile == CapacityProfile.Activity ? 2 : 0, result.QueryOracle[4]);
        Assert.Equal(profile switch
        {
            CapacityProfile.Numeric => 12, CapacityProfile.RepeatedStacks => 92,
            CapacityProfile.NovelStacks => 108, _ => 60
        }, result.SqlRows);
        var before = SHA256.HashData(File.ReadAllBytes(path));
        var verified = CapacityReadonly.Verify(path, result, Deadline());
        Assert.True(verified.Equal);
        Assert.Equal(25, verified.QueryLatencies.Count);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Single(Directory.GetFiles(_root));
        Assert.All(verified.QueryPlans, plan => Assert.StartsWith("SEARCH records USING COVERING INDEX ix_", plan));
    }

    [Fact]
    public void QueryAndTraversalOracleDetectDifferentMutations()
    {
        var path = Path.Combine(_root, "records.sqlite");
        var result = Result(CapacityProfile.Activity);
        using (var store = Store(path, result))
        {
            store.Commit([CapacityProtocol.Generate(CapacityProfile.Activity, 7, 7, 9)]);
            store.Seal();
        }
        Mutate(path, "UPDATE attributes SET value=999 WHERE key_id=0");
        Assert.Throws<InvalidOperationException>(() => CapacityReadonly.Verify(path, result, Deadline()));
        Mutate(path, "UPDATE attributes SET value=28 WHERE key_id=0; UPDATE records SET key_id=8");
        var error = Assert.Throws<InvalidOperationException>(() => CapacityReadonly.Verify(path, result, Deadline()));
        Assert.Equal("query-oracle-mismatch", error.Message);
    }

    [Fact]
    public void FailedTransactionDoesNotAdvanceCommitMembership()
    {
        var path = Path.Combine(_root, "records.sqlite");
        var result = Result(CapacityProfile.Numeric);
        using var store = Store(path, result);
        Mutate(path, "CREATE TRIGGER fail_insert BEFORE INSERT ON records WHEN NEW.seq=1 BEGIN SELECT RAISE(ABORT,'component-fault'); END");
        Assert.Throws<SqliteException>(() => store.Commit([
            CapacityProtocol.Generate(CapacityProfile.Numeric, 0, 0, 0),
            CapacityProtocol.Generate(CapacityProfile.Numeric, 1, 1, 1)]));
        Assert.Equal(0, result.Committed);
        Assert.Equal(0, result.SqlRows);
        Assert.All(result.QueryOracle, count => Assert.Equal(0, count));
        store.Seal();
        Assert.True(CapacityReadonly.Verify(path, result, Deadline()).Equal);
    }

    [Fact]
    public void QueueReservationsAndDictionaryCapsAreInsertionTimeBounds()
    {
        var queue = new CapacityQueue();
        var record = CapacityProtocol.Generate(CapacityProfile.Numeric, 0, 0, 0);
        for (var index = 0; index < CapacityProtocol.QueueRecords; index++)
            Assert.True(queue.TryOffer(record));
        Assert.False(queue.TryOffer(record));
        Assert.Equal(CapacityProtocol.QueueRecords, queue.PeakRecords);
        Assert.True(queue.TryTake(out _));
        Assert.True(queue.TryOffer(record));
        queue.Complete();
        Assert.False(queue.TryOffer(record));
        Assert.False(CapacityStore.CanAddStack(CapacityProtocol.StackLimit, 0, 1));
        Assert.True(CapacityStore.CanAddStack(CapacityProtocol.StackLimit - 1, CapacityProtocol.DictionaryBytes - 256, 256));
        Assert.False(CapacityStore.CanAddStack(1, CapacityProtocol.DictionaryBytes - 255, 256));
    }

    [Fact]
    public void AlreadyAgedBacklogFillsBoundedBatchBeforeAgeFlush()
    {
        var queue = new CapacityQueue();
        for (var index = 0; index <= CapacityProtocol.BatchRecords; index++)
            Assert.True(queue.TryOffer(CapacityProtocol.Generate(CapacityProfile.Numeric, index, index, index)));
        queue.Complete();
        var batch = new List<CapacityRecord>();
        long oldest = -1;
        Assert.True(CapacityRunner.FillAvailableBatch(queue, batch, 0, ref oldest));
        Assert.Equal(CapacityProtocol.BatchRecords, batch.Count);
        Assert.Equal(0, oldest);
        Assert.Equal(1, queue.Count);
        Assert.False(queue.IsDrained);
        batch.Clear();
        Assert.True(CapacityRunner.FillAvailableBatch(queue, batch, 0, ref oldest));
        Assert.Single(batch);
        Assert.Equal(CapacityProtocol.BatchRecords, oldest);
        Assert.True(queue.IsDrained);
    }

    [Fact]
    public void FillingPartialBatchDoesNotRenewOldestOfferOrExceedBound()
    {
        var queue = new CapacityQueue();
        var record = CapacityProtocol.Generate(CapacityProfile.Numeric, 0, 0, 20);
        for (var index = 0; index < CapacityProtocol.BatchRecords; index++)
            Assert.True(queue.TryOffer(record));
        var batch = new List<CapacityRecord> { record };
        long oldest = 7;
        Assert.True(CapacityRunner.FillAvailableBatch(queue, batch, 100, ref oldest));
        Assert.Equal(CapacityProtocol.BatchRecords, batch.Count);
        Assert.Equal(7, oldest);
        Assert.Equal(1, queue.Count);
        Assert.False(CapacityRunner.FillAvailableBatch(queue, batch, 100, ref oldest));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void EmptyQueueLeavesPartialBatchAgeUnchanged()
    {
        var batch = new List<CapacityRecord> { CapacityProtocol.Generate(CapacityProfile.Numeric, 0, 0, 0) };
        long oldest = 7;
        Assert.False(CapacityRunner.FillAvailableBatch(new CapacityQueue(), batch, 100, ref oldest));
        Assert.Equal(7, oldest);
        Assert.Single(batch);
    }

    [Fact]
    public void LogicalCapAndOversizedBatchAreExplicitWithoutLargeFixtures()
    {
        var result = Result(CapacityProfile.Numeric);
        result.RetainedLogicalRecordBytes = CapacityProtocol.LogicalBytes - 72;
        using var store = Store(Path.Combine(_root, "records.sqlite"), result);
        store.Commit([CapacityProtocol.Generate(CapacityProfile.Numeric, 0, 0, 0),
            CapacityProtocol.Generate(CapacityProfile.Numeric, 1, 1, 1)]);
        Assert.Equal(1, result.Committed);
        Assert.Equal(1, result.LogicalCapRejected);
        var error = Assert.Throws<InvalidOperationException>(() => store.Commit(new CapacityRecord[257]));
        Assert.Equal("batch-cap", error.Message);
    }

    [Fact]
    public void ProducerOnlyHasNoSqliteArtifactOrSqlCommitPopulation()
    {
        var result = Result(CapacityProfile.Activity);
        result.ProducerOnly = true;
        using (var store = Store(Path.Combine(_root, "absent.sqlite"), result))
        {
            store.Commit([CapacityProtocol.Generate(CapacityProfile.Activity, 1, 1, 1)]);
            store.Seal();
        }
        Assert.Equal(1, result.ControlConsumed);
        Assert.Equal(0, result.Committed);
        Assert.Equal(0, result.SqlRows);
        Assert.Empty(Directory.GetFiles(_root));
        Assert.Equal(0, result.CommitLatency.Count);
    }

    [Fact]
    public void NominalSlotsNeverRebaseToLateActualOffersAndHistogramsRemainFixed()
    {
        Assert.Equal([1_000, 10_000, 50_000, 100_000], CapacityProtocol.Rates);
        Assert.Equal(999_990, CapacityProtocol.NominalSlot(99_999, 100_000, 1_000_000));
        var late = CapacityProtocol.Generate(CapacityProfile.Numeric, 1, 10, 5_000);
        Assert.Equal(10, late.Nominal);
        Assert.Equal(5_000, late.Offered);
        Assert.Equal(20, CapacityProtocol.NominalSlot(2, 100_000, 1_000_000));
        var histogram = new CapacityHistogram();
        histogram.AddTicks(0);
        histogram.AddTicks(11 * Stopwatch.Frequency);
        Assert.Equal(8, histogram.Counts.Length);
        Assert.Equal(1, histogram.Counts[0]);
        Assert.Equal(1, histogram.Counts[7]);
    }

    [Fact]
    public void WorkerDoesNotRenewAnExpiredSupervisorBudget()
    {
        var result = CapacityRunner.RunWorker(_root, CapacityProfile.Numeric, 1_000,
            producerOnly: true, deadline: 0, componentRecords: 32);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal("TimeoutException:worker-deadline", result.Failure);
        Assert.Equal(0, result.DeadlineTimestamp);
        Assert.Equal(0, result.Offered);
        Assert.Equal(32, result.NotOffered);
        Assert.Equal("worker.json", Path.GetFileName(Assert.Single(Directory.GetFiles(_root))));
    }

    [Fact]
    public void ExpiredWorkerDeadlineFailsExplicitly()
    {
        var path = Path.Combine(_root, "records.sqlite");
        var result = Result(CapacityProfile.Numeric);
        using (var store = new CapacityStore(path, result, Stopwatch.GetTimestamp(), 0))
            Assert.Throws<TimeoutException>(() => store.Commit([CapacityProtocol.Generate(CapacityProfile.Numeric, 0, 0, 0)]));
    }

    [Fact]
    public void ExpiredReaderDeadlineAndProtocolMismatchFailExplicitly()
    {
        var path = Path.Combine(_root, "records.sqlite");
        var result = Result(CapacityProfile.Numeric);
        using (var store = Store(path, result)) store.Seal();
        Assert.Throws<TimeoutException>(() => CapacityReadonly.Verify(path, result, 0));
        result.ProtocolHash = new string('0', 64);
        Assert.Equal("protocol-mismatch",
            Assert.Throws<InvalidOperationException>(() => CapacityReadonly.Verify(path, result, Deadline())).Message);
    }

    [Fact]
    public void CanonicalConfigurationMatchesImplementationBounds()
    {
        using var configuration = JsonDocument.Parse(CapacityProtocol.Configuration);
        var json = configuration.RootElement;
        Assert.Equal("available-before-age-check", json.GetProperty("batchFill").GetString());
        var bounds = new Dictionary<string, long>
        {
            ["queueRecords"] = CapacityProtocol.QueueRecords,
            ["recordReservationBytes"] = CapacityProtocol.RecordReservationBytes,
            ["batchRecords"] = CapacityProtocol.BatchRecords,
            ["batchBytes"] = CapacityProtocol.BatchBytes,
            ["batchAgeMilliseconds"] = CapacityProtocol.BatchAgeMilliseconds,
            ["stackLimit"] = CapacityProtocol.StackLimit,
            ["frameLimit"] = CapacityProtocol.FrameLimit,
            ["dictionaryBytes"] = CapacityProtocol.DictionaryBytes,
            ["logicalBytes"] = CapacityProtocol.LogicalBytes,
            ["packageBytes"] = CapacityProtocol.PackageBytes,
            ["workspaceBytes"] = CapacityProtocol.WorkspaceBytes,
            ["diagnosticRssBytes"] = CapacityProtocol.DiagnosticRssBytes,
            ["caseSeconds"] = CapacityProtocol.CaseSeconds,
            ["outputBytes"] = CapacityProtocol.OutputBytes
        };
        foreach (var (name, value) in bounds) Assert.Equal(value, json.GetProperty(name).GetInt64());
        Assert.Equal(CapacityProtocol.Rates, json.GetProperty("rates").EnumerateArray().Select(static n => n.GetInt32()));
        Assert.Equal(Enum.GetNames<CapacityProfile>(), json.GetProperty("profiles").EnumerateArray().Select(static n => n.GetString()));
        Assert.Matches("^[0-9a-f]{64}$", CapacityProtocol.Hash);
        Assert.Equal(5, CapacityProtocol.Queries.Length);
    }

    private static long Deadline() => Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
    private static CapacityResult Result(CapacityProfile profile) => new()
    {
        Profile = profile.ToString(), QueryLow = 0, QueryHigh = 1_000
    };
    private static CapacityStore Store(string path, CapacityResult result)
        => new(path, result, Stopwatch.GetTimestamp(), Deadline());

    private static void Mutate(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
