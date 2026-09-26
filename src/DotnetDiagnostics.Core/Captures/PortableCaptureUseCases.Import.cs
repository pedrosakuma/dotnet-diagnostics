using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DotnetDiagnostics.Core.Artifacts;

namespace DotnetDiagnostics.Core.Captures;

public sealed partial class PortableCaptureUseCases
{
    private static readonly JsonSerializerOptions ImportJson = new()
    {
        MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(PortableCaptureJson.Default, CaptureJsonContext.Default)
            .WithAddedModifier(RequireMetadata)
    };
    private static readonly JsonSerializerOptions ImportIndexJson = new()
    {
        MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, TypeInfoResolver = PortableCaptureJson.Default.WithAddedModifier(RequireMetadata)
    };

    private static void RequireMetadata(JsonTypeInfo type)
    {
        foreach (var property in type.Properties)
        {
            if (property.Set is null) continue;
            if (type.Type == typeof(CaptureInfo))
                property.IsRequired = property.Name is "CaptureId" or "OwnerId" or "Name" or "CreatedUtc" or "State" or "Artifacts" or "Quality";
            else if (type.Type == typeof(CaptureArtifactInfo))
                property.IsRequired = property.Name is "ArtifactId" or "Kind" or "Name";
            else if (type.Type == typeof(PortableEntry))
                property.IsRequired = property.Name != "label";
            else if (type.Type != typeof(CaptureArtifactProvenance))
                property.IsRequired = true;
        }
    }

    private sealed record PreparedImport(PortableEntry Entry, CaptureManifest Source, CaptureManifest Destination,
        PortableEntryMapping Mapping, string Work, string Frames, SqliteAdmissionResult Admission, byte[] ManifestBytes);

    private async Task<PortableImportResult> ImportCoreAsync(CaptureImportRequest request, Stream source,
        CaptureAccess access, AuthorizePortableImport authorize, CancellationToken cancellationToken,
        PortableCaptureStorage? suppliedStorage = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Operation);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(authorize);
        CapturePackage.ValidateAccess(access);
        CapturePackage.ValidateId(request.Operation.Id);
        if (!source.CanRead || request.ArchiveBytes <= 0 || !PortableCaptureProvenance.IsHash(request.ArchiveSha256))
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Import requires readable bytes, positive length and lowercase SHA-256.");
        PortableBounds.Check("MaxArchiveBytes", request.ArchiveBytes, _options.MaxArchiveBytes);
        using var deadline = new CancellationTokenSource(_options.OperationTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        var root = _store.InitializePortableRoot();
        var initial = new PortableImportResult(request.Operation.Id, null, request.ArchiveSha256, false, false, [], null);
        var fingerprint = PortableCaptureStorage.Digest(CapturePackage.Utf8.GetBytes(
            FormattableString.Invariant($"import/{request.ArchiveBytes}/{request.ArchiveSha256}")));
        using var ownedStorage = suppliedStorage is null
            ? PortableCaptureStorage.Begin(_store, request.Operation, access, fingerprint,
                _clock.GetUtcNow(), new(initial, [], false)) : null;
        var storage = suppliedStorage ?? ownedStorage!;
        if (storage.Reused && suppliedStorage is null) return storage.Receipt.Import!.Result;
        var budget = new PortableImportBudget(storage);
        var publication = new ImportPublication(_store, root, storage, budget, access, initial);
        var ioOutstanding = false;
        var index = -1;
        try
        {
            using var validator = _store.PortableValidator();
            var work = SafeArtifactPath.ResolveCaptureDirectory(storage.DirectoryPath, "work");
            var first = SafeArtifactPath.ResolveCaptureDirectory(work, "00");
            var probe = SafeArtifactPath.ResolveCaptureDirectory(first, "analysis");
            try { await IsolatedCaptureWorker.EnsureAvailableAsync(_importWorker!, probe, token).ConfigureAwait(false); }
            catch (CaptureStoreException ex) when (ex.Code == CaptureErrorCode.UnsupportedFormat)
            {
                throw CapturePackage.Error(CaptureErrorCode.UnsupportedFormat, "ImportWorkerUnavailable: required isolation is unavailable.", ex);
            }
            _ = PortableWorkerIdentity.Capture(Environment.ProcessId);
            if (suppliedStorage is null)
                await ReceiveArchive(source, storage, budget, request, token).ConfigureAwait(false);
            using var archive = OpenRead(storage.ArchivePath);
            var inventory = await PortableZip.InspectAsync(archive, _options, token).ConfigureAwait(false);
            var bundle = await ReadIndex(archive, inventory, token).ConfigureAwait(false);
            publication.Result = publication.Result with { BundleId = bundle.BundleId,
                Entries = bundle.Entries.Select(static entry => new PortableEntryResult(entry.EntryId, PortableEntryState.Pending, null, null)).ToArray() };
            publication.SaveProgress();
            var prepared = new List<PreparedImport>(bundle.Entries.Length);
            long metadata = checked(1024 * 1024 + inventory[0].Hash.Bytes * 8);
            for (var entry = 0; entry < bundle.Entries.Length; entry++)
            {
                token.ThrowIfCancellationRequested();
                index = entry;
                var directory = SafeArtifactPath.ResolveCaptureDirectory(work, entry.ToString("D2", CultureInfo.InvariantCulture));
                var sourceDirectory = SafeArtifactPath.ResolveCaptureDirectory(directory, "source");
                var analysis = SafeArtifactPath.ResolveCaptureDirectory(directory, "analysis");
                var members = inventory.Skip(2 + entry * 3).Take(3).ToArray();
                foreach (var member in members)
                    await Extract(archive, member, Path.Combine(sourceDirectory, member.Name[(member.Name.LastIndexOf('/') + 1)..]),
                        budget, token).ConfigureAwait(false);
                metadata = checked(metadata + members[0].Hash.Bytes * 8 + members[2].Hash.Bytes * 8 + 64 * 512);
                PortableBounds.Check("PortableMetadataBytes", metadata, 4 * 1024 * 1024);
                var sourceManifest = ReadImportedManifest(sourceDirectory, bundle.Entries[entry], members);
                var mapping = NewMapping(bundle.Entries[entry], sourceManifest.Info);
                var destination = DestinationManifest(sourceManifest, bundle.Entries[entry], mapping, access);
                _ = CapturePackage.ValidateManifest(destination, destination.Info.CaptureId);
                var manifestBytes = PortableCaptureJson.Encode(destination, CaptureJsonContext.Default.CaptureManifest, 128 * 1024);
                var provisional = Path.Combine(analysis, "evidence.frames");
                SqliteAdmissionResult admission;
                using (var evidence = budget.Create(provisional, 512L * 1024 * 1024))
                {
                    var native = new SqliteAdmissionRequest(_importWorker!.Executable, _importWorker.SqliteLibrary,
                        sourceDirectory, Path.Combine(sourceDirectory, CapturePackage.Database), CapturePackage.FormatOf(sourceManifest),
                        sourceManifest.Info.Artifacts, sourceManifest.Info.Quality.Persisted)
                    {
                        IncludeUsage = true, BeforeInput = Started, AfterExit = Exited
                    };
                    admission = await IsolatedCaptureWorker.AdmitSqliteAsync(native, evidence, new()
                    {
                        Store = _store.PortableStoreOptions, RowsPerTable = _options.MaxRowsPerTable,
                        RowsPerCapture = _options.MaxRowsPerCapture, TokensPerSnapshot = _options.MaxTokensPerSnapshot,
                        TokensPerCapture = _options.MaxTokensPerCapture
                    }, token).ConfigureAwait(false);
                }
                var validated = Path.Combine(analysis, "validated.frames");
                using (var input = OpenRead(provisional))
                using (var output = budget.Create(validated, 512L * 1024 * 1024))
                using (var dimensions = new PortableScalarIndex(budget.Create(Path.Combine(analysis, "strings.index"), 80_000_000),
                    budget.Create(Path.Combine(analysis, "strings.data"), 128L * 1024 * 1024)))
                using (var records = budget.Create(Path.Combine(analysis, "records.index"), 48_000_000))
                {
                    var ids = mapping.Artifacts.ToDictionary(static item => item.EntryArtifactId,
                        static item => item.LocalArtifactId, StringComparer.Ordinal);
                    _ = PortableFrameValidation.Validate(input, output, dimensions, records, sourceManifest, ids,
                        _store.PortableStoreOptions, _options, metadata, token);
                }
                prepared.Add(new(bundle.Entries[entry], sourceManifest, destination, mapping, directory, validated, admission, manifestBytes));
            }
            archive.Dispose();
            var descriptors = prepared.Select(static entry => new PortableImportEntry(entry.Entry.EntryId, entry.Entry.Label,
                entry.Source.Info, CapturePackage.FormatOf(entry.Source))).ToArray();
            index = -1;
            await publication.PrepareAsync(Array.AsReadOnly(descriptors), prepared.Select(static entry => entry.Mapping).ToArray(),
                authorize, token).ConfigureAwait(false);
            for (index = 0; index < prepared.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var entry = prepared[index];
                using var writer = _store.PortableWriter();
                var reservation = checked(_store.PortableStoreOptions.MaxPackageBytes + _store.PortableStoreOptions.MaxDatabaseBytes);
                budget.Reserve(reservation);
                var destination = SafeArtifactPath.ResolveCaptureDirectory(entry.Work, "destination");
                var cpu = TimeSpan.FromSeconds(60) - entry.Admission.CpuTime;
                var wall = TimeSpan.FromSeconds(120) - entry.Admission.WallTime;
                if (cpu <= TimeSpan.Zero || wall <= TimeSpan.Zero || entry.Admission.VmInstructions >= 200_000_000)
                    throw CapturePackage.Error(CaptureErrorCode.CapacityExceeded, "EntryWorkerBudget: admission consumed the rebuild budget.");
                using (var frames = OpenRead(entry.Frames))
                    _ = await IsolatedCaptureWorker.RebuildValidatedAsync(new(_importWorker!.Executable, _importWorker.SqliteLibrary,
                        destination, _store.PortableStoreOptions.MaxDatabaseBytes, 200_000_000 - entry.Admission.VmInstructions,
                        new() { CpuTime = cpu, WallTime = wall }) { BeforeInput = Started, AfterExit = Exited }, frames, token).ConfigureAwait(false);
                WritePrivate(Path.Combine(destination, CapturePackage.Manifest), entry.ManifestBytes);
                using (SafeArtifactPath.CreateRestrictedFile(Path.Combine(destination, CapturePackage.Lease))) { }
                var seal = new CaptureSeal(CapturePackage.Hash(Path.Combine(destination, CapturePackage.Manifest)),
                    CapturePackage.Hash(Path.Combine(destination, CapturePackage.Database)));
                WritePrivate(Path.Combine(destination, CapturePackage.Seal),
                    PortableCaptureJson.Encode(seal, CaptureJsonContext.Default.CaptureSeal, 128 * 1024));
                PortableBounds.Check("MaxPackageBytes", CapturePackage.PackageBytes(destination), _store.PortableStoreOptions.MaxPackageBytes);
                using (var reader = _store.ReadTrustedImportStaging(destination, entry.Destination))
                    PortableSourceValidation.Validate(reader, _store.PortableStoreOptions, _options, metadata, token);
                await publication.PublishAsync(destination, entry.Destination.Info, index, reservation,
                    Array.AsReadOnly(descriptors), authorize, token).ConfigureAwait(false);
            }
            return publication.Complete();
        }
        catch (Exception error) when (error is CaptureStoreException or OperationCanceledException or IOException or
            UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or InvalidOperationException or
            NotSupportedException or OverflowException)
        {
            return publication.Fail(error, index, cancellationToken.IsCancellationRequested, ioOutstanding);
        }

        void Started(int processId)
        {
            ioOutstanding = true;
            storage.SaveImport(storage.Receipt.Import! with { Worker = PortableWorkerIdentity.Capture(processId),
                ParentIo = PortableWorkerIdentity.Capture(Environment.ProcessId) });
        }
        void Exited()
        {
            ioOutstanding = false;
            storage.SaveImport(storage.Receipt.Import! with { Worker = null, ParentIo = null });
        }
    }

    private static PortableFailure Failure(CaptureStoreException error, string? entry)
    {
        var colon = error.Message.IndexOf(':');
        var reason = colon is > 0 and < 128 ? error.Message[..colon] : error.Code.ToString();
        return new(error.Code, reason, entry, error.Data["PortableLimit"] as string,
            error.Data["PortableObserved"] is long observed ? observed : null,
            error.Data["PortableMaximum"] is long maximum ? maximum : null);
    }

    private async Task ReceiveArchive(Stream source, PortableCaptureStorage storage, PortableImportBudget budget,
        CaptureImportRequest request, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        budget.Reserve(request.ArchiveBytes);
        using (var output = budget.Create(storage.ArchivePath, request.ArchiveBytes, reserved: true))
        {
            var buffer = new byte[PortableBounds.BufferBytes];
            long received = 0;
            while (true)
            {
                var wanted = PortableBounds.ReadSize(request.ArchiveBytes - received);
                var count = await source.ReadAsync(buffer.AsMemory(0, wanted), token).ConfigureAwait(false);
                if (count == 0) break;
                received = checked(received + count);
                if (received > request.ArchiveBytes) throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Archive.LengthMismatch");
                PortableBounds.Check("MaxArchiveBytes", received, _options.MaxArchiveBytes);
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            if (received != request.ArchiveBytes ||
                !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), request.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Archive.DigestOrLengthMismatch");
            await output.FlushAsync(token).ConfigureAwait(false);
        }
    }

    private async Task<PortableIndex> ReadIndex(Stream archive, IReadOnlyList<PortableZipMember> members, CancellationToken token)
    {
        var bytes = await ReadMember(archive, members[0], _options.MaxIndexBytes, token).ConfigureAwait(false);
        var seal = ReadJson<PortableIndexSeal>(await ReadMember(archive, members[1], 1024, token).ConfigureAwait(false), 16);
        if (seal.ArchiveVersion != 1 || seal.IndexBytes != bytes.Length ||
            seal.IndexSha256 != PortableCaptureStorage.Digest(bytes)) throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Index.Integrity");
        var index = ReadJson<PortableIndex>(bytes, 16, (members.Count - 2) / 3);
        if (index.ArchiveVersion != 1 || index.RequiredArchiveReaderVersion != 1 ||
            index.RequiredFeatures is null || !index.RequiredFeatures.Order(StringComparer.Ordinal).SequenceEqual(Features.Order(StringComparer.Ordinal)))
            throw CapturePackage.Error(CaptureErrorCode.UnsupportedFormat, "Index.VersionOrFeatures");
        CapturePackage.ValidateId(index.BundleId);
        if (index.Entries is null || index.Entries.Length == 0 || index.Entries.Length > _options.MaxEntries ||
            members.Count != 2 + 3 * index.Entries.Length) throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Index.Membership");
        var identities = new HashSet<string>(StringComparer.Ordinal) { index.BundleId };
        for (var i = 0; i < index.Entries.Length; i++)
        {
            var entry = index.Entries[i] ?? throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Index.Entry");
            CapturePackage.ValidateId(entry.EntryId);
            CapturePackage.ValidateId(entry.SourceCaptureId);
            CapturePackage.ValidateText(entry.Label, 256, "Label");
            if (!identities.Add(entry.EntryId) || entry.Label?.Any(char.IsControl) == true ||
                entry.Members is not { Length: 3 }) throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Index.Entry");
            if (!CapturePackage.IsSupportedFormat(entry.Format)) throw CapturePackage.Error(CaptureErrorCode.UnsupportedFormat, "Index.PackageVersion");
            for (var m = 0; m < 3; m++)
            {
                var expected = entry.Members[m];
                var actual = members[2 + i * 3 + m];
                if (expected is null || expected.Name != MemberNames[m] ||
                    actual.Name != $"entries/{entry.EntryId}/{MemberNames[m]}" ||
                    expected.Bytes != actual.Hash.Bytes || expected.Sha256 != actual.Hash.Sha256)
                    throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Index.MemberIntegrity");
            }
        }
        return index;
    }

    private static async Task<byte[]> ReadMember(Stream input, PortableZipMember member, int maximum, CancellationToken token)
    {
        PortableBounds.Check("MetadataBytes", member.Hash.Bytes, maximum);
        var bytes = new byte[checked((int)member.Hash.Bytes)];
        input.Position = member.Offset;
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    private static T ReadJson<T>(byte[] bytes, int depth, int entries = 16)
    {
        PortableMetadataPreflight.Check(bytes, depth, entries);
        _ = PortableSnapshotImport.CheckJson(bytes, 2_000_000, depth);
        return JsonSerializer.Deserialize<T>(bytes, depth == 16 ? ImportIndexJson : ImportJson) ??
            throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Metadata.Null");
    }

    private static async Task Extract(Stream archive, PortableZipMember member, string path, PortableImportBudget budget,
        CancellationToken token)
    {
        archive.Position = member.Offset;
        budget.Reserve(member.Hash.Bytes);
        using var target = budget.Create(path, member.Hash.Bytes, reserved: true);
        var buffer = new byte[PortableBounds.BufferBytes];
        long remaining = member.Hash.Bytes;
        while (remaining > 0)
        {
            var count = (int)Math.Min(remaining, buffer.Length);
            await archive.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            remaining -= count;
        }
    }

    private static CaptureManifest ReadImportedManifest(string directory, PortableEntry entry, PortableZipMember[] members)
    {
        var manifest = ReadJson<CaptureManifest>(File.ReadAllBytes(Path.Combine(directory, CapturePackage.Manifest)), 32);
        _ = CapturePackage.ValidateManifest(manifest, entry.SourceCaptureId);
        var seal = ReadJson<CaptureSeal>(File.ReadAllBytes(Path.Combine(directory, CapturePackage.Seal)), 32);
        if (manifest.Info.State != CaptureState.Sealed || CapturePackage.FormatOf(manifest) != entry.Format ||
            !string.Equals(seal.ManifestHash, members[0].Hash.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(seal.DatabaseHash, members[1].Hash.Sha256, StringComparison.OrdinalIgnoreCase))
            throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Capture.SealOrFormat");
        return manifest;
    }

    private static PortableEntryMapping NewMapping(PortableEntry entry, CaptureInfo source)
    {
        var used = source.Artifacts.Select(static a => a.ArtifactId).ToHashSet(StringComparer.Ordinal);
        foreach (var artifact in source.Artifacts)
            if (artifact.SourceArtifactId is not null) used.Add(artifact.SourceArtifactId);
        used.Add(source.CaptureId);
        used.Add(entry.EntryId);
        string Fresh()
        {
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var id = Guid.NewGuid().ToString("N");
                if (used.Add(id)) return id;
            }
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "IdentityCollision");
        }
        var capture = Fresh();
        var artifacts = source.Artifacts.Select(artifact => new PortableArtifactMapping(artifact.ArtifactId,
            source.PortableSource is null ? artifact.ArtifactId :
                source.PortableSource.ArtifactMap.Single(item => item.LocalArtifactId == artifact.ArtifactId).OriginArtifactId, Fresh())).ToArray();
        return new(entry.EntryId, entry.Label, source.CaptureId, capture, artifacts);
    }

    private CaptureManifest DestinationManifest(CaptureManifest source, PortableEntry entry, PortableEntryMapping mapping, CaptureAccess access)
    {
        var info = source.Info;
        var hashes = new PortableCaptureMemberHashes(entry.Members[0].Sha256, entry.Members[1].Sha256, entry.Members[2].Sha256);
        var origin = info.PortableSource?.Origin ?? new PortableCaptureOrigin(info.CaptureId, info.OwnerId, info.Name, info.GroupId,
            info.CreatedUtc, info.DerivedFrom, info.SourceHashes, CapturePackage.FormatOf(source), info.Quality, info.Artifacts);
        var artifacts = info.Artifacts.Select(artifact => artifact with
        {
            ArtifactId = mapping.Artifacts.Single(item => item.EntryArtifactId == artifact.ArtifactId).LocalArtifactId
        }).ToArray();
        var destination = info with
        {
            CaptureId = mapping.LocalCaptureId, OwnerId = access.OwnerId, Artifacts = Array.AsReadOnly(artifacts),
            PortableSource = new(origin, info.PortableSource?.OriginMemberHashes ?? hashes,
                new(info.CaptureId, CapturePackage.FormatOf(source), hashes), mapping.Artifacts, _clock.GetUtcNow())
        };
        string[] features = artifacts.Any(static artifact => artifact.SourceArtifactId is not null)
            ? ["normalized-scalars-v1", "artifact-provenance-v1", "portable-source-v1", CapturePackage.RecoveryIdentityFeature]
            : ["normalized-scalars-v1", "artifact-provenance-v1", "portable-source-v1"];
        return new(destination, 3, 1, 1, 1, 3, 3, features, _store.PortableStoreOptions.MaxPackageBytes);
    }

    private static void WritePrivate(string path, byte[] bytes)
    {
        using var file = SafeArtifactPath.CreateRestrictedFile(path);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }
}
