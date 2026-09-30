using System.Security.Cryptography;
using DotnetDiagnostics.Core.Artifacts;

namespace DotnetDiagnostics.Core.Captures;

/// <summary>A private byte-stage lease sharing ordinary portable admission and receipts. No filesystem path is exposed.</summary>
public sealed class PortableCaptureTransfer : IAsyncDisposable
{
    private readonly PortableCaptureStorage _storage;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly Func<CancellationToken, ValueTask>? _authorize;
    private readonly Action? _releaseSources;
    private readonly Func<CancellationToken, Task<PortableImportResult>>? _import;
    private PortableImportResult? _result;
    private PortableImportJournal? _pendingTerminal;
    private bool _disposed;
    private bool _closing;
    private bool _committed;

    internal PortableCaptureTransfer(PortableCaptureStorage storage, PortableExportResult export,
        Func<CancellationToken, ValueTask> authorize, Action releaseSources)
    {
        _storage = storage;
        Export = export;
        ArchiveBytes = export.ArchiveBytes;
        ArchiveSha256 = export.ArchiveSha256;
        _authorize = authorize;
        _releaseSources = releaseSources;
    }

    internal PortableCaptureTransfer(PortableCaptureStorage storage, CaptureImportRequest request,
        Func<CancellationToken, Task<PortableImportResult>> import)
    {
        _storage = storage;
        ArchiveBytes = request.ArchiveBytes;
        ArchiveSha256 = request.ArchiveSha256;
        _import = import;
        if (storage.Reused)
        {
            if (storage.Receipt.Import is not { Terminal: true } terminal)
                throw CapturePackage.Error(CaptureErrorCode.StorageFailure, "ImportReceiptNotTerminal: retry requires reconciliation.");
            _result = terminal.Result;
        }
    }

    public PortableExportResult? Export { get; }
    public long ArchiveBytes { get; }
    public string ArchiveSha256 { get; }
    public long ReceivedBytes => _storage.Receipt.Transfer?.ReceivedBytes ?? 0;
    public PortableImportResult? ExistingResult => _result;

    /// <summary>Rechecks current trusted-source policy and reads at most one 64 KiB buffer.</summary>
    public async Task<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            if (Export is null || offset < 0 || offset > ArchiveBytes || destination.Length > PortableBounds.BufferBytes)
                throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Invalid transfer read.");
            await _authorize!(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            using var file = Open(FileAccess.Read);
            file.Position = offset;
            var wanted = (int)Math.Min(destination.Length, ArchiveBytes - offset);
            await file.ReadExactlyAsync(destination[..wanted], cancellationToken).ConfigureAwait(false);
            return wanted;
        }
        finally { _io.Release(); }
    }

    /// <summary>Appends sequential bytes and flushes their accepted offset into the durable receipt.</summary>
    public async Task AppendAsync(long offset, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            if (_import is null || _committed || _closing || _pendingTerminal is not null || _result is not null || bytes.IsEmpty ||
                bytes.Length > PortableBounds.BufferBytes || offset != ReceivedBytes ||
                offset > ArchiveBytes - bytes.Length)
                throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "OffsetMismatch: invalid upload offset or length.");
            using var file = Open(FileAccess.Write);
            file.Position = offset;
            await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            file.Flush(flushToDisk: true);
            _storage.SaveTransfer(upload: true, checked(offset + bytes.Length));
        }
        finally { _io.Release(); }
    }

    /// <summary>Verifies the completed upload and hands the same archive, receipt and slot to isolated import.</summary>
    public async Task<PortableImportResult> CommitAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(600));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        Enter();
        try
        {
            if (_result is not null) return _result;
            if (_import is null || _committed || _closing || _pendingTerminal is not null)
                throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Invalid import transfer.");
            if (ReceivedBytes != ArchiveBytes)
                throw CapturePackage.Error(CaptureErrorCode.Incomplete, "UploadIncomplete: exact declared bytes are required.");
            using (var file = Open(FileAccess.Read))
            {
                if (file.Length != ArchiveBytes ||
                    !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)),
                        ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                {
                    var journal = _storage.Receipt.Import!;
                    var failure = journal.Result with
                    {
                        Failure = new(CaptureErrorCode.CorruptPackage, "Archive.DigestOrLengthMismatch", null, null, null, null)
                    };
                    PersistTerminal(journal with { Terminal = true, Result = failure });
                    throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Archive.DigestOrLengthMismatch");
                }
            }
            _committed = true;
            try { return _result = await _import(token).ConfigureAwait(false); }
            catch (CaptureStoreException exception)
            {
                var journal = _storage.Receipt.Import!;
                if (!journal.Terminal)
                    PersistTerminal(journal with { Terminal = true, Result = journal.Result with
                    {
                        Failure = new(exception.Code, exception.Code == CaptureErrorCode.UnsupportedFormat
                            ? "ImportWorkerUnavailable" : "ImportFailed", null, null, null, null)
                    } });
                throw;
            }
        }
        finally { _io.Release(); }
    }

    private void PersistTerminal(PortableImportJournal journal)
    {
        _pendingTerminal = journal;
        _storage.SaveImport(journal);
        _result = journal.Result;
        _pendingTerminal = null;
    }

    private FileStream Open(FileAccess access)
    {
        CapturePackage.RejectLinks(_storage.ArchivePath);
        return new(_storage.ArchivePath, FileMode.Open, access, FileShare.Read, 1, FileOptions.Asynchronous);
    }

    private void Enter()
    {
        if (!_io.Wait(0)) throw CapturePackage.Error(CaptureErrorCode.Busy, "Transfer I/O is already active.");
        if (_disposed)
        {
            _io.Release();
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "TransferExpired: transfer was disposed.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Never release admission while a read, append or publication still owns I/O.
        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _closing = true;
            if (_import is null) _storage.Abandon();
            else
            {
                if (_pendingTerminal is not null) PersistTerminal(_pendingTerminal);
                if (!_committed && _result is null)
                {
                    var journal = _storage.Receipt.Import!;
                    var cancelled = journal.Result with { Cancelled = true,
                        Failure = new(CaptureErrorCode.Incomplete, "Cancelled", null, null, null, null) };
                    PersistTerminal(journal with { Terminal = true, Result = cancelled });
                }
                _storage.CleanImport();
            }
            _releaseSources?.Invoke();
            _storage.Dispose();
            _disposed = true;
        }
        finally { _io.Release(); }
    }
}

public sealed partial class PortableCaptureUseCases
{
    /// <summary>Reserves a private upload without starting a validator or an import-operation deadline.</summary>
    public PortableCaptureTransfer BeginUpload(CaptureImportRequest request, CaptureAccess access,
        AuthorizePortableImport authorize)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorize);
        CapturePackage.ValidateAccess(access);
        CapturePackage.ValidateId(request.Operation.Id);
        if (request.ArchiveBytes <= 0 || !PortableCaptureProvenance.IsHash(request.ArchiveSha256))
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Invalid archive length or hash.");
        PortableBounds.Check("MaxArchiveBytes", request.ArchiveBytes, _options.MaxArchiveBytes);
        _ = _store.InitializePortableRoot();
        var initial = new PortableImportResult(request.Operation.Id, null, request.ArchiveSha256, false, false, [], null);
        var fingerprint = PortableCaptureStorage.Digest(CapturePackage.Utf8.GetBytes(
            FormattableString.Invariant($"import/{request.ArchiveBytes}/{request.ArchiveSha256}")));
        var storage = PortableCaptureStorage.Begin(_store, request.Operation, access, fingerprint,
            _clock.GetUtcNow(), new(initial, [], false));
        try
        {
            if (!storage.Reused)
            {
                storage.Reserve(request.ArchiveBytes);
                using (SafeArtifactPath.CreateRestrictedFile(storage.ArchivePath)) { }
                storage.SaveTransfer(upload: true, 0);
            }
            return new(storage, request, token =>
            {
                if (_importWorker is null)
                    throw CapturePackage.Error(CaptureErrorCode.UnsupportedFormat, "ImportWorkerUnavailable: isolated import is not configured.");
                _importWorker.Validate();
                return ImportCoreAsync(request, Stream.Null, access, authorize, token, storage);
            });
        }
        catch
        {
            try { storage.CleanImport(); }
            finally { storage.Dispose(); }
            throw;
        }
    }
}
