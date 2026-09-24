using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace DotnetDiagnostics.Core.Captures;

/// <summary>One bounded admission queue and one dedicated batched SQLite writer per capture.</summary>
public sealed class CaptureWriter : IAsyncDisposable
{
    private sealed record Offer(string ArtifactId, CaptureRecord Record, long Bytes, long OfferedAt);
    private sealed record SqliteSettings(string JournalMode, long Synchronous, long PageLimit);
    private readonly string _directory;
    private readonly CaptureStoreOptions _options;
    private readonly FileStream _lease;
    private readonly FileStream _slot;
    private readonly Channel<Offer> _queue;
    private readonly object _gate = new();
    private readonly object _lifecycle = new();
    private readonly ConcurrentDictionary<string, CaptureArtifactInfo> _artifacts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CaptureSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly Task _worker;
    private CaptureManifest _manifest;
    private Task<CaptureInfo>? _shutdown;
    private Exception? _failure;
    private int _stopping, _offersInFlight, _abort, _queueRecords, _largestBatch, _interrupted;
    private long _offered, _accepted, _persisted, _recordRejected, _queueRejected, _storageRejected;
    private long _logicalBytes, _queueBytes, _transactions, _observedBytes;
    private long _snapshotRejected;
    private SqliteSettings? _settings;
    private long? _sourceRejected;
    private bool _unknownTail;

    internal CaptureWriter(string directory, CaptureStoreOptions options, CaptureManifest manifest,
        FileStream lease, FileStream slot)
    {
        _directory = directory;
        _options = options;
        _manifest = manifest;
        _lease = lease;
        _slot = slot;
        _queue = Channel.CreateBounded<Offer>(new BoundedChannelOptions(options.QueueRecords)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(RunAsync);
    }

    public CaptureReference Reference => new(_manifest.Info.CaptureId);

    public string AddArtifact(string kind, string name)
    {
        CapturePackage.ValidateText(kind, 1024, nameof(kind));
        CapturePackage.ValidateText(name, 1024, nameof(name));
        if (string.IsNullOrWhiteSpace(kind) || name is null)
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Artifact kind and name are required.");
        lock (_gate)
        {
            EnsureActive();
            if (_artifacts.Count >= _options.MaxArtifacts)
                throw CapturePackage.Error(CaptureErrorCode.CapacityExceeded, "Artifact count limit reached.");
            var artifact = new CaptureArtifactInfo(Guid.NewGuid().ToString("N"), kind, name);
            if (!_artifacts.TryAdd(artifact.ArtifactId, artifact))
                throw CapturePackage.Error(CaptureErrorCode.StorageFailure, "Generated artifact identity collided.");
            _manifest = _manifest with { Info = _manifest.Info with { Artifacts = _artifacts.Values.ToArray() } };
            try { CapturePackage.WriteJson(_directory, CapturePackage.Manifest, _manifest); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CaptureStoreException)
            {
                _failure = CapturePackage.Translate(ex);
                Interlocked.Exchange(ref _abort, 1);
                throw CapturePackage.Translate(ex);
            }

            return artifact.ArtifactId;
        }
    }

    /// <summary>Sets bounded, already-known producer facts without attaching to or enriching a process.</summary>
    public void SetArtifactProvenance(string artifactId, CaptureArtifactProvenance provenance)
    {
        CapturePackage.ValidateId(artifactId);
        CapturePackage.ValidateProvenance(provenance);
        var canonical = provenance with
        {
            StartedAt = provenance.StartedAt?.ToUniversalTime(),
            ProcessStartUtc = provenance.ProcessStartUtc?.ToUniversalTime()
        };
        lock (_gate)
        {
            EnsureActive();
            if (!_artifacts.TryGetValue(artifactId, out var artifact))
                throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Unknown artifact ID.");
            var updated = artifact with { Provenance = canonical };
            var manifest = _manifest with
            {
                Info = _manifest.Info with
                {
                    Artifacts = _manifest.Info.Artifacts.Select(a => a.ArtifactId == artifactId ? updated : a).ToArray()
                }
            };
            try { CapturePackage.WriteJson(_directory, CapturePackage.Manifest, manifest); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CaptureStoreException)
            {
                _failure = CapturePackage.Translate(ex);
                Interlocked.Exchange(ref _abort, 1);
                throw CapturePackage.Translate(ex);
            }
            _artifacts[artifactId] = updated;
            _manifest = manifest;
        }
    }

    /// <summary>No SQL or filesystem work. False means an explicitly counted bounded-admission rejection.</summary>
    public bool TryAppend(string artifactId, CaptureRecord record)
    {
        EnsureActive();
        Interlocked.Increment(ref _offersInFlight);
        try
        {
            EnsureActive();
            Interlocked.Increment(ref _offered);
            if (!Monitor.TryEnter(_gate))
            {
                Interlocked.Increment(ref _queueRejected);
                return false;
            }
            try
            {
                if (artifactId is null || !_artifacts.ContainsKey(artifactId) || !TryOwn(record, out var owned, out var size))
                {
                    Interlocked.Increment(ref _recordRejected);
                    return false;
                }
                if (_failure is not null || Volatile.Read(ref _abort) != 0 || size > _options.MaxLogicalBytes - _logicalBytes)
                {
                    Interlocked.Increment(ref _storageRejected);
                    return false;
                }
                if (Volatile.Read(ref _queueRecords) >= _options.QueueRecords || size > _options.QueueBytes - Interlocked.Read(ref _queueBytes))
                {
                    Interlocked.Increment(ref _queueRejected);
                    return false;
                }
                Interlocked.Increment(ref _queueRecords);
                Interlocked.Add(ref _queueBytes, size);
                _logicalBytes += size;
                if (!_queue.Writer.TryWrite(new Offer(artifactId, owned!, size, Stopwatch.GetTimestamp())))
                {
                    Interlocked.Decrement(ref _queueRecords);
                    Interlocked.Add(ref _queueBytes, -size);
                    _logicalBytes -= size;
                    Interlocked.Increment(ref _queueRejected);
                    return false;
                }
                Interlocked.Increment(ref _accepted);
                return true;
            }
            finally { Monitor.Exit(_gate); }
        }
        finally { Interlocked.Decrement(ref _offersInFlight); }
    }

    /// <summary>
    /// Stores one bounded, explicitly versioned UTF-8 JSON compatibility snapshot per artifact.
    /// This does not replace independently queryable occurrence records.
    /// </summary>
    public void SetSnapshot(string artifactId, int version, ReadOnlyMemory<byte> utf8Json)
    {
        lock (_gate)
        {
            EnsureActive();
            try { SetSnapshotCore(artifactId, version, utf8Json); }
            catch (CaptureStoreException)
            {
                Interlocked.Increment(ref _snapshotRejected);
                throw;
            }
        }
    }

    private void SetSnapshotCore(string artifactId, int version, ReadOnlyMemory<byte> utf8Json)
    {
        EnsureActive();
        if (version < 1 || utf8Json.Length > _options.MaxSnapshotBytes)
            throw CapturePackage.Error(CaptureErrorCode.CapacityExceeded, "Snapshot version or byte bound is invalid.");
        try
        {
            using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Snapshot must be valid UTF-8 JSON.", ex);
        }
        lock (_gate)
        {
            EnsureActive();
            if (!_artifacts.TryGetValue(artifactId, out var artifact))
                throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Unknown artifact ID.");
            if (_snapshots.ContainsKey(artifactId))
                throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "A snapshot can only be set once per artifact.");
            // All retained snapshots share a single per-capture byte budget, not MaxArtifacts times that budget.
            var retained = _snapshots.Values.Sum(static s => (long)s.Utf8Json.Length);
            if (utf8Json.Length > _options.MaxSnapshotBytes - retained || utf8Json.Length > _options.MaxLogicalBytes - _logicalBytes)
                throw CapturePackage.Error(CaptureErrorCode.CapacityExceeded, "Capture snapshot or logical byte budget exhausted.");
            _snapshots.Add(artifactId, new CaptureSnapshot(version, utf8Json.ToArray(), artifact.Kind));
            _logicalBytes += utf8Json.Length;
        }
    }

    public void SetSourceRejected(long? count)
    {
        if (count < 0) throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Source rejection count cannot be negative.");
        lock (_gate) { EnsureActive(); _sourceRejected = count; }
    }

    public CaptureWriterMetrics GetMetrics()
    {
        lock (_gate)
        {
            var settings = Volatile.Read(ref _settings);
            return new(GetQuality(Volatile.Read(ref _interrupted) != 0), _logicalBytes, Interlocked.Read(ref _queueBytes),
                Volatile.Read(ref _queueRecords), Interlocked.Read(ref _transactions), Volatile.Read(ref _largestBatch), Interlocked.Read(ref _observedBytes),
                settings?.JournalMode, settings?.Synchronous, settings?.PageLimit);
        }
    }

    public Task<CaptureInfo> CompleteAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycle) return _shutdown ??= StopAsync(seal: true, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Task<CaptureInfo> task;
        lock (_lifecycle) task = _shutdown ??= StopAsync(seal: false, CancellationToken.None);
        await task.ConfigureAwait(false);
    }

    internal void SetRecovery(string source, IReadOnlyDictionary<string, string> hashes)
    {
        lock (_gate)
        {
            _unknownTail = true;
            _manifest = _manifest with { Info = _manifest.Info with { DerivedFrom = source, SourceHashes = hashes } };
        }
    }

    internal async Task AppendRecoveryAsync(string artifactId, CaptureRecord record, CancellationToken cancellationToken)
    {
        if (!TryOwn(record, out var owned, out var size) || size > _options.QueueBytes)
            throw CapturePackage.Error(CaptureErrorCode.CapacityExceeded, "Committed source record cannot fit the configured recovery bounds.");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                EnsureActive();
                if (_failure is not null) throw CapturePackage.Translate(_failure);
                if (size > _options.MaxLogicalBytes - _logicalBytes)
                    throw CapturePackage.Error(CaptureErrorCode.CapacityExceeded, "Recovered evidence exceeds the configured logical budget.");
                if (Volatile.Read(ref _queueRecords) < _options.QueueRecords && size <= _options.QueueBytes - Interlocked.Read(ref _queueBytes))
                {
                    Interlocked.Increment(ref _queueRecords);
                    Interlocked.Add(ref _queueBytes, size);
                    _logicalBytes += size;
                    if (!_queue.Writer.TryWrite(new Offer(artifactId, owned!, size, Stopwatch.GetTimestamp())))
                        throw CapturePackage.Error(CaptureErrorCode.StorageFailure, "Recovery queue closed unexpectedly.");
                    Interlocked.Increment(ref _offered);
                    Interlocked.Increment(ref _accepted);
                    return;
                }
            }
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private void EnsureActive()
    {
        if (Volatile.Read(ref _stopping) != 0)
            throw CapturePackage.Error(CaptureErrorCode.Closed, "Capture writer is already stopping or closed.");
    }

    private bool TryOwn(CaptureRecord? record, out CaptureRecord? owned, out long size)
    {
        owned = null;
        size = 128;
        if (record is null || record.Fields?.Count > _options.MaxFields ||
            record.NumericValue is { } numeric && !double.IsFinite(numeric) || record.DurationNanoseconds < 0)
            return false;
        try
        {
            if (!CountString(record.Category, ref size) || !CountString(record.Name, ref size) ||
                !CountString(record.Unit, ref size)) return false;
            var fields = new CaptureField[record.Fields?.Count ?? 0];
            for (var i = 0; i < fields.Length; i++)
            {
                var field = record.Fields![i];
                if (field is null || field.Name is null || !Enum.IsDefined(field.Kind) ||
                    (field.Kind == CaptureFieldKind.Text) != (field.StringValue is not null) ||
                    (field.Kind == CaptureFieldKind.SignedInteger) != field.Int64Value.HasValue ||
                    (field.Kind == CaptureFieldKind.FloatingPoint) != field.DoubleValue.HasValue ||
                    (field.Kind == CaptureFieldKind.Boolean) != field.BooleanValue.HasValue ||
                    field.DoubleValue is { } value && !double.IsFinite(value))
                    return false;
                size += 64;
                if (!CountString(field.Name, ref size) || !CountString(field.StringValue, ref size) ||
                    !CountString(field.Unit, ref size)) return false;
                fields[i] = field;
            }
            if (size > _options.MaxRecordBytes) return false;
            owned = record with { Fields = fields };
            return true;
        }
        catch (EncoderFallbackException) { return false; }
    }

    private bool CountString(string? text, ref long size)
    {
        if (text is null) return size <= _options.MaxRecordBytes;
        if (text.Length > _options.MaxRecordBytes) return false;
        size += 24L + text.Length * 2L + CapturePackage.Utf8.GetByteCount(text);
        return size <= _options.MaxRecordBytes;
    }

    private CaptureQuality GetQuality(bool interrupted)
    {
        var offered = Interlocked.Read(ref _offered);
        var persisted = Interlocked.Read(ref _persisted);
        var record = Interlocked.Read(ref _recordRejected);
        var queue = Interlocked.Read(ref _queueRejected);
        var storage = Interlocked.Read(ref _storageRejected);
        return new(offered, Interlocked.Read(ref _accepted), persisted, record, queue, storage,
            offered - persisted - record - queue - storage, _sourceRejected, interrupted, _unknownTail,
            Interlocked.Read(ref _snapshotRejected));
    }

    private async Task<CaptureInfo> StopAsync(bool seal, CancellationToken cancellationToken)
    {
        lock (_gate) Interlocked.Exchange(ref _stopping, 1);
        using var registration = cancellationToken.Register(() => Interlocked.Exchange(ref _abort, 1));
        try
        {
            while (Volatile.Read(ref _offersInFlight) != 0) await Task.Yield();
            _queue.Writer.TryComplete();
            await _worker.ConfigureAwait(false);
            if (_failure is not null) throw CapturePackage.Translate(_failure);
            cancellationToken.ThrowIfCancellationRequested();
            if (seal && Volatile.Read(ref _abort) == 0)
            {
                using (var connection = CapturePackage.Connect(_directory, immutable: false))
                {
                    CapturePackage.Execute(connection, "PRAGMA synchronous=FULL; PRAGMA wal_checkpoint(TRUNCATE);");
                    CapturePackage.ValidateDatabase(connection, CapturePackage.CurrentFormat);
                }
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                    _manifest = _manifest with { Info = _manifest.Info with { State = CaptureState.Sealed, Quality = GetQuality(false) } };
                CapturePackage.WriteJson(_directory, CapturePackage.Manifest, _manifest);
                var packageSeal = new CaptureSeal(CapturePackage.Hash(Path.Combine(_directory, CapturePackage.Manifest)),
                    CapturePackage.Hash(Path.Combine(_directory, CapturePackage.Database)));
                cancellationToken.ThrowIfCancellationRequested();
                CapturePackage.WriteJson(_directory, CapturePackage.Seal, packageSeal);
            }
            else
            {
                WriteInterruption();
            }
            return _manifest.Info;
        }
        catch (Exception ex)
        {
            try { WriteInterruption(); }
            catch (Exception metadataError) when (metadataError is IOException or UnauthorizedAccessException or CaptureStoreException)
            {
                throw CapturePackage.Error(CaptureErrorCode.StorageFailure,
                    "Capture failed and interruption metadata could not be persisted; the missing seal still denotes interruption.",
                    new AggregateException(ex, metadataError));
            }
            if (ex is OperationCanceledException) throw;
            throw CapturePackage.Translate(ex);
        }
        finally
        {
            lock (_gate) _snapshots.Clear();
            _lease.Dispose();
            _slot.Dispose();
        }
    }

    private void WriteInterruption()
    {
        lock (_gate)
        {
            Volatile.Write(ref _interrupted, 1);
            _unknownTail = true;
            _manifest = _manifest with { Info = _manifest.Info with
            {
                State = CaptureState.Interrupted, Quality = GetQuality(true)
            } };
        }
        CapturePackage.WriteJson(_directory, CapturePackage.Manifest, _manifest);
    }

    private async Task RunAsync()
    {
        var batch = new List<Offer>(_options.BatchRecords);
        try
        {
            using var connection = CapturePackage.Connect(_directory, immutable: false);
            CapturePackage.Execute(connection, "PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=256;");
            CapturePackage.Execute(connection, "PRAGMA max_page_count=" +
                (_options.MaxDatabaseBytes / 4096).ToString(System.Globalization.CultureInfo.InvariantCulture) + ";");
            using (var pragmas = connection.CreateCommand())
            {
                pragmas.CommandText = "PRAGMA journal_mode;";
                var journal = (string)pragmas.ExecuteScalar()!;
                pragmas.CommandText = "PRAGMA synchronous;";
                var synchronous = (long)pragmas.ExecuteScalar()!;
                pragmas.CommandText = "PRAGMA max_page_count;";
                var pageLimit = (long)pragmas.ExecuteScalar()!;
                Volatile.Write(ref _settings, new(journal, synchronous, pageLimit));
                if (journal != "wal" || synchronous != 2 || pageLimit != _options.MaxDatabaseBytes / 4096)
                    throw CapturePackage.Error(CaptureErrorCode.StorageFailure, "SQLite did not accept the required WAL/FULL/page-limit configuration.");
            }
            using var inserts = new CaptureInserts(connection, _options);
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                while (_queue.Reader.TryRead(out var offer))
                {
                    batch.Add(offer);
                    if (batch.Count == _options.BatchRecords) break;
                }
                // Fill immediately available work first. Age is tested only after that drain.
                if (batch.Count < _options.BatchRecords && !_queue.Reader.Completion.IsCompleted &&
                    Stopwatch.GetElapsedTime(batch[0].OfferedAt) < _options.MaxBatchAge)
                {
                    var delay = _options.MaxBatchAge - Stopwatch.GetElapsedTime(batch[0].OfferedAt);
                    if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
                    while (batch.Count < _options.BatchRecords && _queue.Reader.TryRead(out var offer)) batch.Add(offer);
                }
                if (Volatile.Read(ref _abort) != 0)
                    throw CapturePackage.Error(CaptureErrorCode.Incomplete, "Capture was cancelled or admission metadata failed.");
                using (var transaction = connection.BeginTransaction())
                {
                    foreach (var offer in batch)
                    {
                        var artifact = _artifacts[offer.ArtifactId];
                        inserts.Artifact(artifact, transaction);
                        inserts.Record(offer.ArtifactId, offer.Record, transaction);
                    }
                    transaction.Commit();
                }
                Interlocked.Add(ref _persisted, batch.Count);
                Interlocked.Increment(ref _transactions);
                Volatile.Write(ref _largestBatch, Math.Max(_largestBatch, batch.Count));
                ReleaseBatch(batch);
                batch.Clear();
                ObservePackage();
            }
            if (Volatile.Read(ref _abort) != 0)
                throw CapturePackage.Error(CaptureErrorCode.Incomplete, "Capture completion was cancelled.");
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var artifact in _artifacts.Values) inserts.Artifact(artifact, transaction);
                foreach (var snapshot in _snapshots) inserts.Snapshot(snapshot.Key, snapshot.Value, transaction);
                transaction.Commit();
            }
            ObservePackage();
        }
        catch (Exception ex)
        {
            _failure ??= ex;
            Interlocked.Exchange(ref _abort, 1);
            // Stop accepting but keep consuming all already admitted populations.
            Interlocked.Add(ref _storageRejected, batch.Count);
            ReleaseBatch(batch);
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var offer))
                {
                    Interlocked.Increment(ref _storageRejected);
                    Interlocked.Decrement(ref _queueRecords);
                    Interlocked.Add(ref _queueBytes, -offer.Bytes);
                }
            }
        }
    }

    private void ReleaseBatch(List<Offer> batch)
    {
        Interlocked.Add(ref _queueRecords, -batch.Count);
        foreach (var offer in batch) Interlocked.Add(ref _queueBytes, -offer.Bytes);
    }

    private void ObservePackage()
    {
        var bytes = CapturePackage.PackageBytes(_directory);
        Interlocked.Exchange(ref _observedBytes, bytes);
        if (bytes > _options.MaxPackageBytes)
            throw CapturePackage.Error(CaptureErrorCode.CapacityExceeded,
                "Observed package bytes exceeded the configured monitor threshold; this is not instantaneous physical containment.");
    }
}
