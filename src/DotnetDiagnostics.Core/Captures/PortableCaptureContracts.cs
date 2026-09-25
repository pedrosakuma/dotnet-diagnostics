namespace DotnetDiagnostics.Core.Captures;

public sealed record PortableOperationKey(string Id, DateTimeOffset RequestedUtc);
public sealed record CaptureExportSelection(string CaptureId, string? Label);
public sealed record CaptureExportRequest(PortableOperationKey Operation, IReadOnlyList<CaptureExportSelection> Entries);
public sealed record CaptureImportRequest(PortableOperationKey Operation, long ArchiveBytes, string ArchiveSha256);
public sealed record PortableArtifactMapping(string EntryArtifactId, string OriginArtifactId, string LocalArtifactId);
public sealed record PortableEntryMapping(string EntryId, string? Label, string SourceCaptureId,
    string LocalCaptureId, IReadOnlyList<PortableArtifactMapping> Artifacts);
public enum PortableEntryState { Pending, Published, Failed, Cancelled, NotAttempted }
public sealed record PortableFailure(CaptureErrorCode Code, string Reason, string? EntryId,
    string? Limit, long? Observed, long? Maximum);
public sealed record PortableEntryResult(string EntryId, PortableEntryState State, PortableEntryMapping? Mapping,
    PortableFailure? Failure);
public sealed record PortableImportResult(string OperationId, string? BundleId, string ArchiveSha256,
    bool Complete, bool Cancelled, IReadOnlyList<PortableEntryResult> Entries, PortableFailure? Failure);
public sealed record PortableExportResult(string OperationId, string BundleId, long ArchiveBytes, string ArchiveSha256);
public sealed record PortableImportEntry(string EntryId, string? Label, CaptureInfo Source, CaptureFormatVersions Format);
public enum PortableAuthorizationPhase { Prepare, Publish }
public delegate ValueTask AuthorizePortableImport(IReadOnlyList<PortableImportEntry> entries,
    PortableAuthorizationPhase phase, string? publishingEntryId, CancellationToken cancellationToken);

/// <summary>Trusted host policy must authorize the whole capture, including every artifact's sensitive records.</summary>
public delegate ValueTask AuthorizePortableExport(CaptureInfo capture, CancellationToken cancellationToken);

/// <summary>Portable v1 ceilings. Operators may lower these bounds, never raise them.</summary>
public sealed record PortableCaptureOptions
{
    public int MaxEntries { get; init; } = 16;
    public long MaxArchiveBytes { get; init; } = 512L * 1024 * 1024;
    public long MaxUncompressedBytes { get; init; } = 512L * 1024 * 1024;
    public int MaxIndexBytes { get; init; } = 128 * 1024;
    public int MaxRowsPerTable { get; init; } = 2_000_000;
    public int MaxRowsPerCapture { get; init; } = 4_000_000;
    public int MaxTokensPerSnapshot { get; init; } = 2_000_000;
    public int MaxTokensPerCapture { get; init; } = 8_000_000;
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(600);

    internal void Validate()
    {
        if (MaxEntries is < 1 or > 16 || MaxArchiveBytes is < 1 or > 512L * 1024 * 1024 ||
            MaxUncompressedBytes is < 1 or > 512L * 1024 * 1024 || MaxIndexBytes is < 1 or > 128 * 1024 ||
            MaxRowsPerTable is < 1 or > 2_000_000 || MaxRowsPerCapture is < 1 or > 4_000_000 ||
            MaxTokensPerSnapshot is < 1 or > 2_000_000 || MaxTokensPerCapture is < 1 or > 8_000_000 ||
            OperationTimeout <= TimeSpan.Zero || OperationTimeout > TimeSpan.FromSeconds(600))
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Portable limits exceed supported finite bounds.");
    }
}

internal static class PortableBounds
{
    internal const int BufferBytes = 64 * 1024;
    internal const int ReceiptBytes = 256 * 1024;
    internal const long ReceiptReservation = ReceiptBytes * 2L;
    internal const long HostBytes = 32L * 1024 * 1024;
    internal const long StoreBytes = 4L * 1024 * 1024 * 1024;
    internal static int ReadSize(long remaining) =>
        remaining >= BufferBytes ? BufferBytes : checked((int)remaining + 1);

    internal static void Check(string name, long observed, long maximum)
    {
        if (observed > maximum)
        {
            var error = CapturePackage.Error(CaptureErrorCode.CapacityExceeded,
                FormattableString.Invariant($"{name}: observed={observed}, maximum={maximum}."));
            error.Data["PortableLimit"] = name;
            error.Data["PortableObserved"] = observed;
            error.Data["PortableMaximum"] = maximum;
            throw error;
        }
    }
}
