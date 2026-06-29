namespace DotnetDiagnostics.Core.Artifacts;

/// <summary>Result of <c>get_bytes(kind="list")</c>: every artifact under the root, newest first.</summary>
public sealed record ArtifactListingEnvelope
{
    public required string Root { get; init; }
    public required int Count { get; init; }
    public required long TotalSizeBytes { get; init; }
    public required IReadOnlyList<ArtifactInfo> Artifacts { get; init; }
}

/// <summary>Result of <c>get_bytes(kind="delete")</c>: the artifact that was removed.</summary>
public sealed record ArtifactDeletionEnvelope
{
    public required string Root { get; init; }
    public required ArtifactInfo Deleted { get; init; }
}
