using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DotnetDiagnostics.Core.Capabilities;

namespace DotnetDiagnostics.Core.CpuSampling;

/// <summary>
/// Selects an <see cref="ICpuSampler"/> implementation based on the detected runtime
/// flavour and requested evidence source. Automatic mode uses the managed EventPipe
/// SampleProfiler for CoreCLR and ETW/perf for NativeAOT.
/// </summary>
public sealed class RoutingCpuSampler : ICpuSampler
{
    private readonly ICapabilityDetector _capabilities;
    private readonly EventPipeCpuSampler _managed;
    private readonly PerfNativeAotCpuSampler _perf;
    private readonly EtwNativeAotCpuSampler _etw;
    private readonly ILogger<RoutingCpuSampler> _logger;

    public RoutingCpuSampler(
        ICapabilityDetector capabilities,
        EventPipeCpuSampler managed,
        PerfNativeAotCpuSampler perf,
        EtwNativeAotCpuSampler etw,
        ILogger<RoutingCpuSampler>? logger = null)
    {
        _capabilities = capabilities;
        _managed = managed;
        _perf = perf;
        _etw = etw;
        _logger = logger ?? NullLogger<RoutingCpuSampler>.Instance;
    }

    public async Task<CpuSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        SourceResolutionOptions? sourceResolution = null,
        MethodInstantiationResolutionOptions? methodInstantiationResolution = null,
        NativeAotSymbolResolutionOptions? nativeAotSymbols = null,
        bool exportTrace = false,
        CancellationToken cancellationToken = default)
        => await SampleAsync(
            processId,
            duration,
            topN,
            sourceResolution,
            methodInstantiationResolution,
            nativeAotSymbols,
            exportTrace,
            CpuSamplingMode.Automatic,
            cancellationToken).ConfigureAwait(false);

    public async Task<CpuSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN,
        SourceResolutionOptions? sourceResolution,
        MethodInstantiationResolutionOptions? methodInstantiationResolution,
        NativeAotSymbolResolutionOptions? nativeAotSymbols,
        bool exportTrace,
        CpuSamplingMode mode,
        CancellationToken cancellationToken = default)
    {
        if (mode == CpuSamplingMode.Os && exportTrace)
        {
            throw new ArgumentException(
                "Raw .nettrace export is available only with the EventPipe CPU backend.",
                nameof(exportTrace));
        }

        if (mode == CpuSamplingMode.Os && methodInstantiationResolution?.Enabled == true)
        {
            throw new ArgumentException(
                "Closed generic instantiation enrichment is available only with the EventPipe CPU backend.",
                nameof(methodInstantiationResolution));
        }

        var caps = await _capabilities.DetectAsync(processId, cancellationToken).ConfigureAwait(false);
        var route = SelectRoute(caps.Runtime, mode);
        if (route == CpuSamplerRoute.EventPipe)
        {
            return await _managed.SampleAsync(
                processId, duration, topN, sourceResolution, methodInstantiationResolution,
                nativeAotSymbols: null, exportTrace, cancellationToken).ConfigureAwait(false);
        }

        if (!caps.CanSampleOsCpu)
        {
            throw BuildOsUnavailableException(processId, caps);
        }

        return await SampleOsAsync(
            processId, duration, topN, sourceResolution,
            caps.Runtime == RuntimeFlavor.NativeAot ? nativeAotSymbols : null,
            cancellationToken).ConfigureAwait(false);
    }

    internal static CpuSamplerRoute SelectRoute(RuntimeFlavor runtime, CpuSamplingMode mode)
        => mode switch
        {
            CpuSamplingMode.Automatic when runtime == RuntimeFlavor.NativeAot => CpuSamplerRoute.Os,
            CpuSamplingMode.Automatic => CpuSamplerRoute.EventPipe,
            CpuSamplingMode.EventPipe when runtime == RuntimeFlavor.NativeAot => throw new CpuSamplingUnavailableException(
                "UnsupportedRuntime",
                "The EventPipe CPU backend is unavailable for NativeAOT. Select the OS backend and satisfy its host prerequisites."),
            CpuSamplingMode.EventPipe => CpuSamplerRoute.EventPipe,
            CpuSamplingMode.Os => CpuSamplerRoute.Os,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown CPU sampling mode."),
        };

    private async Task<CpuSampleResult> SampleOsAsync(
        int processId,
        TimeSpan duration,
        int topN,
        SourceResolutionOptions? sourceResolution,
        NativeAotSymbolResolutionOptions? nativeAotSymbols,
        CancellationToken cancellationToken)
    {
        // OS-explicit dispatch: ETW on Windows, perf on Linux.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (_etw.IsAvailable())
            {
                _logger.LogInformation("Routing CPU sample for pid {Pid} to ETW kernel profiling (NativeAOT on Windows).", processId);
                return await _etw.SampleAsync(processId, duration, topN, sourceResolution, methodInstantiationResolution: null, nativeAotSymbols: null, exportTrace: false, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"OS-backed CPU sampling for process {processId} is unavailable. " +
                "On Windows, ETW kernel profiling requires administrative elevation " +
                "(or SeSystemProfilePrivilege). Run the diagnostics process as Administrator to enable " +
                "on-CPU sampling.");
        }

        if (_perf.IsAvailable())
        {
            _logger.LogInformation("Routing CPU sample for pid {Pid} to perf fallback (NativeAOT on Linux).", processId);
            return await _perf.SampleAsync(processId, duration, topN, sourceResolution, methodInstantiationResolution: null, nativeAotSymbols, exportTrace: false, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"OS-backed CPU sampling for process {processId} is unavailable. " +
            "On Linux, the perf backend requires the 'perf' binary in PATH, CAP_PERFMON (or CAP_SYS_ADMIN), " +
            "and perf_event_paranoid <= 2 on the host. Install linux-perf in the diagnostics image and add " +
            "the narrow capability to the container's securityContext.");
    }

    private static CpuSamplingUnavailableException BuildOsUnavailableException(
        int processId,
        DiagnosticCapabilities capabilities)
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new CpuSamplingUnavailableException(
                "PermissionDenied",
                $"OS-backed CPU sampling for process {processId} is unavailable. Windows ETW profiling requires " +
                "administrative elevation or SeSystemProfilePrivilege; no EventPipe fallback was attempted.")
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !capabilities.PerfInstalled
                ? new CpuSamplingUnavailableException(
                    "UnsupportedPrerequisite",
                    $"OS-backed CPU sampling for process {processId} is unavailable because no working perf binary was found; " +
                    "no EventPipe fallback was attempted.")
                : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    ? new CpuSamplingUnavailableException(
                        "PermissionDenied",
                        $"OS-backed CPU sampling for process {processId} is unavailable. Linux perf requires a working perf binary, " +
                        "same-UID target access, and perf_event_paranoid <= 2 or CAP_PERFMON/CAP_SYS_ADMIN; no EventPipe fallback was attempted.")
                    : new CpuSamplingUnavailableException(
                        "UnsupportedPlatform",
                        $"OS-backed CPU sampling for process {processId} is supported only on Linux and Windows; no EventPipe fallback was attempted.");
}

internal enum CpuSamplerRoute
{
    EventPipe,
    Os,
}

/// <summary>An explicit CPU backend could not be selected because a prerequisite is absent.</summary>
public sealed class CpuSamplingUnavailableException : InvalidOperationException
{
    public CpuSamplingUnavailableException(string errorKind, string message)
        : base(message)
    {
        ErrorKind = errorKind;
    }

    public string ErrorKind { get; }
}
