namespace DotnetDiagnostics.Core.Artifacts;

/// <summary>
/// One file living under the artifact root (<see cref="IArtifactRootProvider.Root"/>). Used by
/// the lifecycle list/delete surface and the TTL reaper to enumerate and prune dumps, traces,
/// and method-byte captures that would otherwise accumulate in a sidecar's <c>/tmp</c>.
/// </summary>
public sealed record ArtifactInfo
{
    /// <summary>Path relative to the artifact root, using <c>/</c> separators. Safe to feed back
    /// into delete/get_bytes since it never escapes the root.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Absolute, fully-qualified path on the server.</summary>
    public required string AbsolutePath { get; init; }

    /// <summary>File size in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Last-write time in UTC.</summary>
    public required DateTimeOffset LastModifiedUtc { get; init; }

    /// <summary>Age in whole seconds relative to the supplied reference instant.</summary>
    public required long AgeSeconds { get; init; }
}
