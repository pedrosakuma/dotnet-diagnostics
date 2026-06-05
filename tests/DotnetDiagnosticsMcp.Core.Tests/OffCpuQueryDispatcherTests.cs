using DotnetDiagnosticsMcp.Core.OffCpu;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class OffCpuQueryDispatcherTests
{
    private const string Handle = "off-handle-1";

    [Fact]
    public void Dispatch_TopStacks_IsDefaultProjection()
    {
        var outcome = OffCpuQueryDispatcher.Dispatch(Artifact(), "topStacks", topN: 25, stackRank: null);

        outcome.Error.Should().BeNull();
        outcome.Data!.View.Should().Be("topStacks");
        outcome.Data.Stacks.Should().NotBeNull();
        outcome.Data.Stacks!.Should().HaveCount(2);
        outcome.Data.Threads.Should().BeNull();
        outcome.Data.Stack.Should().BeNull();
    }

    [Fact]
    public void Dispatch_ByThread_ReturnsThreadRollup()
    {
        var outcome = OffCpuQueryDispatcher.Dispatch(Artifact(), "byThread", topN: 25, stackRank: null);

        outcome.Error.Should().BeNull();
        outcome.Data!.View.Should().Be("byThread");
        outcome.Data.Threads.Should().NotBeNull();
        outcome.Data.Threads!.Should().HaveCount(2);
        outcome.Data.Stacks.Should().BeNull();
    }

    [Fact]
    public void Dispatch_UnknownView_FallsBackToTopStacks_PreservingRawView()
    {
        // Mirrors the server's original switch: an unrecognized view renders topStacks but the raw
        // (unnormalized) view string is echoed back unchanged.
        var outcome = OffCpuQueryDispatcher.Dispatch(Artifact(), "Bogus", topN: 25, stackRank: null);

        outcome.Error.Should().BeNull();
        outcome.Data!.View.Should().Be("Bogus");
        outcome.Data.Stacks.Should().NotBeNull();
    }

    [Fact]
    public void Dispatch_Stack_ReturnsRequestedRank()
    {
        var outcome = OffCpuQueryDispatcher.Dispatch(Artifact(), "stack", topN: 25, stackRank: 2);

        outcome.Error.Should().BeNull();
        outcome.Data!.View.Should().Be("stack");
        outcome.Data.Stack.Should().NotBeNull();
        outcome.Data.Stack!.LeafFrame.Should().Be("LeafB");
    }

    [Fact]
    public void Dispatch_Stack_MissingRank_InvalidArgument()
    {
        var outcome = OffCpuQueryDispatcher.Dispatch(Artifact(), "stack", topN: 25, stackRank: null);

        outcome.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public void Dispatch_Stack_OutOfRange_ReportsOutOfRange()
    {
        var outcome = OffCpuQueryDispatcher.Dispatch(Artifact(), "stack", topN: 25, stackRank: 99);

        outcome.Error!.Kind.Should().Be("OutOfRange");
    }

    [Fact]
    public void Dispatch_TopNBelowOne_InvalidArgument()
    {
        var outcome = OffCpuQueryDispatcher.Dispatch(Artifact(), "topStacks", topN: 0, stackRank: null);

        outcome.Error!.Kind.Should().Be("InvalidArgument");
    }

    [Fact]
    public void SessionViews_ListsThreeViews()
    {
        OffCpuQueryDispatcher.SessionViews.Should().Equal("topStacks", "byThread", "stack");
    }

    private static OffCpuSnapshotArtifact Artifact()
    {
        var stackA = new OffCpuStackHotspot("LeafA", 1200, 3, "Sleeping",
            new[] { new OffCpuFrame("App.dll", "LeafA"), new OffCpuFrame("App.dll", "RootA") });
        var stackB = new OffCpuStackHotspot("LeafB", 800, 2, "Waiting",
            new[] { new OffCpuFrame("App.dll", "LeafB"), new OffCpuFrame("App.dll", "RootB") });
        var threads = new[]
        {
            new OffCpuThreadView(101, "worker-1", 1200, 3, "LeafA"),
            new OffCpuThreadView(102, "worker-2", 800, 2, "LeafB"),
        };
        return new OffCpuSnapshotArtifact(
            ProcessId: 4242,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(5),
            TotalOffCpuMicros: 2000,
            SchedSwitches: 5,
            Stacks: new[] { stackA, stackB },
            Threads: threads,
            SymbolSource: "user+kernel");
    }
}
