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

        var captureId = Guid.NewGuid().ToString("N");
        var schedPerfDataPath = Path.Combine(Path.GetTempPath(),
            $"diagnosticsmcp-offcpu-sched-{processId}-{captureId}.data");
        var syscallPerfDataPath = Path.Combine(Path.GetTempPath(),
            $"diagnosticsmcp-offcpu-syscalls-{processId}-{captureId}.data");
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

            var recordResult = await RecordAsync(
                processId,
                schedPerfDataPath,
                syscallPerfDataPath,
                duration,
                notes,
                cancellationToken).ConfigureAwait(false);

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

            AddPerfDataCapNote(
                schedPerfDataPath,
                SchedPerfDataMaxBytes,
                "sched_switch perf.data",
                $"off-CPU stack capture stopped early — results cover less than the requested {duration.TotalSeconds:F0}s. Consider a shorter window on busy hosts.",
                notes);
            if (recordResult.SyscallCaptureSucceeded)
            {
                AddPerfDataCapNote(
                    syscallPerfDataPath,
                    SyscallPerfDataMaxBytes,
                    "raw_syscalls perf.data",
                    "syscall attribution is truncated; base off-CPU stacks remain available but some spans may be missing syscall labels.",
                    notes);
            }

            Func<ulong, DotnetDiagnostics.Core.Memory.MethodIdentity?>? resolver = jitMap is null
                ? null
                : jitMap.Resolve;
            return await RunScriptAsync(
                schedPerfDataPath,
                recordResult.SyscallCaptureSucceeded ? syscallPerfDataPath : null,
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
            TryDelete(schedPerfDataPath);
            TryDelete(syscallPerfDataPath);
            // Delete /tmp/perf-<pid>.map so a recycled PID can't pick up stale managed
            // symbols on the next capture (the OS would otherwise leave it until reboot).
            if (jitMap is not null)
            {
                TryDelete(jitMap.MapPath);
            }
        }
    }

    // Independent hard file caps for the split capture. The sched side keeps DWARF callchains
    // for stack quality; raw syscalls are target-scoped and stackless so they should stay much
    // smaller, but still get their own cap instead of sharing a global 512 MiB failure mode.
    internal const long SchedPerfDataMaxBytes = 128L * 1024 * 1024;
    internal const long SyscallPerfDataMaxBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan PerfRecordGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PerfScriptTimeout = TimeSpan.FromMinutes(2);

    private readonly record struct SplitRecordResult(bool SyscallCaptureSucceeded);
    private readonly record struct PerfProcessResult(int ExitCode, string Stdout, string Stderr);

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

    private async Task<SplitRecordResult> RecordAsync(
        int pid,
        string schedOutputPath,
        string syscallOutputPath,
        TimeSpan duration,
        List<string> notes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notes);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timeout = duration + PerfRecordGrace;
        var schedTask = RunPerfAsync(
            BuildSchedRecordArguments(schedOutputPath, duration),
            timeout,
            "perf record (sched)",
            linkedCts.Token);
        var syscallTask = RunPerfAsync(
            BuildSyscallRecordArguments(pid, syscallOutputPath, duration),
            timeout,
            "perf record (syscalls)",
            linkedCts.Token);

        PerfProcessResult schedResult;
        try
        {
            schedResult = await schedTask.ConfigureAwait(false);
        }
        catch
        {
            linkedCts.Cancel();
            await ObserveCanceledSyscallCaptureAsync(syscallTask).ConfigureAwait(false);
            throw;
        }

        if (schedResult.ExitCode != 0)
        {
            linkedCts.Cancel();
            await ObserveCanceledSyscallCaptureAsync(syscallTask).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"perf record (sched) exited with code {schedResult.ExitCode}. stderr: {schedResult.Stderr.Trim()}");
        }

        var syscallSucceeded = await TryCompleteSyscallCaptureAsync(syscallTask, notes, ct).ConfigureAwait(false);
        return new SplitRecordResult(syscallSucceeded);
    }

    internal static IReadOnlyList<string> BuildSchedRecordArguments(string outputPath, TimeSpan duration)
    {
        var seconds = GetRecordSeconds(duration);
        return
        [
            "record",
            "-a",
            "-e", "sched:sched_switch",
            "--call-graph", "dwarf",
            "--max-size", PerfNativeAotCpuSampler.FormatPerfFileSize(SchedPerfDataMaxBytes),
            "-o", outputPath,
            "--", "sleep", seconds.ToString(CultureInfo.InvariantCulture),
        ];
    }

    internal static IReadOnlyList<string> BuildSyscallRecordArguments(int pid, string outputPath, TimeSpan duration)
    {
        var seconds = GetRecordSeconds(duration);
        return
        [
            "record",
            "-p", pid.ToString(CultureInfo.InvariantCulture),
            "-e", "raw_syscalls:sys_enter,raw_syscalls:sys_exit",
            "--max-size", PerfNativeAotCpuSampler.FormatPerfFileSize(SyscallPerfDataMaxBytes),
            "-o", outputPath,
            "--", "sleep", seconds.ToString(CultureInfo.InvariantCulture),
        ];
    }

    private static int GetRecordSeconds(TimeSpan duration) =>
        Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds));

    private async Task<bool> TryCompleteSyscallCaptureAsync(
        Task<PerfProcessResult> syscallTask,
        List<string> notes,
        CancellationToken ct)
    {
        try
        {
            var result = await syscallTask.ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                return true;
            }

            notes.Add(
                $"Syscall companion capture failed with exit code {result.ExitCode}; base off-CPU stacks were returned without syscall labels. stderr: {TrimForNote(result.Stderr)}");
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Syscall companion capture failed for off-CPU capture; spans will not be labeled with a syscall.");
            notes.Add(
                $"Syscall companion capture failed ({ex.GetType().Name}: {TrimForNote(ex.Message)}); base off-CPU stacks were returned without syscall labels.");
            return false;
        }
    }

    private async Task<OffCpuSampleResult> RunScriptAsync(
        string schedPerfDataPath,
        string? syscallPerfDataPath,
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        int topN,
        HashSet<int> targetTids,
        Func<ulong, DotnetDiagnostics.Core.Memory.MethodIdentity?>? addressResolver,
        List<string> notes,
        CancellationToken ct)
    {
        // Best-effort syscall correlation pass (issue #829/#839) BEFORE the sched_switch script pass:
        // the interval index must be fully built before any span is emitted so each span can be
        // labeled at insertion time rather than buffering all spans for a later enrichment pass
        // (keeping PerfSchedScriptParser's streaming design — see resource-boundedness.md — intact).
        SyscallIntervalIndex? syscallIndex = null;
        if (syscallPerfDataPath is not null)
        {
            try
            {
                var buildResult = await BuildSyscallIntervalIndexAsync(syscallPerfDataPath, targetTids, ct).ConfigureAwait(false);
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
                if (syscallIndex is null)
                {
                    notes.Add("Syscall companion capture produced no target raw_syscalls events; base off-CPU stacks were returned without syscall labels.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Syscall correlation pass failed for off-CPU capture on pid {Pid}; spans will not be labeled with a syscall.", processId);
                notes.Add(
                    $"Syscall correlation failed ({ex.GetType().Name}: {TrimForNote(ex.Message)}); base off-CPU stacks were returned without syscall labels.");
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePerfPath()!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("script");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(schedPerfDataPath);
        startInfo.ArgumentList.Add("--no-inline");

        _logger.LogDebug("Spawning perf: {Bin} script -i {PerfDataPath} --no-inline", startInfo.FileName, schedPerfDataPath);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        return await BoundedProcessExecution.RunAsync(
            process,
            PerfScriptTimeout,
            "perf script (sched)",
            async boundedToken =>
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
                    boundedToken).ConfigureAwait(false);
                await process.WaitForExitAsync(boundedToken).ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"perf script exited with code {process.ExitCode}. stderr: {stderr.Trim()}");
                }

                return result;
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a second, independent <c>perf script</c> pass over the companion <c>perf.data</c> file to
    /// extract the target-scoped <c>raw_syscalls:sys_enter</c>/<c>sys_exit</c> events and correlate
    /// them into a <see cref="SyscallIntervalIndex"/> (issue #829/#839). Best-effort: failures are
    /// caught by the caller and surfaced as degradation notes rather than failing the whole off-CPU
    /// capture — a missing syscall label degrades the enrichment, not the underlying off-CPU span
    /// data. <c>-G</c>/<c>--hide-call-graph</c> is retained defensively; the companion raw-syscall
    /// capture itself is stackless so it does not carry the global DWARF callchains that caused the
    /// issue #839 data-volume spike.
    /// </summary>
    /// <summary>Result of the best-effort syscall correlation pre-pass: the interval index (if any events were found) plus whether the raw per-event parse budget was hit.</summary>
    private readonly record struct SyscallIntervalBuildResult(SyscallIntervalIndex? Index, bool ParserHitCap, long ParserDroppedCount);

    private async Task<SyscallIntervalBuildResult> BuildSyscallIntervalIndexAsync(
        string perfDataPath,
        HashSet<int> targetTids,
        CancellationToken ct)
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
        startInfo.ArgumentList.Add("-G");

        _logger.LogDebug("Spawning perf: {Bin} script -i {PerfDataPath} --no-inline -G", startInfo.FileName, perfDataPath);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var parseResult = await BoundedProcessExecution.RunAsync(
            process,
            PerfScriptTimeout,
            "perf script (syscalls)",
            async boundedToken =>
            {
                var parsed = await PerfSyscallScriptParser.ParseAsync(process.StandardOutput, targetTids, boundedToken).ConfigureAwait(false);
                await process.WaitForExitAsync(boundedToken).ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"perf script (syscalls) exited with code {process.ExitCode}. stderr: {stderr.Trim()}");
                }

                return parsed;
            },
            ct).ConfigureAwait(false);

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
        // (`span.OutTimestampSeconds`) comes from a real sched_switch event inside the paired
        // capture window, i.e. strictly less than "the rest of time", so this cannot fabricate a
        // false correlation beyond the capture window.
        var captureEndTs = double.PositiveInfinity;
        var index = SyscallIntervalIndex.Build(parseResult.Events, captureEndTs);
        return new SyscallIntervalBuildResult(index, parseResult.HitCap, parseResult.DroppedCount);
    }

    private async Task<PerfProcessResult> RunPerfAsync(
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
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

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
                return new PerfProcessResult(
                    process.ExitCode,
                    await stdoutTask.ConfigureAwait(false),
                    await stderrTask.ConfigureAwait(false));
            },
            ct).ConfigureAwait(false);
    }

    private static async Task ObserveCanceledSyscallCaptureAsync(Task<PerfProcessResult> syscallTask)
    {
        try
        {
            await syscallTask.ConfigureAwait(false);
        }
        catch
        {
            // The required sched capture already failed/canceled; preserve that original outcome.
        }
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

    private static void AddPerfDataCapNote(
        string path,
        long maxBytes,
        string label,
        string impact,
        List<string> notes)
    {
        try
        {
            var sizeBytes = new FileInfo(path).Length;
            if (sizeBytes >= maxBytes)
            {
                notes.Add($"{label} hit the {maxBytes / (1024 * 1024)} MiB cap; {impact}");
            }
        }
        catch
        {
            // Best-effort diagnostic note only; missing file errors surface through perf/script failures.
        }
    }

    private static string TrimForNote(string value)
    {
        const int MaxChars = 400;
        var trimmed = value.Trim();
        return trimmed.Length <= MaxChars ? trimmed : trimmed[..MaxChars] + "...";
    }
}
