using DotnetDiagnostics.Cli;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

/// <summary>
/// Coverage for the per-command artifact sandbox used by the <c>session</c> REPL (issue #300). The
/// host is built once; the REPL re-points this provider at each command's resolved root and resets it
/// back to the session default afterwards, so <c>dump --out</c> / <c>get-bytes --dump-file</c> keep
/// their one-shot semantics without rebuilding the host.
/// </summary>
public sealed class MutableArtifactRootProviderTests : IDisposable
{
    private readonly string _sessionDefault =
        Path.Combine(Path.GetTempPath(), "mutable-root-default-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Default_Root_IsAbsoluteAndCreated()
    {
        var provider = new MutableArtifactRootProvider(_sessionDefault);

        Path.IsPathRooted(provider.Root).Should().BeTrue();
        provider.Root.Should().Be(Path.GetFullPath(_sessionDefault));
        Directory.Exists(provider.Root).Should().BeTrue();
    }

    [Fact]
    public void Set_RepointsRoot_AndCreatesDirectory()
    {
        var provider = new MutableArtifactRootProvider(_sessionDefault);
        var target = Path.Combine(Path.GetTempPath(), "mutable-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            provider.Set(target);

            provider.Root.Should().Be(Path.GetFullPath(target));
            Directory.Exists(provider.Root).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }

    [Fact]
    public void Reset_RestoresSessionDefault()
    {
        var provider = new MutableArtifactRootProvider(_sessionDefault);
        var sessionDefault = provider.Root;
        var target = Path.Combine(Path.GetTempPath(), "mutable-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            provider.Set(target);
            provider.Root.Should().NotBe(sessionDefault);

            provider.Reset();

            provider.Root.Should().Be(sessionDefault);
        }
        finally
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_sessionDefault))
        {
            Directory.Delete(_sessionDefault, recursive: true);
        }
    }
}
