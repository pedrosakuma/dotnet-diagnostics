using DotnetDiagnostics.Core.Threads;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class FrameVariableResolverTests
{
    private static ThreadSnapshotArtifact DumpArtifact(string? dumpPath) => new(
        ThreadSnapshotOrigin.Dump, 2718, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(10),
        "Core", "10.0.0", Array.Empty<ManagedThread>(), Array.Empty<MonitorLockState>())
    {
        DumpFilePath = dumpPath,
    };

    private static ThreadSnapshotArtifact LiveArtifact(int pid) => new(
        ThreadSnapshotOrigin.Live, pid, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(10),
        "Core", "10.0.0", Array.Empty<ManagedThread>(), Array.Empty<MonitorLockState>());

    [Fact]
    public async Task Resolve_DumpOriginWithoutPath_Throws()
    {
        var resolver = new ClrMdFrameVariableResolver();
        var act = () => resolver.ResolveAsync(DumpArtifact(null), 1, false, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*retained dump path*");
    }

    [Fact]
    public async Task Resolve_LiveOriginWithoutPid_Throws()
    {
        var resolver = new ClrMdFrameVariableResolver();
        var act = () => resolver.ResolveAsync(LiveArtifact(0), 1, false, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*usable process id*");
    }
}
