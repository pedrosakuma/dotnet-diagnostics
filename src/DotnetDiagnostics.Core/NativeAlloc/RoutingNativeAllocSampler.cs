using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.NativeAlloc;

/// <summary>
/// Platform router for <see cref="INativeAllocSampler"/>. Routes Linux to
/// <see cref="PerfNativeAllocSampler"/> (perf uprobes on the libc malloc/calloc/realloc
/// allocator) and Windows to <see cref="EtwNativeAllocSampler"/> (NT Kernel Logger VirtualAlloc
/// with stack walk). Both backends emit the same <see cref="NativeAllocSampleResult"/> /
/// <c>CpuSampleTraceArtifact</c> shape via <see cref="NativeAllocStackAggregator"/>, so the
/// downstream <c>query_snapshot(view="call-tree")</c> drilldown stays platform-agnostic.
/// </summary>
public sealed class RoutingNativeAllocSampler : INativeAllocSampler
{
    private readonly PerfNativeAllocSampler _linux;
    private readonly EtwNativeAllocSampler _windows;
    private readonly ILogger<RoutingNativeAllocSampler> _logger;

    public RoutingNativeAllocSampler(
        PerfNativeAllocSampler linux,
        EtwNativeAllocSampler windows,
        ILogger<RoutingNativeAllocSampler>? logger = null)
    {
        _linux = linux;
        _windows = windows;
        _logger = logger ?? NullLogger<RoutingNativeAllocSampler>.Instance;
    }

    public bool IsAvailable()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return _linux.IsAvailable();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return _windows.IsAvailable();
        return false;
    }

    public Task<NativeAllocSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        long samplePeriod = 1000,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return _linux.SampleAsync(processId, duration, topN, samplePeriod, cancellationToken);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!_windows.IsAvailable())
            {
                throw new UnauthorizedAccessException(EtwNativeAllocSampler.PermissionDeniedMessage);
            }
            return _windows.SampleAsync(processId, duration, topN, samplePeriod, cancellationToken);
        }

        throw new NotSupportedException(
            "Native allocation sampling is only supported on Linux (perf uprobes on the libc " +
            "malloc/calloc/realloc allocators) and Windows (ETW VirtualAlloc with stack walk) in this release.");
    }
}
