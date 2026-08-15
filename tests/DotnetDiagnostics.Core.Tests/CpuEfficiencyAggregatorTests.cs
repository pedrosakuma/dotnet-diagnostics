using DotnetDiagnostics.Core.CpuEfficiency;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Drives <see cref="CpuEfficiencyAggregator"/>'s metric-name → <see cref="CpuEfficiencySample"/>
/// projection, including ratio computation and null-propagation when a metric is unavailable or a
/// denominator is zero (must never surface as NaN/Infinity in the JSON payload).
/// </summary>
public sealed class CpuEfficiencyAggregatorTests
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);

    [Fact]
    public void ComputesRatios_WhenAllInputsAvailable()
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["instructions"] = 800,
            ["cycles"] = 1000,
            ["cache-references"] = 500,
            ["cache-misses"] = 50,
            ["branch-instructions"] = 200,
            ["branch-misses"] = 20,
            ["stalled-cycles-frontend"] = 100,
            ["stalled-cycles-backend"] = 150,
            ["dTLB-load-misses"] = 4,
            ["iTLB-load-misses"] = 6,
            ["page-faults"] = 3,
            ["context-switches"] = 7,
            ["cpu-migrations"] = 2,
        };

        var sample = CpuEfficiencyAggregator.Build(4242, StartedAt, Duration, "perf-stat", values, []);

        sample.ProcessId.Should().Be(4242);
        sample.Backend.Should().Be("perf-stat");
        sample.InstructionsPerCycle.Should().Be(0.8);
        sample.CacheMissRate.Should().Be(0.1);
        sample.BranchMissRate.Should().Be(0.1);
        sample.StalledCyclesFrontendRate.Should().Be(0.1);
        sample.StalledCyclesBackendRate.Should().Be(0.15);
        sample.DTlbMisses.Should().Be(4);
        sample.ITlbMisses.Should().Be(6);
        sample.TlbMissRate.Should().Be(10.0 / 800);
        sample.PageFaults.Should().Be(3);
        sample.ContextSwitches.Should().Be(7);
        sample.CpuMigrations.Should().Be(2);
        sample.Notes.Should().BeNull();
    }

    [Fact]
    public void MissingNumeratorOrDenominator_YieldsNullRatio_NotNaNOrInfinity()
    {
        // cycles present, instructions absent (e.g. <not supported> on this host) — IPC must be
        // null, never Infinity/NaN.
        var values = new Dictionary<string, long>(StringComparer.Ordinal) { ["cycles"] = 1000 };

        var sample = CpuEfficiencyAggregator.Build(1, StartedAt, Duration, "perf-stat", values, []);

        sample.InstructionsPerCycle.Should().BeNull();
        sample.CacheMissRate.Should().BeNull();
        sample.BranchMissRate.Should().BeNull();
        sample.TlbMissRate.Should().BeNull();
    }

    [Fact]
    public void ZeroDenominator_YieldsNullRatio()
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["instructions"] = 500,
            ["cycles"] = 0,
        };

        var sample = CpuEfficiencyAggregator.Build(1, StartedAt, Duration, "perf-stat", values, []);

        sample.InstructionsPerCycle.Should().BeNull();
        sample.Cycles.Should().Be(0);
    }

    [Fact]
    public void OnlyOneTlbSideAvailable_StillComputesCombinedTlbMissRate()
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["instructions"] = 1000,
            ["dTLB-load-misses"] = 10,
        };

        var sample = CpuEfficiencyAggregator.Build(1, StartedAt, Duration, "perf-stat", values, []);

        sample.DTlbMisses.Should().Be(10);
        sample.ITlbMisses.Should().BeNull();
        sample.TlbMissRate.Should().Be(0.01);
    }

    [Fact]
    public void NeitherTlbSideAvailable_TlbMissRateIsNull()
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal) { ["instructions"] = 1000 };

        var sample = CpuEfficiencyAggregator.Build(1, StartedAt, Duration, "perf-stat", values, []);

        sample.TlbMissRate.Should().BeNull();
    }

    [Fact]
    public void NotesArePassedThroughWhenPresent()
    {
        var notes = new List<string> { "stalled-cycles-frontend: not supported by this CPU/host." };

        var sample = CpuEfficiencyAggregator.Build(1, StartedAt, Duration, "perf-stat", new Dictionary<string, long>(), notes);

        sample.Notes.Should().BeEquivalentTo(notes);
    }

    [Fact]
    public void EmptyNotesList_YieldsNullNotes()
    {
        var sample = CpuEfficiencyAggregator.Build(1, StartedAt, Duration, "perf-stat", new Dictionary<string, long>(), []);

        sample.Notes.Should().BeNull();
    }
}
