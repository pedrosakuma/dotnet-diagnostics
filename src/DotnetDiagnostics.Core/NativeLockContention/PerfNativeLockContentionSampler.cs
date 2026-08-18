using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.NativeAlloc;
using DotnetDiagnostics.Core.OffCpu;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.NativeLockContention;

/// <summary>
/// Linux native-lock-contention sampler. Creates a dynamic uprobe on the target's libc mutex entry
/// points (<c>perf probe -x &lt;libc&gt; '&lt;event&gt;=pthread_mutex_lock'</c>), records a
/// DWARF-unwound callchain on every Nth hit (<c>perf record -e probe_libc:&lt;event&gt;
/// --call-graph dwarf -c N -p &lt;pid&gt;</c>), then reuses the shared
/// <c>perf script → call-tree</c> pipeline of <see cref="PerfNativeAotCpuSampler"/> to attribute
/// the mutex calls to a call site.
/// </summary>
/// <remarks>
/// <para>Structurally this is the lock-contention sibling of <see cref="PerfNativeAllocSampler"/>
/// (issue #830) — same uprobe-on-libc-symbol mechanism, same Nth-sample throttling, same
/// unique-per-run probe naming, same best-effort teardown, same DWARF unwinding. The stack
/// aggregation is delegated to the (deliberately OS/attribution-agnostic)
/// <see cref="NativeAllocStackAggregator"/> rather than duplicating it, since the aggregation math
/// has no dependency on "allocation" vs. "lock call" semantics — it just merges leaf→root frame
/// lists into a call tree plus ranked hotspots.</para>
/// <para>See <see cref="INativeLockContentionSampler"/> for the contract, the wait-vs-call-count
/// caveat, and the privilege requirements. The produced <see cref="CpuSampleTraceArtifact"/> is
/// registered under the <c>native-lock-contention-sample</c> handle kind and walked with
/// <c>query_snapshot(view="call-tree")</c>.</para>
/// </remarks>
public sealed partial class PerfNativeLockContentionSampler : INativeLockContentionSampler
{
    // Narrow first cut (issue #830): only the mutex lock/unlock entry points. Condition
    // variables, semaphores, and reader-writer locks are explicitly out of scope — pthread_mutex_lock
    // is mandatory (like malloc for the allocation sampler), pthread_mutex_unlock is best-effort.
    private static readonly string[] DefaultMutexFunctions = { "pthread_mutex_lock", "pthread_mutex_unlock" };

    // 512 MiB cap mirrors the native-allocation / off-CPU samplers: bounds disaster on a
    // mutex-hot multi-minute run.
    private const long PerfDataMaxBytes = 512L * 1024 * 1024;
    private const long PerfScriptSampleBudget = 250_000;
    private static readonly TimeSpan PerfProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PerfProbeCleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PerfScriptTimeout = TimeSpan.FromMinutes(2);

    private readonly ILogger<PerfNativeLockContentionSampler> _logger;
    private readonly JitMapEmitter _jitMapEmitter;
    private readonly string _configuredPath;
    private string? _resolvedPath;
    private bool _resolutionAttempted;
    private readonly object _resolveLock = new();

    public PerfNativeLockContentionSampler(
        ILogger<PerfNativeLockContentionSampler>? logger = null,
        string perfPath = "perf",
        JitMapEmitter? jitMapEmitter = null)
    {
        _logger = logger ?? NullLogger<PerfNativeLockContentionSampler>.Instance;
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

    public async Task<NativeLockContentionSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        long samplePeriod = 5000,
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
                "Native lock-contention sampling is only supported on Linux (perf uprobes on the libc " +
                "pthread_mutex_lock/pthread_mutex_unlock entry points) in this release. It is not " +
                "available on Windows — see collect_sample(kind=\"native-lock-contention\") documentation " +
                "for why there is no supported ETW equivalent yet.");
        }
        if (ResolvePerfPath() is null)
        {
            throw new NotSupportedException(
                "The perf binary was not found on this Linux host. Install linux-perf (the " +
                "'linux-tools'/'perf' package) so native lock-contention sampling can create a uprobe on " +
                "the target libc mutex entry points.");
        }

        var libc = ProcMapsLibcResolver.Resolve(processId)
            ?? throw new NotSupportedException(
                $"Could not locate a libc mapping in /proc/{processId}/maps — the process may have " +
                "exited, be statically linked, or use an unsupported C library. Native lock-contention " +
                "sampling needs a shared libc to uprobe the native mutex implementation.");

        // Unique per run: pid + short guid keeps the uprobe event name from colliding with a
        // concurrent sampler or a stale leftover probe from a crashed run.
        var runToken = $"{processId}_{Guid.NewGuid():N}"[..16];

        var perfDataPath = Path.Combine(Path.GetTempPath(),
            $"diagnosticsmcp-nativelockcontention-{processId}-{Guid.NewGuid():N}.data");
        var startedAt = DateTimeOffset.UtcNow;
        var notes = new List<string>
        {
            "Counts are sampled mutex-call hits (pthread_mutex_lock/pthread_mutex_unlock), not " +
            "measured wait time; an uncontended fast-path lock (single CAS, no futex syscall) is " +
            "indistinguishable from a genuinely blocked one at this uprobe. Corroborate with " +
            "collect_sample(kind=\"off_cpu\") to confirm the thread actually blocked.",
            "uprobe overhead: every mutex call still traps even though only 1-in-samplePeriod " +
            "callchains are recorded — keep the window short on mutex-hot workloads.",
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
            foreach (var fn in DefaultMutexFunctions)
            {
                var eventName = PerfNativeAllocSampler.BuildEventName(fn, runToken);
                var (exit, stdout, stderr) = await RunPerfAsync(
                    new[] { "probe", "-x", libc.HostPath, $"{eventName}={fn}" },
                    PerfProbeTimeout,
                    $"perf probe creation for {fn}",
                    cancellationToken).ConfigureAwait(false);
                if (exit == 0)
                {
                    var tracepoint = PerfNativeAllocSampler.ParseCreatedTracepoint(stdout + "\n" + stderr) ?? $"probe_libc:{eventName}";
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
                    "perf probe could not create a uprobe on the target libc mutex entry point. This " +
                    "usually means the sidecar lacks CAP_SYS_ADMIN / tracefs write access. " +
                    $"Last perf stderr: {lastProbeError}");
            }

            // pthread_mutex_lock is the primary signal and mandatory; pthread_mutex_unlock is
            // best-effort. A run that probed only unlock would silently miss the acquisition side.
            if (!probedFunctions.Contains("pthread_mutex_lock", StringComparer.Ordinal))
            {
                throw new NotSupportedException(
                    "Could not uprobe pthread_mutex_lock on the target libc — the symbol may be " +
                    "stripped, inlined, or interposed by a custom threading library. Native " +
                    $"lock-contention sampling needs pthread_mutex_lock to attribute the primary signal. perf stderr: {lastProbeError}");
            }

            if (probedFunctions.Count < DefaultMutexFunctions.Length)
            {
                var missing = DefaultMutexFunctions.Except(probedFunctions, StringComparer.Ordinal);
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

            var aggregate = await RunScriptAsync(perfDataPath, topN, jitMap, cancellationToken).ConfigureAwait(false);
            if (aggregate.Truncated)
            {
                notes.Add($"Stopped parsing perf script after {PerfScriptSampleBudget:N0} samples to keep mutex-hot captures bounded; hotspots reflect the processed prefix only.");
            }
            PerfJitSymbolizationNotes.Add(notes, aggregate.JitCandidateFrames, aggregate.ResolvedJitFrames, aggregate.UnresolvedJitCandidateFrames);

            if (aggregate.Total == 0)
            {
                notes.Add("No mutex-call samples landed in the window — the workload may not have " +
                          "called pthread_mutex_lock/unlock natively, or samplePeriod is too high for a quiet process.");
            }

            var stampedRoot = CallTreeIdentityProjector.Stamp(aggregate.Root, aggregate.Identities);
            var artifact = new CpuSampleTraceArtifact(processId, startedAt, duration, aggregate.Total, stampedRoot, null, aggregate.Identities, aggregate.SymbolSource);
            var summary = new NativeLockContentionSample(
                processId,
                startedAt,
                duration,
                aggregate.Total,
                aggregate.Hotspots,
                probedFunctions,
                libc.InNamespacePath,
                samplePeriod,
                aggregate.SymbolSource.ToString(),
                notes);
            return new NativeLockContentionSampleResult(summary, artifact);
        }
        finally
        {
            foreach (var probe in createdProbes)
            {
                try
                {
                    // Best-effort teardown of the global kernel uprobe. CancellationToken.None so
                    // cleanup still runs when the caller cancelled the sampling window.
                    await RunPerfAsync(
                        new[] { "probe", "-d", probe },
                        PerfProbeCleanupTimeout,
                        $"perf probe teardown for {probe}",
                        CancellationToken.None).ConfigureAwait(false);
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
        // -c <period>: record one callchain per <period> mutex-call hits to throttle unwind cost.
        args.AddRange(new[]
        {
            "--call-graph", "dwarf",
            "-c", samplePeriod.ToString(CultureInfo.InvariantCulture),
            "-p", pid.ToString(CultureInfo.InvariantCulture),
            "--max-size", PerfNativeAotCpuSampler.FormatPerfFileSize(PerfDataMaxBytes),
            "-o", outputPath,
            "--", "sleep", seconds.ToString(CultureInfo.InvariantCulture),
        });

        var (exit, _, stderr) = await RunPerfAsync(
            args,
            duration + TimeSpan.FromSeconds(15),
            "perf record (native-lock-contention)",
            ct).ConfigureAwait(false);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"perf record (native-lock-contention) exited with code {exit}. stderr: {stderr.Trim()}");
        }
    }

    private async Task<PerfScriptAggregationResult> RunScriptAsync(string perfDataPath, int topN, JitMapResult? jitMap, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePerfPath()!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("script");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(perfDataPath);
        startInfo.ArgumentList.Add("--no-inline");

        _logger.LogDebug("Spawning perf: {Bin} script -i {PerfDataPath} --no-inline", startInfo.FileName, perfDataPath);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        return await BoundedProcessExecution.RunAsync(
            process,
            PerfScriptTimeout,
            "perf script (native-lock-contention)",
            async boundedToken =>
            {
            var aggregate = await PerfNativeAotCpuSampler.AggregateAsync(
                process.StandardOutput,
                processId: 0,
                topN: topN,
                jitMap: jitMap,
                sampleBudget: PerfScriptSampleBudget,
                cancellationToken: boundedToken).ConfigureAwait(false);
            if (aggregate.Truncated && !process.HasExited)
            {
                try { process.Kill(true); } catch { /* best effort */ }
            }

            await process.WaitForExitAsync(boundedToken).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0 && !aggregate.Truncated)
            {
                throw new InvalidOperationException(
                    $"perf script exited with code {process.ExitCode}. stderr: {stderr.Trim()}");
            }

            return aggregate;
            },
            ct).ConfigureAwait(false);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunPerfAsync(
        IReadOnlyList<string> args,
        TimeSpan timeout,
        string operation,
        CancellationToken ct)
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
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        return await BoundedProcessExecution.RunAsync(
            process,
            timeout,
            operation,
            async boundedToken =>
            {
                await process.WaitForExitAsync(boundedToken).ConfigureAwait(false);
                return (
                    process.ExitCode,
                    await stdoutTask.ConfigureAwait(false),
                    await stderrTask.ConfigureAwait(false));
            },
            ct).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
