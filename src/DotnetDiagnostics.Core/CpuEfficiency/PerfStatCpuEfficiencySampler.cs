using System.Diagnostics;
using System.Runtime.InteropServices;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.CpuSampling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.CpuEfficiency;

/// <summary>
/// Linux aggregate CPU microarchitecture-efficiency sampler driven by
/// <c>perf stat -x, -e &lt;events&gt; -p &lt;pid&gt; -- sleep N</c> — a whole-window COUNTING invocation,
/// as opposed to the sampling-mode <c>perf record</c> invocations used elsewhere in this codebase
/// (<see cref="PerfNativeAotCpuSampler"/>, <see cref="OffCpu.PerfSchedOffCpuSampler"/>). <c>perf stat</c>
/// does not produce a <c>perf.data</c> file to script/parse — it prints one summary line per
/// requested event directly, which <see cref="PerfStatOutputParser"/> reads via the stable
/// <c>-x,</c> (comma-separated) machine-readable form.
/// </summary>
/// <remarks>
/// <para>
/// Requirements (validated by <see cref="IsAvailable"/>): Linux host, <c>perf</c> binary in
/// <c>PATH</c>, and <c>perf_event_paranoid &lt;= 2</c> (the near-universal distro default) — unlike
/// the system-wide <c>sched_switch</c> tracing used by the off-CPU sampler, per-process hardware/
/// software counting for a same-UID target does not need <c>CAP_PERFMON</c>/<c>CAP_SYS_ADMIN</c> or a
/// negative <c>perf_event_paranoid</c>. This assumes the diagnostics sidecar runs as the same UID as
/// the target process (see AGENTS.md "Diagnostic socket UID").
/// </para>
/// <para>
/// We request the kernel/perf GENERIC event aliases (<c>cycles</c>, <c>instructions</c>,
/// <c>cache-misses</c>, <c>branch-misses</c>, <c>stalled-cycles-frontend</c>, ...) rather than raw
/// <c>cpu/…/</c> vendor-specific event syntax. These generic aliases are already normalized by the
/// kernel's perf subsystem to the correct underlying PMU event for the running CPU (Intel vs. AMD),
/// which sidesteps the vendor-naming split entirely for the common case; a host whose kernel/CPU
/// combination doesn't support a given alias reports it as <c>&lt;not supported&gt;</c>, which
/// <see cref="PerfStatOutputParser"/> turns into a null field plus a note rather than a failure.
/// </para>
/// </remarks>
public sealed class PerfStatCpuEfficiencySampler : ICpuEfficiencySampler
{
    // Kernel/perf generic event aliases — see the class remarks for why these are preferred over
    // raw vendor-specific `cpu/…/` syntax. Order matters only for readability of `perf stat` output;
    // parsing is keyed by event name, not position.
    internal static readonly string[] Events =
    [
        "cycles",
        "instructions",
        "cache-references",
        "cache-misses",
        "branch-instructions",
        "branch-misses",
        "stalled-cycles-frontend",
        "stalled-cycles-backend",
        "dTLB-load-misses",
        "iTLB-load-misses",
        "page-faults",
        "context-switches",
        "cpu-migrations",
    ];

    private readonly ILogger<PerfStatCpuEfficiencySampler> _logger;
    private readonly string _configuredPath;
    private string? _resolvedPath;
    private bool _resolutionAttempted;
    private readonly object _resolveLock = new();

    public PerfStatCpuEfficiencySampler(
        ILogger<PerfStatCpuEfficiencySampler>? logger = null,
        string perfPath = "perf")
    {
        _logger = logger ?? NullLogger<PerfStatCpuEfficiencySampler>.Instance;
        _configuredPath = perfPath;
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
        if (ResolvePerfPath() is null) return false;

        // Match PerfHostProbe.CanRunPerfStatCounting exactly (perf_event_paranoid <= 2 for a
        // same-UID target) so the capability surfaced by inspect_process(view="capabilities")
        // and this soft-availability gate never disagree.
        return PerfHostProbe.Detect().CanRunPerfStatCounting;
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
            throw new InvalidOperationException(
                "perf is not available on this host. Install linux-perf; per-process counting via " +
                "'perf stat' additionally requires perf_event_paranoid <= 2 (the near-universal " +
                "distro default) for a same-UID target.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var seconds = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds));
        var args = $"stat -x, -e {string.Join(',', Events)} -p {processId} -- sleep {seconds}";
        _logger.LogDebug("Spawning perf for CPU efficiency capture: {Bin} {Args}", ResolvePerfPath()!, args);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolvePerfPath()!,
                Arguments = args,
                RedirectStandardOutput = true,
                // perf stat writes its summary to stderr by default, regardless of -x,.
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* best effort */ }
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        // perf stat exits non-zero when the target pid disappeared mid-capture or the -e event
        // list itself is malformed; either is an actionable failure distinct from the per-event
        // graceful degradation ("<not supported>") which perf reports with exit code 0.
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"perf stat exited with code {process.ExitCode} for pid {processId}. The target may have " +
                $"exited during the capture window. stderr: {stderr.Trim()}");
        }

        // -x, output can land on either stream depending on perf version; concatenate both so the
        // parser sees every event line regardless.
        var parsed = PerfStatOutputParser.Parse(stdout + "\n" + stderr);
        return CpuEfficiencyAggregator.Build(
            processId,
            startedAt,
            duration,
            "perf-stat",
            parsed.Values,
            parsed.UnavailableEvents);
    }
}
