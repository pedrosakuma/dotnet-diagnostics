using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Evidence;

namespace DotnetDiagnostics.Core.Tests.DurableCaptureSpike;

internal sealed record DurableSpikeLimits(
    int MaxEvents,
    int MaxNotes,
    int MaxStringChars,
    long MaxEstimatedCopyBytes,
    long MaxRepresentationBytes,
    long MaxManifestBytes,
    long MaxSealBytes,
    long MaxPackageBytes,
    int MaxPackageFiles,
    int MaxHistoricalPackagesToScan);

internal sealed record DurableSpikeTarget(
    int ProcessId,
    DateTimeOffset? ProcessStartedAt,
    string? Runtime,
    string? RuntimeVersion,
    string? OperatingSystem,
    string? ProcessArchitecture,
    string? ManagedEntrypointAssembly,
    string? CommandLineDigest,
    string? HostBinding);

internal enum DurableSpikeOperation
{
    Read,
    Recover,
    Delete,
}

internal sealed record DurableSpikeAccessRequest(
    DurableSpikeOperation Operation,
    string CaptureId,
    string ArtifactId,
    string RepresentationKind);

internal sealed record DurableSpikeWriteOptions(
    bool PublishSeal = true,
    string ContractVersion = DurableContentionCaptureSpikeStore.ContractVersion,
    string RepresentationVersion = DurableContentionCaptureSpikeStore.RepresentationVersion,
    string? DerivedFromCaptureId = null,
    string? RecoveryReason = null,
    bool RecoveryTailUnknown = false);

internal sealed record DurableSpikePackage(
    string CaptureId,
    string ArtifactId,
    string RelativeLocator,
    string AbsolutePath);

internal sealed class DurableSpikeOpenedCapture : IDisposable
{
    private readonly IDisposable _lease;

    internal DurableSpikeOpenedCapture(
        DurableSpikeManifest manifest,
        ContentionSnapshot snapshot,
        DiagnosticHandle handle,
        IDisposable lease)
    {
        Manifest = manifest;
        Snapshot = snapshot;
        Handle = handle;
        _lease = lease;
    }

    internal DurableSpikeManifest Manifest { get; }
    internal ContentionSnapshot Snapshot { get; }
    internal DiagnosticHandle Handle { get; }

    public void Dispose() => _lease.Dispose();
}

internal sealed record DurableSpikeCleanupPolicy(
    DateTimeOffset NowUtc,
    TimeSpan? MaxAge,
    int MaxPackages,
    long MaxTotalBytes);

internal sealed record DurableSpikeCleanupResult(
    IReadOnlyList<string> DeletedCaptureIds,
    IReadOnlyList<string> SkippedLeasedCaptureIds,
    long RemainingBytes);

internal sealed record DurableSpikeTimedWriter(bool CompletedWithinTimeout, Task Completion);

internal sealed class DurableSpikeException : Exception
{
    internal DurableSpikeException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    internal string Code { get; }
}

internal sealed class DurableContentionCaptureSpikeStore
{
    internal const string ContractVersion = "dc-spike/1";
    internal const string RepresentationKind = CollectionHandleKinds.ContentionSnapshot;
    internal const string RepresentationVersion = "contention-snapshot/1";
    internal const string MinimumReaderContractVersion = ContractVersion;
    internal const string WriterVersion = "dc2-test-spike/1";
    internal const string ReaderVersion = "dc2-test-spike/1";

    private const string ManifestFileName = "manifest.json";
    private const string SealFileName = "seal.json";
    private const string RepresentationsDirectoryName = "representations";

    private static readonly string[] SupportedViews = ["summary", "byCallSite", "byOwner"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string _root;
    private readonly DurableSpikeLimits _limits;
    private readonly Func<DurableSpikeAccessRequest, bool> _authorize;
    private readonly DurableSpikeLeaseRegistry _leases = new(PathComparer);

    internal DurableContentionCaptureSpikeStore(
        string root,
        DurableSpikeLimits limits,
        Func<DurableSpikeAccessRequest, bool>? authorize = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _limits = limits;
        _authorize = authorize ?? (static _ => true);
        Directory.CreateDirectory(root);
        _root = SafeArtifactPath.ResolvePath(root, ".", "root");
    }

    internal int RepresentationMaterializationCount { get; private set; }

    internal DurableSpikePackage Write(
        ContentionSnapshot snapshot,
        DurableSpikeTarget target,
        DateTimeOffset retentionExpiresAt,
        DurableSpikeWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(target);
        options ??= new DurableSpikeWriteOptions();

        ValidateSnapshot(snapshot);
        ValidateVersion(options.ContractVersion, nameof(options.ContractVersion));
        ValidateVersion(options.RepresentationVersion, nameof(options.RepresentationVersion));

        var estimatedCopyBytes = EstimateCopyBytes(snapshot);
        EnsureAtMost(estimatedCopyBytes, _limits.MaxEstimatedCopyBytes, "EstimatedCopyLimitExceeded");

        var captureId = $"capture-{Guid.NewGuid():N}";
        var artifactId = $"artifact-{Guid.NewGuid():N}";
        var relativeLocator = captureId;
        var stagingRelative = $".writing-{captureId}";
        var stagingPath = SafeArtifactPath.ResolveDirectory(_root, stagingRelative, stagingRelative, "locator");

        var finalPath = ResolveNewPackagePath(relativeLocator);
        using var writerLease = _leases.AcquireExclusive(finalPath);
        try
        {
            var representationRelative = $"{RepresentationsDirectoryName}/{artifactId}.json";
            var representationDirectory = SafeArtifactPath.ResolveDirectory(
                stagingPath,
                RepresentationsDirectoryName,
                RepresentationsDirectoryName,
                "representationDirectory");
            var representationPath = Path.Combine(representationDirectory, $"{artifactId}.json");
            WriteJsonBounded(representationPath, snapshot, _limits.MaxRepresentationBytes, "RepresentationLimitExceeded");
            var representationLength = new FileInfo(representationPath).Length;

            var manifest = new DurableSpikeManifest(
                ContractVersion: options.ContractVersion,
                MinimumReaderContractVersion: MinimumReaderContractVersion,
                WriterVersion: WriterVersion,
                CaptureId: captureId,
                CreatedAt: DateTimeOffset.UtcNow,
                RetentionExpiresAt: retentionExpiresAt,
                Target: target,
                Artifact: new DurableSpikeArtifactDescriptor(
                    ArtifactId: artifactId,
                    RepresentationKind: RepresentationKind,
                    RepresentationVersion: options.RepresentationVersion,
                    RelativePath: representationRelative,
                    SupportedViews: SupportedViews,
                    StructuredQualitySchema: EvidenceQuality.SchemaV1,
                    StructuredQualityState: "legacy-unknown"),
                Accounting: new DurableSpikeBudgetAccounting(
                    EstimatedCopyBytes: estimatedCopyBytes,
                    ActualRepresentationBytes: representationLength,
                    ActiveBatchBytes: representationLength),
                DerivedFromCaptureId: options.DerivedFromCaptureId,
                RecoveryReason: options.RecoveryReason,
                RecoveryTailUnknown: options.RecoveryTailUnknown,
                DerivationReaderVersion: options.DerivedFromCaptureId is null ? null : ReaderVersion);

            var manifestPath = Path.Combine(stagingPath, ManifestFileName);
            WriteJsonBounded(manifestPath, manifest, _limits.MaxManifestBytes, "ManifestLimitExceeded");

            if (options.PublishSeal)
            {
                var seal = new DurableSpikeSeal(
                    CaptureId: captureId,
                    ManifestLength: new FileInfo(manifestPath).Length,
                    ManifestSha256: HashFile(manifestPath),
                    RepresentationLength: representationLength,
                    RepresentationSha256: HashFile(representationPath));
                WriteJsonBounded(
                    Path.Combine(stagingPath, SealFileName),
                    seal,
                    _limits.MaxSealBytes,
                    "SealLimitExceeded");
            }

            EnsurePackageWithinLimits(stagingPath);
            Directory.Move(stagingPath, finalPath);
            return new DurableSpikePackage(captureId, artifactId, relativeLocator, finalPath);
        }
        catch
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }

            throw;
        }
    }

    internal DurableSpikeOpenedCapture Open(
        string relativeLocator,
        string expectedCaptureId,
        string expectedArtifactId,
        IDiagnosticHandleStore handles,
        TimeSpan handleTtl)
    {
        var packagePath = ResolveExistingPackage(relativeLocator);
        var readerLease = _leases.AcquireRead(packagePath);
        try
        {
            var sealPath = Path.Combine(packagePath, SealFileName);
            if (!File.Exists(sealPath))
            {
                throw Failure("RecoveryRequired", "The package has no valid seal and cannot be opened normally.");
            }

            EnsurePackageWithinLimits(packagePath);
            var seal = ReadJsonBounded<DurableSpikeSeal>(sealPath, _limits.MaxSealBytes, "SealLimitExceeded");
            var manifestPath = Path.Combine(packagePath, ManifestFileName);
            ValidateFile(manifestPath, seal.ManifestLength, seal.ManifestSha256, _limits.MaxManifestBytes);
            var manifest = ReadJsonBounded<DurableSpikeManifest>(
                manifestPath,
                _limits.MaxManifestBytes,
                "ManifestLimitExceeded");
            ValidateManifest(manifest, expectedCaptureId, expectedArtifactId);
            if (!string.Equals(seal.CaptureId, manifest.CaptureId, StringComparison.Ordinal))
            {
                throw Failure("IdentityMismatch", "The seal and manifest capture identifiers differ.");
            }

            Authorize(DurableSpikeOperation.Read, manifest);

            var representationPath = ResolvePackageMember(packagePath, manifest.Artifact.RelativePath);
            ValidateFile(
                representationPath,
                seal.RepresentationLength,
                seal.RepresentationSha256,
                _limits.MaxRepresentationBytes);
            var snapshot = ReadJsonBounded<ContentionSnapshot>(
                representationPath,
                _limits.MaxRepresentationBytes,
                "RepresentationLimitExceeded");
            RepresentationMaterializationCount++;
            ValidateSnapshot(snapshot);

            var handle = handles.RegisterWithMetadata(
                snapshot.ProcessId,
                RepresentationKind,
                snapshot,
                handleTtl,
                evictWhenProcessExits: false,
                origin: HandleOrigin.Imported,
                producingTool: "dc2-test-spike");
            return new DurableSpikeOpenedCapture(manifest, snapshot, handle, readerLease);
        }
        catch
        {
            readerLease.Dispose();
            throw;
        }
    }

    internal DurableSpikePackage Recover(
        string relativeLocator,
        string expectedCaptureId,
        string expectedArtifactId,
        DateTimeOffset retentionExpiresAt)
    {
        var sourcePath = ResolveExistingPackage(relativeLocator);
        using var recoveryLease = _leases.AcquireExclusive(sourcePath);
        if (File.Exists(Path.Combine(sourcePath, SealFileName)))
        {
            throw Failure("RecoveryNotRequired", "A sealed package must be opened read-only, not recovered.");
        }

        var before = SnapshotFiles(sourcePath);
        var manifest = ReadJsonBounded<DurableSpikeManifest>(
            Path.Combine(sourcePath, ManifestFileName),
            _limits.MaxManifestBytes,
            "ManifestLimitExceeded");
        ValidateManifest(manifest, expectedCaptureId, expectedArtifactId);
        Authorize(DurableSpikeOperation.Recover, manifest);

        var representationPath = ResolvePackageMember(sourcePath, manifest.Artifact.RelativePath);
        var snapshot = ReadJsonBounded<ContentionSnapshot>(
            representationPath,
            _limits.MaxRepresentationBytes,
            "RepresentationLimitExceeded");
        RepresentationMaterializationCount++;
        ValidateSnapshot(snapshot);

        var recovered = Write(
            snapshot,
            manifest.Target,
            retentionExpiresAt,
            new DurableSpikeWriteOptions(
                DerivedFromCaptureId: manifest.CaptureId,
                RecoveryReason: "explicit-recovery-of-unsealed-package",
                RecoveryTailUnknown: true));

        if (!FileSnapshotsMatch(SnapshotFiles(sourcePath), before))
        {
            throw Failure("SourceMutated", "Explicit recovery mutated the interrupted source package.");
        }
        return recovered;
    }

    internal void Delete(string relativeLocator, string expectedCaptureId)
    {
        var packagePath = ResolveExistingPackage(relativeLocator);
        using var deletionLease = _leases.AcquireExclusive(packagePath);
        var manifest = ReadJsonBounded<DurableSpikeManifest>(
            Path.Combine(packagePath, ManifestFileName),
            _limits.MaxManifestBytes,
            "ManifestLimitExceeded");
        if (!string.Equals(manifest.CaptureId, expectedCaptureId, StringComparison.Ordinal))
        {
            throw Failure("IdentityMismatch", "The requested capture identifier does not match the package.");
        }

        Authorize(DurableSpikeOperation.Delete, manifest);
        Directory.Delete(packagePath, recursive: true);
    }

    internal DurableSpikeCleanupResult Cleanup(DurableSpikeCleanupPolicy policy)
    {
        var directories = Directory.EnumerateDirectories(_root)
            .Where(static path => !Path.GetFileName(path).StartsWith(".writing-", StringComparison.Ordinal))
            .Select(path => SafeArtifactPath.ResolvePath(_root, path, "cleanupPath"))
            .Distinct(PathComparer)
            .Take(_limits.MaxHistoricalPackagesToScan + 1)
            .ToList();
        if (directories.Count > _limits.MaxHistoricalPackagesToScan)
        {
            throw Failure("HistoryScanLimitExceeded", "The historical package scan limit was exceeded.");
        }

        var entries = directories.Select(path =>
        {
            var manifest = ReadJsonBounded<DurableSpikeManifest>(
                Path.Combine(path, ManifestFileName),
                _limits.MaxManifestBytes,
                "ManifestLimitExceeded");
            return new CleanupEntry(path, manifest, PackageSize(path));
        }).OrderBy(static entry => entry.Manifest.CreatedAt).ToList();

        var totalBytes = entries.Sum(static entry => entry.SizeBytes);
        var deleted = new List<string>();
        var skipped = new List<string>();
        foreach (var entry in entries)
        {
            var expired = policy.MaxAge is { } maxAge
                && policy.NowUtc - entry.Manifest.CreatedAt > maxAge;
            var overCount = entries.Count - deleted.Count > policy.MaxPackages;
            var overBytes = totalBytes > policy.MaxTotalBytes;
            if (!expired && !overCount && !overBytes)
            {
                continue;
            }

            if (!_leases.TryAcquireExclusive(entry.Path, out var lease))
            {
                skipped.Add(entry.Manifest.CaptureId);
                continue;
            }

            using (lease)
            {
                Directory.Delete(entry.Path, recursive: true);
            }

            deleted.Add(entry.Manifest.CaptureId);
            totalBytes -= entry.SizeBytes;
        }

        return new DurableSpikeCleanupResult(deleted, skipped, totalBytes);
    }

    internal async Task<DurableSpikeTimedWriter> StartTimedWriterAsync(
        string relativeLocator,
        Func<Task> writer,
        TimeSpan timeout)
    {
        var packagePath = ResolveExistingPackage(relativeLocator);
        var lease = _leases.AcquireExclusive(packagePath);
        var completion = RunWriterAsync(writer, lease);
        var completed = await Task.WhenAny(completion, Task.Delay(timeout)).ConfigureAwait(false) == completion;
        if (completed)
        {
            await completion.ConfigureAwait(false);
        }

        return new DurableSpikeTimedWriter(completed, completion);
    }

    internal string MovePackage(string relativeLocator, string newRelativeLocator)
    {
        ValidateRelativeLocator(newRelativeLocator);
        var source = ResolveExistingPackage(relativeLocator);
        using var lease = _leases.AcquireExclusive(source);
        var destination = ResolveNewPackagePath(newRelativeLocator);
        using var destinationLease = _leases.AcquireExclusive(destination);
        Directory.Move(source, destination);
        return newRelativeLocator;
    }

    private static async Task RunWriterAsync(Func<Task> writer, IDisposable lease)
    {
        try
        {
            await writer().ConfigureAwait(false);
        }
        finally
        {
            lease.Dispose();
        }
    }

    private void ValidateManifest(
        DurableSpikeManifest manifest,
        string expectedCaptureId,
        string expectedArtifactId)
    {
        if (!string.Equals(manifest.ContractVersion, ContractVersion, StringComparison.Ordinal)
            || !string.Equals(manifest.MinimumReaderContractVersion, MinimumReaderContractVersion, StringComparison.Ordinal))
        {
            throw Failure("UnsupportedContractVersion", $"Unsupported contract version '{manifest.ContractVersion}'.");
        }
        if (!string.Equals(manifest.Artifact.RepresentationKind, RepresentationKind, StringComparison.Ordinal))
        {
            throw Failure("UnsupportedArtifactKind", $"Unsupported artifact kind '{manifest.Artifact.RepresentationKind}'.");
        }
        if (!string.Equals(manifest.Artifact.RepresentationVersion, RepresentationVersion, StringComparison.Ordinal))
        {
            throw Failure(
                "UnsupportedRepresentationVersion",
                $"Unsupported representation version '{manifest.Artifact.RepresentationVersion}'.");
        }
        if (!string.Equals(manifest.CaptureId, expectedCaptureId, StringComparison.Ordinal)
            || !string.Equals(manifest.Artifact.ArtifactId, expectedArtifactId, StringComparison.Ordinal))
        {
            throw Failure("IdentityMismatch", "The requested durable identifiers do not match the package.");
        }
        if (!manifest.Artifact.SupportedViews.SequenceEqual(SupportedViews, StringComparer.Ordinal))
        {
            throw Failure("UnsupportedViewContract", "The package advertises an unsupported view set.");
        }
    }

    private void Authorize(DurableSpikeOperation operation, DurableSpikeManifest manifest)
    {
        var request = new DurableSpikeAccessRequest(
            operation,
            manifest.CaptureId,
            manifest.Artifact.ArtifactId,
            manifest.Artifact.RepresentationKind);
        if (!_authorize(request))
        {
            throw Failure("AuthorizationDenied", $"Host policy denied durable capture operation '{operation}'.");
        }
    }

    private void ValidateSnapshot(ContentionSnapshot snapshot)
    {
        if (snapshot.Events.Count > _limits.MaxEvents)
        {
            throw Failure("EventCountLimitExceeded", "The retained contention event limit was exceeded.");
        }
        if (snapshot.Notes.Count > _limits.MaxNotes)
        {
            throw Failure("NoteCountLimitExceeded", "The contention note limit was exceeded.");
        }

        foreach (var note in snapshot.Notes)
        {
            ValidateString(note, "note");
        }
        foreach (var item in snapshot.Events)
        {
            ValidateString(item.CallSiteMethod, "callSiteMethod");
            ValidateString(item.CallSiteModule, "callSiteModule");
        }
    }

    private void ValidateString(string value, string field)
    {
        if (value.Length > _limits.MaxStringChars)
        {
            throw Failure("StringLimitExceeded", $"Field '{field}' exceeded the configured character limit.");
        }
    }

    private long EstimateCopyBytes(ContentionSnapshot snapshot)
    {
        long estimate = 256;
        foreach (var item in snapshot.Events)
        {
            estimate = checked(estimate + 96);
            estimate = checked(estimate + Encoding.UTF8.GetByteCount(item.CallSiteMethod));
            estimate = checked(estimate + Encoding.UTF8.GetByteCount(item.CallSiteModule));
        }
        foreach (var note in snapshot.Notes)
        {
            estimate = checked(estimate + Encoding.UTF8.GetByteCount(note));
        }
        return estimate;
    }

    private string ResolveExistingPackage(string relativeLocator)
    {
        ValidateRelativeLocator(relativeLocator);
        var resolved = SafeArtifactPath.ResolvePath(_root, relativeLocator, "locator");
        if (!Directory.Exists(resolved))
        {
            throw Failure("CaptureNotFound", $"No capture exists at locator '{relativeLocator}'.");
        }
        return resolved;
    }

    private string ResolveNewPackagePath(string relativeLocator)
    {
        ValidateRelativeLocator(relativeLocator);
        var resolved = SafeArtifactPath.ResolvePath(_root, relativeLocator, "locator");
        if (Directory.Exists(resolved) || File.Exists(resolved))
        {
            throw Failure("CaptureAlreadyExists", $"A capture already exists at locator '{relativeLocator}'.");
        }
        return resolved;
    }

    private static void ValidateRelativeLocator(string relativeLocator)
    {
        if (string.IsNullOrWhiteSpace(relativeLocator)
            || Path.IsPathRooted(relativeLocator)
            || relativeLocator.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(static part => part == ".."))
        {
            throw Failure("InvalidLocator", "Capture locators must be relative and cannot contain '..'.");
        }
    }

    private static string ResolvePackageMember(string packagePath, string relativePath)
    {
        ValidateRelativeLocator(relativePath);
        return SafeArtifactPath.ResolvePath(packagePath, relativePath, "representationPath");
    }

    private void EnsurePackageWithinLimits(string packagePath)
    {
        var files = Directory.EnumerateFiles(packagePath, "*", SearchOption.AllDirectories)
            .Take(_limits.MaxPackageFiles + 1)
            .ToList();
        if (files.Count > _limits.MaxPackageFiles)
        {
            throw Failure("PackageFileCountLimitExceeded", "The package file-count limit was exceeded.");
        }
        EnsureAtMost(files.Sum(static file => new FileInfo(file).Length), _limits.MaxPackageBytes, "PackageLimitExceeded");
    }

    private static long PackageSize(string packagePath)
        => Directory.EnumerateFiles(packagePath, "*", SearchOption.AllDirectories)
            .Sum(static path => new FileInfo(path).Length);

    private static void ValidateFile(string path, long expectedLength, string expectedHash, long maximumLength)
    {
        if (!File.Exists(path))
        {
            throw Failure("MissingPackageMember", $"Required package member '{Path.GetFileName(path)}' is missing.");
        }
        var actualLength = new FileInfo(path).Length;
        EnsureAtMost(actualLength, maximumLength, "InputLimitExceeded");
        if (actualLength != expectedLength || !string.Equals(HashFile(path), expectedHash, StringComparison.Ordinal))
        {
            throw Failure("IntegrityMismatch", $"Package member '{Path.GetFileName(path)}' failed integrity validation.");
        }
    }

    private static T ReadJsonBounded<T>(string path, long maximumLength, string limitCode)
    {
        if (!File.Exists(path))
        {
            throw Failure("MissingPackageMember", $"Required package member '{Path.GetFileName(path)}' is missing.");
        }
        EnsureAtMost(new FileInfo(path).Length, maximumLength, limitCode);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
            ?? throw Failure("InvalidJson", $"Package member '{Path.GetFileName(path)}' contained no value.");
    }

    private static void WriteJsonBounded<T>(string path, T value, long maximumLength, string limitCode)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var bounded = new BoundedWriteStream(file, maximumLength, limitCode);
        JsonSerializer.Serialize(bounded, value, JsonOptions);
        bounded.Flush();
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, FileStamp> SnapshotFiles(string path)
        => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .ToDictionary(
                file => Path.GetRelativePath(path, file),
                file => new FileStamp(
                    new FileInfo(file).Length,
                    File.GetLastWriteTimeUtc(file),
                    HashFile(file)),
                StringComparer.Ordinal);

    private static bool FileSnapshotsMatch(
        IReadOnlyDictionary<string, FileStamp> left,
        IReadOnlyDictionary<string, FileStamp> right)
        => left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static void EnsureAtMost(long actual, long maximum, string code)
    {
        if (actual > maximum)
        {
            throw Failure(code, $"Observed {actual} bytes, exceeding the configured limit of {maximum} bytes.");
        }
    }

    private static void ValidateVersion(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw Failure("InvalidVersion", $"Version field '{field}' is empty or too long.");
        }
    }

    private static DurableSpikeException Failure(string code, string message) => new(code, message);

    private sealed record CleanupEntry(string Path, DurableSpikeManifest Manifest, long SizeBytes);
    private sealed record FileStamp(long Length, DateTime LastWriteUtc, string Sha256);

    private sealed class BoundedWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maximumLength;
        private readonly string _limitCode;
        private long _written;

        internal BoundedWriteStream(Stream inner, long maximumLength, string limitCode)
        {
            _inner = inner;
            _maximumLength = maximumLength;
            _limitCode = limitCode;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Reserve(count);
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Reserve(buffer.Length);
            _inner.Write(buffer);
        }

        private void Reserve(int count)
        {
            if (_written + count > _maximumLength)
            {
                throw Failure(
                    _limitCode,
                    $"Serialization would exceed the configured limit of {_maximumLength} bytes.");
            }
            _written += count;
        }
    }
}

internal sealed record DurableSpikeManifest(
    string ContractVersion,
    string MinimumReaderContractVersion,
    string WriterVersion,
    string CaptureId,
    DateTimeOffset CreatedAt,
    DateTimeOffset RetentionExpiresAt,
    DurableSpikeTarget Target,
    DurableSpikeArtifactDescriptor Artifact,
    DurableSpikeBudgetAccounting Accounting,
    string? DerivedFromCaptureId,
    string? RecoveryReason,
    bool RecoveryTailUnknown,
    string? DerivationReaderVersion);

internal sealed record DurableSpikeArtifactDescriptor(
    string ArtifactId,
    string RepresentationKind,
    string RepresentationVersion,
    string RelativePath,
    IReadOnlyList<string> SupportedViews,
    string StructuredQualitySchema,
    string StructuredQualityState);

internal sealed record DurableSpikeBudgetAccounting(
    long EstimatedCopyBytes,
    long ActualRepresentationBytes,
    long ActiveBatchBytes);

internal sealed record DurableSpikeSeal(
    string CaptureId,
    long ManifestLength,
    string ManifestSha256,
    long RepresentationLength,
    string RepresentationSha256);

internal sealed class DurableSpikeLeaseRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LeaseState> _states;

    internal DurableSpikeLeaseRegistry(StringComparer pathComparer)
    {
        _states = new Dictionary<string, LeaseState>(pathComparer);
    }

    internal IDisposable AcquireRead(string key)
    {
        lock (_gate)
        {
            var state = GetOrCreate(key);
            if (state.Writer)
            {
                throw new DurableSpikeException("LeaseConflict", "An exclusive package operation is active.");
            }
            state.Readers++;
            return new Lease(this, key, exclusive: false);
        }
    }

    internal IDisposable AcquireExclusive(string key)
    {
        if (!TryAcquireExclusive(key, out var lease))
        {
            throw new DurableSpikeException("LeaseConflict", "The package has an active reader or writer.");
        }
        return lease!;
    }

    internal bool TryAcquireExclusive(string key, out IDisposable? lease)
    {
        lock (_gate)
        {
            var state = GetOrCreate(key);
            if (state.Writer || state.Readers > 0)
            {
                lease = null;
                return false;
            }
            state.Writer = true;
            lease = new Lease(this, key, exclusive: true);
            return true;
        }
    }

    private LeaseState GetOrCreate(string key)
    {
        if (!_states.TryGetValue(key, out var state))
        {
            state = new LeaseState();
            _states.Add(key, state);
        }
        return state;
    }

    private void Release(string key, bool exclusive)
    {
        lock (_gate)
        {
            var state = _states[key];
            if (exclusive)
            {
                state.Writer = false;
            }
            else
            {
                state.Readers--;
            }
            if (!state.Writer && state.Readers == 0)
            {
                _states.Remove(key);
            }
        }
    }

    private sealed class LeaseState
    {
        internal bool Writer { get; set; }
        internal int Readers { get; set; }
    }

    private sealed class Lease : IDisposable
    {
        private DurableSpikeLeaseRegistry? _owner;
        private readonly string _key;
        private readonly bool _exclusive;

        internal Lease(DurableSpikeLeaseRegistry owner, string key, bool exclusive)
        {
            _owner = owner;
            _key = key;
            _exclusive = exclusive;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(_key, _exclusive);
        }
    }
}
