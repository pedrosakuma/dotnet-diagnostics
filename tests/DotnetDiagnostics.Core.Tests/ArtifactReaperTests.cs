using DotnetDiagnostics.Core.Artifacts;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

public sealed class ArtifactReaperTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemArtifactLifecycle _lifecycle;

    public ArtifactReaperTests()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "test-artifacts", nameof(ArtifactReaperTests), Guid.NewGuid().ToString("N"));
        _lifecycle = new FileSystemArtifactLifecycle(new TestArtifactRootProvider(_root));
    }

    [Fact]
    public void Sweep_PrunesAgedAndKeepsFresh()
    {
        var now = DateTimeOffset.UtcNow;
        Write("aged.dmp", now.AddHours(-30));
        Write("fresh.dmp", now.AddMinutes(-5));

        var reaper = new ArtifactReaper(_lifecycle);
        var removed = reaper.Sweep(TimeSpan.FromHours(24), now);

        removed.Should().Be(1);
        File.Exists(Path.Combine(_root, "fresh.dmp")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "aged.dmp")).Should().BeFalse();
    }

    [Fact]
    public void Sweep_DisabledTtl_IsNoOp()
    {
        Write("aged.dmp", DateTimeOffset.UtcNow.AddDays(-5));
        new ArtifactReaper(_lifecycle).Sweep(TimeSpan.Zero).Should().Be(0);
        File.Exists(Path.Combine(_root, "aged.dmp")).Should().BeTrue();
    }

    [Theory]
    [InlineData(null, 24)]
    [InlineData("12", 12)]
    [InlineData("0", 0)]
    [InlineData("-1", 0)]
    [InlineData("NaN", 0)]
    [InlineData("Infinity", 0)]
    [InlineData("garbage", 24)]
    public void ResolveTtl_HonoursEnvironment(string? envValue, double expectedHours)
    {
        var prev = Environment.GetEnvironmentVariable(ArtifactReaper.TtlHoursEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ArtifactReaper.TtlHoursEnvironmentVariable, envValue);
            ArtifactReaper.ResolveTtl().Should().Be(TimeSpan.FromHours(expectedHours));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ArtifactReaper.TtlHoursEnvironmentVariable, prev);
        }
    }

    private void Write(string relative, DateTimeOffset modified)
    {
        var path = Path.Combine(_root, relative);
        File.WriteAllBytes(path, new byte[8]);
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
