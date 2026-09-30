using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core.Counters;
using Microsoft.Data.Sqlite;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Sqlite;

internal sealed class DurableSqliteStorageAdapterFactory : IDurableCounterStorageAdapterFactory
{
    internal const string AdapterId = "A";
    internal const string AdapterVersion = "dc5-sqlite-p1/1";
    internal const string ConfigurationSchema = "durable-sqlite-p1-config/1";
    internal const string DatabaseRelativePath = "canonical/counters.db";

    internal static DurableStorageAdapterIdentity SqliteIdentity { get; } = new(
        AdapterId,
        AdapterVersion,
        ConfigurationSchema,
        "successful bounded SQLite batch transaction commit under P1");

    public DurableStorageAdapterIdentity Identity => SqliteIdentity;

    public IDurableCounterStorageAdapter Create(DurableStorageAdapterCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DurableSqliteConfiguration.Validate(request.Configuration);
        DurableSqliteLimits.Validate(request.Limits);
        return new DurableSqliteStorageAdapter(request);
    }

    public IDurableCounterReadonlyStore OpenReadonly(DurableStorageOpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DurableSqliteLimits.Validate(request.Limits);
        DurableSqlitePackage.ValidateManifest(request.Manifest);
        var databasePath = DurableSqlitePackage.ResolveDeclaredDatabase(request.PackageRoot, request.Manifest);
        return DurableSqliteReadonlyStore.Open(
            databasePath,
            request.Manifest,
            request.Limits,
            expectedCaptureId: request.Manifest.CaptureId,
            expectedArtifactId: request.Manifest.ArtifactId);
    }

    public ValueTask<DurableStorageRecoveryResult> RecoverAsync(
        DurableStorageRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        DurableStorageRecoveryRules.Validate(
            request,
            new DurableStorageRecoveryResult(
                request.NewCaptureId,
                request.NewArtifactId,
                request.SourceCaptureId,
                request.Reason,
                VolatileTailUnknown: true,
                RecoveredMembers: []));
        return ValueTask.FromResult(DurableSqliteRecovery.Recover(request, cancellationToken));
    }
}

internal static class DurableSqliteConfiguration
{
    private static readonly byte[] CanonicalP1 = """{"profile":"P1"}"""u8.ToArray();

    internal static string Digest { get; } =
        Convert.ToHexString(SHA256.HashData(CanonicalP1)).ToLowerInvariant();

    internal static void Validate(JsonElement configuration)
    {
        if (configuration.ValueKind != JsonValueKind.Object)
        {
            throw new DurableStorageExperimentException(
                "InvalidSqliteConfiguration",
                "Candidate A configuration must be an object containing only profile='P1'.");
        }

        var properties = configuration.EnumerateObject().ToArray();
        if (properties.Length != 1
            || !string.Equals(properties[0].Name, "profile", StringComparison.Ordinal)
            || properties[0].Value.ValueKind != JsonValueKind.String
            || !string.Equals(properties[0].Value.GetString(), "P1", StringComparison.Ordinal))
        {
            throw new DurableStorageExperimentException(
                "UnsupportedSqliteConfiguration",
                "Candidate A supports exactly the frozen P1 configuration and rejects all other settings.");
        }
    }
}

internal static class DurableSqliteLimits
{
    internal const long PackageBytes = 268_435_456;
    internal const long ReaderBufferBytes = 8_388_608;
    internal const int CacheKiB = 2_048;

    internal static void Validate(DurableCounterPipelineLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var expected = new DurableCounterPipelineLimits(BatchMaxAge: TimeSpan.FromMilliseconds(100));
        if (limits != expected)
        {
            throw new DurableStorageExperimentException(
                "SqliteProtocolLimitMismatch",
                "Candidate A requires the exact frozen revision 3 pipeline and query limits.");
        }
    }
}

internal sealed class DurableSqliteFaultController : IDurableStorageFaultController
{
    private readonly Func<DurableStorageFaultContext, CancellationToken, ValueTask> _barrier;

    internal DurableSqliteFaultController(
        bool constrainPagesAfterFirstCommit = false,
        Func<DurableStorageFaultContext, CancellationToken, ValueTask>? barrier = null,
        DurableSqliteOperationHooks? operations = null)
    {
        ConstrainPagesAfterFirstCommit = constrainPagesAfterFirstCommit;
        _barrier = barrier ?? ((_, _) => ValueTask.CompletedTask);
        Operations = operations ?? DurableSqliteOperationHooks.Default;
    }

    internal bool ConstrainPagesAfterFirstCommit { get; }

    internal DurableSqliteOperationHooks Operations { get; }

    public ValueTask ReachAsync(DurableStorageFaultContext context, CancellationToken cancellationToken)
        => _barrier(context, cancellationToken);
}

internal sealed class DurableSqliteOperationHooks
{
    internal static DurableSqliteOperationHooks Default { get; } = new();

    internal DurableSqliteOperationHooks(
        Action<SqliteTransaction>? commit = null,
        Action<SqliteTransaction>? rollback = null,
        Action<SqliteTransaction>? dispose = null)
    {
        Commit = commit ?? (static transaction => transaction.Commit());
        Rollback = rollback ?? (static transaction => transaction.Rollback());
        Dispose = dispose ?? (static transaction => transaction.Dispose());
    }

    internal Action<SqliteTransaction> Commit { get; }

    internal Action<SqliteTransaction> Rollback { get; }

    internal Action<SqliteTransaction> Dispose { get; }
}

internal sealed class DurableSqliteStorageAdapter : IDurableCounterStorageAdapter
{
    private readonly DurableStorageAdapterCreateRequest _request;
    private readonly SqliteConnection _connection;
    private readonly string _databasePath;
    private readonly object _writerGate = new();
    private DurableSqliteReadonlyStore? _reader;
    private bool _sinkFinalized;
    private bool _preSealFinalized;
    private bool _disposed;
    private int _batchOrdinal;

    internal DurableSqliteStorageAdapter(DurableStorageAdapterCreateRequest request)
    {
        _request = request;
        DurableSqlitePackage.PrepareNewStagingRoot(request.StagingRoot);
        _databasePath = DurableSqlitePackage.DatabasePath(request.StagingRoot);
        _connection = DurableSqliteDatabase.OpenWritable(_databasePath);
        try
        {
            DurableSqliteDatabase.ApplyAndVerifyP1(_connection);
            DurableSqliteDatabase.CreateSchema(_connection, request.CaptureId, request.ArtifactId);
            DurableSqlitePackage.EnsureObservedGrowthFits(request.StagingRoot, DurableSqliteLimits.ReaderBufferBytes);
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    public DurableStorageAdapterIdentity Identity => DurableSqliteStorageAdapterFactory.SqliteIdentity;

    public IDurableCounterReadonlyStore Reader
        => _reader ?? throw new DurableStorageExperimentException(
            "SqliteReaderUnavailable",
            "Candidate A reader access is unavailable until pre-seal finalization completes.");

    public ValueTask<DurableCounterCommitResult> CommitAsync(
        IReadOnlyList<DurableCounterSinkRecord> records,
        CancellationToken cancellationToken)
    {
        if (!Monitor.TryEnter(_writerGate))
        {
            return ValueTask.FromResult(new DurableCounterCommitResult(
                DurableCounterCommitOutcome.Failed,
                "SqliteConcurrentWriter"));
        }
        try
        {
            return CommitCore(records, cancellationToken);
        }
        finally
        {
            Monitor.Exit(_writerGate);
        }
    }

    private ValueTask<DurableCounterCommitResult> CommitCore(
        IReadOnlyList<DurableCounterSinkRecord> records,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_sinkFinalized || _preSealFinalized)
        {
            return ValueTask.FromResult(new DurableCounterCommitResult(
                DurableCounterCommitOutcome.Failed,
                "SqliteWriterFinalized"));
        }

        try
        {
            ValidateBatch(records);
        }
        catch (DurableStorageExperimentException exception)
        {
            return ValueTask.FromResult(new DurableCounterCommitResult(
                DurableCounterCommitOutcome.Failed,
                exception.Code));
        }

        var ordinal = checked(++_batchOrdinal);
        var sequences = records.Select(static item => item.Record.Sequence).ToArray();
        var operations = _request.Faults is DurableSqliteFaultController sqliteFaults
            ? sqliteFaults.Operations
            : DurableSqliteOperationHooks.Default;
        try
        {
            _request.Faults.ReachAsync(
                new DurableStorageFaultContext(
                    DurableStorageFaultBarrier.BeforeBatchWrite,
                    ordinal,
                    sequences,
                    KnownCommitOutcome: null),
                cancellationToken).AsTask().GetAwaiter().GetResult();
            _request.Faults.ReachAsync(
                new DurableStorageFaultContext(
                    DurableStorageFaultBarrier.StorageFullNextBatch,
                    ordinal,
                    sequences,
                    KnownCommitOutcome: null),
                cancellationToken).AsTask().GetAwaiter().GetResult();

            if (_request.Faults is DurableSqliteFaultController
                {
                    ConstrainPagesAfterFirstCommit: true
                } && ordinal == 2)
            {
                DurableSqliteDatabase.ConstrainToCurrentPageCount(_connection);
            }

            DurableSqlitePackage.EnsureObservedGrowthFits(
                _request.StagingRoot,
                checked(records.Sum(static item => (long)item.Record.EncodedBytes)
                    + DurableSqliteLimits.ReaderBufferBytes));
        }
        catch (Exception exception) when (IsCatchable(exception))
        {
            return ValueTask.FromResult(KnownFailed(exception));
        }

        SqliteTransaction? transaction = null;
        try
        {
            transaction = _connection.BeginTransaction();
            using var command = DurableSqliteDatabase.CreateInsertCommand(_connection, transaction);
            foreach (var item in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DurableSqliteDatabase.Bind(command, item.Record);
                command.ExecuteNonQuery();
            }
            _request.Faults.ReachAsync(
                new DurableStorageFaultContext(
                    DurableStorageFaultBarrier.BeforeCommit,
                    ordinal,
                    sequences,
                    KnownCommitOutcome: null),
                cancellationToken).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (IsCatchable(exception))
        {
            return ValueTask.FromResult(ResolvePreCommitFailure(
                transaction,
                operations,
                sequences,
                exception));
        }

        try
        {
            operations.Commit(transaction);
        }
        catch (Exception exception) when (IsCatchable(exception))
        {
            var cleanup = TryDispose(transaction, operations);
            return ValueTask.FromResult(Unknown(
                "CommitStage",
                exception,
                cleanup));
        }

        var commitCleanup = TryDispose(transaction, operations);
        if (commitCleanup is not null)
        {
            return ValueTask.FromResult(Unknown(
                "CommitCleanup",
                commitCleanup));
        }

        try
        {
            _request.Faults.ReachAsync(
                new DurableStorageFaultContext(
                    DurableStorageFaultBarrier.AfterCommitBeforeAcknowledgement,
                    ordinal,
                    sequences,
                    DurableCounterCommitOutcome.Committed),
                cancellationToken).AsTask().GetAwaiter().GetResult();
            return ValueTask.FromResult(new DurableCounterCommitResult(DurableCounterCommitOutcome.Committed));
        }
        catch (Exception exception) when (IsCatchable(exception))
        {
            return ValueTask.FromResult(new DurableCounterCommitResult(
                DurableCounterCommitOutcome.Unknown,
                $"PostCommitAcknowledgement:{exception.GetType().Name}"));
        }
    }

    private DurableCounterCommitResult ResolvePreCommitFailure(
        SqliteTransaction? transaction,
        DurableSqliteOperationHooks operations,
        IReadOnlyList<long> sequences,
        Exception failure)
    {
        if (transaction is null)
        {
            return KnownFailed(failure);
        }

        Exception? rollbackFailure = null;
        try
        {
            operations.Rollback(transaction);
        }
        catch (Exception exception) when (IsCatchable(exception))
        {
            rollbackFailure = exception;
        }

        var cleanupFailure = TryDispose(transaction, operations);
        if (rollbackFailure is null && cleanupFailure is null)
        {
            return KnownFailed(failure);
        }

        if (failure is SqliteException { SqliteErrorCode: 13 }
            && cleanupFailure is null
            && BatchIsAbsent(sequences))
        {
            return KnownFailed(failure);
        }

        return Unknown(
            "PreCommitRollbackOrCleanup",
            failure,
            rollbackFailure,
            cleanupFailure);
    }

    private bool BatchIsAbsent(IReadOnlyList<long> sequences)
    {
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM records WHERE sequence = $sequence);";
            var parameter = command.Parameters.Add("$sequence", SqliteType.Integer);
            command.Prepare();
            foreach (var sequence in sequences)
            {
                parameter.Value = sequence;
                if (Convert.ToInt32(
                        command.ExecuteScalar(),
                        System.Globalization.CultureInfo.InvariantCulture) != 0)
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception exception) when (IsCatchable(exception))
        {
            return false;
        }
    }

    private static Exception? TryDispose(
        SqliteTransaction transaction,
        DurableSqliteOperationHooks operations)
    {
        try
        {
            operations.Dispose(transaction);
            return null;
        }
        catch (Exception exception) when (IsCatchable(exception))
        {
            return exception;
        }
    }

    private static DurableCounterCommitResult KnownFailed(Exception exception)
        => new(
            DurableCounterCommitOutcome.Failed,
            exception is SqliteException { SqliteErrorCode: 13 }
                ? "SqliteFull"
                : exception is DurableStorageExperimentException storage
                    ? storage.Code
                    : exception.GetType().Name);

    private static DurableCounterCommitResult Unknown(
        string stage,
        params Exception?[] exceptions)
        => new(
            DurableCounterCommitOutcome.Unknown,
            $"{stage}:{string.Join(
                ',',
                exceptions.Where(static exception => exception is not null)
                    .Select(static exception => exception!.GetType().Name))}");

    private static bool IsCatchable(Exception exception)
        => exception is not StackOverflowException
            and not OutOfMemoryException
            and not AccessViolationException;

    public ValueTask FinalizeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        _sinkFinalized = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask<DurableStoragePreSealResult> FinalizePreSealAsync(
        DurableCounterQualityReport finalPipelineQuality,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(finalPipelineQuality);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sinkFinalized)
        {
            throw new DurableStorageExperimentException(
                "SqliteSinkNotFinalized",
                "Candidate A sink completion must precede pre-seal finalization.");
        }
        if (_preSealFinalized)
        {
            throw new DurableStorageExperimentException(
                "SqliteAlreadyFinalized",
                "Candidate A pre-seal finalization is single-use.");
        }

        var retained = DurableSqliteDatabase.CountRecords(_connection);
        var quality = new DurableStorageQualityReport(
            finalPipelineQuality,
            retained,
            VolatileTailUnknown: false);
        DurableStorageQualityRules.Validate(quality);

        DurableSqlitePackage.EnsureObservedGrowthFits(
            _request.StagingRoot,
            checked(new FileInfo(_databasePath).Length + DurableSqliteLimits.ReaderBufferBytes));
        DurableSqliteDatabase.CreateQueryIndex(_connection);
        DurableSqliteDatabase.CheckpointForSeal(_connection);
        _connection.Dispose();

        DurableSqlitePackage.DeleteEmptyAuxiliaryFiles(_databasePath);
        var member = DurableSqlitePackage.DescribeDatabase(_request.StagingRoot);
        var result = new DurableStoragePreSealResult(
            CanonicalMembers: [member],
            QueryMembers: [],
            FinalBytes: member.Length,
            AdapterConfigurationDigest: DurableSqliteConfiguration.Digest);
        DurableStorageSealRules.ValidatePreSeal(result, _request.Limits);

        _reader = DurableSqliteReadonlyStore.Open(
            _databasePath,
            manifest: null,
            _request.Limits,
            _request.CaptureId,
            _request.ArtifactId,
            quality);
        _preSealFinalized = true;
        return ValueTask.FromResult(result);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_reader is not null)
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
        }
        _connection.Dispose();
    }

    private void ValidateBatch(IReadOnlyList<DurableCounterSinkRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count is < 1 or > 64 || records.Count > _request.Limits.BatchRecords)
        {
            throw new DurableStorageExperimentException(
                "SqliteBatchRecordLimit",
                "Candidate A accepts one to 64 records per transaction.");
        }

        long ownedBytes = 0;
        long previousSequence = 0;
        foreach (var item in records)
        {
            var record = item.Record;
            ownedBytes = checked(ownedBytes + item.Encoded.Length);
            if (item.Encoded.Length != record.EncodedBytes
                || record.EncodedBytes is < 1 or > DurableStorageFrameContract.MaximumRecordBytes
                || record.Sequence <= previousSequence
                || record.Sequence <= 0
                || !double.IsFinite(record.Value)
                || record.Kind is not (CounterKind.Mean or CounterKind.Sum)
                || !HasValidIntervalState(record)
                || !HasValidDisplayScaleState(record)
                || record.CoverageGap is not (CoverageGapState.Unknown or CoverageGapState.NoGap or CoverageGapState.Gap)
                || record.ResetState != CounterResetState.Unknown
                || string.IsNullOrEmpty(record.Provider)
                || string.IsNullOrEmpty(record.Name)
                || string.IsNullOrEmpty(record.DisplayName)
                || string.IsNullOrEmpty(record.ClockDomain)
                || string.IsNullOrEmpty(record.ClockOrigin))
            {
                throw new DurableStorageExperimentException(
                    "InvalidSqliteRecord",
                    "Candidate A received a record outside the shared logical schema.");
            }
            var providerBytes = Encoding.UTF8.GetByteCount(record.Provider);
            var nameBytes = Encoding.UTF8.GetByteCount(record.Name);
            var displayNameBytes = Encoding.UTF8.GetByteCount(record.DisplayName);
            var unitBytes = record.Unit is null ? 0 : Encoding.UTF8.GetByteCount(record.Unit);
            var clockDomainBytes = Encoding.UTF8.GetByteCount(record.ClockDomain);
            var clockOriginBytes = Encoding.UTF8.GetByteCount(record.ClockOrigin);
            if (providerBytes > _request.Limits.ProviderUtf8Bytes
                || nameBytes > _request.Limits.NameUtf8Bytes
                || displayNameBytes > _request.Limits.DisplayNameUtf8Bytes
                || unitBytes > _request.Limits.UnitUtf8Bytes
                || (long)providerBytes + nameBytes + displayNameBytes + unitBytes
                    + clockDomainBytes + clockOriginBytes > record.EncodedBytes)
            {
                throw new DurableStorageExperimentException(
                    "InvalidSqliteRecordStrings",
                    "Candidate A received record text outside the frozen UTF-8 boundaries.");
            }
            previousSequence = record.Sequence;
        }
        if (ownedBytes > DurableStorageFrameContract.MaximumOwnedBytesPerBatch
            || ownedBytes > _request.Limits.BatchOwnedBytes)
        {
            throw new DurableStorageExperimentException(
                "SqliteBatchByteLimit",
                "Candidate A batch exceeds the frozen owned-byte boundary.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static bool HasValidIntervalState(DurableCounterRecord record)
        => record.IntervalState switch
        {
            CounterMetadataState.Missing => record.IntervalSec is null,
            CounterMetadataState.Valid => record.IntervalSec is > 0 && double.IsFinite(record.IntervalSec.Value),
            CounterMetadataState.Nonpositive => record.IntervalSec is <= 0 && double.IsFinite(record.IntervalSec.Value),
            CounterMetadataState.Nonfinite => record.IntervalSec is null,
            _ => false,
        };

    private static bool HasValidDisplayScaleState(DurableCounterRecord record)
        => record.DisplayScaleState switch
        {
            CounterMetadataState.Missing => record.DisplayScaleTicks is null,
            CounterMetadataState.Valid => record.DisplayScaleTicks is > 0,
            CounterMetadataState.Nonpositive => record.DisplayScaleTicks is <= 0,
            _ => false,
        };
}

internal static class DurableSqliteDatabase
{
    private const string SchemaVersion = "durable-sqlite-schema/1";
    private static readonly string[] RequiredSchemaObjects =
        ["adapter_metadata", "ix_records_key_sequence", "records"];

    internal static SqliteConnection OpenWritable(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    internal static void ApplyAndVerifyP1(SqliteConnection connection)
    {
        ExecuteScalarText(connection, "PRAGMA journal_mode=WAL;").ShouldEqualOrdinalIgnoreCase("wal", "journal_mode");
        ExecuteNonQuery(connection, "PRAGMA synchronous=FULL;");
        ExecuteNonQuery(connection, "PRAGMA cache_size=-2048;");
        ExecuteNonQuery(connection, "PRAGMA mmap_size=0;");
        VerifyIntegerPragma(connection, "synchronous", 2);
        VerifyIntegerPragma(connection, "cache_size", -2_048);
        VerifyIntegerPragma(connection, "mmap_size", 0);

        var pageSize = ExecuteScalarInt64(connection, "PRAGMA page_size;");
        var maxPages = DurableSqliteLimits.PackageBytes / pageSize;
        ExecuteScalarInt64(connection, $"PRAGMA max_page_count={maxPages};");
        if (ExecuteScalarInt64(connection, "PRAGMA max_page_count;") != maxPages)
        {
            throw new DurableStorageExperimentException(
                "SqlitePragmaDrift",
                "Candidate A could not apply its main-database page limit.");
        }
    }

    internal static void CreateSchema(SqliteConnection connection, string captureId, string artifactId)
    {
        ExecuteNonQuery(connection, """
            CREATE TABLE adapter_metadata (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                schema_version TEXT NOT NULL,
                adapter_id TEXT NOT NULL,
                adapter_version TEXT NOT NULL,
                profile TEXT NOT NULL,
                configuration_digest TEXT NOT NULL,
                capture_id TEXT NOT NULL,
                artifact_id TEXT NOT NULL
            ) STRICT;
            CREATE TABLE records (
                sequence INTEGER PRIMARY KEY CHECK (sequence > 0),
                provider TEXT NOT NULL,
                name TEXT NOT NULL,
                display_name TEXT NOT NULL,
                unit TEXT NULL,
                value REAL NOT NULL,
                kind INTEGER NOT NULL CHECK (kind IN (0, 1)),
                interval_sec REAL NULL,
                interval_state INTEGER NOT NULL CHECK (interval_state BETWEEN 0 AND 3),
                display_scale_ticks INTEGER NULL,
                display_scale_state INTEGER NOT NULL CHECK (display_scale_state BETWEEN 0 AND 2),
                source_time_ticks INTEGER NULL,
                clock_domain TEXT NOT NULL,
                clock_origin TEXT NOT NULL,
                coverage_gap INTEGER NOT NULL CHECK (coverage_gap BETWEEN 0 AND 2),
                reset_state INTEGER NOT NULL CHECK (reset_state = 0),
                encoded_bytes INTEGER NOT NULL CHECK (encoded_bytes BETWEEN 1 AND 4096)
            ) STRICT;
            """);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO adapter_metadata (
                singleton, schema_version, adapter_id, adapter_version, profile,
                configuration_digest, capture_id, artifact_id)
            VALUES (1, $schema, $adapter, $version, 'P1', $digest, $capture, $artifact);
            """;
        command.Parameters.AddWithValue("$schema", SchemaVersion);
        command.Parameters.AddWithValue("$adapter", DurableSqliteStorageAdapterFactory.AdapterId);
        command.Parameters.AddWithValue("$version", DurableSqliteStorageAdapterFactory.AdapterVersion);
        command.Parameters.AddWithValue("$digest", DurableSqliteConfiguration.Digest);
        command.Parameters.AddWithValue("$capture", captureId);
        command.Parameters.AddWithValue("$artifact", artifactId);
        command.ExecuteNonQuery();
    }

    internal static SqliteCommand CreateInsertCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO records (
                sequence, provider, name, display_name, unit, value, kind,
                interval_sec, interval_state, display_scale_ticks, display_scale_state,
                source_time_ticks, clock_domain, clock_origin, coverage_gap, reset_state,
                encoded_bytes)
            VALUES (
                $sequence, $provider, $name, $displayName, $unit, $value, $kind,
                $intervalSec, $intervalState, $displayScaleTicks, $displayScaleState,
                $sourceTimeTicks, $clockDomain, $clockOrigin, $coverageGap, $resetState,
                $encodedBytes);
            """;
        foreach (var name in new[]
        {
            "$sequence", "$provider", "$name", "$displayName", "$unit", "$value", "$kind",
            "$intervalSec", "$intervalState", "$displayScaleTicks", "$displayScaleState",
            "$sourceTimeTicks", "$clockDomain", "$clockOrigin", "$coverageGap", "$resetState",
            "$encodedBytes",
        })
        {
            command.Parameters.Add(new SqliteParameter(name, null));
        }
        command.Prepare();
        return command;
    }

    internal static void Bind(SqliteCommand command, DurableCounterRecord record)
    {
        command.Parameters["$sequence"].Value = record.Sequence;
        command.Parameters["$provider"].Value = record.Provider;
        command.Parameters["$name"].Value = record.Name;
        command.Parameters["$displayName"].Value = record.DisplayName;
        command.Parameters["$unit"].Value = record.Unit is null ? DBNull.Value : record.Unit;
        command.Parameters["$value"].Value = record.Value;
        command.Parameters["$kind"].Value = (int)record.Kind;
        command.Parameters["$intervalSec"].Value = record.IntervalSec.HasValue
            ? record.IntervalSec.Value
            : DBNull.Value;
        command.Parameters["$intervalState"].Value = (int)record.IntervalState;
        command.Parameters["$displayScaleTicks"].Value = record.DisplayScaleTicks.HasValue
            ? record.DisplayScaleTicks.Value
            : DBNull.Value;
        command.Parameters["$displayScaleState"].Value = (int)record.DisplayScaleState;
        command.Parameters["$sourceTimeTicks"].Value = record.SourceTimeTicks.HasValue
            ? record.SourceTimeTicks.Value
            : DBNull.Value;
        command.Parameters["$clockDomain"].Value = record.ClockDomain;
        command.Parameters["$clockOrigin"].Value = record.ClockOrigin;
        command.Parameters["$coverageGap"].Value = (int)record.CoverageGap;
        command.Parameters["$resetState"].Value = (int)record.ResetState;
        command.Parameters["$encodedBytes"].Value = record.EncodedBytes;
    }

    internal static void CreateQueryIndex(SqliteConnection connection)
        => ExecuteNonQuery(
            connection,
            "CREATE INDEX IF NOT EXISTS ix_records_key_sequence ON records(provider COLLATE BINARY, name COLLATE BINARY, sequence);");

    internal static long CountRecords(SqliteConnection connection)
        => ExecuteScalarInt64(connection, "SELECT COUNT(*) FROM records;");

    internal static void CheckpointForSeal(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) != 0 || reader.GetInt64(1) != reader.GetInt64(2))
        {
            throw new DurableStorageExperimentException(
                "SqliteCheckpointIncomplete",
                "Candidate A could not checkpoint every WAL frame before sealing.");
        }
    }

    internal static void ConstrainToCurrentPageCount(SqliteConnection connection)
    {
        var pageCount = ExecuteScalarInt64(connection, "PRAGMA page_count;");
        var applied = ExecuteScalarInt64(connection, $"PRAGMA max_page_count={pageCount};");
        if (applied != pageCount)
        {
            throw new DurableStorageExperimentException(
                "SqliteFaultInjectionFailed",
                "Candidate A could not constrain max_page_count to the current allocation.");
        }
    }

    internal static void VerifyMetadata(
        SqliteConnection connection,
        string expectedCaptureId,
        string expectedArtifactId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_version, adapter_id, adapter_version, profile,
                   configuration_digest, capture_id, artifact_id
            FROM adapter_metadata WHERE singleton = 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read()
            || !string.Equals(reader.GetString(0), SchemaVersion, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), DurableSqliteStorageAdapterFactory.AdapterId, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(2), DurableSqliteStorageAdapterFactory.AdapterVersion, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(3), "P1", StringComparison.Ordinal)
            || !string.Equals(reader.GetString(4), DurableSqliteConfiguration.Digest, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(5), expectedCaptureId, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(6), expectedArtifactId, StringComparison.Ordinal)
            || reader.Read())
        {
            throw new DurableStorageExperimentException(
                "SqliteMetadataMismatch",
                "Candidate A database metadata does not match the requested package identity.");
        }
    }

    internal static void UpdatePackageIdentity(
        SqliteConnection connection,
        string captureId,
        string artifactId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE adapter_metadata
            SET capture_id = $capture, artifact_id = $artifact
            WHERE singleton = 1;
            """;
        command.Parameters.AddWithValue("$capture", captureId);
        command.Parameters.AddWithValue("$artifact", artifactId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new DurableStorageExperimentException(
                "SqliteMetadataMismatch",
                "Candidate A recovery could not update the copied package identity.");
        }
    }

    internal static void VerifyReadOnlySchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_schema
            WHERE type IN ('table', 'index')
              AND name IN ('adapter_metadata', 'records', 'ix_records_key_sequence')
            ORDER BY name COLLATE BINARY;
            """;
        using var reader = command.ExecuteReader();
        var names = new List<string>(3);
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }
        if (!names.SequenceEqual(RequiredSchemaObjects, StringComparer.Ordinal))
        {
            throw new DurableStorageExperimentException(
                "SqliteSchemaIncomplete",
                "Candidate A sealed database is missing its table or pre-seal query index.");
        }
    }

    internal static void VerifyIntegrity(SqliteConnection connection)
    {
        if (!string.Equals(
                ExecuteScalarText(connection, "PRAGMA quick_check(1);"),
                "ok",
                StringComparison.Ordinal))
        {
            throw new DurableStorageExperimentException(
                "SqliteIntegrityFailure",
                "Candidate A database failed SQLite quick_check.");
        }
    }

    private static void VerifyIntegerPragma(SqliteConnection connection, string name, long expected)
    {
        if (ExecuteScalarInt64(connection, $"PRAGMA {name};") != expected)
        {
            throw new DurableStorageExperimentException(
                "SqlitePragmaDrift",
                $"Candidate A PRAGMA {name} did not retain the frozen P1 value.");
        }
    }

    internal static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static long ExecuteScalarInt64(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static string ExecuteScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
    }
}

internal static class DurableSqlitePackage
{
    internal static string DatabasePath(string root)
        => Path.Combine(root, DurableStoragePackageLayout.CanonicalDirectory, "counters.db");

    internal static void PrepareNewStagingRoot(string stagingRoot)
    {
        if (string.IsNullOrWhiteSpace(stagingRoot))
        {
            throw new DurableStorageExperimentException("InvalidSqliteStagingRoot", "A staging root is required.");
        }
        var root = Path.GetFullPath(stagingRoot);
        Directory.CreateDirectory(root);
        if (Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new DurableStorageExperimentException(
                "SqliteStagingNotEmpty",
                "Candidate A requires an exclusive empty staging root.");
        }
        Directory.CreateDirectory(Path.Combine(root, DurableStoragePackageLayout.CanonicalDirectory));
    }

    internal static string ResolveDeclaredDatabase(
        string packageRoot,
        DurableStoragePackageManifest manifest)
    {
        var member = manifest.Members.SingleOrDefault(static item =>
            string.Equals(
                item.RelativePath,
                DurableSqliteStorageAdapterFactory.DatabaseRelativePath,
                StringComparison.Ordinal)
            && item.Role == DurableStorageMemberRole.CanonicalData)
            ?? throw new DurableStorageExperimentException(
                "SqliteDatabaseMemberMissing",
                "Candidate A manifest does not declare canonical/counters.db.");
        if (member.Length < 1 || member.Length > DurableSqliteLimits.PackageBytes)
        {
            throw new DurableStorageExperimentException(
                "SqliteDatabaseMemberInvalid",
                "Candidate A database member length is outside the package limit.");
        }
        var path = DatabasePath(Path.GetFullPath(packageRoot));
        if (!File.Exists(path) || new FileInfo(path).Length != member.Length)
        {
            throw new DurableStorageExperimentException(
                "SqliteDatabaseMemberMismatch",
                "Candidate A database file does not match the host-validated manifest length.");
        }
        return path;
    }

    internal static void ValidateManifest(DurableStoragePackageManifest manifest)
    {
        if (!string.Equals(manifest.ContractVersion, DurableStorageExperimentVersions.PackageContract, StringComparison.Ordinal)
            || !string.Equals(manifest.RecordSchemaVersion, DurableStorageExperimentVersions.RecordSchema, StringComparison.Ordinal)
            || manifest.Adapter != DurableSqliteStorageAdapterFactory.SqliteIdentity)
        {
            throw new DurableStorageExperimentException(
                "SqliteManifestMismatch",
                "Candidate A package manifest uses an incompatible contract, record schema, or adapter identity.");
        }
    }

    internal static DurableStorageMember DescribeDatabase(string root)
    {
        var path = DatabasePath(root);
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new DurableStorageMember(
            DurableSqliteStorageAdapterFactory.DatabaseRelativePath,
            stream.Length,
            hash,
            DurableStorageMemberRole.CanonicalData);
    }

    internal static void EnsureObservedGrowthFits(string root, long reservedGrowth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reservedGrowth);
        long observed = 0;
        if (Directory.Exists(root))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                observed = checked(observed + new FileInfo(path).Length);
            }
        }
        if (observed > DurableSqliteLimits.PackageBytes
            || reservedGrowth > DurableSqliteLimits.PackageBytes - observed)
        {
            throw new DurableStorageExperimentException(
                "SqlitePackagePeakLimit",
                "Candidate A cannot reserve the next bounded operation within the 256 MiB package ceiling.");
        }
    }

    internal static void EnsureRecoveryPeakFits(
        string sourceRoot,
        string recoveryRoot,
        long reservedGrowth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reservedGrowth);
        var sourceBytes = MeasureFiles(sourceRoot);
        var recoveryBytes = MeasureFiles(recoveryRoot);
        if (sourceBytes > DurableSqliteLimits.PackageBytes
            || recoveryBytes > DurableSqliteLimits.PackageBytes
            || reservedGrowth > DurableSqliteLimits.PackageBytes - sourceBytes - recoveryBytes)
        {
            throw new DurableStorageExperimentException(
                "SqliteRecoveryPeakLimit",
                "Candidate A source, recovery output, and next bounded operation exceed the combined 256 MiB peak ceiling.");
        }
    }

    private static long MeasureFiles(string root)
    {
        long bytes = 0;
        if (!Directory.Exists(root))
        {
            return bytes;
        }
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            bytes = checked(bytes + new FileInfo(path).Length);
        }
        return bytes;
    }

    internal static void DeleteEmptyAuxiliaryFiles(string databasePath)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var path = databasePath + suffix;
            if (!File.Exists(path))
            {
                continue;
            }
            if (new FileInfo(path).Length != 0)
            {
                throw new DurableStorageExperimentException(
                    "SqliteAuxiliaryFileRetained",
                    "Candidate A retained a non-empty SQLite auxiliary file after final checkpoint.");
            }
            File.Delete(path);
        }
    }
}

internal static class SqliteValueChecks
{
    internal static void ShouldEqualOrdinalIgnoreCase(this string actual, string expected, string pragma)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new DurableStorageExperimentException(
                "SqlitePragmaDrift",
                $"Candidate A PRAGMA {pragma} returned '{actual}', expected '{expected}'.");
        }
    }
}
