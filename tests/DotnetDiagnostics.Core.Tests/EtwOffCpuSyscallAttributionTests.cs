using DotnetDiagnostics.Core.OffCpu;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Tests for the Windows off-CPU syscall/wait-reason attribution enrichment (issue #829) on
/// <see cref="EtwOffCpuSampler"/>. These exercise the OS-agnostic pieces of the enrichment —
/// <c>ResolveSyscallLabel</c>'s lookback-window correlation and <c>NormalizeWaitReason</c>'s
/// <c>KWAIT_REASON</c> → shared-vocabulary mapping — directly, without a live elevated Windows ETW
/// capture (which only runs on Windows CI; see the class remarks on <see cref="EtwOffCpuSampler"/>
/// and AGENTS.md for why that path can't be exercised in this sandbox). The generic
/// <c>TryGetIoLabel</c> extraction is not unit-tested here because it requires a concrete
/// <c>TraceEvent</c> instance from a real ETL/live session; it is a thin, low-risk wrapper around
/// TraceEvent's own <c>TaskName</c>/<c>OpcodeName</c> properties and is exercised end-to-end only by
/// a live capture.
/// </summary>
public sealed class EtwOffCpuSyscallAttributionTests
{
    [Theory]
    [InlineData("ExecutionDelay", "Sleep")]
    [InlineData("UserRequest", "Sync")]
    [InlineData("EventPairHigh", "Sync")]
    [InlineData("EventPairLow", "Sync")]
    [InlineData("LpcReceive", "Sync")]
    [InlineData("LpcReply", "Sync")]
    [InlineData("PageIn", "Disk")]
    [InlineData("PageOut", "Disk")]
    [InlineData("FreePage", "Other")]
    [InlineData("SystemAllocation", "Other")]
    [InlineData("VirtualMemory", "Other")]
    [InlineData("Executive", "Other")]
    [InlineData("Suspended", "Other")]
    [InlineData("Unknown", "Other")]
    [InlineData("SomeFutureEnumValueNotYetMapped", "Other")]
    public void NormalizeWaitReason_MapsKnownReasonsToSharedVocabulary(string waitReason, string expectedBucket)
    {
        EtwOffCpuSampler.NormalizeWaitReason(waitReason).Should().Be(expectedBucket);
    }

    [Fact]
    public void ResolveSyscallLabel_UsesIoEvent_WhenWithinLookbackWindow()
    {
        var lastIoByThread = new Dictionary<int, (double Ts, string Label)>
        {
            [42] = (10.000, "FileIO:Read"),
        };

        var label = EtwOffCpuSampler.ResolveSyscallLabel(
            tid: 42,
            blockedAtTs: 10.005,
            waitReason: "UserRequest",
            lastIoByThread: lastIoByThread);

        label.Should().Be("FileIO:Read");
    }

    [Fact]
    public void ResolveSyscallLabel_FallsBackToNormalizedWaitReason_WhenIoEventTooOld()
    {
        var lastIoByThread = new Dictionary<int, (double Ts, string Label)>
        {
            // Well outside the lookback window (see EtwOffCpuSampler.IoLookbackWindowSeconds).
            [42] = (9.000, "TcpIp:Send"),
        };

        var label = EtwOffCpuSampler.ResolveSyscallLabel(
            tid: 42,
            blockedAtTs: 10.005,
            waitReason: "ExecutionDelay",
            lastIoByThread: lastIoByThread);

        label.Should().Be("Sleep");
    }

    [Fact]
    public void ResolveSyscallLabel_FallsBackToNormalizedWaitReason_WhenNoIoEventForThread()
    {
        var lastIoByThread = new Dictionary<int, (double Ts, string Label)>
        {
            [99] = (10.000, "FileIO:Write"),
        };

        var label = EtwOffCpuSampler.ResolveSyscallLabel(
            tid: 42,
            blockedAtTs: 10.001,
            waitReason: "PageIn",
            lastIoByThread: lastIoByThread);

        label.Should().Be("Disk");
    }

    [Fact]
    public void ResolveSyscallLabel_IgnoresIoEvent_WhenItIsAfterTheBlockTimestamp()
    {
        // An I/O event logged AFTER the thread already blocked cannot be the cause of the block —
        // ResolveSyscallLabel only trusts entries at or before blockedAtTs.
        var lastIoByThread = new Dictionary<int, (double Ts, string Label)>
        {
            [42] = (10.010, "FileIO:Read"),
        };

        var label = EtwOffCpuSampler.ResolveSyscallLabel(
            tid: 42,
            blockedAtTs: 10.000,
            waitReason: "UserRequest",
            lastIoByThread: lastIoByThread);

        label.Should().Be("Sync");
    }
}
