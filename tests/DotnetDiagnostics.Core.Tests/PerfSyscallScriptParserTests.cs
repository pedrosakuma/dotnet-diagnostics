using DotnetDiagnostics.Core.OffCpu;
using FluentAssertions;
using System.Globalization;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Drives <see cref="PerfSyscallScriptParser"/> against representative <c>perf script -G</c>
/// output captured from the target-scoped companion <c>raw_syscalls:sys_enter</c>/<c>sys_exit</c>
/// tracepoints (issues #829/#839).
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
    public async Task ParsesPidSlashTidHeader_ForTargetScopedPerfRecord()
    {
        const string script = """
                    target  1234/1000 [001]   1.000000: raw_syscalls:sys_enter: NR 202 (7ffd728cd07c, 0, 0, 0, 0, 0)

            """;
        using var reader = new StringReader(script);

        var events = (await PerfSyscallScriptParser.ParseAsync(reader, new HashSet<int> { 1000 })).Events;

        events.Should().ContainSingle();
        events[0].Tid.Should().Be(1000);
        events[0].SyscallId.Should().Be(202);
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
        using var reader = new StringReader(BuildCappedEnterLinesScript(overBy));

        var result = await PerfSyscallScriptParser.ParseAsync(reader, new HashSet<int> { 1000 });

        result.Events.Should().HaveCount(PerfSyscallScriptParser.MaxParsedEvents);
        result.HitCap.Should().BeTrue();
        result.DroppedCount.Should().Be(overBy);
    }

    /// <summary>
    /// Generates <see cref="PerfSyscallScriptParser.MaxParsedEvents"/> + <paramref name="overBy"/>
    /// synthetic <c>raw_syscalls:sys_enter</c> lines for tid 1000, each with a distinct
    /// fractional-second timestamp. Uses <see cref="CultureInfo.InvariantCulture"/> explicitly (issue
    /// #854) so the generated fixture text always uses a dot decimal separator for the timestamp,
    /// regardless of the ambient thread culture the test process happens to be running under.
    /// </summary>
    internal static string BuildCappedEnterLinesScript(int overBy)
    {
        var lines = new System.Text.StringBuilder(PerfSyscallScriptParser.MaxParsedEvents * 64);
        for (var i = 0; i < PerfSyscallScriptParser.MaxParsedEvents + overBy; i++)
        {
            lines.Append(CultureInfo.InvariantCulture, $"target  1000 [001]   {1.0 + i * 0.000001:F6}: raw_syscalls:sys_enter: NR 0 (0, 0, 0, 0, 0, 0)\n");
        }

        return lines.ToString();
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

/// <summary>
/// Regression coverage for issue #854: a generated perf-script fixture timestamp was formatted
/// with the ambient thread culture (rendering a comma decimal separator on e.g. a pt-BR machine),
/// which desyncs the synthetic fixture text from what <see cref="PerfSyscallScriptParser"/> expects
/// (a dot-decimal <c>raw_syscalls</c> timestamp) and can corrupt or drop events depending on how the
/// parser's numeric tokenizer reacts to the unexpected separator. Runs non-parallel (dedicated
/// collection) because it mutates process-global culture state.
/// </summary>
[Collection(nameof(PerfSyscallScriptParserCultureTests))]
[CollectionDefinition(nameof(PerfSyscallScriptParserCultureTests), DisableParallelization = true)]
public sealed class PerfSyscallScriptParserCultureTests
{
    [Theory]
    [InlineData("pt-BR")]
    [InlineData("fr-FR")]
    public async Task CapsParsedEventCount_ProducesIdenticalResults_UnderCommaDecimalCulture(string cultureName)
    {
        var originalCurrent = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;
        try
        {
            var commaDecimalCulture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = commaDecimalCulture;
            CultureInfo.CurrentUICulture = commaDecimalCulture;
            // Sanity: this culture really does format with a comma, so the assertions below are
            // meaningful (i.e. they would fail if the fixture builder regressed to ambient-culture
            // interpolation).
            (1.5).ToString("F1", CultureInfo.CurrentCulture).Should().Be("1,5");

            const int overBy = 7;
            using var reader = new StringReader(PerfSyscallScriptParserTests.BuildCappedEnterLinesScript(overBy));

            var result = await PerfSyscallScriptParser.ParseAsync(reader, new HashSet<int> { 1000 });

            result.Events.Should().HaveCount(PerfSyscallScriptParser.MaxParsedEvents);
            result.HitCap.Should().BeTrue();
            result.DroppedCount.Should().Be(overBy);
            result.Events[0].TimestampSeconds.Should().Be(1.000000);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCurrent;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }
}
