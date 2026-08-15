using DotnetDiagnostics.Core.CpuEfficiency;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// xunit <see cref="FactAttribute"/> variant that produces a genuine (green, not failing)
/// <c>Skipped</c> result when <see cref="PerfStatCpuEfficiencySampler.IsAvailable"/> reports the
/// current host cannot even attempt <c>perf stat</c> counting mode (perf not installed, or
/// <c>perf_event_paranoid</c> blocks per-process counting — see
/// <see cref="Capabilities.PerfHostProbe.CanRunPerfStatCounting"/>).
/// </summary>
/// <remarks>
/// Issue #828 explicitly requires the vPMU-unavailable case to soft-skip rather than fail —
/// this repo's shared <c>SkipException</c> helper intentionally surfaces as a FAILURE (documented
/// in its own XML doc: "xunit 2.x has no dynamic skip..."), which is the wrong tool for a case
/// that is expected/common on CI runners and dev sandboxes with no PMU passthrough. Setting
/// <see cref="FactAttribute.Skip"/> in the constructor (evaluated at test discovery time, before
/// any test body runs) is xunit 2.x's actual supported mechanism for a real, green "Skipped"
/// outcome. Note this only covers the "perf isn't even worth trying" gate; the still-narrower
/// "perf ran but reported &lt;not supported&gt; per event" vPMU-absent case is NOT a skip at all —
/// it is a fully successful <see cref="CpuEfficiencySample"/> with populated <c>Notes</c>, asserted
/// directly in the test body.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class SkipIfPerfStatCountingUnavailableFactAttribute : FactAttribute
{
    public SkipIfPerfStatCountingUnavailableFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "cpu-efficiency perf-stat backend is Linux-only in this release.";
            return;
        }

        if (!new PerfStatCpuEfficiencySampler().IsAvailable())
        {
            Skip = "perf is not installed, or perf_event_paranoid blocks per-process counting mode " +
                   "on this host — expected in most CI/dev-container/virtualized environments " +
                   "(see PerfHostProbe.CanRunPerfStatCounting).";
        }
    }
}
