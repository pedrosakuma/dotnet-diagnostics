using System.Runtime.InteropServices;
using DotnetDiagnostics.Core.Etw;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.CpuEfficiency;

/// <summary>
/// Windows aggregate CPU microarchitecture-efficiency sampler driven by a short-lived, real-time
/// NT Kernel Logger session with the <c>PMCProfile</c> keyword enabled, using
/// <see cref="TraceEventProfileSources"/> to select the hardware performance-monitoring counters
/// (the same underlying mechanism BenchmarkDotNet's own <c>EtwProfiler</c>/<c>HardwareCounters</c>
/// diagnoser uses for its offline benchmark runs — see issue #828's discussion of why that
/// diagnoser can't be reused as-is for an already-running process).
/// </summary>
/// <remarks>
/// <para>
/// <b>Windows PMC is fundamentally a SAMPLING mechanism, not a counting one</b> (unlike Linux's
/// <c>perf stat</c>): the kernel fires a <c>PerfInfoPMCSample</c> event every <c>Interval</c>
/// occurrences of the configured hardware event, on whichever thread happens to be running at that
/// moment — there is no OS-level per-process aggregate counter to just read back. We approximate an
/// aggregate count by multiplying (samples attributed to the target process) × (configured
/// interval) for each configured profile source, then compute ratios (IPC, cache/branch-miss rate)
/// from those approximations — the same estimation BenchmarkDotNet's own hardware-counter diagnoser
/// performs. This makes the Windows numbers order-of-magnitude accurate rather than exact, which we
/// surface via <see cref="CpuEfficiencySample.Backend"/> == <c>etw-pmc</c> so callers don't conflate
/// it with the Linux backend's exact aggregate counts.
/// </para>
/// <para>
/// Profile source names differ across Windows/CPU combinations (Intel vs. AMD, and older vs. newer
/// <c>TraceEventProfileSources</c> catalogs), so each metric tries an ordered list of known aliases
/// and silently skips to the next if the current host doesn't expose it — surfaced as a
/// <see cref="CpuEfficiencySample.Notes"/> entry when NONE of a metric's aliases are available.
/// Stalled-cycle breakdown and TLB miss rate are not requested at all: no commonly available Windows
/// profile source corresponds to them (see the design discussion in issue #828), so those fields
/// always come back null with an explanatory note rather than a best-effort proxy.
/// </para>
/// <para>
/// Requirements (validated by <see cref="IsAvailable"/>): Windows host with administrative elevation
/// (or <c>SeSystemProfilePrivilege</c>) — the same requirement as <see cref="OffCpu.EtwOffCpuSampler"/>,
/// which we reuse verbatim via its internal elevation-probe helpers rather than duplicating the
/// P/Invoke token-privilege plumbing. Kernel PMC sessions are documented (Microsoft, BenchmarkDotNet)
/// as unavailable under Hyper-V/most VMs regardless of privilege — that failure surfaces as an
/// <see cref="InvalidOperationException"/> from <c>EnableKernelProvider</c>, translated into a
/// structured degradation rather than a crash. Concurrent kernel sessions are serialized through the
/// shared <see cref="KernelEtwSessionGate"/> (one NT Kernel Logger slot system-wide, shared with the
/// off-CPU/native-alloc/NativeAOT-CPU samplers).
/// </para>
/// </remarks>
public sealed class EtwPmcCpuEfficiencySampler : ICpuEfficiencySampler
{
    private readonly ILogger<EtwPmcCpuEfficiencySampler> _logger;

    public EtwPmcCpuEfficiencySampler(ILogger<EtwPmcCpuEfficiencySampler>? logger = null)
    {
        _logger = logger ?? NullLogger<EtwPmcCpuEfficiencySampler>.Instance;
    }

    // Ordered alias lists per metric — first name present in TraceEventProfileSources.GetInfo() on
    // this host wins. Names observed across TraceEvent versions / Intel & AMD hosts; a host
    // exposing none of a metric's aliases skips that metric with a note rather than failing.
    private static readonly IReadOnlyDictionary<string, string[]> ProfileSourceAliases =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["cycles"] = ["TotalCycles", "UnhaltedCoreCycles"],
            ["instructions"] = ["TotalIssues", "InstructionRetired", "Instructions"],
            ["cache-references"] = ["LLCReference", "CacheReference"],
            ["cache-misses"] = ["LLCMisses", "CacheMisses"],
            ["branch-instructions"] = ["BranchInstructions"],
            ["branch-misses"] = ["BranchMispredictions"],
        };

    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
    public bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogTrace("ETW PMC CPU-efficiency sampler not available: not running on Windows.");
            return false;
        }

        try
        {
            var access = OffCpu.EtwOffCpuSampler.GetKernelLoggerAccess();
            return OffCpu.EtwOffCpuSampler.HasKernelLoggerAccess(access.IsAdministrator, access.HasSystemProfilePrivilege);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ETW PMC CPU-efficiency sampler not available: failed to inspect Windows token privileges.");
            return false;
        }
    }

    public async Task<CpuEfficiencySample> SampleAsync(
        int processId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be (0, 5min].");
        }
        if (!IsAvailable())
        {
            throw new UnauthorizedAccessException(OffCpu.EtwOffCpuSampler.KernelLoggerPermissionDeniedMessage);
        }

        // Mirrors the off-CPU sampler's session-start sequence: a process granted only
        // SeSystemProfilePrivilege (without local Administrators membership) has the privilege
        // present but disabled by default — enable it before opening the kernel session or
        // EnableKernelProvider fails despite IsAvailable() reporting true.
        if (OperatingSystem.IsWindows())
        {
            OffCpu.EtwOffCpuSampler.EnsureSystemProfilePrivilegeEnabledIfPresent();
        }

        await KernelEtwSessionGate.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CaptureAsync(processId, duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            KernelEtwSessionGate.Gate.Release();
        }
    }

    private async Task<CpuEfficiencySample> CaptureAsync(int processId, TimeSpan duration, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var notes = new List<string>();

        var sourcesById = SafeGetProfileSourceInfo(notes);
        var (sourceIdToMetric, sourceIds, intervals) = SelectProfileSources(sourcesById, notes);

        // Track the interval actually handed to TraceEventProfileSources.Set(...) per source id —
        // NOT ProfileSourceInfo.Interval, which can differ from Math.Max(MinInterval, Interval)
        // when a host's minimum supported interval exceeds the source's nominal default. Scaling
        // sample counts back up by the wrong interval would silently under-report every PMC metric.
        var configuredIntervalBySourceId = new Dictionary<int, int>();
        for (var i = 0; i < sourceIds.Length; i++)
        {
            configuredIntervalBySourceId[sourceIds[i]] = intervals[i];
        }

        var sampleCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        long contextSwitches = 0;
        long pageFaults = 0;

        var sessionName = $"dotnet-diag-mcp-cpueff-{processId}-{Guid.NewGuid():N}";
        var pmcRequested = sourceIds.Length > 0;
        var pmcEnabled = pmcRequested;
        TraceEventSession? session = null;
        try
        {
            if (pmcRequested)
            {
                // Must be configured BEFORE EnableKernelProvider is called with PMCProfile —
                // this call requires the same elevation as the kernel session itself.
                TraceEventProfileSources.Set(sourceIds, intervals);
            }

            session = new TraceEventSession(sessionName) { StopOnDispose = true };

            session.Source.Kernel.PerfInfoPMCSample += data =>
            {
                if (data.ProcessID != processId) return;
                if (sourceIdToMetric.TryGetValue(data.ProfileSource, out var metric))
                {
                    sampleCounts[metric] = sampleCounts.GetValueOrDefault(metric) + 1;
                }
            };
            session.Source.Kernel.ThreadCSwitch += data =>
            {
                // Mirrors the off-CPU sampler's SchedSwitches semantic: count the OUT side once
                // per switch involving one of the target's threads.
                if (data.OldProcessID == processId) Interlocked.Increment(ref contextSwitches);
            };
            session.Source.Kernel.MemoryHardFault += data =>
            {
                if (data.ProcessID == processId) Interlocked.Increment(ref pageFaults);
            };

            var baseKeywords = KernelTraceEventParser.Keywords.ContextSwitch |
                                KernelTraceEventParser.Keywords.MemoryHardFaults |
                                KernelTraceEventParser.Keywords.Process;
            try
            {
                session.EnableKernelProvider(pmcRequested ? baseKeywords | KernelTraceEventParser.Keywords.PMCProfile : baseKeywords);
            }
            catch (Exception ex) when (pmcRequested && IsKernelSessionFailure(ex))
            {
                // A vPMU-less guest (common under Hyper-V/most VMs) typically fails specifically
                // when PMCProfile is requested. Degrade to context-switches/page-faults only,
                // rather than failing the whole capture — those kernel events don't need a PMU.
                pmcEnabled = false;
                notes.Add(
                    $"IPC/cache-miss/branch-miss rates unavailable: the ETW kernel PMC session failed to " +
                    $"start ({ex.Message}). This is expected under Hyper-V/most VMs, which do not expose a " +
                    $"virtualized PMU to the guest — see issue #828. Falling back to context-switches/page-" +
                    $"faults only, which do not require PMC hardware.");
                session.EnableKernelProvider(baseKeywords);
            }

            // Real-time kernel sessions require pumping the ETW callback thread; run Process()
            // on the thread pool and race it against a duration timer so we do not require the
            // full pump loop to observe cancellation immediately.
            var processTask = Task.Run(() => session.Source.Process(), cancellationToken);
            try
            {
                await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                session.Stop();
                try { await processTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
                catch { /* best effort — session.Dispose() below unblocks Process() either way */ }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsKernelSessionFailure(ex))
        {
            throw new InvalidOperationException(
                $"Failed to start the ETW kernel session. This is expected under Hyper-V/most VMs, which " +
                $"do not expose a virtualized PMU to the guest — see issue #828. Details: {ex.Message}", ex);
        }
        finally
        {
            try { session?.Stop(); } catch { /* best effort */ }
            session?.Dispose();
        }

        if (pmcRequested && !pmcEnabled)
        {
            sourceIdToMetric = new Dictionary<int, string>();
        }

        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (metric, count) in sampleCounts)
        {
            var interval = configuredIntervalBySourceId.TryGetValue(GetSourceIdForMetric(sourceIdToMetric, metric), out var configured)
                ? configured
                : 1;
            values[metric] = count * Math.Max(1, interval);
        }
        values["context-switches"] = contextSwitches;
        values["page-faults"] = pageFaults;

        notes.Add("cpu-migrations: no direct ETW kernel equivalent is commonly exposed on Windows; left unavailable.");
        notes.Add("stalled-cycles-frontend/backend and TLB miss rate: no commonly available Windows PMC profile source maps to these; Linux-only in this release (see issue #828).");
        notes.Add("Windows counters are estimated from PMC SAMPLING (count of samples × configured interval), not exact hardware aggregate counts like the Linux perf-stat backend — treat as order-of-magnitude, not exact.");

        return CpuEfficiencyAggregator.Build(processId, startedAt, duration, "etw-pmc", values, notes);
    }

    private static int GetSourceIdForMetric(IReadOnlyDictionary<int, string> sourceIdToMetric, string metric)
    {
        foreach (var (id, m) in sourceIdToMetric)
        {
            if (string.Equals(m, metric, StringComparison.Ordinal)) return id;
        }
        return -1;
    }

    private Dictionary<int, ProfileSourceInfo> SafeGetProfileSourceInfo(List<string> notes)
    {
        try
        {
            var byName = TraceEventProfileSources.GetInfo();
            var byId = new Dictionary<int, ProfileSourceInfo>();
            foreach (var info in byName.Values)
            {
                byId[info.ID] = info;
            }
            return byId;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TraceEventProfileSources.GetInfo() failed; PMC-derived metrics will be unavailable.");
            notes.Add($"Could not enumerate hardware profile sources on this host: {ex.Message}. IPC/cache-miss/branch-miss rates are unavailable.");
            return new Dictionary<int, ProfileSourceInfo>();
        }
    }

    private static (IReadOnlyDictionary<int, string> SourceIdToMetric, int[] SourceIds, int[] Intervals) SelectProfileSources(
        Dictionary<int, ProfileSourceInfo> sourcesById,
        List<string> notes)
    {
        var byName = new Dictionary<string, ProfileSourceInfo>(StringComparer.Ordinal);
        foreach (var info in sourcesById.Values)
        {
            byName[info.Name] = info;
        }

        var sourceIdToMetric = new Dictionary<int, string>();
        var ids = new List<int>();
        var intervals = new List<int>();

        foreach (var (metric, aliases) in ProfileSourceAliases)
        {
            ProfileSourceInfo? match = null;
            foreach (var alias in aliases)
            {
                if (byName.TryGetValue(alias, out var info))
                {
                    match = info;
                    break;
                }
            }

            if (match is null)
            {
                notes.Add($"{metric}: no matching Windows PMC profile source found on this host (tried: {string.Join(", ", aliases)}). This is common under Hyper-V/most VMs, which do not expose a virtualized PMU to the guest.");
                continue;
            }

            sourceIdToMetric[match.ID] = metric;
            ids.Add(match.ID);
            intervals.Add(Math.Max(match.MinInterval, match.Interval));
        }

        return (sourceIdToMetric, [.. ids], [.. intervals]);
    }

    private static bool IsKernelSessionFailure(Exception ex) => ex is InvalidOperationException or UnauthorizedAccessException;
}
