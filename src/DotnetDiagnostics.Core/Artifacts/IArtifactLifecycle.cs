namespace DotnetDiagnostics.Core.Artifacts;

/// <summary>
/// Lifecycle operations over the files written below <see cref="IArtifactRootProvider.Root"/>:
/// enumerate them, delete one safely, and prune everything aged past a TTL. Centralised so the
/// MCP <c>get_bytes</c> list/delete views and the TTL reaper background service share one
/// sandboxed implementation (no path escapes the root — see <see cref="SafeArtifactPath"/>).
/// </summary>
public interface IArtifactLifecycle
{
    /// <summary>Absolute artifact-root path being managed.</summary>
    string Root { get; }

    /// <summary>
    /// Lists every regular file under the root (recursively), newest first. Ages are computed
    /// relative to <paramref name="nowUtc"/> (defaults to <see cref="DateTimeOffset.UtcNow"/>).
    /// </summary>
    IReadOnlyList<ArtifactInfo> List(DateTimeOffset? nowUtc = null);

    /// <summary>
    /// Deletes a single artifact named by a path relative to the root. The path is re-validated
    /// through the sandbox so traversal / absolute / symlink escapes are rejected and only files
    /// inside the root are removed.
    /// </summary>
    /// <returns>Metadata of the file that was deleted.</returns>
    ArtifactInfo Delete(string relativePath, DateTimeOffset? nowUtc = null);

    /// <summary>
    /// Deletes every artifact older than <paramref name="ttl"/> relative to <paramref name="nowUtc"/>.
    /// A non-positive TTL is a no-op (disabled). Returns the artifacts that were pruned.
    /// </summary>
    IReadOnlyList<ArtifactInfo> Prune(TimeSpan ttl, DateTimeOffset? nowUtc = null);
}
