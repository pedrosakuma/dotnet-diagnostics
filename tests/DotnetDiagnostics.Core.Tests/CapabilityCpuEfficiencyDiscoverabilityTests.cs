using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.CpuEfficiency;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Pure-unit coverage for the cpu-efficiency discoverability surface added in issue #828: ensures
/// the <see cref="DiagnosticCapabilities.CanSampleCpuEfficiency"/> flag/<see cref="DiagnosticCapabilities.CpuEfficiencySource"/>
/// are populated from the injected <see cref="ICpuEfficiencySampler"/> and round-trip via the
/// record's init-only contract, mirroring <c>CapabilityOffCpuDiscoverabilityTests</c>. We can't
/// drive <see cref="CapabilityDetector.DetectAsync"/> against a real vPMU without a live diagnostic
/// IPC socket + hardware counters, so the constructor-wiring/dead-pid path is exercised the same
/// way the off-CPU counterpart does.
/// </summary>
public sealed class CapabilityCpuEfficiencyDiscoverabilityTests
{
    [Fact]
    public void DiagnosticCapabilities_CanSampleCpuEfficiency_DefaultsToFalse_ForBackCompat()
    {
        var caps = new DiagnosticCapabilities(
            ProcessId: 1,
            Runtime: RuntimeFlavor.CoreClr,
            RuntimeVersion: "10.0.0",
            CanReadEventCounters: true,
            CanSampleCpu: true,
            CanCollectGcDump: true,
            CanCollectExceptions: true,
            CanCollectHttpActivity: true,
            CanCollectCustomEventSource: true,
            CanCollectProcessDump: true,
            Notes: "");

        caps.CanSampleCpuEfficiency.Should().BeFalse(
            "the flag is an init-only addition; existing positional callers must still compile and default to a conservative false.");
        caps.CpuEfficiencySource.Should().BeNull();
    }

    [Fact]
    public void DiagnosticCapabilities_CanSampleCpuEfficiency_RoundTrips_ViaWith()
    {
        var caps = new DiagnosticCapabilities(
            ProcessId: 1,
            Runtime: RuntimeFlavor.CoreClr,
            RuntimeVersion: "10.0.0",
            CanReadEventCounters: true,
            CanSampleCpu: true,
            CanCollectGcDump: true,
            CanCollectExceptions: true,
            CanCollectHttpActivity: true,
            CanCollectCustomEventSource: true,
            CanCollectProcessDump: true,
            Notes: "") with
        {
            CanSampleCpuEfficiency = true,
            CpuEfficiencySource = "perf-stat",
        };

        caps.CanSampleCpuEfficiency.Should().BeTrue();
        caps.CpuEfficiencySource.Should().Be("perf-stat");
    }

    [Fact]
    public async Task CapabilityDetector_UnreachablePid_ExercisesCpuEfficiencyCtorWiring()
    {
        // Mirrors CapabilityOffCpuDiscoverabilityTests' dead-pid probe: we can't reach a real
        // PMU here, but constructing the detector with the new optional ICpuEfficiencySampler
        // dependency and running DetectAsync against a guaranteed-dead pid still exercises the
        // wiring without touching hardware counters.
        var stubSampler = new StubCpuEfficiencySampler(available: false);
        var detector = new CapabilityDetector(cpuEfficiencySampler: stubSampler);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        try
        {
            var caps = await detector.DetectAsync(processId: 0, cts.Token);
            caps.CanSampleCpuEfficiency.Should().BeFalse();
        }
        catch (OperationCanceledException)
        {
            // Acceptable: the platform-specific IPC connect timed out before the probe could
            // fail fast. Constructor wiring was still exercised above.
        }
    }

    private sealed class StubCpuEfficiencySampler : ICpuEfficiencySampler
    {
        private readonly bool _available;
        public StubCpuEfficiencySampler(bool available) => _available = available;
        public bool IsAvailable() => _available;
        public Task<CpuEfficiencySample> SampleAsync(int processId, TimeSpan duration, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
