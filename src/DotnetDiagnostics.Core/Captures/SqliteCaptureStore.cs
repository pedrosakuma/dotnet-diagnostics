using System.Globalization;
using DotnetDiagnostics.Core.Artifacts;

namespace DotnetDiagnostics.Core.Captures;

/// <summary>
/// Opt-in, one-package-per-capture local storage. Construction performs no filesystem or SQLite work.
/// The artifact root must be private to trusted host code; packages are not an untrusted import format.
/// </summary>
public sealed partial class SqliteCaptureStore
{
    private readonly IArtifactRootProvider _root;
    private readonly CaptureStoreOptions _options;

    public SqliteCaptureStore(IArtifactRootProvider root, CaptureStoreOptions? options = null)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _options = options ?? new();
        _options.Validate();
    }

    public Task<CaptureWriter> CreateAsync(CaptureCreateRequest request, CaptureAccess access,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CapturePackage.ValidateAccess(access);
        CapturePackage.ValidateText(request.Name, 1024, nameof(request.Name));
        CapturePackage.ValidateText(request.GroupId, 1024, nameof(request.GroupId));
        if (request.Name is null)
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Capture name is required.");
        cancellationToken.ThrowIfCancellationRequested();
        FileStream? slot = null;
        FileStream? lease = null;
        try
        {
            var root = Root(create: true);
            using var admission = AcquireControl(root, ".admission");
            CheckAdmission(root, _options.MaxPackageBytes);
            slot = AcquireWriterSlot(root);
            var id = Guid.NewGuid().ToString("N");
            var directory = SafeArtifactPath.ResolveCaptureDirectory(root, id);
            using (SafeArtifactPath.CreateRestrictedFile(Path.Combine(directory, CapturePackage.Lease))) { }
            lease = CapturePackage.AcquireLease(directory, exclusive: true);
            var info = new CaptureInfo(id, access.OwnerId, request.Name, request.GroupId, DateTimeOffset.UtcNow,
                CaptureState.Recording, Array.Empty<CaptureArtifactInfo>(), new CaptureQuality(UnknownTail: true));
            var format = CapturePackage.CurrentFormat;
            var manifest = new CaptureManifest(info, format.PackageVersion, format.SchemaVersion, format.RecordVersion,
                format.IndexVersion, format.WriterVersion, format.RequiredReaderVersion,
                ["normalized-scalars-v1", "artifact-provenance-v1"], _options.MaxPackageBytes);
            CapturePackage.WriteJson(directory, CapturePackage.Manifest, manifest);
            using (SafeArtifactPath.CreateRestrictedFile(Path.Combine(directory, CapturePackage.Database))) { }
            using (var connection = CapturePackage.Connect(directory, immutable: false))
            {
                CapturePackage.Execute(connection, "PRAGMA page_size=4096; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
                CapturePackage.Execute(connection, "PRAGMA max_page_count=" +
                    (_options.MaxDatabaseBytes / 4096).ToString(CultureInfo.InvariantCulture) + ";");
                CapturePackage.Execute(connection, CapturePackage.Schema);
            }
            var writer = new CaptureWriter(directory, _options, manifest, lease, slot);
            lease = null;
            slot = null;
            return Task.FromResult(writer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lease?.Dispose();
            slot?.Dispose();
            throw CapturePackage.Translate(ex);
        }
    }

    public Task<CaptureReader> OpenAsync(string captureId, CaptureAccess access, CancellationToken cancellationToken = default)
    {
        CapturePackage.ValidateId(captureId);
        CapturePackage.ValidateAccess(access);
        cancellationToken.ThrowIfCancellationRequested();
        FileStream? lease = null;
        Microsoft.Data.Sqlite.SqliteConnection? connection = null;
        try
        {
            var directory = PackagePath(captureId);
            lease = CapturePackage.AcquireLease(directory, exclusive: false);
            _ = PackagePath(captureId);
            CapturePackage.ValidateMembers(directory);
            if (CapturePackage.PackageBytes(directory) > _options.MaxPackageBytes ||
                new FileInfo(Path.Combine(directory, CapturePackage.Database)).Length > _options.MaxDatabaseBytes)
                throw CapturePackage.Error(CaptureErrorCode.CapacityExceeded, "Package exceeds configured read byte bounds.");
            var manifest = CapturePackage.ReadManifest(directory, captureId);
            CapturePackage.Authorize(manifest.Info, access);
            ValidateSeal(directory, manifest);
            connection = CapturePackage.Connect(directory, immutable: true);
            var format = CapturePackage.FormatOf(manifest);
            CapturePackage.ValidateDatabase(connection, format);
            var reader = new CaptureReader(connection, lease, manifest.Info, _options, format);
            connection = null;
            lease = null;
            return Task.FromResult(reader);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw CapturePackage.Translate(ex); }
        finally { connection?.Dispose(); lease?.Dispose(); }
    }

    public Task<CaptureCatalogPage> ListAsync(CaptureAccess access, int pageSize = 100, string? afterCaptureId = null,
        CancellationToken cancellationToken = default)
    {
        CapturePackage.ValidateAccess(access);
        if (afterCaptureId is not null) CapturePackage.ValidateId(afterCaptureId);
        if (pageSize < 1 || pageSize > _options.MaxCatalogPageSize)
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Catalog page size exceeds the configured bound.");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var root = Root(create: false);
            if (!Directory.Exists(root)) return Task.FromResult(new CaptureCatalogPage(Array.Empty<CaptureInfo>(), null));
            using var admission = AcquireControl(root, ".admission");
            var results = new List<CaptureInfo>(pageSize + 1);
            foreach (var directory in CatalogDirectories(root).Order(StringComparer.Ordinal))
            {
                var id = Path.GetFileName(directory);
                if (afterCaptureId is not null && string.CompareOrdinal(id, afterCaptureId) <= 0) continue;
                CapturePackage.ValidateMembers(directory);
                var manifest = CapturePackage.ReadManifest(directory, id);
                if (!access.AllOwners && manifest.Info.OwnerId != access.OwnerId) continue;
                // A stale Recording manifest without a seal is conservatively interrupted; list is not open.
                var info = manifest.Info;
                if (!File.Exists(Path.Combine(directory, CapturePackage.Seal)))
                    info = info with { State = CaptureState.Interrupted, Quality = info.Quality with { Interrupted = true, UnknownTail = true } };
                results.Add(info);
                if (results.Count > pageSize) break;
            }
            var more = results.Count > pageSize;
            if (more) results.RemoveAt(results.Count - 1);
            return Task.FromResult(new CaptureCatalogPage(results.AsReadOnly(), more ? results[^1].CaptureId : null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw CapturePackage.Translate(ex); }
    }

    public Task DeleteAsync(string captureId, CaptureAccess access, CancellationToken cancellationToken = default)
    {
        CapturePackage.ValidateId(captureId);
        CapturePackage.ValidateAccess(access);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var root = Root(create: false);
            using var admission = AcquireControl(root, ".admission");
            var directory = PackagePath(captureId);
            using (var lease = CapturePackage.AcquireLease(directory, exclusive: true))
            {
                CapturePackage.ValidateMembers(directory);
                CapturePackage.Authorize(CapturePackage.ReadManifest(directory, captureId).Info, access);
                // A durable tombstone hides the package before any evidence member is removed.
                var tombstone = Path.Combine(root, ".deleted-" + captureId);
                using (var marker = SafeArtifactPath.CreateRestrictedFile(tombstone)) marker.Flush(flushToDisk: true);
            }
            // Admission serializes new opens with the tombstone check only through the package lease;
            // Open checks the tombstone again after acquiring that lease.
            DeleteMembers(directory);
            Directory.Delete(directory);
            File.Delete(Path.Combine(root, ".deleted-" + captureId));
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw CapturePackage.Translate(ex); }
    }

    public async Task<CaptureInfo> RecoverAsync(string captureId, CaptureAccess access, CancellationToken cancellationToken = default)
    {
        CapturePackage.ValidateId(captureId);
        CapturePackage.ValidateAccess(access);
        cancellationToken.ThrowIfCancellationRequested();
        string? scratch = null;
        CaptureWriter? writer = null;
        FileStream? sourceLease = null;
        Exception? recoveryFailure = null;
        Exception? cleanupFailure = null;
        CaptureInfo? result = null;
        try
        {
            var source = PackagePath(captureId);
            var lease = CapturePackage.AcquireLease(source, exclusive: true);
            sourceLease = lease;
            _ = PackagePath(captureId);
            CapturePackage.ValidateMembers(source);
            var manifest = CapturePackage.ReadManifest(source, captureId);
            CapturePackage.Authorize(manifest.Info, access);
            if (File.Exists(Path.Combine(source, CapturePackage.Seal)))
                throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Recovery accepts interrupted packages only; sealed packages are never migrated in place.");
            var sourceBytes = CapturePackage.PackageBytes(source);
            if (sourceBytes > _options.MaxPackageBytes)
                throw CapturePackage.Error(CaptureErrorCode.CapacityExceeded, "Recovery copy exceeds the configured package bound.");
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(source))
                hashes.Add(Path.GetFileName(file), Path.GetFileName(file) == CapturePackage.Lease
                    ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(lease))
                    : CapturePackage.Hash(file));
            var root = Root(create: false);
            using (var admission = AcquireControl(root, ".admission"))
            {
                CheckAdmission(root, sourceBytes + _options.MaxPackageBytes);
                var scratchId = ".recovery-" + Guid.NewGuid().ToString("N");
                scratch = SafeArtifactPath.ResolveCaptureDirectory(root, scratchId);
                foreach (var name in new[] { CapturePackage.Database, CapturePackage.Database + "-wal" })
                {
                    var path = Path.Combine(source, name);
                    if (!File.Exists(path)) continue;
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var output = SafeArtifactPath.CreateRestrictedFile(Path.Combine(scratch, name));
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
            }
            using var copy = CapturePackage.Connect(scratch, immutable: false);
            var sourceFormat = CapturePackage.FormatOf(manifest);
            CapturePackage.ValidateDatabase(copy, sourceFormat);
            using var recovered = new CaptureReader(copy, null, manifest.Info, _options, sourceFormat);
            writer = await CreateAsync(new CaptureCreateRequest(manifest.Info.Name, manifest.Info.GroupId), access, cancellationToken).ConfigureAwait(false);
            writer.SetRecovery(captureId, hashes);
            writer.SetSourceRejected(manifest.Info.Quality.SourceRejected);
            foreach (var artifact in manifest.Info.Artifacts)
            {
                var newId = writer.AddRecoveredArtifact(artifact);
                long after = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = recovered.Query(new CaptureRecordQuery(artifact.ArtifactId, AfterRecordId: after, PageSize: Math.Min(256, _options.MaxQueryPageSize)));
                    foreach (var entry in page.Records)
                    {
                        // Recovery is not a producer callback: await capacity instead of losing committed evidence.
                        await writer.AppendRecoveryAsync(newId, entry.Record, cancellationToken).ConfigureAwait(false);
                    }
                    if (page.NextAfterRecordId is null) break;
                    after = page.NextAfterRecordId.Value;
                }
                var snapshot = recovered.ReadSnapshot(artifact.ArtifactId);
                if (snapshot is not null) writer.SetSnapshot(newId, snapshot.Version, snapshot.Utf8Json);
            }
            result = await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            recoveryFailure = ex;
        }
        finally
        {
            try
            {
                if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Disposal reawaits the cached shutdown task; its already-propagating failure is not a second cleanup error.
                if (!ReferenceEquals(ex, recoveryFailure)) cleanupFailure = ex;
            }
            try
            {
                if (scratch is not null)
                {
                    CapturePackage.ValidateMembers(scratch);
                    DeleteMembers(scratch);
                    Directory.Delete(scratch);
                }
            }
            catch (Exception ex)
            {
                cleanupFailure = cleanupFailure is null ? ex : new AggregateException(cleanupFailure, ex);
            }
            try { sourceLease?.Dispose(); }
            catch (Exception ex)
            {
                cleanupFailure = cleanupFailure is null ? ex : new AggregateException(cleanupFailure, ex);
            }
        }
        if (cleanupFailure is not null)
            throw CapturePackage.Error(CaptureErrorCode.StorageFailure,
                "Recovery cleanup failed; source evidence is retained and any derived package remains separately identifiable.",
                recoveryFailure is null ? cleanupFailure : new AggregateException(recoveryFailure, cleanupFailure));
        if (recoveryFailure is OperationCanceledException)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(recoveryFailure);
        if (recoveryFailure is not null) throw CapturePackage.Translate(recoveryFailure);
        return result ?? throw CapturePackage.Error(CaptureErrorCode.StorageFailure, "Recovery produced no derived capture.");
    }

    private string Root(bool create)
    {
        var root = Path.GetFullPath(_root.Root);
        var captures = Path.Combine(root, "captures");
        CapturePackage.RejectLinks(captures);
        if (!create)
        {
            if (Directory.Exists(captures)) ValidateStoreMarker(Path.Combine(captures, ".capture-store"));
            return captures;
        }
        captures = SafeArtifactPath.ResolveCaptureDirectory(root, "captures");
        var marker = Path.Combine(captures, ".capture-store");
        CapturePackage.RejectLinks(marker);
        if (!File.Exists(marker))
        {
            FileStream? created;
            try { created = SafeArtifactPath.CreateRestrictedFile(marker); }
            catch (IOException) when (File.Exists(marker)) { created = null; }
            if (created is not null)
            {
                using (created)
                {
                    created.Write("dotnet-diagnostics-captures/1"u8);
                    created.Flush(flushToDisk: true);
                }
            }
        }
        ValidateStoreMarker(marker);
        return captures;
    }

    private static void ValidateStoreMarker(string marker)
    {
        CapturePackage.RejectLinks(marker);
        FileStream stream;
        try { stream = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (IOException ex) when (File.Exists(marker))
        {
            throw CapturePackage.Error(CaptureErrorCode.Busy, "Capture store marker is being initialized or is unavailable.", ex);
        }
        using (stream)
        {
            ReadOnlySpan<byte> expected = "dotnet-diagnostics-captures/1"u8;
            if (stream.Length != expected.Length)
                throw CapturePackage.Error(CaptureErrorCode.UnsupportedFormat, "Capture store marker has unsupported contents.");
            Span<byte> content = stackalloc byte[expected.Length];
            stream.ReadExactly(content);
            if (!content.SequenceEqual(expected))
                throw CapturePackage.Error(CaptureErrorCode.UnsupportedFormat, "Capture store marker has unsupported contents.");
        }
    }

    private string PackagePath(string id)
    {
        var root = Root(create: false);
        CapturePackage.RejectLinks(Path.Combine(root, id));
        var path = SafeArtifactPath.ResolveCapturePath(root, id);
        CapturePackage.RejectLinks(path);
        if (File.Exists(Path.Combine(root, ".deleted-" + id)))
            throw CapturePackage.Error(CaptureErrorCode.Deleted, "Capture has been tombstoned; deletion may require operator cleanup.");
        if (!Directory.Exists(path)) throw CapturePackage.Error(CaptureErrorCode.NotFound, "Capture package does not exist.");
        return path;
    }

    private static void ValidateSeal(string directory, CaptureManifest manifest)
    {
        if (manifest.Info.State != CaptureState.Sealed || !File.Exists(Path.Combine(directory, CapturePackage.Seal)))
            throw CapturePackage.Error(CaptureErrorCode.Incomplete, "Capture is not sealed; use explicit derived recovery.");
        if (File.Exists(Path.Combine(directory, CapturePackage.Database + "-wal")) ||
            File.Exists(Path.Combine(directory, CapturePackage.Database + "-shm")) ||
            File.Exists(Path.Combine(directory, CapturePackage.Manifest + ".pending")) ||
            File.Exists(Path.Combine(directory, CapturePackage.Seal + ".pending")))
            throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "A sealed package cannot contain WAL/SHM or unpublished metadata evidence.");
        var seal = CapturePackage.ReadJson<CaptureSeal>(Path.Combine(directory, CapturePackage.Seal));
        if (seal.ManifestHash != CapturePackage.Hash(Path.Combine(directory, CapturePackage.Manifest)) ||
            seal.DatabaseHash != CapturePackage.Hash(Path.Combine(directory, CapturePackage.Database)))
            throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Capture seal hash validation failed.");
    }

    private static FileStream AcquireControl(string root, string name)
    {
        var path = Path.Combine(root, name);
        CapturePackage.RejectLinks(path);
        if (!File.Exists(path))
        {
            try { using var created = SafeArtifactPath.CreateRestrictedFile(path); }
            catch (IOException) when (File.Exists(path)) { /* A competing creator owns the same control file. */ }
        }
        CapturePackage.RejectLinks(path);
        try { return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw CapturePackage.Error(CaptureErrorCode.Busy, "Store admission/control lease is busy; retry later.", ex); }
    }

    private FileStream AcquireWriterSlot(string root)
    {
        for (var i = 0; i < _options.MaxActiveWriters; i++)
        {
            try { return AcquireControl(root, ".writer-" + i.ToString(CultureInfo.InvariantCulture)); }
            catch (CaptureStoreException ex) when (ex.Code == CaptureErrorCode.Busy) { }
        }
        throw CapturePackage.Error(CaptureErrorCode.CapacityExceeded, "Store active-writer limit reached.");
    }

    private static List<string> CatalogDirectories(string root, bool includeDeleted = false)
    {
        var directories = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.')) continue;
            CapturePackage.ValidateId(name);
            if (!includeDeleted && File.Exists(Path.Combine(root, ".deleted-" + name))) continue;
            CapturePackage.RejectLinks(directory);
            if (directories.Count == 256)
                throw CapturePackage.Error(CaptureErrorCode.CapacityExceeded, "Catalog exceeds the frozen maximum package count.");
            directories.Add(directory);
        }
        return directories;
    }

    private void CheckAdmission(string root, long reservation)
    {
        var directories = CatalogDirectories(root, includeDeleted: true);
        if (directories.Count >= _options.MaxCaptures)
            throw CapturePackage.Error(CaptureErrorCode.CapacityExceeded, "Store capture-count admission limit reached.");
        long bytes = 0;
        foreach (var directory in directories)
        {
            var actual = CapturePackage.PackageBytes(directory);
            var sealedPackage = File.Exists(Path.Combine(directory, CapturePackage.Seal));
            var manifestPath = Path.Combine(directory, CapturePackage.Manifest);
            var reserved = !sealedPackage && File.Exists(manifestPath)
                ? CapturePackage.ReadManifest(directory, Path.GetFileName(directory)).ReservationBytes : 512L * 1024 * 1024;
            bytes = checked(bytes + (sealedPackage ? actual : Math.Max(actual, reserved)));
        }
        foreach (var directory in Directory.EnumerateDirectories(root, ".recovery-*"))
        {
            CapturePackage.RejectLinks(directory);
            bytes = checked(bytes + CapturePackage.PackageBytes(directory));
        }
        bytes = checked(bytes + PortableCaptureStorage.AccountedBytes(root));
        if (reservation > _options.MaxStoreBytes - bytes)
            throw CapturePackage.Error(CaptureErrorCode.CapacityExceeded, "Store byte admission budget exhausted (including unsealed package reservations).");
    }

    private static void DeleteMembers(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            CapturePackage.RejectLinks(path);
            File.Delete(path);
        }
    }
}
