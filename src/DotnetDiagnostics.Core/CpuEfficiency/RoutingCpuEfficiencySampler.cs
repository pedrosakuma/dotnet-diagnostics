using System.Runtime.InteropServices;
using DotnetDiagnostics.Core.OffCpu;

namespace DotnetDiagnostics.Core.CpuEfficiency;

/// <summary>
/// Platform router for <see cref="ICpuEfficiencySampler"/>. Routes Linux to
/// <see cref="PerfStatCpuEfficiencySampler"/> (perf stat aggregate counting) and Windows to
/// <see cref="EtwPmcCpuEfficiencySampler"/> (ETW kernel PMC sampling). Both backends emit the same
/// <see cref="CpuEfficiencySample"/> shape with nullable per-metric fields, so the MCP tool layer
/// stays platform-agnostic. Mirrors <see cref="RoutingOffCpuSampler"/>.
/// </summary>
public sealed class RoutingCpuEfficiencySampler : ICpuEfficiencySampler
{
    private readonly PerfStatCpuEfficiencySampler _linux;
    private readonly EtwPmcCpuEfficiencySampler _windows;

    public RoutingCpuEfficiencySampler(
        PerfStatCpuEfficiencySampler linux,
        EtwPmcCpuEfficiencySampler windows)
    {
        _linux = linux;
        _windows = windows;
    }

    public bool IsAvailable()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return _linux.IsAvailable();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return _windows.IsAvailable();
        return false;
    }

    public Task<CpuEfficiencySample> SampleAsync(
        int processId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return _linux.SampleAsync(processId, duration, cancellationToken);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (!_windows.IsAvailable())
            {
                throw new UnauthorizedAccessException(EtwOffCpuSampler.KernelLoggerPermissionDeniedMessage);
            }
            return _windows.SampleAsync(processId, duration, cancellationToken);
        }

        throw new NotSupportedException(
            "CPU efficiency sampling is only supported on Linux (perf stat) and Windows (ETW PMC) in this release.");
    }
}
