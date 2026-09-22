using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.AppendFirst;

using DotnetDiagnostics.Core.Tests.DurableCounterSpike;

/// <summary>
/// Identity constants for candidate B (append-first). Only configuration
/// profile "P1" (the frozen durability profile from the accepted comparison
/// protocol) is understood; anything else is rejected rather than silently
/// coerced.
/// </summary>
internal static class DurableAppendFirstIdentity
{
    internal const string AdapterId = "B";
    internal const string AdapterVersion = "durable-append-first/2";
    internal const string ConfigurationSchema = "durable-append-first-config/1";
    internal const string RequiredProfile = "P1";

    internal const string CommitAcknowledgementDescription =
        "One FileStream.Flush(flushToDisk: true) per completed batch, issued immediately " +
        "after the batch's commit-footer bytes are written and before CommitAsync returns " +
        "Committed. Never a per-record fsync. A fault reached strictly after that flush can " +
        "only interrupt the acknowledgement path (reported Unknown), never the already-" +
        "durable batch (never reported Failed). The derived SQLite query index is not " +
        "written during ingestion at all: it is built entirely from the sealed, checksum-" +
        "reverified canonical log inside FinalizePreSealAsync, before the seal is published.";
}

/// <summary>Thrown when a write would exceed the frozen combined package byte ceiling.</summary>
internal sealed class DurableAppendFirstCapacityExceededException : Exception
{
    internal DurableAppendFirstCapacityExceededException(string message) : base(message)
    {
    }
}

internal sealed record DurableAppendFirstFileOperations(
    Action<FileStream, long>? Resize = null,
    Action<FileStream, bool>? Flush = null);

/// <summary>
/// Tracks canonical + derived bytes against the frozen 256 MiB combined package
/// ceiling for one package (a live capture or one recovery output). Canonical
/// growth is reserved exactly, before each write, since the writer fully controls
/// that byte count. The derived SQLite index's on-disk growth is only observable
/// after SQLite performs its own page allocation; it is checked at the end of
/// index construction or recovery, not after every transaction or before growth. This asymmetry is
/// a known, explicit limitation, not a claim of byte-exact pre-reservation on the
/// SQLite side.
/// </summary>
internal sealed class DurableAppendFirstCapacityGuard
{
    internal const long CapBytes = 268_435_456;

    private readonly object _gate = new();
    private long _canonicalReserved;
    private long _observedQueryBytes;

    internal void ReserveCanonicalGrowth(long bytes)
    {
        lock (_gate)
        {
            if (_canonicalReserved + bytes + _observedQueryBytes > CapBytes)
            {
                throw new DurableAppendFirstCapacityExceededException(
                    "Reserving canonical growth would exceed the frozen 256 MiB combined package byte cap.");
            }
            _canonicalReserved += bytes;
        }
    }

    internal void ReleaseCanonicalGrowth(long bytes)
    {
        lock (_gate)
        {
            _canonicalReserved -= bytes;
        }
    }

    /// <summary>Best-effort post-write check of the derived index's observed file size.</summary>
    internal void ObserveQueryBytes(long queryFileBytes)
    {
        lock (_gate)
        {
            _observedQueryBytes = queryFileBytes;
            if (_canonicalReserved + _observedQueryBytes > CapBytes)
            {
                throw new DurableAppendFirstCapacityExceededException(
                    "Observed derived-index growth pushed the combined package bytes past the frozen 256 MiB cap.");
            }
        }
    }

    internal long CanonicalReserved
    {
        get { lock (_gate) { return _canonicalReserved; } }
    }
}

/// <summary>
/// F1's actual injection seam: the instrumented write attempt for the first
/// segment of a new batch. A fault fires here, inside the write call itself
/// (never in a callback unrelated to the real canonical stream), and is always
/// surfaced as a declared storage-full <see cref="IOException"/>, except cancellation. Nothing for the
/// batch reaches the underlying stream when it fires.
/// </summary>
internal sealed class DurableAppendFirstInstrumentedCanonicalWriter
{
    private readonly FileStream _stream;
    private readonly IDurableStorageFaultController _faults;
    private readonly DurableAppendFirstFileOperations? _operations;

    internal DurableAppendFirstInstrumentedCanonicalWriter(
        FileStream stream, IDurableStorageFaultController faults, DurableAppendFirstFileOperations? operations)
    {
        _stream = stream;
        _faults = faults;
        _operations = operations;
    }

    internal long Position => _stream.Position;

    internal void SetLength(long length)
    {
        if (_operations?.Resize is { } resize) resize(_stream, length);
        else _stream.SetLength(length);
    }

    internal void Flush(bool flushToDisk)
    {
        if (_operations?.Flush is { } flush) flush(_stream, flushToDisk);
        else _stream.Flush(flushToDisk);
    }

    /// <summary>
    /// Reaches the storage-full fault barrier as part of attempting the first
    /// write of a new batch's frame bytes, then performs that write only if the
    /// barrier did not fault. This is "the write stream" throwing, not a
    /// side-channel hook: a fired fault is wrapped as <see cref="IOException"/>
    /// (declared storage-full) unless it already is one.
    /// </summary>
    internal async ValueTask WriteNextBatchFirstSegmentAsync(
        ReadOnlyMemory<byte> data, int batchOrdinal, IReadOnlyList<long> sequences, CancellationToken cancellationToken)
    {
        try
        {
            await _faults.ReachAsync(
                new DurableStorageFaultContext(DurableStorageFaultBarrier.StorageFullNextBatch, batchOrdinal, sequences, null),
                cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception fault)
        {
            throw new IOException(
                "Instrumented storage-full fault fired at the canonical write stream before completing this batch.",
                fault);
        }
        await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        => _stream.WriteAsync(data, cancellationToken);

    internal ValueTask DisposeAsync() => _stream.DisposeAsync();
}

/// <summary>
/// Candidate B (append-first) storage adapter. During acquisition it only ever
/// appends bounded, checksummed, framed batches to a canonical append-only file --
/// it never touches the derived SQLite index. The index is built entirely inside
/// <see cref="FinalizePreSealAsync"/>, streamed in bounded batches from a fresh,
/// checksum-reverifying re-read of the sealed canonical log, and only published if
/// that re-read is fully clean and reconciles exactly against what ingestion
/// counted as committed. <see cref="Reader"/> is unavailable during acquisition and
/// only becomes usable after that finalization succeeds, at which point it carries
/// the exact terminal quality passed to <see cref="FinalizePreSealAsync"/>.
/// </summary>
internal sealed class DurableAppendFirstStorageAdapter : IDurableCounterStorageAdapter
{
    private readonly string _canonicalPath;
    private readonly string _queryPath;
    private readonly DurableCounterPipelineLimits _limits;
    private readonly IDurableStorageFaultController _faults;
    private readonly DurableAppendFirstCapacityGuard _capacity = new();
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly DurableAppendFirstInstrumentedCanonicalWriter _canonicalWriter;
    private readonly string _configurationDigest;

    private int _batchOrdinal;
    private long? _lastCommittedSequence;
    private long _totalCommittedRecords;
    private bool _sealed;
    private bool _sealFailed;
    private bool _writeFailed;
    private bool _disposed;
    private DurableAppendFirstQueryIndex? _reader;

    internal DurableAppendFirstStorageAdapter(
        string stagingRoot,
        DurableCounterPipelineLimits limits,
        IDurableStorageFaultController faults,
        string configurationDigest,
        DurableAppendFirstFileOperations? fileOperations = null)
    {
        _limits = limits;
        _faults = faults;
        _configurationDigest = configurationDigest;

        var canonicalDirectory = Path.Combine(stagingRoot, DurableStoragePackageLayout.CanonicalDirectory);
        var queryDirectory = Path.Combine(stagingRoot, DurableStoragePackageLayout.QueryDirectory);
        Directory.CreateDirectory(canonicalDirectory);
        Directory.CreateDirectory(queryDirectory);
        _canonicalPath = Path.Combine(canonicalDirectory, "records.bin");
        _queryPath = Path.Combine(queryDirectory, "index.db");

        var stream = new FileStream(
            _canonicalPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, bufferSize: 4096, useAsync: true);
        _canonicalWriter = new DurableAppendFirstInstrumentedCanonicalWriter(stream, faults, fileOperations);
    }

    public DurableStorageAdapterIdentity Identity { get; } = new(
        DurableAppendFirstIdentity.AdapterId,
        DurableAppendFirstIdentity.AdapterVersion,
        DurableAppendFirstIdentity.ConfigurationSchema,
        DurableAppendFirstIdentity.CommitAcknowledgementDescription);

    /// <summary>
    /// Adapter-owned; borrowed by the caller. Unavailable during acquisition (throws);
    /// becomes usable only after <see cref="FinalizePreSealAsync"/> completes successfully.
    /// Only the adapter disposes it, in <see cref="DisposeAsync"/>.
    /// </summary>
    public IDurableCounterReadonlyStore Reader
        => _reader ?? throw new InvalidOperationException(
            "Adapter-owned Reader access is unavailable during acquisition; it becomes usable only after FinalizePreSealAsync completes.");

    public async ValueTask<DurableCounterCommitResult> CommitAsync(
        IReadOnlyList<DurableCounterSinkRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return new DurableCounterCommitResult(DurableCounterCommitOutcome.Committed);
        }
        if (records.Count > DurableStorageFrameContract.MaximumRecordsPerBatch)
        {
            throw new DurableStorageExperimentException(
                "BatchRecordsLimit", "A batch exceeds the frozen maximum records-per-batch limit.");
        }

        // One writer, no concurrent analysis: batches are committed strictly one at a time.
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sealed || _writeFailed)
            {
                throw new InvalidOperationException("This adapter has already been finalized for sealing.");
            }
            return await CommitBatchAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    private async ValueTask<DurableCounterCommitResult> CommitBatchAsync(
        IReadOnlyList<DurableCounterSinkRecord> records, CancellationToken cancellationToken)
    {
        var ordinal = ++_batchOrdinal;

        // Owned buffers are only valid until this call returns: copy synchronously, before
        // any await, and never retain the caller's ReadOnlyMemory beyond this point.
        var owned = new (long Sequence, byte[] Payload)[records.Count];
        for (var i = 0; i < records.Count; i++)
        {
            owned[i] = (records[i].Record.Sequence, records[i].Encoded.ToArray());
        }

        var sequences = Array.ConvertAll(owned, item => item.Sequence);

        // Cross-frame sequence watermark: reject a duplicate/out-of-order batch before
        // any byte of it is written. A regression accepted into the durable log would
        // otherwise only surface later as an opaque SQLite primary-key failure during
        // index build; this rejects it immediately and explicitly instead.
        if (_lastCommittedSequence.HasValue && owned[0].Sequence <= _lastCommittedSequence.Value)
        {
            throw new DurableStorageExperimentException(
                "SequenceRegression",
                $"Batch {ordinal}'s first sequence {owned[0].Sequence} does not exceed the last committed sequence {_lastCommittedSequence.Value}.");
        }

        byte[] frame = DurableAppendFirstFrame.Encode(owned);

        _capacity.ReserveCanonicalGrowth(frame.Length);
        var frameStartOffset = _canonicalWriter.Position;
        var (beforeFooter, footer) = DurableAppendFirstFrame.SplitFooter(frame);

        try
        {
            // F1: the instrumented write stream's own seam, reached as part of the
            // very first write attempt for this batch. Nothing for this batch has
            // touched disk if this faults.
            await _canonicalWriter.WriteNextBatchFirstSegmentAsync(beforeFooter, ordinal, sequences, cancellationToken)
                .ConfigureAwait(false);

            // F2: BeforeCommit is reached strictly before the commit-footer bytes exist on
            // disk. A fault here leaves the batch pre-commit; it must never appear as
            // recovered, so any exception rolls the file back to the last known-good frame
            // boundary before propagating.
            // (Barrier reached by the writer's caller via the shared fault controller.)
            await ReachBeforeCommitAsync(ordinal, sequences, cancellationToken).ConfigureAwait(false);

            await _canonicalWriter.WriteAsync(footer, cancellationToken).ConfigureAwait(false);
            _canonicalWriter.Flush(flushToDisk: true);
        }
        catch (Exception writeFailure) when (writeFailure is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            try
            {
                _canonicalWriter.SetLength(frameStartOffset);
                _canonicalWriter.Flush(flushToDisk: true);
            }
            catch (Exception rollbackFailure) when (rollbackFailure is not OutOfMemoryException
                and not StackOverflowException and not AccessViolationException)
            {
                _writeFailed = true;
                return new DurableCounterCommitResult(DurableCounterCommitOutcome.Unknown,
                    $"CanonicalRollbackUncertain:{writeFailure.GetType().Name}:{rollbackFailure.GetType().Name}");
            }
            _capacity.ReleaseCanonicalGrowth(frame.Length);
            throw;
        }

        // Past this point the batch is durably committed: the commit-footer bytes and the
        // checksum that guards them are flushed to disk. Ingestion never touches the
        // derived index; only the watermark/count bookkeeping below is updated.
        _lastCommittedSequence = owned[^1].Sequence;
        _totalCommittedRecords += owned.Length;

        try
        {
            await _faults.ReachAsync(
                new DurableStorageFaultContext(
                    DurableStorageFaultBarrier.AfterCommitBeforeAcknowledgement,
                    ordinal,
                    sequences,
                    DurableCounterCommitOutcome.Committed),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The batch is durable; only the acknowledgement path was interrupted. This can
            // never be reported as Failed -- it is explicitly Unknown.
            return new DurableCounterCommitResult(
                DurableCounterCommitOutcome.Unknown,
                $"Batch {ordinal} is durably committed; acknowledgement was interrupted: {exception.Message}");
        }

        return new DurableCounterCommitResult(DurableCounterCommitOutcome.Committed);
    }

    private ValueTask ReachBeforeCommitAsync(int ordinal, IReadOnlyList<long> sequences, CancellationToken cancellationToken)
        => _faults.ReachAsync(
            new DurableStorageFaultContext(DurableStorageFaultBarrier.BeforeCommit, ordinal, sequences, null),
            cancellationToken);

    public async ValueTask<DurableStoragePreSealResult> FinalizePreSealAsync(
        DurableCounterQualityReport finalPipelineQuality, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finalPipelineQuality);
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sealed || _sealFailed)
            {
                throw new InvalidOperationException(
                    "This adapter has already been finalized for sealing (or a prior finalization attempt failed terminally).");
            }
            if (_writeFailed)
            {
                throw new DurableStorageExperimentException("RecoveryRequired", "An uncertain canonical write requires explicit recovery.");
            }
            _sealed = true;

            // Close the write-mode canonical stream: it is already fully flushed by
            // CommitAsync, one Flush(true) per completed batch.
            await _canonicalWriter.DisposeAsync().ConfigureAwait(false);

            DurableAppendFirstQueryIndex? index = null;
            try
            {
                index = DurableAppendFirstQueryIndex.CreateWritable(_queryPath, _limits);
                var (recordCount, lastSequence) = await BuildIndexFromCanonicalLogAsync(index, cancellationToken)
                    .ConfigureAwait(false);

                // Reconcile the freshly re-scanned, checksum-reverified canonical log
                // against what ingestion itself counted as committed. This is a
                // stronger check than count+max alone would be: every frame's checksum
                // was independently reverified during the scan (BuildIndexFromCanonicalLogAsync
                // rejects on the first Truncated/Corrupt boundary), so a substituted or
                // corrupted frame cannot silently pass this reconciliation.
                if (recordCount != _totalCommittedRecords
                    || (_lastCommittedSequence.HasValue && lastSequence != _lastCommittedSequence.Value)
                    || (!_lastCommittedSequence.HasValue && recordCount != 0))
                {
                    throw new DurableStorageExperimentException(
                        "IndexCanonicalReconciliationMismatch",
                        $"Rebuilt index has {recordCount} record(s) ending at sequence {(lastSequence?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a")}, " +
                        $"but ingestion committed {_totalCommittedRecords} record(s) ending at {(_lastCommittedSequence?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a")}.");
                }

                var terminalQuality = new DurableStorageQualityReport(
                    finalPipelineQuality, RetainedRecords: index.CountRows(), VolatileTailUnknown: false);
                DurableStorageQualityRules.Validate(terminalQuality);

                index.SealForLiveReading(terminalQuality);

                var canonicalMember = BuildMember(
                    _canonicalPath,
                    DurableStoragePackageLayout.CanonicalMember(Path.GetFileName(_canonicalPath)),
                    DurableStorageMemberRole.CanonicalData);
                var queryMember = BuildMember(
                    _queryPath,
                    DurableStoragePackageLayout.QueryMember(Path.GetFileName(_queryPath)),
                    DurableStorageMemberRole.QueryIndex);
                var finalBytes = canonicalMember.Length + queryMember.Length;

                var result = new DurableStoragePreSealResult(
                    [canonicalMember], [queryMember], finalBytes, _configurationDigest);
                DurableStorageSealRules.ValidatePreSeal(result, _limits);
                _reader = index;
                index = null;
                return result;
            }
            catch
            {
                // Index build/reconciliation failure must never publish a seal, and must
                // never rewrite the already-committed admission outcomes on the canonical
                // log: only the half-built derived index (if any) is discarded.
                _sealFailed = true;
                if (index is not null)
                {
                    try
                    {
                        await index.DisposeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        TryDelete(_queryPath);
                    }
                }
                throw;
            }
        }
        finally
        {
            _writerGate.Release();
        }
    }

    /// <summary>
    /// Streams the sealed canonical log from the start in bounded per-frame batches,
    /// re-verifying every frame's checksum and footer as it goes (via
    /// <see cref="DurableAppendFirstFrame.ReadNext"/>), inserting each valid batch into
    /// the derived index as it is read. Stops -- and throws -- at the first
    /// Truncated or Corrupt boundary: an ingestion-sealed log is expected to be
    /// entirely clean, so anything else found here is an index-build failure, not a
    /// tolerated tail.
    /// </summary>
    private async Task<(long RecordCount, long? LastSequence)> BuildIndexFromCanonicalLogAsync(
        DurableAppendFirstQueryIndex index, CancellationToken cancellationToken)
    {
        long recordCount = 0;
        long? lastSequence = null;
        var samples = new Dictionary<(string Provider, string Name), DurableCounterRecord>();
        using var source = new FileStream(_canonicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scan = DurableAppendFirstFrame.ReadNext(source);
            if (scan.Outcome == DurableAppendFirstFrameOutcome.CleanEnd)
            {
                break;
            }
            if (scan.Outcome is DurableAppendFirstFrameOutcome.Truncated or DurableAppendFirstFrameOutcome.Corrupt)
            {
                throw new DurableStorageExperimentException(
                    "IndexBuildSourceNotClean",
                    $"The sealed canonical log is not entirely clean at byte offset {scan.StartPosition} " +
                    $"({scan.Outcome}: {scan.Reason}). Index build failure must not publish a seal.");
            }

            var decoded = scan.Records!.Select(item => DurableAppendFirstRecordCodec.Decode(item.Payload)).ToArray();
            index.InsertBatch(decoded);
            foreach (var record in decoded)
            {
                var key = (record.Provider, record.Name);
                if (!samples.ContainsKey(key))
                {
                    if (samples.Count >= _limits.DistinctKeys)
                    {
                        throw new DurableStorageExperimentException("DistinctKeysLimit", "Canonical keys exceed the shared limit.");
                    }
                    samples.Add(key, record);
                }
            }
            recordCount += decoded.Length;
            lastSequence = decoded[^1].Sequence;
        }
        _capacity.ObserveQueryBytes(SafeFileLength(_queryPath));
        foreach (var sample in samples.Values)
        {
            index.VerifyRecord(sample);
        }
        return (recordCount, lastSequence);
    }

    public async ValueTask FinalizeAsync(CancellationToken cancellationToken)
    {
        // The shared pipeline calls this once admission is fully drained. Candidate B has
        // no separate ingest-side flush to perform here: every batch is already durably
        // flushed by the time CommitAsync returns Committed. Sealing (index build,
        // manifest, hashing) happens explicitly and only via FinalizePreSealAsync.
        await ValueTask.CompletedTask;
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
        else if (!_sealed)
        {
            await _canonicalWriter.DisposeAsync().ConfigureAwait(false);
        }
        _writerGate.Dispose();
    }

    private static DurableStorageMember BuildMember(string path, string relativePath, DurableStorageMemberRole role)
    {
        var length = new FileInfo(path).Length;
        var hash = ComputeSha256(path);
        return new DurableStorageMember(relativePath, length, hash, role);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static long SafeFileLength(string path)
        => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

/// <summary>Factory for candidate B. Fresh open/recovery never creates a writer.</summary>
internal sealed class DurableAppendFirstStorageAdapterFactory : IDurableCounterStorageAdapterFactory
{
    private readonly Action<int>? _recoveryFrameCommitted;
    private readonly DurableAppendFirstFileOperations? _fileOperations;

    internal DurableAppendFirstStorageAdapterFactory(
        Action<int>? recoveryFrameCommitted = null, DurableAppendFirstFileOperations? fileOperations = null)
    {
        _recoveryFrameCommitted = recoveryFrameCommitted;
        _fileOperations = fileOperations;
    }

    public DurableStorageAdapterIdentity Identity { get; } = new(
        DurableAppendFirstIdentity.AdapterId,
        DurableAppendFirstIdentity.AdapterVersion,
        DurableAppendFirstIdentity.ConfigurationSchema,
        DurableAppendFirstIdentity.CommitAcknowledgementDescription);

    public IDurableCounterStorageAdapter Create(DurableStorageAdapterCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var digest = ValidateAndDigestConfiguration(request.Configuration);
        return new DurableAppendFirstStorageAdapter(request.StagingRoot, request.Limits, request.Faults, digest, _fileOperations);
    }

    public IDurableCounterReadonlyStore OpenReadonly(DurableStorageOpenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var manifest = request.Manifest;
        if (manifest.Adapter != Identity)
        {
            throw new DurableStorageExperimentException("AdapterIdentityMismatch", "The package does not identify this version of candidate B.");
        }
        if (manifest.ContractVersion != DurableStorageExperimentVersions.PackageContract
            || manifest.RecordSchemaVersion != DurableStorageExperimentVersions.RecordSchema)
        {
            throw new DurableStorageExperimentException("PackageSchemaMismatch", "The package contract or record schema is unsupported.");
        }
        var queryMember = manifest.Members.FirstOrDefault(member => member.Role == DurableStorageMemberRole.QueryIndex)
            ?? throw new DurableStorageExperimentException("MissingQueryMember", "The sealed manifest has no query index member.");
        var queryPath = Path.Combine(request.PackageRoot, queryMember.RelativePath);
        VerifyMember(queryPath, queryMember);

        var canonicalMember = manifest.Members.FirstOrDefault(member => member.Role == DurableStorageMemberRole.CanonicalData)
            ?? throw new DurableStorageExperimentException("MissingCanonicalMember", "The sealed manifest has no canonical member.");
        VerifyMember(Path.Combine(request.PackageRoot, canonicalMember.RelativePath), canonicalMember);

        // Retained records is ground truth from the already-sealed database itself, not a
        // fabricated or re-derived accounting number: a plain read-only COUNT(*) against
        // the immutable file that was already verified byte-for-byte via VerifyMember above.
        var probe = DurableAppendFirstQueryIndex.OpenReadOnly(queryPath, request.Limits);
        long retainedRecords;
        try
        {
            retainedRecords = probe.CountRows();
        }
        finally
        {
            probe.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        var fixedQuality = new DurableStorageQualityReport(manifest.FinalPipelineQuality, retainedRecords, manifest.VolatileTailUnknown);
        DurableStorageQualityRules.Validate(fixedQuality, manifest);

        // Ordinary reopen never rebuilds or mutates anything; it only opens the
        // already-complete, already-sealed derived index strictly read-only.
        return DurableAppendFirstQueryIndex.OpenReadOnly(queryPath, request.Limits, fixedQuality);
    }

    public async ValueTask<DurableStorageRecoveryResult> RecoverAsync(
        DurableStorageRecoveryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DurableStorageRecoveryRules.Validate(request, new DurableStorageRecoveryResult(
            request.NewCaptureId, request.NewArtifactId, request.SourceCaptureId, request.Reason, true, []));
        var sourceRoot = Path.GetFullPath(request.SourcePackageRoot);
        var destinationRoot = Path.GetFullPath(request.RecoveryStagingRoot);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(sourceRoot, destinationRoot, pathComparison)
            || destinationRoot.StartsWith(Path.TrimEndingDirectorySeparator(sourceRoot) + Path.DirectorySeparatorChar, pathComparison)
            || new DirectoryInfo(destinationRoot).LinkTarget is not null
            || (Directory.Exists(destinationRoot) && Directory.EnumerateFileSystemEntries(destinationRoot).Any()))
        {
            throw new DurableStorageExperimentException(
                "InvalidRecoveryStaging", "Recovery requires a new empty staging root outside the source package.");
        }
        var capacity = new DurableAppendFirstCapacityGuard();
        foreach (var path in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            capacity.ReserveCanonicalGrowth(new FileInfo(path).Length);
        }
        var sourceCanonical = Path.Combine(request.SourcePackageRoot, DurableStoragePackageLayout.CanonicalDirectory, "records.bin");
        if (!File.Exists(sourceCanonical))
        {
            throw new DurableStorageExperimentException("MissingCanonicalMember", "The source package has no canonical data to recover.");
        }

        // Recovered output is emitted as an ordinary new package under canonical/ and
        // query/ (CanonicalData/QueryIndex roles), not a candidate-private
        // RecoveryOutput-only shape: a host that builds a manifest around
        // RecoveredMembers must be able to hand that manifest straight to
        // OpenReadonly's existing role-based member lookup without any extra mapping.
        var canonicalDirectory = Path.Combine(request.RecoveryStagingRoot, DurableStoragePackageLayout.CanonicalDirectory);
        var queryDirectory = Path.Combine(request.RecoveryStagingRoot, DurableStoragePackageLayout.QueryDirectory);
        Directory.CreateDirectory(canonicalDirectory);
        Directory.CreateDirectory(queryDirectory);
        var recoveredCanonicalPath = Path.Combine(canonicalDirectory, "records.bin");
        var recoveredIndexPath = Path.Combine(queryDirectory, "index.db");

        FileStream? recoveredCanonical = null;
        DurableAppendFirstQueryIndex? recoveredIndex = null;
        var canonicalCreated = false;
        var indexCreationAttempted = false;
        var succeeded = false;
        try
        {
            recoveredCanonical = new FileStream(
                recoveredCanonicalPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, useAsync: true);
            canonicalCreated = true;
            indexCreationAttempted = true;
            recoveredIndex = DurableAppendFirstQueryIndex.CreateWritable(recoveredIndexPath, new DurableCounterPipelineLimits());

            long? runningLastSequence = null;
            var recoveredRecordCount = 0;

            // Source bytes are opened read-only and never modified: recovery only ever
            // writes to the new recovery staging root.
            using (var source = new FileStream(sourceCanonical, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var startPosition = source.Position;
                    var scan = DurableAppendFirstFrame.ReadNext(source);
                    if (scan.Outcome == DurableAppendFirstFrameOutcome.CleanEnd)
                    {
                        break;
                    }
                    if (scan.Outcome == DurableAppendFirstFrameOutcome.Truncated)
                    {
                        // Structural incompleteness does not establish its physical cause.
                        // Retain only the verified prefix and keep the volatile tail unknown.
                        break;
                    }
                    if (scan.Outcome == DurableAppendFirstFrameOutcome.Corrupt)
                    {
                        // Corruption (bad checksum/footer/magic/membership) is a hard,
                        // explicit recovery failure -- it must never be laundered into a
                        // "verified prefix" result. The shared DurableStorageRecoveryResult
                        // contract has no field to carry this distinction, so it is
                        // surfaced only via this typed exception's message; a common
                        // extension for it (if the host needs a machine-readable
                        // distinction) is a genuine shared-contract gap, not something
                        // this candidate should invent privately.
                        throw new DurableStorageExperimentException(
                            "RecoveryCorruptedSource",
                            $"The source canonical log is corrupt at byte offset {startPosition} ({scan.Reason}). " +
                            "Recovery refuses to publish any result over corrupted source data.");
                    }

                    // scan.Outcome == Valid
                    var firstSequence = scan.Records![0].Sequence;
                    if (runningLastSequence.HasValue && firstSequence <= runningLastSequence.Value)
                    {
                        // A duplicate/out-of-order frame across the frame boundary: reject
                        // with a typed error before inserting anything from this frame,
                        // rather than letting it reach the derived index's primary key and
                        // fail there opaquely.
                        throw new DurableStorageExperimentException(
                            "RecoveredSequenceRegression",
                            $"Frame at byte offset {startPosition} starts at sequence {firstSequence}, " +
                            $"which does not exceed the previously recovered sequence {runningLastSequence.Value}.");
                    }

                    source.Position = startPosition;
                    var frameBytes = new byte[scan.ConsumedBytes];
                    var read = source.Read(frameBytes, 0, frameBytes.Length);
                    if (read != frameBytes.Length)
                    {
                        throw new DurableStorageExperimentException("RecoveryReadFailure", "Failed to re-read an already-validated frame.");
                    }

                    capacity.ReserveCanonicalGrowth(frameBytes.Length);
                    await recoveredCanonical.WriteAsync(frameBytes, cancellationToken).ConfigureAwait(false);

                    var decoded = scan.Records!.Select(item => DurableAppendFirstRecordCodec.Decode(item.Payload)).ToArray();
                    recoveredIndex.InsertBatch(decoded);
                    recoveredRecordCount += decoded.Length;
                    runningLastSequence = scan.Records![^1].Sequence;
                    _recoveryFrameCommitted?.Invoke(recoveredRecordCount);
                }
            }

            recoveredCanonical.Flush(flushToDisk: true);
            await recoveredCanonical.DisposeAsync().ConfigureAwait(false);
            recoveredCanonical = null;
            capacity.ObserveQueryBytes(SafeFileLength(recoveredIndexPath));
            recoveredIndex.CloseForSeal();
            await recoveredIndex.DisposeAsync().ConfigureAwait(false);
            recoveredIndex = null;

            var canonicalMember = new DurableStorageMember(
                DurableStoragePackageLayout.CanonicalMember(Path.GetFileName(recoveredCanonicalPath)),
                new FileInfo(recoveredCanonicalPath).Length,
                ComputeSha256(recoveredCanonicalPath),
                DurableStorageMemberRole.CanonicalData);
            var indexMember = new DurableStorageMember(
                DurableStoragePackageLayout.QueryMember(Path.GetFileName(recoveredIndexPath)),
                new FileInfo(recoveredIndexPath).Length,
                ComputeSha256(recoveredIndexPath),
                DurableStorageMemberRole.QueryIndex);

            var result = new DurableStorageRecoveryResult(
                request.NewCaptureId,
                request.NewArtifactId,
                request.SourceCaptureId,
                request.Reason,
                VolatileTailUnknown: true,
                RecoveredMembers: [canonicalMember, indexMember]);
            DurableStorageRecoveryRules.Validate(request, result);
            succeeded = true;
            return result;
        }
        finally
        {
            // Every path out of this method -- success, cancellation, or any thrown
            // failure -- releases both handles so the caller's staging directory is
            // always deletable afterward.
            try
            {
                if (recoveredCanonical is not null)
                {
                    await recoveredCanonical.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    if (recoveredIndex is not null)
                    {
                        await recoveredIndex.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (!succeeded)
                    {
                        try
                        {
                            if (canonicalCreated) TryDelete(recoveredCanonicalPath);
                        }
                        finally
                        {
                            if (indexCreationAttempted) TryDelete(recoveredIndexPath);
                        }
                    }
                }
            }
        }
    }

    private static string ValidateAndDigestConfiguration(JsonElement configuration)
    {
        if (configuration.ValueKind != JsonValueKind.Object
            || !configuration.TryGetProperty("profile", out var profileElement)
            || profileElement.ValueKind != JsonValueKind.String
            || profileElement.GetString() != DurableAppendFirstIdentity.RequiredProfile)
        {
            throw new DurableStorageExperimentException(
                "UnsupportedConfigurationProfile",
                $"Candidate B only implements configuration profile '{DurableAppendFirstIdentity.RequiredProfile}'.");
        }
        // Configuration count is exactly one recognized key by design (P1-only); extra
        // unknown keys are rejected rather than silently ignored.
        var propertyCount = 0;
        foreach (var _ in configuration.EnumerateObject())
        {
            propertyCount++;
        }
        if (propertyCount != 1)
        {
            throw new DurableStorageExperimentException(
                "UnsupportedConfigurationProfile",
                "Candidate B's P1 configuration accepts exactly one property: 'profile'.");
        }
        var canonicalBytes = Encoding.UTF8.GetBytes($"{{\"profile\":\"{DurableAppendFirstIdentity.RequiredProfile}\"}}");
        return Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
    }

    private static void VerifyMember(string path, DurableStorageMember member)
    {
        if (!File.Exists(path))
        {
            throw new DurableStorageExperimentException("MissingPackageFile", $"Package member '{member.RelativePath}' is missing on disk.");
        }
        var info = new FileInfo(path);
        if (info.Length != member.Length)
        {
            throw new DurableStorageExperimentException("PackageMemberLengthMismatch", $"Package member '{member.RelativePath}' length does not match the manifest.");
        }
        var actualHash = ComputeSha256(path);
        if (!string.Equals(actualHash, member.Sha256, StringComparison.Ordinal))
        {
            throw new DurableStorageExperimentException("PackageMemberHashMismatch", $"Package member '{member.RelativePath}' hash does not match the manifest.");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static long SafeFileLength(string path)
        => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
