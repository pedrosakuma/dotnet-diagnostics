using DotnetDiagnostics.Core.OffCpu;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

using SyscallEvent = PerfSyscallScriptParser.SyscallEvent;

/// <summary>
/// Exercises <see cref="SyscallIntervalIndex"/>'s enter/exit pairing and lookup logic in
/// isolation from the perf process plumbing (issue #829).
/// </summary>
public sealed class SyscallIntervalIndexTests
{
    [Fact]
    public void Lookup_ReturnsSyscallId_WhenTimestampFallsInsideAClosedInterval()
    {
        var events = new List<SyscallEvent>
        {
            new(Tid: 1000, TimestampSeconds: 1.000, SyscallId: 202, IsEnter: true),  // futex enter
            new(Tid: 1000, TimestampSeconds: 1.240, SyscallId: 202, IsEnter: false), // futex exit
        };

        var index = SyscallIntervalIndex.Build(events, captureEndTs: 5.0);

        index.Lookup(1000, 1.100).Should().Be(202, "the block point (1.1s) falls inside the futex enter/exit window");
        index.Lookup(1000, 0.500).Should().BeNull("before the syscall started");
        index.Lookup(1000, 2.000).Should().BeNull("after the syscall completed");
    }

    [Fact]
    public void Lookup_ReturnsSyscallId_ForStillOpenInterval_AtCaptureEnd()
    {
        // Thread entered a syscall and never exited before the capture window ended — mirrors
        // the sched_switch sampler's own IsCensored span handling: still attributable, just a
        // lower-bound-until-capture-end interval instead of a closed one.
        var events = new List<SyscallEvent>
        {
            new(Tid: 2000, TimestampSeconds: 4.000, SyscallId: 232, IsEnter: true), // epoll_wait, never exits
        };

        var index = SyscallIntervalIndex.Build(events, captureEndTs: 5.0);

        index.Lookup(2000, 4.999).Should().Be(232);
        index.Lookup(2000, 5.500).Should().BeNull("beyond the capture end bound");
    }

    [Fact]
    public void Lookup_ReturnsNull_ForUnknownTid()
    {
        var index = SyscallIntervalIndex.Build(new List<SyscallEvent>(), captureEndTs: 1.0);
        index.Lookup(999, 0.5).Should().BeNull();
    }

    [Fact]
    public void Lookup_HandlesMultipleNonOverlappingIntervalsPerTid()
    {
        var events = new List<SyscallEvent>
        {
            new(Tid: 1, TimestampSeconds: 1.0, SyscallId: 0, IsEnter: true),   // read
            new(Tid: 1, TimestampSeconds: 1.1, SyscallId: 0, IsEnter: false),
            new(Tid: 1, TimestampSeconds: 2.0, SyscallId: 202, IsEnter: true), // futex
            new(Tid: 1, TimestampSeconds: 2.5, SyscallId: 202, IsEnter: false),
        };

        var index = SyscallIntervalIndex.Build(events, captureEndTs: 3.0);

        index.Lookup(1, 1.05).Should().Be(0);
        index.Lookup(1, 1.5).Should().BeNull("gap between the two syscalls — thread was running, not blocked in a syscall");
        index.Lookup(1, 2.25).Should().Be(202);
    }

    [Fact]
    public void Lookup_ReturnsSyscallId_ForStillOpenInterval_WhenCaptureEndIsPositiveInfinity()
    {
        // Regression coverage (issue #829 code review): the real caller
        // (PerfSchedOffCpuSampler.BuildSyscallIntervalIndexAsync) deliberately passes
        // double.PositiveInfinity rather than the max observed *syscall* timestamp, because
        // sched_switch and raw_syscalls are independent tracepoints and the sched_switch OUT
        // timestamp we look up against can legitimately be later than the last syscall line seen
        // in this pass. Using a finite "captureEndTs" derived only from syscall events would
        // wrongly close the open interval before that later OUT timestamp and drop the label.
        var events = new List<SyscallEvent>
        {
            new(Tid: 3000, TimestampSeconds: 1.000000, SyscallId: 202, IsEnter: true), // futex, never exits
        };

        var index = SyscallIntervalIndex.Build(events, captureEndTs: double.PositiveInfinity);

        // A block point observed well after the last syscall line in this (independent) pass.
        index.Lookup(3000, 1.000100).Should().Be(202);
        index.Lookup(3000, 999.0).Should().Be(202, "PositiveInfinity keeps the interval open for any later lookup within the same capture");
    }

    [Fact]
    public void HitCap_And_DroppedCount_ReportZero_WhenUnderCap()
    {
        var events = new List<SyscallEvent>
        {
            new(Tid: 1, TimestampSeconds: 1.0, SyscallId: 0, IsEnter: true),
            new(Tid: 1, TimestampSeconds: 1.1, SyscallId: 0, IsEnter: false),
        };

        var index = SyscallIntervalIndex.Build(events, captureEndTs: 3.0);

        index.HitCap.Should().BeFalse();
        index.DroppedCount.Should().Be(0);
    }
}
