using DotnetDiagnostics.Core.Artifacts;

namespace DotnetDiagnostics.Core.Tests;

public sealed class ManagedCaptureArtifactBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "capture-boundary-" + Guid.NewGuid().ToString("N"));

    public ManagedCaptureArtifactBoundaryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void GenericWritesRejectReservedNamespaceBeforeCreatingDirectories()
    {
        Assert.Throws<ArtifactPathException>(() => SafeArtifactPath.ResolveDirectory(_root, "captures/new", "unused"));
        Assert.False(Directory.Exists(Path.Combine(_root, "captures")));
    }

    [Fact]
    public void CaptureHelpersRetainTraversalAndOutsideRootChecks()
    {
        Assert.Throws<ArtifactPathException>(() => SafeArtifactPath.ResolveCaptureDirectory(_root, "../escape"));
        Assert.Throws<ArtifactPathException>(() => SafeArtifactPath.ResolveCapturePath(_root, "../escape"));
        var directory = SafeArtifactPath.ResolveCaptureDirectory(_root, "captures/test-package");
        Assert.Equal(directory, SafeArtifactPath.ResolveCapturePath(_root, "captures/test-package"));
    }

    [Fact]
    public void GenericReadsDeletesAndReapingCannotTouchManagedFiles()
    {
        var managed = CreateManagedFile();
        var ordinary = Path.Combine(_root, "ordinary.txt");
        File.WriteAllText(ordinary, "ordinary");
        File.SetLastWriteTimeUtc(managed, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(ordinary, DateTime.UtcNow.AddDays(-2));
        var lifecycle = new FileSystemArtifactLifecycle(new RootProvider(_root));
        Assert.Equal("ordinary.txt", Assert.Single(lifecycle.List()).RelativePath);
        Assert.Throws<ArtifactPathException>(() => SafeArtifactPath.ResolvePath(_root, managed));
        Assert.Throws<ArtifactPathException>(() => lifecycle.Delete(Path.GetRelativePath(_root, managed)));
        Assert.Single(lifecycle.Prune(TimeSpan.FromHours(1)));
        Assert.True(File.Exists(managed));
        Assert.False(File.Exists(ordinary));
    }

    [Fact]
    public void RerootingInsideManagedPackageDoesNotBypassBoundary()
    {
        var managed = CreateManagedFile();
        var package = Path.GetDirectoryName(managed)!;
        Assert.Throws<ArtifactPathException>(() => SafeArtifactPath.ResolvePath(package, Path.GetFileName(managed)));
        Assert.Throws<ArtifactPathException>(() => SafeArtifactPath.ResolveDirectory(package, "native", "unused"));
        var lifecycle = new FileSystemArtifactLifecycle(new RootProvider(package));
        Assert.Empty(lifecycle.List());
        Assert.Empty(lifecycle.Prune(TimeSpan.FromTicks(1)));
        Assert.True(File.Exists(managed));
    }

    [Fact]
    public void SymlinkAliasCannotBypassBoundary()
    {
        if (OperatingSystem.IsWindows()) return;
        var managed = CreateManagedFile();
        var alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, Path.GetDirectoryName(managed)!);
        Assert.Throws<ArtifactPathException>(() => SafeArtifactPath.ResolvePath(_root, "alias/data.sqlite"));
        Assert.Empty(new FileSystemArtifactLifecycle(new RootProvider(alias)).List());
    }

    [Fact]
    public void UnmarkedRootNamedCapturesKeepsOrdinaryArtifactBehavior()
    {
        var unrelated = Path.Combine(_root, "other", "captures");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "ordinary.txt"), "ordinary");
        Assert.Equal(Path.Combine(unrelated, "ordinary.txt"), SafeArtifactPath.ResolvePath(unrelated, "ordinary.txt"));
    }

    private string CreateManagedFile()
    {
        var directory = SafeArtifactPath.ResolveCaptureDirectory(_root, "captures/test-package");
        File.WriteAllText(Path.Combine(_root, "captures", ".capture-store"), "dotnet-diagnostics-captures/1");
        var file = Path.Combine(directory, "data.sqlite");
        File.WriteAllText(file, "owned");
        return file;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed record RootProvider(string Root) : IArtifactRootProvider;
}
