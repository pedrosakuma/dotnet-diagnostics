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

public sealed class FrameVariableAttributionTests
{
    private static FrameIdentity Id(ulong sp, ulong ip, ulong m) => new(sp, ip, m);

    [Fact]
    public void SharedStackPointer_DistinctIdentity_AttributesOnlyToOwner()
    {
        var runtime1 = Id(100, 1, 7);
        var runtime2 = Id(100, 2, 7);
        var managed = Id(100, 3, 8);
        var r = FrameVariableAttribution.Attribute(
            new[] { runtime1, runtime2, managed },
            new[] { (managed, "local") });

        r.PerFrame[0].Should().BeEmpty();
        r.PerFrame[1].Should().BeEmpty();
        r.PerFrame[2].Should().Equal("local");
        r.Ambiguous.Should().Be(0);
    }

    [Fact]
    public void IndistinguishableFrames_ReportedAmbiguousNotDuplicated()
    {
        var a = Id(100, 1, 7);
        var r = FrameVariableAttribution.Attribute(new[] { a, a }, new[] { (a, "x") });

        r.PerFrame.SelectMany(l => l).Should().BeEmpty();
        r.Ambiguous.Should().Be(1);
    }

    [Fact]
    public void StackPointerOnlyMatch_UniqueFrame_Attributes()
    {
        var r = FrameVariableAttribution.Attribute(new[] { Id(100, 1, 7), Id(200, 5, 7) }, new[] { (Id(200, 9, 7), "x") });

        r.PerFrame[1].Should().Equal("x");
    }

    [Fact]
    public void SharedStackPointer_NoIdentityMatch_IsAmbiguous()
    {
        var r = FrameVariableAttribution.Attribute(new[] { Id(100, 1, 7), Id(100, 2, 8) }, new[] { (Id(100, 9, 9), "x") });

        r.Ambiguous.Should().Be(1);
        r.PerFrame.SelectMany(l => l).Should().BeEmpty();
    }

    [Fact]
    public void UnknownFrame_CountedUnmatched()
    {
        var r = FrameVariableAttribution.Attribute(new[] { Id(100, 1, 7) }, new[] { (Id(300, 1, 7), "x") });

        r.Unmatched.Should().Be(1);
    }
}
