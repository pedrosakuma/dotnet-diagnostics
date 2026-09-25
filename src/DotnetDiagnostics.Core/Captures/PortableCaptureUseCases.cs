using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using DotnetDiagnostics.Core.Artifacts;

namespace DotnetDiagnostics.Core.Captures;

/// <summary>Bounded export from an ID-selected private store. Does not admit external SQLite files.</summary>
public sealed class PortableCaptureUseCases
{
    private static readonly string[] Features = ["independent-captures-v1", "stored-zip-v1", "index-sha256-v1"];
    private static readonly string[] MemberNames = [CapturePackage.Manifest, CapturePackage.Database, CapturePackage.Seal];
    private readonly SqliteCaptureStore _store;
    private readonly AuthorizePortableExport _authorize;
    private readonly PortableCaptureOptions _options;
    private readonly TimeProvider _clock;

    public PortableCaptureUseCases(SqliteCaptureStore store, AuthorizePortableExport authorizeExport,
        PortableCaptureOptions? options = null, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authorize = authorizeExport ?? throw new ArgumentNullException(nameof(authorizeExport));
        _options = options ?? new();
        _options.Validate();
        _clock = timeProvider ?? TimeProvider.System;
    }

    public async Task<PortableExportResult> ExportAsync(CaptureExportRequest request, Stream destination,
        CaptureAccess access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Operation);
        ArgumentNullException.ThrowIfNull(request.Entries);
        ArgumentNullException.ThrowIfNull(destination);
        CapturePackage.ValidateAccess(access);
        if (!destination.CanWrite || request.Entries.Count == 0)
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Export requires writable output and at least one explicit capture.");
        PortableBounds.Check("MaxEntries", request.Entries.Count, _options.MaxEntries);
        var selections = new CaptureExportSelection[request.Entries.Count];
        for (var i = 0; i < selections.Length; i++)
        {
            var selection = request.Entries[i] ?? throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Capture selection is null.");
            CapturePackage.ValidateId(selection.CaptureId);
            CapturePackage.ValidateText(selection.Label, 256, nameof(selection.Label));
            if (selection.Label?.Any(char.IsControl) == true)
                throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Labels cannot contain control characters.");
            selections[i] = selection;
        }
        var fingerprint = PortableCaptureStorage.Digest(PortableCaptureJson.Encode(selections,
            PortableCaptureJson.Default.CaptureExportSelectionArray, 32 * 1024));
        using var deadline = new CancellationTokenSource(_options.OperationTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        PortableCaptureStorage? storage = null;
        var sources = new Dictionary<string, Source>(StringComparer.Ordinal);
        var ready = false;
        try
        {
            token.ThrowIfCancellationRequested();
            storage = PortableCaptureStorage.Begin(_store, request.Operation, access, fingerprint, _clock.GetUtcNow());
            long metadataBytes = 1024 * 1024;
            foreach (var id in selections.Select(static s => s.CaptureId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var path = _store.PortablePackagePath(id);
                var manifestBytes = new FileInfo(Path.Combine(path, CapturePackage.Manifest)).Length;
                PortableBounds.Check("ManifestBytes", manifestBytes, 128 * 1024);
                metadataBytes = checked(metadataBytes + manifestBytes * 8);
                PortableBounds.Check("PortableMetadataBytes", metadataBytes, 4 * 1024 * 1024);
                FileStream? lease = CapturePackage.AcquireLease(path, exclusive: false);
                try
                {
                    _ = _store.PortablePackagePath(id);
                    CapturePackage.ValidateMembers(path);
                    var manifest = CapturePackage.ReadManifest(path, id);
                    CapturePackage.Authorize(manifest.Info, access);
                    PortableBounds.Check("MaxArtifacts", manifest.Info.Artifacts.Count, _store.PortableStoreOptions.MaxArtifacts);
                    await _authorize(manifest.Info, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    using var reader = await _store.OpenAsync(id, access, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    var source = new Source(path, reader.Info, reader.Format, lease);
                    sources.Add(id, source);
                    lease = null;
                    PortableSourceValidation.Validate(reader, _store.PortableStoreOptions, _options, metadataBytes, token);
                    source.Members = await MeasureSourceAsync(source, token).ConfigureAwait(false);
                }
                finally { lease?.Dispose(); }
            }
            PortableExportResult result;
            if (storage.Reused)
            {
                result = storage.Receipt.Result!;
                PortableBounds.Check("MaxArchiveBytes", result.ArchiveBytes, _options.MaxArchiveBytes);
                using var existing = OpenRead(storage.ArchivePath);
                _ = await PortableZip.InspectAsync(existing, _options, token).ConfigureAwait(false);
                existing.Position = 0;
                var actual = await PortableZip.MeasureAsync(existing, _options.MaxArchiveBytes, token).ConfigureAwait(false);
                if (actual.Bytes != result.ArchiveBytes || actual.Sha256 != result.ArchiveSha256)
                    throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Staged export does not match its retry receipt.");
                ready = true;
            }
            else
            {
                var members = BuildMembers(selections, sources, storage.Receipt);
                var size = PortableZip.Size(members, _options);
                storage.Reserve(size);
                var pending = Path.Combine(storage.DirectoryPath, "bundle.pending");
                using (var output = SafeArtifactPath.CreateRestrictedFile(pending))
                {
                    await PortableZip.WriteAsync(output, members, _options, token).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                using (var staged = OpenRead(pending))
                {
                    var inspected = await PortableZip.InspectAsync(staged, _options, token).ConfigureAwait(false);
                    for (var i = 0; i < members.Count; i++)
                        if (inspected[i].Name != members[i].Name || inspected[i].Hash != members[i].Hash)
                            throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Staged archive membership or integrity differs from its index.");
                    staged.Position = 0;
                    var whole = await PortableZip.MeasureAsync(staged, _options.MaxArchiveBytes, token).ConfigureAwait(false);
                    result = new(request.Operation.Id, storage.Receipt.BundleId, whole.Bytes, whole.Sha256);
                }
                token.ThrowIfCancellationRequested();
                File.Move(pending, storage.ArchivePath);
                storage.Complete(result);
                ready = true;
            }
            foreach (var source in sources.Values)
            {
                _ = _store.PortablePackagePath(source.Info.CaptureId);
                CapturePackage.Authorize(source.Info, access);
                await _authorize(source.Info, token).ConfigureAwait(false);
            }
            using (var input = OpenRead(storage.ArchivePath))
                await CopyResultAsync(input, destination, result, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            storage.LimitRetention(_clock.GetUtcNow().AddMinutes(5));
            return result;
        }
        catch (Exception ex)
        {
            if (storage is not null)
            {
                try
                {
                    if (ready && !token.IsCancellationRequested) storage.LimitRetention(_clock.GetUtcNow().AddMinutes(5));
                    else storage.Abandon();
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or CaptureStoreException)
                {
                    throw CapturePackage.Error(CaptureErrorCode.StorageFailure,
                        "CleanupFailed: inspect the operation receipt; private bytes remain accounted.",
                        new AggregateException(ex, cleanup));
                }
            }
            if (ex is OperationCanceledException)
            {
                if (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    throw CapturePackage.Error(CaptureErrorCode.CapacityExceeded, "OperationTimeout: portable operation deadline exceeded.", ex);
                throw;
            }
            throw CapturePackage.Translate(ex);
        }
        finally
        {
            foreach (var source in sources.Values) source.Dispose();
            storage?.Dispose();
        }
    }

    /// <summary>Explicit lifecycle hook for hosts; performs no background work or automatic capture deletion.</summary>
    public Task CleanupExpiredExportsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { PortableCaptureStorage.Cleanup(_store, _clock.GetUtcNow()); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw CapturePackage.Translate(ex); }
        return Task.CompletedTask;
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "The frozen instance API reserves import for a future configured isolated worker; no fallback is implemented.")]
    public Task<PortableImportResult> ImportAsync(CaptureImportRequest request, Stream source, CaptureAccess access,
        AuthorizePortableImport authorize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw CapturePackage.Error(CaptureErrorCode.UnsupportedFormat,
            "ImportWorkerUnavailable: isolated import is not configured; no source data was processed.");
    }

    public Task<PortableImportResult> GetImportResultAsync(PortableOperationKey operation, CaptureAccess access,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CapturePackage.ValidateAccess(access);
        ArgumentNullException.ThrowIfNull(operation);
        CapturePackage.ValidateId(operation.Id);
        try { _ = _store.PortableRoot(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw CapturePackage.Translate(ex); }
        throw CapturePackage.Error(CaptureErrorCode.NotFound, "No import receipt exists in this exporter-only store.");
    }

    private async Task<PortableContentHash[]> MeasureSourceAsync(Source source, CancellationToken token)
    {
        CapturePackage.ValidateMembers(source.Path);
        var hashes = new PortableContentHash[3];
        for (var i = 0; i < hashes.Length; i++)
        {
            using var input = OpenRead(Path.Combine(source.Path, MemberNames[i]));
            var maximum = i == 1 ? _store.PortableStoreOptions.MaxDatabaseBytes : 128 * 1024;
            PortableBounds.Check("MemberBytes", input.Length, maximum);
            if (i == 1)
            {
                var header = new byte[100];
                await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
                var pageSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(16));
                var size = pageSize == 1 ? 65536 : pageSize;
                if (!header.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8) || size < 512 ||
                    (size & (size - 1)) != 0 ||
                    (long)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28)) * size != input.Length)
                    throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "SQLite page geometry is invalid.");
                input.Position = 0;
            }
            hashes[i] = await PortableZip.MeasureAsync(input, maximum, token).ConfigureAwait(false);
            if (i != 1)
            {
                input.Position = 0;
                var bytes = new byte[checked((int)hashes[i].Bytes)];
                await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                _ = PortableSourceValidation.CountJson(bytes, 128 * 1024, 32);
            }
        }
        var seal = CapturePackage.ReadJson<CaptureSeal>(Path.Combine(source.Path, CapturePackage.Seal));
        if (!string.Equals(seal.ManifestHash, hashes[0].Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(seal.DatabaseHash, hashes[1].Sha256, StringComparison.OrdinalIgnoreCase))
            throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Source seal changed during export validation.");
        return hashes;
    }

    private List<PortableZipInput> BuildMembers(CaptureExportSelection[] selections, Dictionary<string, Source> sources,
        PortableExportReceipt receipt)
    {
        var entries = new PortableEntry[selections.Length];
        var members = new List<PortableZipInput>(2 + 3 * entries.Length);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Length; i++)
        {
            var source = sources[selections[i].CaptureId];
            string id;
            do { id = Guid.NewGuid().ToString("N"); } while (!ids.Add(id));
            var content = new PortableMember[3];
            for (var j = 0; j < content.Length; j++)
            {
                var name = MemberNames[j];
                var hash = source.Members[j];
                content[j] = new(name, hash.Bytes, hash.Sha256);
                var path = Path.Combine(source.Path, name);
                members.Add(new("entries/" + id + "/" + name, hash, () => OpenRead(path)));
            }
            entries[i] = new(id, selections[i].Label, source.Info.CaptureId, source.Format, content);
        }
        var index = PortableCaptureJson.Encode(new PortableIndex(1, 1, Features, receipt.BundleId,
            receipt.CreatedUtc, entries), PortableCaptureJson.Default.PortableIndex, _options.MaxIndexBytes);
        var seal = PortableCaptureJson.Encode(new PortableIndexSeal(1, index.Length, PortableCaptureStorage.Digest(index)),
            PortableCaptureJson.Default.PortableIndexSeal, 1024);
        members.Insert(0, new("bundle.seal.json", PortableZip.Measure(seal), () => new MemoryStream(seal, writable: false)));
        members.Insert(0, new("bundle.json", PortableZip.Measure(index), () => new MemoryStream(index, writable: false)));
        return members;
    }

    private async Task CopyResultAsync(Stream input, Stream destination, PortableExportResult result, CancellationToken token)
    {
        var buffer = new byte[PortableBounds.BufferBytes];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long copied = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(buffer.AsMemory(0, PortableBounds.ReadSize(result.ArchiveBytes - copied)), token).ConfigureAwait(false);
                if (read == 0) break;
                copied += read;
                PortableBounds.Check("MaxArchiveBytes", copied, Math.Min(_options.MaxArchiveBytes, result.ArchiveBytes));
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            if (copied != result.ArchiveBytes ||
                !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), result.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Export output does not match the staged archive.");
            await destination.FlushAsync(token).ConfigureAwait(false);
        }
        finally { Array.Clear(buffer); }
    }

    private static FileStream OpenRead(string path)
    {
        CapturePackage.RejectLinks(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, PortableBounds.BufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private sealed class Source(string path, CaptureInfo info, CaptureFormatVersions format, FileStream lease) : IDisposable
    {
        internal string Path { get; } = path;
        internal CaptureInfo Info { get; } = info;
        internal CaptureFormatVersions Format { get; } = format;
        internal PortableContentHash[] Members { get; set; } = [];
        public void Dispose() => lease.Dispose();
    }
}
