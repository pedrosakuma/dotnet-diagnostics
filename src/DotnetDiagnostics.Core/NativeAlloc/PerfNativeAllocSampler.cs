using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.OffCpu;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.NativeAlloc;

/// <summary>
/// Linux native-allocation sampler. Creates a dynamic uprobe on the target's libc allocator
/// (<c>perf probe -x &lt;libc&gt; '&lt;event&gt;=malloc'</c>), records a DWARF-unwound callchain on
/// every Nth hit (<c>perf record -e probe_libc:&lt;event&gt; --call-graph dwarf -c N -p &lt;pid&gt;</c>),
/// then reuses the shared <c>perf script → call-tree</c> pipeline of
/// <see cref="PerfNativeAotCpuSampler"/> to attribute the allocations to a call site.
/// </summary>
/// <remarks>
/// <para>See <see cref="INativeAllocSampler"/> for the contract and the overhead / privilege
/// caveats. The produced <see cref="CpuSampleTraceArtifact"/> is registered under the
/// <c>native-alloc-sample</c> handle kind and walked with <c>query_snapshot(view="call-tree")</c>.</para>
/// </remarks>
public sealed partial class PerfNativeAllocSampler : INativeAllocSampler
{
    // glibc allocators worth probing for a first cut. malloc is mandatory; the rest are
    // best-effort (some libc builds may not export every symbol as a probe target).
    private static readonly string[] DefaultAllocators = { "malloc", "calloc", "realloc" };

    // Matches the "Added new event:\n  probe_libc:NAME (on malloc in ...)" line perf probe prints.
    [GeneratedRegex(@"([A-Za-z0-9_]+:[A-Za-z0-9_]+)\s+\(on\b", RegexOptions.None)]
    private static partial Regex CreatedTracepointRegex();

    // 512 MiB cap mirrors the off-CPU sampler: bounds disaster on an allocator-hot multi-minute run.
    private const long PerfDataMaxBytes = 512L * 1024 * 1024;

    private readonly ILogger<PerfNativeAllocSampler> _logger;
    private readonly JitMapEmitter _jitMapEmitter;
    private readonly string _configuredPath;
    private string? _resolvedPath;
    private bool _resolutionAttempted;
    private readonly object _resolveLock = new();

    public PerfNativeAllocSampler(
        ILogger<PerfNativeAllocSampler>? logger = null,
        string perfPath = "perf",
        JitMapEmitter? jitMapEmitter = null)
    {
        _logger = logger ?? NullLogger<PerfNativeAllocSampler>.Instance;
        _configuredPath = perfPath;
        _jitMapEmitter = jitMapEmitter ?? new JitMapEmitter();
    }

    private string? ResolvePerfPath()
    {
        if (_resolutionAttempted) return _resolvedPath;
        lock (_resolveLock)
        {
            if (_resolutionAttempted) return _resolvedPath;
            _resolvedPath = PerfBinaryResolver.Resolve(
                _configuredPath,
                PerfBinaryResolver.EnumerateDefaultLinuxToolsCandidates,
                PerfBinaryResolver.ProbePerfVersion);
            _resolutionAttempted = true;
            return _resolvedPath;
        }
    }

    public bool IsAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return false;
        return ResolvePerfPath() is not null;
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
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new PlatformNotSupportedException(
                "Native allocation sampling is only supported on Linux (perf uprobes on the libc " +
                "malloc/calloc/realloc allocators) in this release. It is not available on Windows or " +
                "macOS — there is no perf-uprobe equivalent wired up for those platforms.");
        }
        if (ResolvePerfPath() is null)
        {
            throw new NotSupportedException(
                "The perf binary was not found on this Linux host. Install linux-perf (the " +
                "'linux-tools'/'perf' package) so native allocation sampling can create a uprobe on " +
                "the target libc allocator.");
        }

        var libc = ProcMapsLibcResolver.Resolve(processId)
            ?? throw new NotSupportedException(
                $"Could not locate a libc mapping in /proc/{processId}/maps — the process may have " +
                "exited, be statically linked, or use an unsupported C library. Native allocation " +
                "sampling needs a shared libc to uprobe the native allocator.");

        // Unique per run: pid + short guid keeps the uprobe event name from colliding with a
        // concurrent sampler or a stale leftover probe from a crashed run.
        var runToken = $"{processId}_{Guid.NewGuid():N}"[..16];

        var perfDataPath = Path.Combine(Path.GetTempPath(),
            $"diagnosticsmcp-nativealloc-{processId}-{Guid.NewGuid():N}.data");
        var startedAt = DateTimeOffset.UtcNow;
        var notes = new List<string>
        {
            "Counts are sampled allocator-call hits (malloc/calloc/realloc), not bytes; a frequent " +
            "small allocator can outrank a rare large one.",
            "uprobe overhead: every allocator call still traps even though only 1-in-samplePeriod " +
            "callchains are recorded — keep the window short on allocator-hot workloads.",
        };
        JitMapResult? jitMap = null;
        var createdProbes = new List<string>();
        var probedFunctions = new List<string>();

        try
        {
            // Emit /tmp/perf-<pid>.map BEFORE recording so managed frames above a P/Invoke
            // boundary resolve to method names instead of raw hex. Best-effort (NativeAOT / a
            // closed diagnostic socket simply yields native-only frames).
            try
            {
                jitMap = await _jitMapEmitter.EmitAsync(processId, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "JIT perf-map emission failed for pid {Pid} (continuing native-only).", processId);
            }

            string? lastProbeError = null;
            foreach (var fn in DefaultAllocators)
            {
                var eventName = BuildEventName(fn, runToken);
                var (exit, stdout, stderr) = await RunPerfAsync(
                    new[] { "probe", "-x", libc.HostPath, $"{eventName}={fn}" }, cancellationToken).ConfigureAwait(false);
                if (exit == 0)
                {
                    var tracepoint = ParseCreatedTracepoint(stdout + "\n" + stderr) ?? $"probe_libc:{eventName}";
                    createdProbes.Add(tracepoint);
                    probedFunctions.Add(fn);
                }
                else
                {
                    lastProbeError = stderr.Trim();
                    _logger.LogDebug("perf probe for {Function} failed (exit {Exit}): {Stderr}", fn, exit, lastProbeError);
                }
            }

            if (createdProbes.Count == 0)
            {
                throw new InvalidOperationException(
                    "perf probe could not create a uprobe on the target libc allocator. This usually " +
                    "means the sidecar lacks CAP_SYS_ADMIN / tracefs write access. " +
                    $"Last perf stderr: {lastProbeError}");
            }

            // malloc is the primary allocator and mandatory; calloc/realloc are best-effort. A run
            // that probed only the secondary allocators would silently miss the dominant call path.
            if (!probedFunctions.Contains("malloc", StringComparer.Ordinal))
            {
                throw new NotSupportedException(
                    "Could not uprobe malloc on the target libc — the symbol may be stripped, inlined, " +
                    "or interposed by a custom allocator (e.g. jemalloc/tcmalloc/mimalloc). Native " +
                    $"allocation sampling needs malloc to attribute the primary allocator. perf stderr: {lastProbeError}");
            }

            if (probedFunctions.Count < DefaultAllocators.Length)
            {
                var missing = DefaultAllocators.Except(probedFunctions, StringComparer.Ordinal);
                notes.Add($"Could not uprobe: {string.Join(", ", missing)} — results cover only {string.Join(", ", probedFunctions)}.");
            }

            await RecordAsync(processId, perfDataPath, duration, samplePeriod, createdProbes, cancellationToken).ConfigureAwait(false);

            try
            {
                if (new FileInfo(perfDataPath).Length >= PerfDataMaxBytes)
                {
                    notes.Add($"perf.data hit the {PerfDataMaxBytes / (1024 * 1024)} MiB cap; capture stopped early — raise samplePeriod or shorten the window.");
                }
            }
            catch { /* best effort */ }

            var script = await RunScriptAsync(perfDataPath, cancellationToken).ConfigureAwait(false);
            // processId: 0 — trust perf record -p <pid> for scoping (mirrors the CPU sampler:
            // avoids dropping samples from worker threads that exited before parsing).
            var (total, hotspots, root, symbolSource, _) = PerfNativeAotCpuSampler.Aggregate(script, processId: 0, topN);

            if (total == 0)
            {
                notes.Add("No allocator-call samples landed in the window — the workload may not have " +
                          "allocated natively, or samplePeriod is too high for a quiet process.");
            }

            var artifact = new CpuSampleTraceArtifact(processId, startedAt, duration, total, root, null, null, symbolSource);
            var summary = new NativeAllocSample(
                processId,
                startedAt,
                duration,
                total,
                hotspots,
                probedFunctions,
                libc.InNamespacePath,
                samplePeriod,
                symbolSource.ToString(),
                notes);
            return new NativeAllocSampleResult(summary, artifact);
        }
        finally
        {
            foreach (var probe in createdProbes)
            {
                try
                {
                    // Best-effort teardown of the global kernel uprobe. CancellationToken.None so
                    // cleanup still runs when the caller cancelled the sampling window.
                    await RunPerfAsync(new[] { "probe", "-d", probe }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to delete uprobe {Probe}.", probe);
                }
            }
            TryDelete(perfDataPath);
            if (jitMap is not null) TryDelete(jitMap.MapPath);
        }
    }

    /// <summary>
    /// Builds a perf-probe event name that is a valid C identifier and unique per run, so
    /// concurrent samplers and stale leftover probes never collide. Exposed for unit tests.
    /// </summary>
    internal static string BuildEventName(string function, string runToken)
    {
        var safeFn = SanitizeIdentifier(function);
        var safeToken = SanitizeIdentifier(runToken);
        return $"diagmcp_{safeFn}_{safeToken}";
    }

    /// <summary>
    /// Extracts the <c>group:event</c> tracepoint name perf prints after a successful
    /// <c>perf probe</c>. Returns null when the output has no recognizable "Added new event" line.
    /// Exposed for unit tests.
    /// </summary>
    internal static string? ParseCreatedTracepoint(string perfProbeOutput)
    {
        if (string.IsNullOrEmpty(perfProbeOutput)) return null;
        var match = CreatedTracepointRegex().Match(perfProbeOutput);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string SanitizeIdentifier(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_')
            {
                chars[i] = '_';
            }
        }
        return new string(chars);
    }

    private async Task RecordAsync(
        int pid, string outputPath, TimeSpan duration, long samplePeriod,
        IReadOnlyList<string> tracepoints, CancellationToken ct)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds));
        var args = new List<string> { "record" };
        foreach (var tp in tracepoints)
        {
            args.Add("-e");
            args.Add(tp);
        }
        // --call-graph dwarf: user-space DWARF unwinding (libc has no frame pointers by default).
        // -c <period>: record one callchain per <period> allocator hits to throttle unwind cost.
        args.AddRange(new[]
        {
            "--call-graph", "dwarf",
            "-c", samplePeriod.ToString(CultureInfo.InvariantCulture),
            "-p", pid.ToString(CultureInfo.InvariantCulture),
            "--max-size", PerfDataMaxBytes.ToString(CultureInfo.InvariantCulture),
            "-o", outputPath,
            "--", "sleep", seconds.ToString(CultureInfo.InvariantCulture),
        });

        var (exit, _, stderr) = await RunPerfAsync(args, ct).ConfigureAwait(false);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"perf record (native-alloc) exited with code {exit}. stderr: {stderr.Trim()}");
        }
    }

    private async Task<string> RunScriptAsync(string perfDataPath, CancellationToken ct)
    {
        var (exit, stdout, stderr) = await RunPerfAsync(
            new[] { "script", "-i", perfDataPath, "--no-inline" }, ct).ConfigureAwait(false);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"perf script exited with code {exit}. stderr: {stderr.Trim()}");
        }
        return stdout;
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunPerfAsync(
        IReadOnlyList<string> args, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePerfPath()!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) startInfo.ArgumentList.Add(a);

        _logger.LogDebug("Spawning perf: {Bin} {Args}", startInfo.FileName, string.Join(' ', args));

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* best effort */ }
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (process.ExitCode, stdout, stderr);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
