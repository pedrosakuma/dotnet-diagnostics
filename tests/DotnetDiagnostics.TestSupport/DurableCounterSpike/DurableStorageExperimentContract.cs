using System.Security.Cryptography;
using System.Text.Json;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike;

internal static class DurableStorageExperimentVersions
{
    internal const string FoundationSchema = "dc5-storage-foundation/1";
    internal const string PackageContract = "durable-counter-package/1";
    internal const string RecordSchema = "durable-counter-record/1";
    internal const string SealSchema = "durable-counter-seal/1";
    internal const string HashAlgorithm = "sha256";
}

internal static class DurableStoragePackageLayout
{
    internal const string ManifestFile = "manifest.json";
    internal const string SealFile = "seal.json";
    internal const string CanonicalDirectory = "canonical";
    internal const string QueryDirectory = "query";
    internal const string RecoveryDirectory = "recovery";

    internal static string CanonicalMember(string fileName) => $"{CanonicalDirectory}/{fileName}";
    internal static string QueryMember(string fileName) => $"{QueryDirectory}/{fileName}";
}

internal static class DurableStorageLogicalSchema
{
    internal static IReadOnlyList<DurableStorageColumn> Columns { get; } =
    [
        new("sequence", "int64", Nullable: false),
        new("provider", "utf8", Nullable: false),
        new("name", "utf8", Nullable: false),
        new("displayName", "utf8", Nullable: false),
        new("unit", "utf8", Nullable: true),
        new("value", "float64-finite", Nullable: false),
        new("kind", "Mean|Sum", Nullable: false),
        new("intervalSec", "float64", Nullable: true),
        new("intervalState", "missing|valid|nonpositive|nonfinite", Nullable: false),
        new("displayScaleTicks", "int64", Nullable: true),
        new("displayScaleState", "missing|valid|nonpositive", Nullable: false),
        new("sourceTimeTicks", "int64-100ns-relative", Nullable: true),
        new("clockDomain", "utf8", Nullable: false),
        new("clockOrigin", "utf8", Nullable: false),
        new("coverageGap", "unknown|noGap|gap", Nullable: false),
        new("resetState", "unknown", Nullable: false),
        new("encodedBytes", "int32", Nullable: false),
    ];
}

internal sealed record DurableStorageColumn(string Name, string Type, bool Nullable);

internal static class DurableStorageFrameContract
{
    internal const uint Magic = 0x44354342;
    internal const ushort Version = 1;
    internal const string Checksum = "sha256";
    internal const int MaximumRecordBytes = 4_096;
    internal const int MaximumRecordsPerBatch = 64;
    internal const int MaximumOwnedBytesPerBatch = 262_144;

    internal static IReadOnlyList<string> RequiredFields { get; } =
    [
        "magic",
        "version",
        "batchLength",
        "recordCount",
        "firstSequence",
        "lastSequence",
        "payloadSha256",
        "commitFooter",
    ];
}

internal sealed record DurableStorageAdapterIdentity(
    string Id,
    string Version,
    string ConfigurationSchema,
    string CommitAcknowledgement);

internal sealed record DurableStorageAdapterCreateRequest(
    string CaptureId,
    string ArtifactId,
    string StagingRoot,
    JsonElement Configuration,
    DurableCounterPipelineLimits Limits,
    IDurableStorageFaultController Faults);

internal sealed record DurableStorageOpenRequest(
    string PackageRoot,
    DurableStoragePackageManifest Manifest,
    DurableCounterPipelineLimits Limits);

internal interface IDurableCounterStorageAdapterFactory
{
    DurableStorageAdapterIdentity Identity { get; }

    IDurableCounterStorageAdapter Create(DurableStorageAdapterCreateRequest request);

    IDurableCounterReadonlyStore OpenReadonly(DurableStorageOpenRequest request);

    ValueTask<DurableStorageRecoveryResult> RecoverAsync(
        DurableStorageRecoveryRequest request,
        CancellationToken cancellationToken);
}

internal interface IDurableCounterStorageAdapter : IDurableCounterSink, IAsyncDisposable
{
    DurableStorageAdapterIdentity Identity { get; }

    /// <summary>Owned and disposed by the adapter; callers must not dispose this borrowed reader.</summary>
    IDurableCounterReadonlyStore Reader { get; }

    ValueTask<DurableStoragePreSealResult> FinalizePreSealAsync(
        DurableCounterQualityReport finalPipelineQuality,
        CancellationToken cancellationToken);
}

internal interface IDurableCounterReadonlyStore : IAsyncDisposable
{
    ValueTask<IReadOnlyList<DurableCounterSummaryRow>> SummaryAsync(CancellationToken cancellationToken);

    ValueTask<DurableCounterSeriesPage> SeriesAsync(
        string provider,
        string name,
        long? afterSequence,
        int pageSize,
        CancellationToken cancellationToken);

    ValueTask<DurableStorageQualityReport> QualityAsync(CancellationToken cancellationToken);
}

internal sealed record DurableStorageQualityReport(
    DurableCounterQualityReport? FinalPipelineQuality,
    long RetainedRecords,
    bool VolatileTailUnknown);

internal static class DurableStorageQualityRules
{
    internal static void Validate(DurableStorageQualityReport report)
    {
        if (report.RetainedRecords < 0)
        {
            throw new DurableStorageExperimentException("InvalidRetainedCount", "Retained record count cannot be negative.");
        }
        if (report.VolatileTailUnknown)
        {
            if (report.FinalPipelineQuality is not null)
            {
                throw new DurableStorageExperimentException(
                    "RecoveryQualityOverclaim",
                    "Recovery cannot claim terminal pipeline accounting for the derived capture.");
            }
            return;
        }
        var quality = report.FinalPipelineQuality
            ?? throw new DurableStorageExperimentException(
                "MissingFinalQuality",
                "A finalized capture must preserve the host's terminal pipeline quality.");
        var accounting = quality.Accounting;
        if (!accounting.IsCleanQuiescent
            || accounting.Offered < 0 || accounting.Rejected < 0 || accounting.Admitted < 0
            || accounting.Committed < 0 || accounting.FailedAfterAdmission < 0
            || accounting.AbandonedKnown < 0 || accounting.UnknownCommitOutcome < 0
            || quality.UnknownCommitOutcome != (accounting.UnknownCommitOutcome > 0)
            || (Int128)accounting.Offered != (Int128)accounting.Rejected + accounting.Admitted
            || (Int128)accounting.Admitted != (Int128)accounting.Committed + accounting.FailedAfterAdmission
                + accounting.AbandonedKnown + accounting.UnknownCommitOutcome)
        {
            throw new DurableStorageExperimentException(
                "InvalidFinalQuality",
                "Terminal quality must preserve quiescent pipeline accounting and commit uncertainty.");
        }
        if (report.RetainedRecords < accounting.Committed
            || (Int128)report.RetainedRecords > (Int128)accounting.Committed + accounting.UnknownCommitOutcome)
        {
            throw new DurableStorageExperimentException(
                "RetainedCountMismatch",
                "Retained records must include known commits and cannot exceed known plus uncertain commits.");
        }
    }

    internal static void Validate(DurableStorageQualityReport report, DurableStoragePackageManifest manifest)
    {
        Validate(report);
        if (report.VolatileTailUnknown != manifest.VolatileTailUnknown
            || !MatchesSnapshot(report.FinalPipelineQuality, manifest.FinalPipelineQuality))
        {
            throw new DurableStorageExperimentException(
                "QualitySnapshotMismatch",
                "Fresh-reader quality must preserve the host-validated manifest snapshot.");
        }
    }

    private static bool MatchesSnapshot(DurableCounterQualityReport? actual, DurableCounterQualityReport? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }
        // Record equality compares dictionary references; compare rejection entries by value.
        return actual with { Accounting = expected.Accounting } == expected
            && actual.Accounting with { Rejections = expected.Accounting.Rejections } == expected.Accounting
            && actual.Accounting.Rejections.Count == expected.Accounting.Rejections.Count
            && actual.Accounting.Rejections.All(entry =>
                expected.Accounting.Rejections.TryGetValue(entry.Key, out var value) && value == entry.Value);
    }
}

internal sealed record DurableStoragePreSealResult(
    IReadOnlyList<DurableStorageMember> CanonicalMembers,
    IReadOnlyList<DurableStorageMember> QueryMembers,
    long FinalBytes,
    string AdapterConfigurationDigest);

internal sealed record DurableStorageRecoveryRequest(
    string SourceCaptureId,
    string SourceArtifactId,
    string NewCaptureId,
    string NewArtifactId,
    string SourcePackageRoot,
    string RecoveryStagingRoot,
    string Reason);

internal sealed record DurableStorageRecoveryResult(
    string CaptureId,
    string ArtifactId,
    string DerivedFromCaptureId,
    string RecoveryReason,
    bool VolatileTailUnknown,
    IReadOnlyList<DurableStorageMember> RecoveredMembers);

internal static class DurableStorageRecoveryRules
{
    internal static void Validate(
        DurableStorageRecoveryRequest request,
        DurableStorageRecoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        if (string.Equals(request.SourceCaptureId, request.NewCaptureId, StringComparison.Ordinal)
            || string.Equals(request.SourceArtifactId, request.NewArtifactId, StringComparison.Ordinal))
        {
            throw new DurableStorageExperimentException(
                "RecoveryIdentityReuse",
                "Explicit recovery must publish new capture and artifact IDs.");
        }
        if (!string.Equals(result.CaptureId, request.NewCaptureId, StringComparison.Ordinal)
            || !string.Equals(result.ArtifactId, request.NewArtifactId, StringComparison.Ordinal)
            || !string.Equals(result.DerivedFromCaptureId, request.SourceCaptureId, StringComparison.Ordinal)
            || !string.Equals(result.RecoveryReason, request.Reason, StringComparison.Ordinal))
        {
            throw new DurableStorageExperimentException(
                "RecoveryProvenanceMismatch",
                "Recovery result identity or derivation provenance does not match the request.");
        }
        if (!result.VolatileTailUnknown)
        {
            throw new DurableStorageExperimentException(
                "RecoveryTailOverclaim",
                "Experimental recovery must preserve the volatile tail as unknown.");
        }
    }
}

internal sealed record DurableStorageMember(
    string RelativePath,
    long Length,
    string Sha256,
    DurableStorageMemberRole Role);

internal enum DurableStorageMemberRole
{
    CanonicalData,
    QueryIndex,
    Manifest,
    Seal,
    RecoveryOutput,
}

internal sealed record DurableStoragePackageManifest(
    string ContractVersion,
    string RecordSchemaVersion,
    string CaptureId,
    string ArtifactId,
    DateTimeOffset CreatedAt,
    DurableStorageAdapterIdentity Adapter,
    string ProtocolJsonSha256,
    string FixtureSha256,
    string PipelineCommit,
    IReadOnlyList<DurableStorageMember> Members,
    string? DerivedFromCaptureId,
    string? RecoveryReason,
    bool VolatileTailUnknown,
    DurableCounterQualityReport? FinalPipelineQuality);

internal sealed record DurableStoragePackageSeal(
    string SealVersion,
    string CaptureId,
    string ArtifactId,
    string ManifestSha256,
    IReadOnlyList<DurableStorageMember> CanonicalMembers);

internal static class DurableStorageSealRules
{
    internal static IReadOnlyList<DurableStorageMember> CanonicalOrder(
        IEnumerable<DurableStorageMember> members)
        => members.OrderBy(static member => member.RelativePath, StringComparer.Ordinal).ToArray();

    internal static void ValidatePreSeal(DurableStoragePreSealResult result, DurableCounterPipelineLimits limits)
    {
        ArgumentNullException.ThrowIfNull(result);
        var members = result.CanonicalMembers.Concat(result.QueryMembers).ToArray();
        if (members.Length == 0)
        {
            throw new DurableStorageExperimentException("MissingPackageMembers", "An adapter returned no package members.");
        }
        if (result.CanonicalMembers.Any(static member =>
                !IsMemberPath(member.RelativePath, DurableStoragePackageLayout.CanonicalDirectory))
            || result.QueryMembers.Any(static member =>
                !IsMemberPath(member.RelativePath, DurableStoragePackageLayout.QueryDirectory)))
        {
            throw new DurableStorageExperimentException(
                "InvalidPackageMember",
                "Package members must use their reserved directory and normalized relative paths.");
        }
        if (members.Select(static member => member.RelativePath).Distinct(StringComparer.Ordinal).Count() != members.Length)
        {
            throw new DurableStorageExperimentException("DuplicatePackageMember", "Package member paths must be unique.");
        }
        if (result.CanonicalMembers.Any(static member => member.Role != DurableStorageMemberRole.CanonicalData)
            || result.QueryMembers.Any(static member => member.Role != DurableStorageMemberRole.QueryIndex))
        {
            throw new DurableStorageExperimentException(
                "InvalidPackageMemberRole",
                "Pre-seal canonical and query member roles must match their declared collections.");
        }
        if (members.Any(static member => member.Length < 0
                || member.Sha256.Length != 64
                || !member.Sha256.All(Uri.IsHexDigit)))
        {
            throw new DurableStorageExperimentException(
                "InvalidPackageMember",
                "Package member length or SHA-256 metadata is invalid.");
        }
        if (result.FinalBytes < 0 || result.FinalBytes > 268_435_456)
        {
            throw new DurableStorageExperimentException(
                "PackageFinalBytesLimit",
                "The adapter result exceeds the frozen final package byte limit.");
        }
        if (result.AdapterConfigurationDigest.Length != 64
            || !result.AdapterConfigurationDigest.All(Uri.IsHexDigit))
        {
            throw new DurableStorageExperimentException(
                "InvalidAdapterConfigurationDigest",
                "Adapter configuration must be identified by a SHA-256 digest.");
        }
        if (limits.RecordEncodedBytes != 4_096
            || limits.BatchRecords != 64
            || limits.BatchOwnedBytes != 262_144)
        {
            throw new DurableStorageExperimentException(
                "ProtocolLimitMismatch",
                "Adapter finalization must use the frozen record and batch boundaries.");
        }
    }

    private static bool IsMemberPath(string path, string directory)
        => path.StartsWith($"{directory}/", StringComparison.Ordinal)
            && !path.Contains('\\')
            && !path.Contains(':')
            && !path.Split('/').Any(static part => string.IsNullOrWhiteSpace(part) || part is "." or "..");
}

internal enum DurableStorageFaultBarrier
{
    BeforeBatchWrite,
    BeforeCommit,
    AfterCommitBeforeAcknowledgement,
    AfterIndexesBeforeSeal,
    ConcurrentCaptureGate,
    StorageFullNextBatch,
}

internal sealed record DurableStorageFaultContext(
    DurableStorageFaultBarrier Barrier,
    int BatchOrdinal,
    IReadOnlyList<long> Sequences,
    DurableCounterCommitOutcome? KnownCommitOutcome);

internal interface IDurableStorageFaultController
{
    ValueTask ReachAsync(DurableStorageFaultContext context, CancellationToken cancellationToken);
}

internal sealed class NoDurableStorageFaults : IDurableStorageFaultController
{
    internal static NoDurableStorageFaults Instance { get; } = new();

    public ValueTask ReachAsync(DurableStorageFaultContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

internal sealed class DurableStorageAdapterRegistry
{
    private readonly IReadOnlyDictionary<string, IDurableCounterStorageAdapterFactory> _factories;

    internal DurableStorageAdapterRegistry(IEnumerable<IDurableCounterStorageAdapterFactory>? factories = null)
    {
        _factories = (factories ?? Array.Empty<IDurableCounterStorageAdapterFactory>())
            .ToDictionary(static factory => factory.Identity.Id, StringComparer.Ordinal);
    }

    internal IReadOnlyList<DurableStorageAdapterIdentity> Available
        => _factories.Values.Select(static factory => factory.Identity)
            .OrderBy(static item => item.Id, StringComparer.Ordinal).ToArray();

    internal IDurableCounterStorageAdapterFactory Require(string adapterId)
        => _factories.TryGetValue(adapterId, out var factory)
            ? factory
            : throw new DurableStorageExperimentException(
                "UnknownAdapter",
                $"No durable storage adapter is registered for '{adapterId}'.");
}

internal sealed record DurableStorageResolvedManifest(
    string Schema,
    bool ExecutionEnabled,
    int ProtocolRevision,
    string ProtocolJson,
    string ProtocolJsonSha256,
    string FixtureManifest,
    string FixtureManifestSha256,
    string PipelineCommit,
    string RuntimeBinary,
    string HostFacts,
    string ClockConversion,
    string? AdapterId,
    string? AdapterCommit,
    JsonElement? AdapterConfiguration);

internal sealed record DurableStorageManifestValidation(
    string Schema,
    bool ExecutionGateClosed,
    int ProtocolRevision,
    string ProtocolJsonSha256,
    string FixtureManifestSha256,
    IReadOnlyList<DurableStorageAdapterIdentity> AvailableAdapters);

internal static class DurableStorageExperimentFoundation
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static DurableStorageManifestValidation ValidateManifest(
        string repositoryRoot,
        string manifestPath,
        DurableStorageAdapterRegistry registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(registry);

        var manifest = JsonSerializer.Deserialize<DurableStorageResolvedManifest>(
            ReadBounded(manifestPath, 1_048_576),
            JsonOptions)
            ?? throw new DurableStorageExperimentException("InvalidManifest", "The resolved manifest was empty.");
        if (!string.Equals(manifest.Schema, DurableStorageExperimentVersions.FoundationSchema, StringComparison.Ordinal))
        {
            throw new DurableStorageExperimentException("UnsupportedManifestSchema", "The manifest schema is unsupported.");
        }
        if (manifest.ExecutionEnabled)
        {
            throw new DurableStorageExperimentException(
                "ExecutionGateClosed",
                "DC5 experiment execution is closed until adapters and a reviewed resolved run manifest are published.");
        }
        RequireIdentity(manifest.PipelineCommit, nameof(manifest.PipelineCommit));
        RequireIdentity(manifest.RuntimeBinary, nameof(manifest.RuntimeBinary));
        RequireIdentity(manifest.HostFacts, nameof(manifest.HostFacts));
        RequireIdentity(manifest.ClockConversion, nameof(manifest.ClockConversion));
        if (manifest.ProtocolRevision != 3)
        {
            throw new DurableStorageExperimentException("ProtocolRevisionMismatch", "Only accepted protocol revision 3 is supported.");
        }

        var protocolPath = ResolveRepositoryFile(repositoryRoot, manifest.ProtocolJson);
        var fixturePath = ResolveRepositoryFile(repositoryRoot, manifest.FixtureManifest);
        var protocolHash = HashFile(protocolPath);
        var fixtureHash = HashFile(fixturePath);
        RequireHash(manifest.ProtocolJsonSha256, protocolHash, "ProtocolHashMismatch");
        RequireHash(manifest.FixtureManifestSha256, fixtureHash, "FixtureHashMismatch");

        using var protocol = JsonDocument.Parse(ReadBounded(protocolPath, 1_048_576));
        if (protocol.RootElement.GetProperty("revision").GetInt32() != manifest.ProtocolRevision)
        {
            throw new DurableStorageExperimentException(
                "ProtocolRevisionMismatch",
                "The resolved protocol revision does not match the protocol document.");
        }

        if (manifest.AdapterId is not null)
        {
            RequireIdentity(manifest.AdapterCommit, nameof(manifest.AdapterCommit));
            registry.Require(manifest.AdapterId);
        }
        else if (manifest.AdapterCommit is not null || manifest.AdapterConfiguration is not null)
        {
            throw new DurableStorageExperimentException(
                "IncompleteAdapterIdentity",
                "Adapter commit/configuration requires an adapter ID.");
        }

        return new DurableStorageManifestValidation(
            manifest.Schema,
            ExecutionGateClosed: true,
            manifest.ProtocolRevision,
            protocolHash,
            fixtureHash,
            registry.Available);
    }

    internal static object Describe(DurableStorageAdapterRegistry registry)
        => new
        {
            schema = DurableStorageExperimentVersions.FoundationSchema,
            executionGate = "closed",
            packageContract = DurableStorageExperimentVersions.PackageContract,
            recordSchema = DurableStorageExperimentVersions.RecordSchema,
            sealSchema = DurableStorageExperimentVersions.SealSchema,
            hashAlgorithm = DurableStorageExperimentVersions.HashAlgorithm,
            availableAdapters = registry.Available,
            commands = new[] { "help", "describe", "validate-manifest" },
        };

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        if (!File.Exists(path))
        {
            throw new DurableStorageExperimentException("MissingFile", $"Required file '{path}' does not exist.");
        }
        var length = new FileInfo(path).Length;
        if (length > maximumBytes)
        {
            throw new DurableStorageExperimentException("InputLimitExceeded", $"File '{path}' exceeds {maximumBytes} bytes.");
        }
        return File.ReadAllBytes(path);
    }

    private static string ResolveRepositoryFile(string repositoryRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Split('/', '\\').Any(static part => part == ".."))
        {
            throw new DurableStorageExperimentException(
                "InvalidManifestPath",
                "Resolved manifest paths must be repository-relative without traversal.");
        }

        var root = Path.GetFullPath(repositoryRoot);
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new DurableStorageExperimentException("InvalidManifestPath", "Resolved manifest path escapes the repository.");
        }
        return resolved;
    }

    private static void RequireIdentity(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DurableStorageExperimentException("MissingIdentity", $"Resolved manifest field '{field}' is required.");
        }
    }

    private static void RequireHash(string declared, string actual, string code)
    {
        if (!string.Equals(declared, actual, StringComparison.Ordinal))
        {
            throw new DurableStorageExperimentException(code, "Resolved manifest hash does not match the referenced file.");
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

internal sealed class DurableStorageExperimentException : Exception
{
    internal DurableStorageExperimentException(string code, string message)
        : base(message) => Code = code;

    internal string Code { get; }
    internal Monitored.DescriptorObservationFailure? DescriptorFailure { get; init; }
}
