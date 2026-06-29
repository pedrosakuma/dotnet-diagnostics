using DotnetDiagnostics.Core.Artifacts;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

public sealed class ArtifactLifecycleTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemArtifactLifecycle _lifecycle;

    public ArtifactLifecycleTests()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "test-artifacts", nameof(ArtifactLifecycleTests), Guid.NewGuid().ToString("N"));
        _lifecycle = new FileSystemArtifactLifecycle(new TestArtifactRootProvider(_root));
    }

    [Fact]
    public void List_ReturnsArtifactsNewestFirstWithSizes()
    {
        Write("old.dmp", 100, DateTimeOffset.UtcNow.AddHours(-3));
        Write("sub/new.nettrace", 50, DateTimeOffset.UtcNow.AddMinutes(-1));

        var listing = _lifecycle.List();

        listing.Should().HaveCount(2);
        listing[0].RelativePath.Should().Be("sub/new.nettrace");
        listing[0].SizeBytes.Should().Be(50);
        listing[1].RelativePath.Should().Be("old.dmp");
        listing[1].AgeSeconds.Should().BeGreaterThan(60 * 60);
    }

    [Fact]
    public void Delete_RemovesArtifactInsideRoot()
    {
        Write("trash.dmp", 10, DateTimeOffset.UtcNow);

        var deleted = _lifecycle.Delete("trash.dmp");

        deleted.RelativePath.Should().Be("trash.dmp");
        File.Exists(Path.Combine(_root, "trash.dmp")).Should().BeFalse();
        _lifecycle.List().Should().BeEmpty();
    }

    [Fact]
    public void Delete_RejectsTraversal()
    {
        var act = () => _lifecycle.Delete("../escape.dmp");
        act.Should().Throw<ArtifactPathException>();
    }

    [Fact]
    public void Delete_RejectsAbsoluteOutsideRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dmp");
        File.WriteAllText(outside, "x");
        try
        {
            var act = () => _lifecycle.Delete(outside);
            act.Should().Throw<ArtifactPathException>();
            File.Exists(outside).Should().BeTrue();
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Delete_MissingArtifact_Throws()
    {
        var act = () => _lifecycle.Delete("nope.dmp");
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Prune_RemovesOnlyAgedArtifacts()
    {
        var now = DateTimeOffset.UtcNow;
        Write("fresh.dmp", 1, now.AddMinutes(-30));
        Write("aged.dmp", 1, now.AddHours(-26));

        var pruned = _lifecycle.Prune(TimeSpan.FromHours(24), now);

        pruned.Should().ContainSingle().Which.RelativePath.Should().Be("aged.dmp");
        _lifecycle.List(now).Should().ContainSingle().Which.RelativePath.Should().Be("fresh.dmp");
    }

    [Fact]
    public void Prune_DisabledTtl_IsNoOp()
    {
        Write("aged.dmp", 1, DateTimeOffset.UtcNow.AddDays(-10));
        _lifecycle.Prune(TimeSpan.Zero).Should().BeEmpty();
        _lifecycle.List().Should().HaveCount(1);
    }

    private void Write(string relative, int size, DateTimeOffset modified)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}
