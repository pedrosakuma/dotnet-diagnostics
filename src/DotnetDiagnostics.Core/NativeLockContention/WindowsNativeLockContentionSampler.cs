using System.Runtime.InteropServices;

namespace DotnetDiagnostics.Core.NativeLockContention;

/// <summary>
/// Windows placeholder for <see cref="INativeLockContentionSampler"/>. Deliberately unimplemented
/// — <see cref="IsAvailable"/> always returns <c>false</c> and <see cref="SampleAsync"/> always
/// throws <see cref="PlatformNotSupportedException"/> with the reasoning below. This is an
/// explicit, documented exception to the repo's usual symmetric-backend convention (see the
/// native-allocation and off-CPU samplers, both of which do ship a working ETW backend) — issue
/// #830 flagged the Windows side as a genuine design risk up front, and this class records the
/// investigation outcome rather than silently omitting the platform.
/// </summary>
/// <remarks>
/// <para><b>Investigation findings (issue #830).</b> Windows ETW does have a classic (MOF)
/// provider that decodes native critical-section contention —
/// <c>Microsoft.Diagnostics.Tracing.Parsers.CritSecTraceProviderTraceEventParser</c>, exposing
/// <c>CritSecCollisionTraceData</c> / <c>CritSecInitTraceData</c> events with a <c>CritSecAddr</c>
/// lock identity, wired to the well-known <c>CritSecTraceProvider</c> GUID
/// (<c>3ac66736-cc59-4cff-8115-8df50e39816b</c>). TraceEvent can <i>decode</i> these events from an
/// already-recorded classic ETL file — but there is no supported way to <i>turn the provider on</i>
/// from this codebase:</para>
/// <list type="bullet">
/// <item><description><c>TraceEventSession.EnableKernelProvider</c> — the same NT Kernel Logger
/// entry point <see cref="DotnetDiagnostics.Core.NativeAlloc.EtwNativeAllocSampler"/> and
/// <c>EtwOffCpuSampler</c> use for <c>VirtualAlloc</c> / <c>ContextSwitch</c> — only accepts
/// <c>KernelTraceEventParser.Keywords</c> flags. That enum has no CritSec / critical-section
/// member (confirmed against the TraceEvent 3.2.2 assembly pinned in
/// <c>Directory.Packages.props</c>): <c>DiskFileIO</c>, <c>VirtualAlloc</c>, <c>ContextSwitch</c>,
/// etc. are present, CritSec is not.</description></item>
/// <item><description>Historically, CritSec collision tracing is turned on by the Windows
/// Performance Toolkit's <c>xperf -on Latency</c> (or WPR's "CPU Usage (Sampled)" + "CS" flag)
/// profile, which manipulates an undocumented extended kernel-logger group mask outside the
/// <c>EnableKernelProvider</c> surface TraceEvent exposes, and in practice requires the separate
/// <c>xperf.exe</c> tool (Windows Performance Toolkit / WDK) to be installed on the host — an
/// external dependency well beyond the single <c>perf</c> binary the Linux backend needs, and a
/// materially different integration shape than every other sampler in this repo.</description></item>
/// <item><description>The managed CLR-level analogs (<c>Microsoft-Windows-DotNETRuntime</c>
/// <c>WaitHandleWaitStart</c>/<c>Stop</c>, or the CLR <c>Contention</c> events already covered by
/// <c>collect_events(kind="contention")</c>) do not see native/OS-level mutex or critical-section
/// blocking at all — they are not a substitute.</description></item>
/// </list>
/// <para>Conclusion: shipping a Windows backend today would mean depending on an external,
/// separately-installed tool (xperf) with an unsupported/undocumented enablement path — a much
/// larger commitment than a NuGet-only ETW session, and not comparable in maintainability to the
/// existing kernel-ETW samplers. Per the issue's own anticipated fallback, this ships as an
/// explicit, well-documented capability-gated stub instead. Revisit if TraceEvent adds a supported
/// enablement path for the CritSec provider, or if a manifest-based provider covering native mutex
/// contention becomes available on a supported Windows release.</para>
/// </remarks>
public sealed class WindowsNativeLockContentionSampler : INativeLockContentionSampler
{
    internal const string NotSupportedMessage =
        "Native lock-contention sampling has no Windows backend in this release. Windows ETW does " +
        "not expose a supported way to enable native critical-section contention tracing: the classic " +
        "CritSecTraceProvider that TraceEvent can decode is not reachable through " +
        "TraceEventSession.EnableKernelProvider (that API only accepts KernelTraceEventParser.Keywords, " +
        "which has no CritSec member) and is otherwise only turned on by the separate xperf.exe tool " +
        "(Windows Performance Toolkit) via an undocumented extended kernel group mask. Use " +
        "collect_sample(kind=\"off_cpu\") to see blocked-thread stacks, or run this capture from a Linux " +
        "sidecar (perf uprobes on pthread_mutex_lock/pthread_mutex_unlock) instead.";

    public bool IsAvailable() => false;

    public Task<NativeLockContentionSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        long samplePeriod = 5000,
        CancellationToken cancellationToken = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException(
                "WindowsNativeLockContentionSampler only runs on Windows; use the Linux perf-uprobe backend on Linux hosts.");
        }

        throw new PlatformNotSupportedException(NotSupportedMessage);
    }
}
