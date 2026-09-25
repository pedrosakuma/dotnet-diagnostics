namespace DotnetDiagnostics.Core.Captures;

/// <summary>Caller identity supplied by the trusted host, never read from a package as authority.</summary>
public sealed record CaptureAccess(string OwnerId, bool AllOwners = false);

public sealed record CaptureCreateRequest(string Name, string? GroupId = null);
public sealed record CaptureReference(string CaptureId);
public enum CaptureState { Recording = 0, Sealed = 1, Interrupted = 2 }
public enum CaptureFieldKind { Null = 0, Text = 1, SignedInteger = 2, FloatingPoint = 3, Boolean = 4 }

/// <summary>A scalar, with exactly the slot designated by Kind populated; units remain explicit.</summary>
public sealed record CaptureField(
    string Name, CaptureFieldKind Kind, string? StringValue = null, long? Int64Value = null,
    double? DoubleValue = null, bool? BooleanValue = null, string? Unit = null);

public sealed record CaptureRecord(
    DateTimeOffset? Timestamp = null, long? ThreadId = null, string? Category = null,
    string? Name = null, double? NumericValue = null, long? DurationNanoseconds = null,
    string? Unit = null, IReadOnlyList<CaptureField>? Fields = null);

/// <summary>Keyset pagination, ordered by occurrence ID; nullable dimensions are not coerced to zero.</summary>
public sealed record CaptureRecordQuery(
    string ArtifactId, DateTimeOffset? From = null, DateTimeOffset? To = null,
    long? ThreadId = null, string? Category = null, string? Name = null,
    long AfterRecordId = 0, int PageSize = 100);

public sealed record CaptureRecordEntry(long RecordId, CaptureRecord Record);
public sealed record CaptureRecordPage(
    IReadOnlyList<CaptureRecordEntry> Records, long? NextAfterRecordId, long AccountedBytes = 0);
/// <summary>Version identifies the producer's representation; Kind is bound to the owning artifact.</summary>
public sealed record CaptureSnapshot(int Version, ReadOnlyMemory<byte> Utf8Json, string? Kind = null);
/// <summary>Already available producer facts only. These fields confer no ownership or authorization.</summary>
public sealed record CaptureArtifactProvenance(
    int? ProcessId = null, string? ProducingTool = null, string? OriginalHandleOrigin = null,
    DateTimeOffset? StartedAt = null, TimeSpan? Duration = null, DateTimeOffset? ProcessStartUtc = null,
    string? RuntimeName = null, string? RuntimeVersion = null);

public sealed record CaptureArtifactInfo(
    string ArtifactId, string Kind, string Name, CaptureArtifactProvenance? Provenance = null);

/// <summary>Source format axes. RequiredReaderVersion is not the identity of the executing reader.</summary>
public sealed record CaptureFormatVersions(
    int PackageVersion, int SchemaVersion, int RecordVersion, int IndexVersion,
    int WriterVersion, int RequiredReaderVersion);

public sealed record CaptureReaderIdentity(string Implementation, int Version);

/// <summary>
/// Offered = Persisted + RecordRejected + QueueRejected + StorageRejected + Pending.
/// Accepted is cumulative queue admission, not a second disjoint population.
/// SourceRejected is unknown until explicitly supplied by the producer.
/// </summary>
public sealed record CaptureQuality(
    long Offered = 0, long Accepted = 0, long Persisted = 0, long RecordRejected = 0,
    long QueueRejected = 0, long StorageRejected = 0, long Pending = 0,
    long? SourceRejected = null, bool Interrupted = false, bool UnknownTail = false, long SnapshotRejected = 0)
{
    public bool IsIncomplete => Interrupted || UnknownTail || RecordRejected != 0 ||
        QueueRejected != 0 || StorageRejected != 0 || SourceRejected > 0 || SnapshotRejected != 0;
    public bool IsComplete => !IsIncomplete && SourceRejected == 0 && Pending == 0;
}

public sealed record CaptureWriterMetrics(
    CaptureQuality Quality, long LogicalBytes, long QueueBytes, int QueueRecords,
    long Transactions, int LargestBatch, long ObservedPackageBytes,
    string? JournalMode = null, long? Synchronous = null, long? DatabasePageLimit = null);

public sealed record CaptureInfo(
    string CaptureId, string OwnerId, string Name, string? GroupId, DateTimeOffset CreatedUtc,
    CaptureState State, IReadOnlyList<CaptureArtifactInfo> Artifacts, CaptureQuality Quality,
    string? DerivedFrom = null, IReadOnlyDictionary<string, string>? SourceHashes = null);

public sealed record CaptureCatalogPage(IReadOnlyList<CaptureInfo> Captures, string? NextAfterCaptureId);
public enum CaptureErrorCode
{
    InvalidInput, NotFound, Forbidden, Busy, CapacityExceeded, Incomplete, UnsupportedFormat,
    CorruptPackage, UnsafePath, StorageFailure, Deleted, Closed
}

public sealed class CaptureStoreException : Exception
{
    public CaptureStoreException(CaptureErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;
    public CaptureErrorCode Code { get; }
}

/// <summary>Provisional configuration bounds, not a universal measured capacity recommendation.</summary>
public sealed record CaptureStoreOptions
{
    public int QueueRecords { get; init; } = 8192;
    /// <summary>Live queued/in-flight logical reservation, released after commit or failure; not measured RAM.</summary>
    public long QueueBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxRecordBytes { get; init; } = 64 * 1024;
    public int MaxFields { get; init; } = 64;
    public int BatchRecords { get; init; } = 256;
    public TimeSpan MaxBatchAge { get; init; } = TimeSpan.FromMilliseconds(50);
    /// <summary>
    /// Cumulative capture-lifetime admitted record and snapshot payload budget, not live RAM.
    /// Successful commits do not release it: committed evidence still consumes the capture budget.
    /// </summary>
    public long MaxLogicalBytes { get; init; } = 128 * 1024 * 1024;
    public long MaxDatabaseBytes { get; init; } = 256 * 1024 * 1024;
    public long MaxPackageBytes { get; init; } = 512 * 1024 * 1024;
    public long MaxStoreBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    public int MaxCaptures { get; init; } = 256;
    public int MaxActiveWriters { get; init; } = 2;
    public int MaxSnapshotBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxArtifacts { get; init; } = 64;
    public int StringCacheEntries { get; init; } = 1024;
    public long StringCacheBytes { get; init; } = 1024 * 1024;
    public int MaxQueryPageSize { get; init; } = 1000;
    public int MaxQueryPageBytes { get; init; } = 1024 * 1024;
    public int MaxCatalogPageSize { get; init; } = 100;

    internal void Validate()
    {
        if (QueueRecords is < 1 or > 8192 || QueueBytes is < 256 or > 16 * 1024 * 1024 ||
            MaxRecordBytes is < 128 or > 64 * 1024 || MaxFields is < 0 or > 64 ||
            BatchRecords is < 1 or > 256 || MaxBatchAge <= TimeSpan.Zero || MaxBatchAge > TimeSpan.FromSeconds(1) ||
            MaxLogicalBytes is < 128 or > 128 * 1024 * 1024 ||
            MaxDatabaseBytes is < 64 * 1024 or > 256 * 1024 * 1024 ||
            MaxPackageBytes < MaxDatabaseBytes || MaxPackageBytes > 512 * 1024 * 1024 ||
            MaxStoreBytes < MaxPackageBytes || MaxCaptures is < 1 or > 256 ||
            MaxActiveWriters is < 1 or > 2 || MaxSnapshotBytes is < 1 or > 8 * 1024 * 1024 ||
            MaxArtifacts is < 1 or > 64 || StringCacheEntries is < 0 or > 4096 ||
            StringCacheBytes is < 0 or > 4 * 1024 * 1024 ||
            MaxQueryPageSize is < 1 or > 1000 || MaxQueryPageBytes is < 1024 or > 16 * 1024 * 1024 ||
            MaxCatalogPageSize is < 1 or > 100)
            throw new CaptureStoreException(CaptureErrorCode.InvalidInput, "Capture limits are outside supported finite bounds.");
    }
}
