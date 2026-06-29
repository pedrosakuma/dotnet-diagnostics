using System.Runtime.InteropServices;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Etw;
using DotnetDiagnostics.Core.Symbols;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.NativeAlloc;

/// <summary>
/// Windows native-allocation sampler driven by the NT Kernel Logger's <c>VirtualAlloc</c>
/// tracepoint (<see cref="KernelTraceEventParser.Keywords.VirtualAlloc"/>) with stack walks
/// enabled. Every committed <c>VirtualAlloc</c> in the target process is recorded with the call
/// chain at the allocation site; those stacks are aggregated by
/// <see cref="NativeAllocStackAggregator"/> into the same <see cref="CpuSampleTraceArtifact"/>
/// the Linux <see cref="PerfNativeAllocSampler"/> emits, so the
/// <c>query_snapshot(view="call-tree")</c> drilldown does not need a Windows branch.
/// </summary>
/// <remarks>
/// <para>
/// This is the Windows analog of the Linux libc-uprobe path. The libc allocator (malloc/calloc/
/// realloc) is a user-mode wrapper over the OS virtual-memory allocator; on Windows the kernel
/// <c>VirtualAlloc</c> tracepoint is the cleanest system-wide, stack-walkable native-allocation
/// signal (it is what PerfView's "Net Virtual Alloc Stacks" view is built on). Like the perf
/// sampler this is <b>hotspot-only</b>: counts are recorded allocation-call hits, not bytes, and
/// it does not do alloc/free retention matching — it answers "who allocates native memory most",
/// not "what leaks".
/// </para>
/// <para>
/// Requirements (validated by <see cref="IsAvailable"/>): Windows host with administrative
/// elevation (or <c>SeSystemProfilePrivilege</c>) — kernel ETW sessions are inherently
/// system-wide. Concurrent captures are serialized through a static gate to keep buffer pressure
/// predictable, mirroring <see cref="EtwNativeAotCpuSampler"/> and <c>EtwOffCpuSampler</c>. A
/// missing-privilege failure surfaces as a structured <see cref="UnauthorizedAccessException"/>
/// (turned into a <c>PermissionDenied</c> envelope by the tool layer) rather than a crash.
/// </para>
/// </remarks>
public sealed class EtwNativeAllocSampler : INativeAllocSampler
{
    // Serialize concurrent kernel ETW sessions across ALL kernel samplers via the shared
    // process-wide gate (see KernelEtwSessionGate): the NT Kernel Logger is one global slot.
    private readonly ILogger<EtwNativeAllocSampler> _logger;
    private readonly SymbolPathBuilder _symbolPathBuilder;

    public EtwNativeAllocSampler(
        ILogger<EtwNativeAllocSampler>? logger = null,
        SymbolPathBuilder? symbolPathBuilder = null)
    {
        _logger = logger ?? NullLogger<EtwNativeAllocSampler>.Instance;
        _symbolPathBuilder = symbolPathBuilder ?? new SymbolPathBuilder();
    }

    internal const string PermissionDeniedMessage =
        "NT Kernel Logger 'VirtualAlloc' provider requires either BUILTIN\\Administrators membership or SeSystemProfilePrivilege ('Profile system performance'). Run the diagnostics sidecar elevated (or grant that right) and restart the service.";

    // Reported as the "allocator" probed and the "libc" path so the shared NativeAllocSample
    // summary reads sensibly on Windows even though there is no libc / no perf sample period.
    private const string ProbedProvider = "VirtualAlloc";
    private const string ProviderDescription = "NT Kernel Logger:VirtualAlloc (ETW)";

    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
    public bool IsAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogTrace("ETW native-alloc sampler not available: not running on Windows.");
            return false;
        }

        // Accept either BUILTIN\Administrators membership or SeSystemProfilePrivilege, matching the
        // off-CPU kernel ETW sampler — both grant NT Kernel Logger access. Reuses the off-CPU token
        // inspection so the two kernel samplers report availability identically.
        try
        {
            var access = DotnetDiagnostics.Core.OffCpu.EtwOffCpuSampler.GetKernelLoggerAccess();
            if (!access.IsAllowed)
            {
                _logger.LogTrace(
                    "ETW native-alloc sampler not available: token is neither BUILTIN\\Administrators nor granted SeSystemProfilePrivilege.");
            }

            return access.IsAllowed;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ETW native-alloc sampler not available: failed to inspect Windows token privileges.");
            return false;
        }
    }

    public async Task<NativeAllocSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        long samplePeriod = 1000,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be (0, 5min].");
        }
        if (topN <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(topN), "topN must be positive.");
        }
        if (samplePeriod <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(samplePeriod), "samplePeriod must be positive.");
        }
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException(
                "The ETW native allocation sampler only runs on Windows (NT Kernel Logger VirtualAlloc " +
                "provider). Use the Linux perf-uprobe backend on Linux hosts.");
        }
        if (!IsAvailable())
        {
            throw new UnauthorizedAccessException(PermissionDeniedMessage);
        }

        if (OperatingSystem.IsWindows())
        {
            // A token may hold SeSystemProfilePrivilege present-but-disabled — enable it before the
            // kernel session starts, mirroring the off-CPU sampler.
            DotnetDiagnostics.Core.OffCpu.EtwOffCpuSampler.EnsureSystemProfilePrivilegeEnabledIfPresent();
        }

        await KernelEtwSessionGate.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CaptureAndProcessAsync(processId, duration, topN, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            KernelEtwSessionGate.Gate.Release();
        }
    }

    private async Task<NativeAllocSampleResult> CaptureAndProcessAsync(
        int processId,
        TimeSpan duration,
        int topN,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var captureDir = Path.Combine(Path.GetTempPath(), $"diagmcp-etw-nativealloc-{processId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(captureDir);

        var sessionName = $"dotnet-diag-mcp-nativealloc-{processId}-{Guid.NewGuid():N}";
        var etlPath = Path.Combine(captureDir, "trace.etl");

        try
        {
            await CaptureEtwAsync(sessionName, etlPath, duration, cancellationToken).ConfigureAwait(false);
            return ProcessEtl(etlPath, processId, startedAt, duration, topN);
        }
        finally
        {
            TryDeleteDirectory(captureDir);
        }
    }

    private async Task CaptureEtwAsync(
        string sessionName,
        string etlPath,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        TraceEventSession? session = null;
        try
        {
            session = new TraceEventSession(sessionName, etlPath)
            {
                StopOnDispose = true,
            };

            // VirtualAlloc: the native allocation tracepoint — fires on every VirtualAlloc/VirtualFree.
            // ImageLoad/Process/Thread: required for module → symbol resolution during conversion.
            var keywords = KernelTraceEventParser.Keywords.VirtualAlloc |
                           KernelTraceEventParser.Keywords.ImageLoad |
                           KernelTraceEventParser.Keywords.Process |
                           KernelTraceEventParser.Keywords.Thread;
            // Walk stacks specifically on VirtualAlloc: the stack captured at allocation time IS the
            // call chain that requested the native memory.
            var stackKeywords = KernelTraceEventParser.Keywords.VirtualAlloc;

            session.EnableKernelProvider(keywords, stackKeywords);
            _logger.LogDebug("ETW native-alloc session '{Session}' started, capturing for {Duration}s.",
                sessionName, duration.TotalSeconds);

            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ETW native-alloc session '{Session}' failed.", sessionName);
            if (IsPermissionFailure(ex))
            {
                throw new UnauthorizedAccessException(PermissionDeniedMessage, ex);
            }

            throw new InvalidOperationException(
                "Failed to start or run the ETW kernel VirtualAlloc session. Ensure admin elevation and " +
                $"that no conflicting kernel session is active. Details: {ex.Message}", ex);
        }
        finally
        {
            try { session?.Stop(); }
            catch (Exception ex) { _logger.LogDebug(ex, "ETW native-alloc session stop failed (best effort)."); }
            session?.Dispose();
        }
    }

    private NativeAllocSampleResult ProcessEtl(
        string etlPath,
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        int topN)
    {
        if (!File.Exists(etlPath))
        {
            throw new InvalidOperationException("ETW native-alloc capture produced no output file.");
        }

        var symbolPath = _symbolPathBuilder.BuildForProcess(processId, null);
        var options = new TraceLogOptions
        {
            LocalSymbolsOnly = true,
            ShouldResolveSymbols = _ => false,
        };
        var etlxPath = TraceLog.CreateFromEventTraceLogFile(etlPath, null, options);

        try
        {
            using var traceLog = TraceLog.OpenOrConvert(etlxPath);
            return AggregateFromTraceLog(traceLog, processId, startedAt, duration, topN, symbolPath);
        }
        finally
        {
            TryDelete(etlxPath);
        }
    }

    private static NativeAllocSampleResult AggregateFromTraceLog(
        TraceLog traceLog,
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        int topN,
        string? symbolPath)
    {
        if (symbolPath is not null)
        {
            try
            {
                using var symbolReader = new SymbolReader(TextWriter.Null, symbolPath);
                foreach (var process in traceLog.Processes)
                {
                    if (process.ProcessID != processId) continue;
                    foreach (var module in process.LoadedModules)
                    {
                        try { traceLog.CodeAddresses.LookupSymbolsForModule(symbolReader, module.ModuleFile); }
                        catch { /* best effort per module */ }
                    }
                }
            }
            catch { /* best effort */ }
        }

        var stacks = ExtractAllocationStacks(traceLog, processId);
        var aggregate = NativeAllocStackAggregator.Aggregate(stacks, topN);

        var notes = new List<string>
        {
            "Counts are native VirtualAlloc-call hits captured via ETW, not bytes; a frequent small " +
            "allocator can outrank a rare large one.",
            "Hotspot-only: this is who allocates native memory most, not what leaks — there is no " +
            "alloc/free retention matching.",
        };
        if (aggregate.TotalSampledAllocations == 0)
        {
            notes.Add("No VirtualAlloc events landed in the window for this process — the workload may " +
                      "not have committed native memory during the capture.");
        }

        var artifact = new CpuSampleTraceArtifact(
            processId, startedAt, duration, aggregate.TotalSampledAllocations, aggregate.Root, null, null, aggregate.SymbolSource);
        var summary = new NativeAllocSample(
            processId,
            startedAt,
            duration,
            aggregate.TotalSampledAllocations,
            aggregate.Hotspots,
            new[] { ProbedProvider },
            ProviderDescription,
            // No perf-style sample period on the ETW path: every VirtualAlloc is recorded.
            SamplePeriod: 1,
            aggregate.SymbolSource.ToString(),
            notes);
        return new NativeAllocSampleResult(summary, artifact);
    }

    /// <summary>
    /// Walks the converted trace and yields one leaf→root frame list per committed
    /// <see cref="VirtualAllocTraceData"/> event in the target process. <c>MEM_COMMIT</c> is the
    /// reservation that actually backs pages — decommit / release events (the free side) are
    /// skipped so the result counts allocations only.
    /// </summary>
    private static IEnumerable<IReadOnlyList<(string Module, string Method)>> ExtractAllocationStacks(
        TraceLog traceLog,
        int processId)
    {
        foreach (var ev in traceLog.Events)
        {
            if (ev is not VirtualAllocTraceData va) continue;
            if (va.ProcessID != processId) continue;
            if ((va.Flags & VirtualAllocTraceData.VirtualAllocFlags.MEM_COMMIT) == 0) continue;

            var stack = ev.CallStack();
            if (stack is null) continue;

            var frames = new List<(string Module, string Method)>();
            var current = stack;
            var depth = 0;
            while (current is not null && depth < 256)
            {
                var ca = current.CodeAddress;
                var module = ca?.ModuleFile?.Name ?? string.Empty;
                frames.Add((module, ResolveMethodName(ca)));
                current = current.Caller;
                depth++;
            }

            if (frames.Count > 0)
            {
                yield return frames;
            }
        }
    }

    private static string ResolveMethodName(TraceCodeAddress? ca)
    {
        if (ca is null) return "[unknown]";
        var name = ca.FullMethodName;
        if (!string.IsNullOrEmpty(name) && name != "?")
        {
            return name;
        }
        return $"0x{ca.Address:X}";
    }

    private static bool IsPermissionFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException)
            {
                return true;
            }

            if (current is System.ComponentModel.Win32Exception win32 &&
                (win32.NativeErrorCode == 5 /* ERROR_ACCESS_DENIED */ ||
                 win32.NativeErrorCode == 1314 /* ERROR_PRIVILEGE_NOT_HELD */))
            {
                return true;
            }

            if (current.Message.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("privilege", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
