using System.Runtime.InteropServices;

namespace DotnetDiagnostics.Core.Artifacts;

/// <summary>
/// Default <see cref="IArtifactLifecycle"/> over the directory tree rooted at
/// <see cref="IArtifactRootProvider.Root"/>. Every caller-supplied path is re-validated through
/// <see cref="SafeArtifactPath.ResolvePath(string, string, string)"/> so delete cannot escape the
/// root via <c>..</c>, an absolute path, or a symlink. Empty directories left behind by a delete
/// are pruned best-effort up to (but never including) the root.
/// </summary>
public sealed class FileSystemArtifactLifecycle : IArtifactLifecycle
{
    private readonly IArtifactRootProvider _rootProvider;

    public FileSystemArtifactLifecycle(IArtifactRootProvider rootProvider)
    {
        _rootProvider = rootProvider ?? throw new ArgumentNullException(nameof(rootProvider));
    }

    public string Root => _rootProvider.Root;

    public IReadOnlyList<ArtifactInfo> List(DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        if (!Directory.Exists(Root))
        {
            return Array.Empty<ArtifactInfo>();
        }

        var results = new List<ArtifactInfo>();
        // Skip reparse points (symlinks/junctions) so enumeration cannot escape the root into
        // a symlinked directory and report files we'd later delete outside MCP_ARTIFACT_ROOT.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };
        foreach (var path in Directory.EnumerateFiles(Root, "*", options))
        {
            try
            {
                results.Add(Describe(new FileInfo(path), now));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip files that vanished or are unreadable between enumeration and stat.
            }
        }

        results.Sort(static (a, b) => b.LastModifiedUtc.CompareTo(a.LastModifiedUtc));
        return results;
    }

    public ArtifactInfo Delete(string relativePath, DateTimeOffset? nowUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var now = nowUtc ?? DateTimeOffset.UtcNow;

        var resolved = SafeArtifactPath.ResolvePath(Root, relativePath, parameterName: nameof(relativePath));
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException($"Artifact not found under root: {relativePath}", resolved);
        }

        var info = Describe(new FileInfo(resolved), now);
        File.Delete(resolved);
        PruneEmptyParents(Path.GetDirectoryName(resolved));
        return info;
    }

    public IReadOnlyList<ArtifactInfo> Prune(TimeSpan ttl, DateTimeOffset? nowUtc = null)
    {
        if (ttl <= TimeSpan.Zero)
        {
            return Array.Empty<ArtifactInfo>();
        }

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var pruned = new List<ArtifactInfo>();
        foreach (var artifact in List(now))
        {
            if (artifact.AgeSeconds < ttl.TotalSeconds)
            {
                continue;
            }

            try
            {
                // Delete through the validated relative-path entrypoint so a symlinked file can
                // never resolve outside the root, even if one slipped past enumeration.
                Delete(artifact.RelativePath, now);
                pruned.Add(artifact);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or ArgumentException)
            {
                // Best-effort: another worker may be holding or already removed the file.
            }
        }

        return pruned;
    }

    private ArtifactInfo Describe(FileInfo file, DateTimeOffset now)
    {
        var modified = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        var ageSeconds = (long)Math.Max(0, (now - modified).TotalSeconds);
        var relative = Path.GetRelativePath(Root, file.FullName).Replace('\\', '/');
        return new ArtifactInfo
        {
            RelativePath = relative,
            AbsolutePath = file.FullName,
            SizeBytes = file.Length,
            LastModifiedUtc = modified,
            AgeSeconds = ageSeconds,
        };
    }

    private void PruneEmptyParents(string? directory)
    {
        var rootFull = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var cmp = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var current = directory;
        while (!string.IsNullOrEmpty(current))
        {
            var full = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, rootFull, cmp))
            {
                return;
            }

            try
            {
                if (!Directory.Exists(full) || Directory.EnumerateFileSystemEntries(full).Any())
                {
                    return;
                }

                Directory.Delete(full);
                current = Path.GetDirectoryName(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}
