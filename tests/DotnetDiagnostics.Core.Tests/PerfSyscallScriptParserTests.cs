using DotnetDiagnostics.Core.OffCpu;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Drives <see cref="PerfSyscallScriptParser"/> against representative <c>perf script -G</c>
/// output captured from the co-recorded <c>raw_syscalls:sys_enter</c>/<c>sys_exit</c>
/// tracepoints (issue #829). Fixtures mirror the header shape already validated by
/// <see cref="PerfSchedScriptParserTests"/> for sched_switch, since both tracepoints are
/// recorded in the same <c>perf record</c> invocation and rendered by the same tool.
/// </summary>
public sealed class PerfSyscallScriptParserTests
{
    [Fact]
    public async Task ParsesEnterAndExitEvents_ForTargetTid()
    {
        const string script = """
                    target  1000 [001]   1.000000: raw_syscalls:sys_enter: NR 202 (7ffd728cd07c, 0, 0, 0, 0, 0)
                    target  1000 [001]   1.240000: raw_syscalls:sys_exit: NR 202 = 0

            """;
        using var reader = new StringReader(script);

        var events = (await PerfSyscallScriptParser.ParseAsync(reader, new HashSet<int> { 1000 })).Events;

        events.Should().HaveCount(2);
        events[0].Tid.Should().Be(1000);
        events[0].SyscallId.Should().Be(202);
        events[0].IsEnter.Should().BeTrue();
        events[0].TimestampSeconds.Should().Be(1.000000);
        events[1].IsEnter.Should().BeFalse();
        events[1].TimestampSeconds.Should().Be(1.240000);
    }

    [Fact]
    public async Task IgnoresEventsForNonTargetTids()
    {
        const string script = """
                     noise   999 [000]   3.000000: raw_syscalls:sys_enter: NR 0 (0, 0, 0, 0, 0, 0)
                    target  1000 [000]   3.100000: raw_syscalls:sys_enter: NR 202 (0, 0, 0, 0, 0, 0)

            """;
        using var reader = new StringReader(script);

        var events = (await PerfSyscallScriptParser.ParseAsync(reader, new HashSet<int> { 1000 })).Events;

        events.Should().ContainSingle();
        events[0].Tid.Should().Be(1000);
    }

    [Fact]
    public async Task SkipsLinesItDoesNotUnderstand_WithoutThrowing()
    {
        // Interleaved sched_switch line (should never appear given -G/-e filtering upstream, but
        // the parser must degrade gracefully rather than crash if it does) plus a blank line.
        const string script = """
                    target  1000 [001]   1.000000: sched:sched_switch: prev_comm=target prev_pid=1000 prev_prio=120 prev_state=S ==> next_comm=swapper/1 next_pid=0 next_prio=120

                    target  1000 [001]   1.100000: raw_syscalls:sys_enter: NR 0 (0, 0, 0, 0, 0, 0)

            """;
        using var reader = new StringReader(script);

        var events = (await PerfSyscallScriptParser.ParseAsync(reader, new HashSet<int> { 1000 })).Events;

        events.Should().ContainSingle();
        events[0].SyscallId.Should().Be(0);
    }

    [Fact]
    public async Task ToleratesAlternateSyscallIdRendering()
    {
        // Some perf builds render "id=<n>" or "id: <n>" instead of "NR <n>" — tolerate both.
        const string script = """
                    target  1000 [001]   1.000000: raw_syscalls:sys_enter: id=98 args: ...

            """;
        using var reader = new StringReader(script);

        var events = (await PerfSyscallScriptParser.ParseAsync(reader, new HashSet<int> { 1000 })).Events;

        events.Should().ContainSingle();
        events[0].SyscallId.Should().Be(98);
    }

    [Fact]
    public async Task CapsParsedEventCount_AtInsertion_ReportingHitCapAndDroppedCount()
    {
        // Resource-boundedness (issue #829 / docs/resource-boundedness.md): once
        // MaxParsedEvents is reached, further matching lines must still be READ (draining the
        // pipe so a real perf script child process is never blocked writing to stdout) but no
        // longer appended to the in-memory list — capped at insertion, not accumulate-then-
        // truncate. Generate MaxParsedEvents + 7 valid enter lines for a single tid and confirm
        // the parser retains exactly the cap and reports the exact overflow count.
        const int overBy = 7;
        var lines = new System.Text.StringBuilder(PerfSyscallScriptParser.MaxParsedEvents * 64);
        for (var i = 0; i < PerfSyscallScriptParser.MaxParsedEvents + overBy; i++)
        {
            lines.Append($"target  1000 [001]   {1.0 + i * 0.000001:F6}: raw_syscalls:sys_enter: NR 0 (0, 0, 0, 0, 0, 0)\n");
        }
        using var reader = new StringReader(lines.ToString());

        var result = await PerfSyscallScriptParser.ParseAsync(reader, new HashSet<int> { 1000 });

        result.Events.Should().HaveCount(PerfSyscallScriptParser.MaxParsedEvents);
        result.HitCap.Should().BeTrue();
        result.DroppedCount.Should().Be(overBy);
    }

    [Fact]
    public async Task BelowCap_ReportsNoTruncation()
    {
        const string script = """
                    target  1000 [001]   1.000000: raw_syscalls:sys_enter: NR 0 (0, 0, 0, 0, 0, 0)

            """;
        using var reader = new StringReader(script);

        var result = await PerfSyscallScriptParser.ParseAsync(reader, new HashSet<int> { 1000 });

        result.HitCap.Should().BeFalse();
        result.DroppedCount.Should().Be(0);
    }
}
