using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DotnetDiagnostics.Core.CpuSampling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.OffCpu;

/// <summary>
/// Linux off-CPU sampler driven by <c>perf record -a -e sched:sched_switch --call-graph dwarf -- sleep N</c>.
/// System-wide capture is required because <c>sched_switch</c> fires on the OUTGOING task only; restricting
/// the recorder to one PID would deny us the matching INCOMING event needed to close each off-CPU span.
/// Post-filter by the target's TID set keeps memory bounded and the result attributable.
/// </summary>
/// <remarks>
/// <para>Requirements (validated by <see cref="IsAvailable"/>): Linux host, <c>perf</c> binary in <c>PATH</c>,
/// and either <c>CAP_PERFMON</c> (kernel ≥ 5.8) or <c>perf_event_paranoid &lt;= -1</c>. <c>-a</c> system-wide
/// tracepoint access is broader than the on-CPU sampler's per-PID profile and may need an extra capability
/// on locked-down hosts; we propagate stderr verbatim when <c>perf record</c> fails so the LLM gets the
/// actionable kernel diagnostic.</para>
/// <para>The blocking stack is the kernel+user callchain captured at <c>sched_switch</c> on the outgoing
/// thread — typically <c>schedule → futex_wait_queue → __pthread_cond_wait</c>. We do NOT attempt to merge
/// managed frames here; that lands in sub-slice 2c together with the <c>depth</c> parameter.</para>
/// </remarks>
public sealed class PerfSchedOffCpuSampler : IOffCpuSampler
{
    private readonly ILogger<PerfSchedOffCpuSampler> _logger;
    private readonly JitMapEmitter _jitMapEmitter;
    private readonly string _configuredPath;
    private string? _resolvedPath;
    private bool _resolutionAttempted;
    private readonly object _resolveLock = new();

    public PerfSchedOffCpuSampler(
        ILogger<PerfSchedOffCpuSampler>? logger = null,
        string perfPath = "perf",
        JitMapEmitter? jitMapEmitter = null)
    {
        _logger = logger ?? NullLogger<PerfSchedOffCpuSampler>.Instance;
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

    public async Task<OffCpuSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        string? symbolPath = null,
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
        if (!IsAvailable())
        {
            throw new InvalidOperationException(
                "perf is not available on this host. Install linux-perf and ensure the diagnostics " +
                "container has CAP_PERFMON (or CAP_SYS_ADMIN) plus permission for system-wide tracepoint " +
                "access (perf_event_paranoid <= -1 on locked-down kernels).");
        }

        var targetTids = ReadTargetTids(processId);
        if (targetTids.Count == 0)
        {
            throw new InvalidOperationException(
                $"Could not enumerate any TID under /proc/{processId}/task. The process may have exited.");
        }
        var initialTidCount = targetTids.Count;

        var perfDataPath = Path.Combine(Path.GetTempPath(),
            $"diagnosticsmcp-offcpu-{processId}-{Guid.NewGuid():N}.data");
        var startedAt = DateTimeOffset.UtcNow;
        var notes = new List<string>();
        // Hoisted above the try so the finally block can clean up the perf-map even when
        // emission succeeded but a later step (perf record / script / parse) threw. The
        // emitter writes /tmp/perf-<pid>.map, so a stale map left behind for a recycled PID
        // would contaminate a later capture's symbolization.
        JitMapResult? jitMap = null;

        try
        {
            // Emit /tmp/perf-<pid>.map BEFORE perf record so that the rundown method addresses
            // are visible to the kernel-side stack collector via perf's standard JIT-map path.
            // Best-effort: failure leaves us with native-only frames in managed code, but does
            // not block the sampling window. NativeAOT targets simply have nothing to emit.
            try
            {
                jitMap = await _jitMapEmitter.EmitAsync(processId, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (jitMap is { MethodCount: > 0 })
                {
                    _logger.LogDebug("JIT perf-map emitted for pid {Pid}: {Methods} methods → {Path}",
                        processId, jitMap.MethodCount, jitMap.MapPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "JIT perf-map emission failed for pid {Pid} (continuing without managed names).", processId);
            }

            await RecordAsync(perfDataPath, duration, cancellationToken).ConfigureAwait(false);

            // Re-snapshot TIDs post-record and union: catches threads that were created
            // during the sampling window. Short-lived threads that both start and exit
            // inside the window are still missed; we surface that as a Note.
            var postTids = ReadTargetTids(processId);
            var newTidCount = 0;
            foreach (var t in postTids)
            {
                if (targetTids.Add(t)) newTidCount++;
            }
            if (newTidCount > 0)
            {
                notes.Add($"{newTidCount} thread(s) appeared after capture start; their pre-creation off-CPU events (if any) are excluded. Short-lived threads that ended before the post-capture rescan are not attributed.");
            }

            // perf.data size sanity check — system-wide sched_switch + DWARF is expensive on busy hosts.
            try
            {
                var sizeBytes = new FileInfo(perfDataPath).Length;
                if (sizeBytes >= PerfDataMaxBytes)
                {
                    notes.Add($"perf.data hit the {PerfDataMaxBytes / (1024 * 1024)} MiB cap; capture stopped early — results cover less than the requested {duration.TotalSeconds:F0}s. Consider a shorter window on busy hosts.");
                }
            }
            catch { /* best effort */ }

            Func<ulong, DotnetDiagnostics.Core.Memory.MethodIdentity?>? resolver = jitMap is null
                ? null
                : jitMap.Resolve;
            return await RunScriptAsync(
                perfDataPath,
                processId,
                startedAt,
                duration,
                topN,
                targetTids,
                resolver,
                notes,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(perfDataPath);
            // Delete /tmp/perf-<pid>.map so a recycled PID can't pick up stale managed
            // symbols on the next capture (the OS would otherwise leave it until reboot).
            if (jitMap is not null)
            {
                TryDelete(jitMap.MapPath);
            }
        }
    }

    // 512 MiB is generous for the default 10s window but bounds disaster on a multi-minute
    // capture on a 96-core box doing thousands of context switches per second.
    private const long PerfDataMaxBytes = 512L * 1024 * 1024;

    private static HashSet<int> ReadTargetTids(int pid)
    {
        var set = new HashSet<int>();
        var taskDir = $"/proc/{pid}/task";
        try
        {
            if (Directory.Exists(taskDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(taskDir))
                {
                    var name = Path.GetFileName(dir);
                    if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tid))
                    {
                        set.Add(tid);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Best effort. PID itself is always added below.
        }
        set.Add(pid);
        return set;
    }

    private async Task RecordAsync(string outputPath, TimeSpan duration, CancellationToken ct)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds));
        // -a: system-wide (mandatory for sched_switch IN-side pairing).
        // -e sched:sched_switch,raw_syscalls:sys_enter,raw_syscalls:sys_exit: sched_switch closes
        // off-CPU spans; the two raw_syscalls tracepoints let us label each span with the
        // syscall that was active at block time (issue #829). perf natively supports multiple
        // comma-separated -e events writing into one perf.data — recording them together in this
        // single invocation is cheaper than a second, separately-triggered `perf record` pass
        // (one -a system-wide window instead of two) and keeps all three tracepoints on the same
        // wall clock, which the tid+timestamp correlation in SyscallIntervalIndex relies on.
        // --call-graph dwarf: user-space DWARF unwinding so we capture user frames, not just the
        // first kernel frame. Bigger perf.data but accuracy-critical for blocking-stack attribution.
        // It also applies to the raw_syscalls events (no cheaper "stackless" per-event modifier is
        // used here for simplicity/portability across perf versions); we intentionally don't parse
        // those callchains — see PerfSyscallScriptParser's -G note — so the extra frames are inert
        // record-time cost, still bounded by the same --max-size cap below.
        var args = $"record -a -e sched:sched_switch,raw_syscalls:sys_enter,raw_syscalls:sys_exit --call-graph dwarf --max-size={PerfNativeAotCpuSampler.FormatPerfFileSize(PerfDataMaxBytes)} -o \"{outputPath}\" -- sleep {seconds}";
        _logger.LogDebug("Spawning perf for off-CPU capture: {Bin} {Args}", ResolvePerfPath()!, args);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolvePerfPath()!,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
            EnableRaisingEvents = true,
        };

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

        await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"perf record (sched) exited with code {process.ExitCode}. stderr: {stderr.Trim()}");
        }
    }

    private async Task<OffCpuSampleResult> RunScriptAsync(
        string perfDataPath,
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        int topN,
        HashSet<int> targetTids,
        Func<ulong, DotnetDiagnostics.Core.Memory.MethodIdentity?>? addressResolver,
        List<string> notes,
        CancellationToken ct)
    {
        // Best-effort syscall correlation pass (issue #829) BEFORE the sched_switch script pass:
        // the interval index must be fully built before any span is emitted so each span can be
        // labeled at insertion time rather than buffering all spans for a later enrichment pass
        // (keeping PerfSchedScriptParser's streaming design — see resource-boundedness.md — intact).
        SyscallIntervalIndex? syscallIndex = null;
        try
        {
            var buildResult = await BuildSyscallIntervalIndexAsync(perfDataPath, targetTids, ct).ConfigureAwait(false);
            syscallIndex = buildResult.Index;
            if (buildResult.ParserHitCap)
            {
                notes.Add(
                    $"Syscall correlation stopped parsing raw_syscalls events after reaching the {PerfSyscallScriptParser.MaxParsedEvents:N0}-event budget; " +
                    $"{buildResult.ParserDroppedCount} event(s) beyond that point were ignored, so some off-CPU spans may be missing a syscall label.");
            }
            if (syscallIndex is { HitCap: true })
            {
                notes.Add(
                    $"Syscall correlation hit the {SyscallIntervalIndex.MaxIntervals}-interval cap; " +
                    $"{syscallIndex.DroppedCount} syscall interval(s) were dropped, so some off-CPU spans may be missing a syscall label.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Syscall correlation pass failed for off-CPU capture on pid {Pid}; spans will not be labeled with a syscall.", processId);
        }

        var args = $"script -i \"{perfDataPath}\" --no-inline";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolvePerfPath()!,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            var result = await AggregateScriptAsync(
                process.StandardOutput,
                processId,
                startedAt,
                duration,
                topN,
                targetTids,
                addressResolver,
                notes,
                syscallIndex,
                ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"perf script exited with code {process.ExitCode}. stderr: {stderr.Trim()}");
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* best effort */ }
            throw;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Runs a second, independent <c>perf script</c> pass over the same <c>perf.data</c> file to
    /// extract the co-recorded <c>raw_syscalls:sys_enter</c>/<c>sys_exit</c> events and correlate
    /// them into a <see cref="SyscallIntervalIndex"/> (issue #829). Best-effort: any failure here
    /// (perf version renders the tracepoints unexpectedly, process error, etc.) returns <c>null</c>
    /// rather than failing the whole off-CPU capture — a missing syscall label degrades the
    /// enrichment, not the underlying off-CPU span data the sampler already provided pre-#829.
    /// <c>-G</c>/<c>--hide-call-graph</c> suppresses the (potentially large) DWARF call chains
    /// perf would otherwise interleave under every event in this pass — we only need the flat
    /// event lines here, not stacks, which keeps <see cref="PerfSyscallScriptParser"/> a simple
    /// line-oriented parser instead of needing the frame-consumption state machine
    /// <see cref="PerfSchedScriptParser"/> uses for DWARF-unwound sched_switch stacks.
    /// </summary>
    /// <summary>Result of the best-effort syscall correlation pre-pass: the interval index (if any events were found) plus whether the raw per-event parse budget was hit.</summary>
    private readonly record struct SyscallIntervalBuildResult(SyscallIntervalIndex? Index, bool ParserHitCap, long ParserDroppedCount);

    private async Task<SyscallIntervalBuildResult> BuildSyscallIntervalIndexAsync(
        string perfDataPath,
        HashSet<int> targetTids,
        CancellationToken ct)
    {
        var args = $"script -i \"{perfDataPath}\" --no-inline -G";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolvePerfPath()!,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        PerfSyscallScriptParser.ParseResult parseResult;
        try
        {
            parseResult = await PerfSyscallScriptParser.ParseAsync(process.StandardOutput, targetTids, ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                _logger.LogDebug(
                    "perf script (syscalls) exited with code {Code}; off-CPU spans will not be labeled with a syscall. stderr: {Stderr}",
                    process.ExitCode, stderr.Trim());
                return default;
            }
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* best effort */ }
            throw;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch { /* best effort */ }
            throw;
        }

        if (parseResult.Events.Count == 0)
        {
            return new SyscallIntervalBuildResult(null, parseResult.HitCap, parseResult.DroppedCount);
        }

        // Deliberately NOT events.Max(...): if the last observed syscall event for a TID is an
        // unmatched sys_enter (the thread was still inside the syscall when perf stopped tracing
        // that TID — the common "blocking syscall, no sys_exit before capture end" case this
        // enrichment exists to label), bounding the resulting open interval at the max *syscall*
        // timestamp can be earlier than the sched_switch OUT timestamp we look up against later
        // (sched_switch and raw_syscalls are independent tracepoints; there is no guarantee the
        // very last line in this pass's output is later than every OUT event). Using
        // PositiveInfinity for the open-interval end means "still in flight through the rest of
        // the capture", which is always safe: every lookup timestamp we're ever asked about
        // (`span.OutTimestampSeconds`) comes from a real event inside the same perf.data, i.e.
        // strictly less than "the rest of time", so this cannot fabricate a false correlation
        // beyond the capture window.
        var captureEndTs = double.PositiveInfinity;
        var index = SyscallIntervalIndex.Build(parseResult.Events, captureEndTs);
        return new SyscallIntervalBuildResult(index, parseResult.HitCap, parseResult.DroppedCount);
    }

    internal static async Task<OffCpuSampleResult> AggregateScriptAsync(
        TextReader reader,
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        int topN,
        HashSet<int> targetTids,
        Func<ulong, DotnetDiagnostics.Core.Memory.MethodIdentity?>? addressResolver = null,
        IReadOnlyList<string>? notes = null,
        SyscallIntervalIndex? syscallIndex = null,
        CancellationToken cancellationToken = default)
    {
        var builder = OffCpuAggregator.CreateBuilder();
        Action<OffCpuSpan> onSpan = syscallIndex is null
            ? builder.AddSpan
            : span => builder.AddSpan(EnrichWithSyscall(span, syscallIndex));
        var switches = await PerfSchedScriptParser.ParseAsync(
            reader,
            targetTids,
            onSpan,
            flushPending: true,
            addressResolver: addressResolver,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return builder.Build(processId, startedAt, duration, switches, topN, "perf-sched-dwarf", notes);
    }

    /// <summary>Labels <paramref name="span"/> with the syscall in flight at its OUT timestamp, if any was correlated.</summary>
    private static OffCpuSpan EnrichWithSyscall(OffCpuSpan span, SyscallIntervalIndex syscallIndex)
    {
        if (span.OutTimestampSeconds is not { } ts) return span;
        var syscallId = syscallIndex.Lookup(span.Tid, ts);
        return syscallId is null ? span : span with { Syscall = SyscallTable.Resolve(syscallId.Value) };
    }

    /// <summary>
    /// Back-compat wrapper around <see cref="OffCpuAggregator.Aggregate"/> that pins the
    /// <c>SymbolSource</c> tag to <c>"perf-sched-dwarf"</c>. Kept so existing unit tests
    /// (which call <c>PerfSchedOffCpuSampler.Aggregate</c> directly) continue to compile and
    /// keep covering the shared aggregation path.
    /// </summary>
    internal static OffCpuSampleResult Aggregate(
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        IReadOnlyList<OffCpuSpan> spans,
        long schedSwitches,
        int topN,
        IReadOnlyList<string>? notes = null)
        => OffCpuAggregator.Aggregate(processId, startedAt, duration, spans, schedSwitches, topN, "perf-sched-dwarf", notes);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
