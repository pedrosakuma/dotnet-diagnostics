using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.NativeLockContention;

/// <summary>
/// Platform router for <see cref="INativeLockContentionSampler"/>. Routes Linux to
/// <see cref="PerfNativeLockContentionSampler"/> (perf uprobes on libc
/// pthread_mutex_lock/pthread_mutex_unlock) and Windows to
/// <see cref="WindowsNativeLockContentionSampler"/> — a documented capability-gated stub, since
/// (unlike native-allocation / off-CPU) there is no supported ETW enablement path for native
/// critical-section contention today. See <see cref="WindowsNativeLockContentionSampler"/>'s
/// remarks for the full investigation. Mirrors <see cref="DotnetDiagnostics.Core.NativeAlloc.RoutingNativeAllocSampler"/>'s
/// OS-explicit dispatch convention.
/// </summary>
public sealed class RoutingNativeLockContentionSampler : INativeLockContentionSampler
{
    private readonly PerfNativeLockContentionSampler _linux;
    private readonly WindowsNativeLockContentionSampler _windows;
    private readonly ILogger<RoutingNativeLockContentionSampler> _logger;

    public RoutingNativeLockContentionSampler(
        PerfNativeLockContentionSampler linux,
        WindowsNativeLockContentionSampler windows,
        ILogger<RoutingNativeLockContentionSampler>? logger = null)
    {
        _linux = linux;
        _windows = windows;
        _logger = logger ?? NullLogger<RoutingNativeLockContentionSampler>.Instance;
    }

    public bool IsAvailable()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return _linux.IsAvailable();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return _windows.IsAvailable();
        return false;
    }

    public Task<NativeLockContentionSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        long samplePeriod = 5000,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return _linux.SampleAsync(processId, duration, topN, samplePeriod, cancellationToken);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogDebug("Native lock-contention sampling requested on Windows — no supported backend; see WindowsNativeLockContentionSampler remarks.");
            throw new PlatformNotSupportedException(WindowsNativeLockContentionSampler.NotSupportedMessage);
        }

        throw new NotSupportedException(
            "Native lock-contention sampling is only supported on Linux (perf uprobes on libc " +
            "pthread_mutex_lock/pthread_mutex_unlock) in this release. There is no Windows backend — " +
            "see collect_sample(kind=\"native-lock-contention\") documentation for why.");
    }
}
