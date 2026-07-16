using System.Globalization;
using System.Text;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Container;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Preflight;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Triage;
using DotnetDiagnostics.Core.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli;

internal static partial class CliCommands
{
    private static CliCommandResult Completion(CliOptions options)
    {
        if (!TryValidateCompletion(options, out var error))
        {
            throw new ArgumentException(error, nameof(options));
        }

        var script = CliCompletionScripts.ForShell(options.CompletionShell!);
        return new CliCommandResult(false, false, new { shell = options.CompletionShell, script }, script);
    }

    private static CliCommandResult Processes(IServiceProvider services)
    {
        var discovery = services.GetRequiredService<IProcessDiscovery>();
        var result = ProcessInspectionUseCases.ListProcesses(discovery);

        return BuildResult(result, static (sb, processes) =>
        {
            if (processes.Count == 0)
            {
                return;
            }

            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{"PID",-8} {"RUNTIME",-16} {"OS/ARCH",-16} ENTRYPOINT");
            foreach (var p in processes)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"{p.ProcessId,-8} {Trunc(p.RuntimeVersion, 16),-16} {Trunc($"{p.OperatingSystem}/{p.ProcessArchitecture}", 16),-16} {p.ManagedEntrypointAssemblyName ?? "<unknown>"}");
            }
        });
    }

    private static async Task<CliCommandResult> CapabilitiesAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var detector = services.GetRequiredService<ICapabilityDetector>();
        var resolver = services.GetRequiredService<IProcessContextResolver>();
        var result = await ProcessInspectionUseCases
            .GetCapabilitiesAsync(detector, resolver, options.Pid, cancellationToken)
            .ConfigureAwait(false);

        // Swap Core's MCP-audience capability narrative for a CLI-authored note before rendering, so
        // neither the human table nor the --json envelope leaks MCP tool names (#302).
        result = CliHintProjection.ProjectCapabilities(result, options.LaunchedByCli);

        return BuildResult(result, static (sb, caps) =>
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Runtime           : {caps.Runtime} {caps.RuntimeVersion}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  CPU sampling      : {caps.CanSampleCpu}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  GC dump           : {caps.CanCollectGcDump}");
            if (!string.IsNullOrWhiteSpace(caps.Notes))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Notes             : {caps.Notes}");
            }
        });
    }

    /// <summary>
    /// Issue #486 — workload classifier (<c>--view triage</c>) and runtime configuration reader
    /// (<c>--view runtime-config</c>). Both views require a live target and use Core services only.
    /// </summary>
    private static async Task<CliCommandResult> InspectAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        return options.View switch
        {
            "triage" => await InspectTriageAsync(services, options, cancellationToken).ConfigureAwait(false),
            "runtime-config" => await InspectRuntimeConfigAsync(services, options, cancellationToken).ConfigureAwait(false),
            "container" => await InspectContainerAsync(services, options, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown inspect view '{options.View}'.", nameof(options)),
        };
    }

    private static async Task<CliCommandResult> InspectTriageAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var collector = services.GetRequiredService<ICounterCollector>();
        var resolver = services.GetRequiredService<IProcessContextResolver>();
        var duration = options.DurationSeconds ?? 5;

        var resolved = await ProcessResolutionHelpers
            .ResolveContextAsync<TriageResult>(resolver, options.Pid, cancellationToken)
            .ConfigureAwait(false);
        if (resolved.Failure is not null)
        {
            return BuildResult(resolved.Failure, static (_, _) => { });
        }

        var pid = resolved.ProcessId;

        var snapshot = await collector.CollectAsync(
            pid,
            TimeSpan.FromSeconds(duration),
            providers: null,
            meters: ["Microsoft.AspNetCore.Hosting"],
            intervalSeconds: 1,
            maxInstrumentTimeSeries: 100,
            cancellationToken).ConfigureAwait(false);

        var requestDuration = HeadlineCounters.FindRequestDuration(snapshot.Meters);
        var requestDurationP95 = requestDuration?.Histogram?.P95;

        var triage = TriageClassifier.Classify(snapshot, requestDurationP95);

        var indicatorsText = triage.TopIndicators?.Count > 0
            ? $" | top: {string.Join(", ", triage.TopIndicators.Take(3).Select(i => $"{i.Name}={i.Value}{i.Unit ?? string.Empty}({i.Level})"))}"
            : string.Empty;
        var hypothesisText = triage.Hypotheses?.Count > 0
            ? $"hypotheses: {string.Join(", ", triage.Hypotheses.Select(static h => $"{h.Name} ({h.Confidence})"))}"
            : triage.ObservedSignals?.Count > 0
                ? "observations require more evidence before assigning a cause"
                : "no salient observed signals";
        var summary = $"Triage: {triage.Assessment} ({triage.Severity}); {hypothesisText}{indicatorsText}";

        var hints = BuildCliTriageHints(triage, pid);
        var ok = ProcessResolutionHelpers.WithContext(DiagnosticResult.Ok(triage, summary, [.. hints]), resolved.Context);
        return BuildResult(ok, static (sb, t) =>
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Assessment: {t.Assessment}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Severity  : {t.Severity}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Legacy    : {t.Verdict} (deprecated; migrate before v1.0)");
            if (t.ObservedSignals?.Count > 0)
            {
                sb.AppendLine("  Observed signals:");
                foreach (var signal in t.ObservedSignals)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {signal.Name} [{signal.Level}] — {signal.Summary}");
                    foreach (var evidence in signal.Evidence)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture,
                            $"      {evidence.Name}={evidence.Value:F2}{evidence.Unit ?? string.Empty} {evidence.Comparison} {evidence.Threshold:F2}{evidence.Unit ?? string.Empty}");
                    }
                }
            }

            if (t.Hypotheses?.Count > 0)
            {
                sb.AppendLine("  Hypotheses:");
                foreach (var hypothesis in t.Hypotheses)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {hypothesis.Name} [{hypothesis.Confidence}] — {hypothesis.Summary}");
                    foreach (var evidence in hypothesis.SupportingEvidence)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture,
                            $"      supports: {evidence.Name}={evidence.Value:F2}{evidence.Unit ?? string.Empty} {evidence.Comparison} {evidence.Threshold:F2}{evidence.Unit ?? string.Empty}");
                    }
                    foreach (var evidence in hypothesis.ContradictingEvidence)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture,
                            $"      contradicts: {evidence.Name}={evidence.Value:F2}{evidence.Unit ?? string.Empty} {evidence.Comparison} {evidence.Threshold:F2}{evidence.Unit ?? string.Empty}");
                    }
                    sb.AppendLine(CultureInfo.InvariantCulture, $"      next: {hypothesis.NextStep}");
                }
            }

            if (t.TopIndicators?.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Indicators:");
                foreach (var ind in t.TopIndicators)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"    {ind.Name,-38} {ind.Value,8:F2} {ind.Unit ?? string.Empty,-12} [{ind.Level}]");
                }
            }
        });
    }

    private static async Task<CliCommandResult> InspectRuntimeConfigAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var inspector = services.GetRequiredService<IRuntimeConfigInspector>();
        var resolver = services.GetRequiredService<IProcessContextResolver>();

        var resolved = await ProcessResolutionHelpers
            .ResolveContextAsync<RuntimeConfigView>(resolver, options.Pid, cancellationToken)
            .ConfigureAwait(false);
        if (resolved.Failure is not null)
        {
            return BuildResult(resolved.Failure, static (_, _) => { });
        }

        var config = await inspector.InspectAsync(resolved.ProcessId, cancellationToken).ConfigureAwait(false);

        var summary = BuildRuntimeConfigSummary(config);
        var hints = BuildCliRuntimeConfigHints(config);
        var ok = ProcessResolutionHelpers.WithContext(DiagnosticResult.Ok(config, summary, [.. hints]), resolved.Context);
        return BuildResult(ok, static (sb, cfg) =>
        {
            sb.AppendLine();
            if (cfg.Gc is { } gc)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  GC server={gc.IsServerGc}  concurrent={gc.IsConcurrent?.ToString() ?? "?"}  background={gc.IsBackground?.ToString() ?? "?"}  heaps={gc.HeapCount}");
                if (gc.LargeObjectHeapCompactionMode is not null)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  LOH compaction={gc.LargeObjectHeapCompactionMode}");
                }
            }

            if (cfg.ThreadPool is { } tp)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  ThreadPool worker={FormatNullableRange(tp.MinWorkerThreads, tp.MaxWorkerThreads)}  iocp={FormatNullableRange(tp.MinIocpThreads, tp.MaxIocpThreads)}  hill-climbing={tp.HillClimbingEnabled?.ToString() ?? "?"}");
            }

            if (cfg.TieredCompilation is { } tc)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  TieredCompilation enabled={tc.Enabled?.ToString() ?? "?"}  quick-jit={tc.QuickJitEnabled?.ToString() ?? "?"}  pgo={tc.DynamicPgoEnabled?.ToString() ?? "?"}");
            }

            if (cfg.AppContextSwitches.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  AppContext switches ({cfg.AppContextSwitches.Count}):");
                foreach (var sw in cfg.AppContextSwitches)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {sw.Name} = {sw.Value ?? "<set>"}");
                }
            }

            if (cfg.EnvVars.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Runtime env vars ({cfg.EnvVars.Count}):");
                foreach (var ev in cfg.EnvVars)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {ev.Name} = {ev.Value}");
                }
            }

            if (cfg.Notes.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Notes:");
                foreach (var note in cfg.Notes)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {note}");
                }
            }
        });
    }

    private static async Task<CliCommandResult> InspectContainerAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var result = await ContainerInspectionUseCases.GetContainerSignals(
            services.GetRequiredService<IContainerSignalsCollector>(),
            services.GetRequiredService<IProcessContextResolver>(),
            options.Pid,
            SamplingDepth.Detail,
            cancellationToken).ConfigureAwait(false);

        return BuildResult(result, static (sb, signals) =>
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Scope            : {(signals.InContainer ? "container" : "host")} ({signals.CgroupVersion})");
            if (!string.IsNullOrWhiteSpace(signals.CgroupPath))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Cgroup path      : {signals.CgroupPath}");
            }

            if (signals.Cpu is { } cpu)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  CPU              : quota={FormatQuota(cpu.QuotaCores)}, throttled={FormatPercent(cpu.ThrottlePercent)} periods ({cpu.NrThrottled}/{cpu.NrPeriods})");
            }

            if (signals.Memory is { } mem)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  Memory           : {FormatMiB(mem.CurrentBytes)}/{FormatMiB(mem.MaxBytes)} ({FormatFraction(mem.UsageFraction)})");
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  OOM / max hits   : {mem.OomKillCount} / {mem.MaxHitCount}");
            }

            if (signals.Pressure is { } psi)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  PSI              : cpu.some={FormatPsi(psi.CpuSomeAvg10)}  mem.some={FormatPsi(psi.MemSomeAvg10)}  mem.full={FormatPsi(psi.MemFullAvg10)}  io.some={FormatPsi(psi.IoSomeAvg10)}  io.full={FormatPsi(psi.IoFullAvg10)}");
            }

            if (signals.Pids is { } pids)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  PIDs             : {pids.Current}/{(pids.Max is null ? "unlimited" : pids.Max.Value.ToString(CultureInfo.InvariantCulture))}");
            }

            if (signals.OomScore is { } oomScore)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  oom_score        : {oomScore}");
            }

            if (signals.Notes.Count > 0)
            {
                sb.AppendLine("  Notes:");
                foreach (var note in signals.Notes)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {note}");
                }
            }
        });
    }

    private static string BuildRuntimeConfigSummary(RuntimeConfigView cfg)
    {
        var parts = new List<string>();
        if (cfg.Gc is { } gc)
        {
            parts.Add($"GC server={gc.IsServerGc}, heaps={gc.HeapCount}");
        }

        if (cfg.ThreadPool is { } tp)
        {
            parts.Add($"ThreadPool worker={FormatNullableRange(tp.MinWorkerThreads, tp.MaxWorkerThreads)}");
        }

        parts.Add($"env={cfg.EnvVars.Count}");
        parts.Add($"switches={cfg.AppContextSwitches.Count}");
        return $"Process {cfg.ProcessId} runtime-config: {string.Join("; ", parts)}.";
    }

    private static string FormatNullableRange(int? min, int? max)
    {
        var minStr = min.HasValue ? min.Value.ToString(CultureInfo.InvariantCulture) : "?";
        var maxStr = max.HasValue ? max.Value.ToString(CultureInfo.InvariantCulture) : "?";
        return $"{minStr}/{maxStr}";
    }

    private static string FormatQuota(double? quotaCores)
        => quotaCores is null ? "unlimited" : $"{quotaCores.Value:F2} cores";

    private static string FormatPercent(double? value)
        => value is null ? "n/a" : $"{value.Value:F1}%";

    private static string FormatFraction(double? value)
        => value is null ? "n/a" : $"{value.Value * 100:F0}%";

    private static string FormatPsi(double? value)
        => value is null ? "n/a" : $"{value.Value:F2}";

    private static string FormatMiB(long bytes)
        => $"{bytes / 1_048_576} MiB";

    private static string FormatMiB(long? bytes)
        => bytes is null ? "unlimited" : FormatMiB(bytes.Value);

    private static List<NextActionHint> BuildCliTriageHints(TriageResult triage, int pid)
    {
        var hints = new List<NextActionHint>();
        foreach (var hypothesis in triage.Hypotheses ?? [])
        {
            switch (hypothesis.Name)
            {
                case TriageClassifier.CpuComputeDemandHypothesis:
                    hints.Add(new NextActionHint("collect", $"{hypothesis.NextStep} Run: collect --kind cpu --pid {pid} --duration 10"));
                    break;
                case TriageClassifier.GcOverheadHypothesis:
                    hints.Add(new NextActionHint("collect", $"{hypothesis.NextStep} Run: collect --kind gc --pid {pid} --duration 10"));
                    break;
                case TriageClassifier.ManagedMemoryActivityHypothesis:
                    hints.Add(new NextActionHint("collect", $"{hypothesis.NextStep} Run: collect --kind allocation --pid {pid} --duration 10"));
                    break;
                case TriageClassifier.ThreadPoolBacklogHypothesis:
                    hints.Add(new NextActionHint("collect", $"{hypothesis.NextStep} Run: collect --kind threadpool --pid {pid} --duration 10"));
                    break;
                case TriageClassifier.SynchronizationContentionHypothesis:
                    hints.Add(new NextActionHint("collect", $"{hypothesis.NextStep} Run: collect --kind contention --pid {pid} --duration 10"));
                    break;
                case TriageClassifier.WaitingOrBackpressureHypothesis:
                    hints.Add(new NextActionHint("collect", $"{hypothesis.NextStep} Run: collect --kind activities --pid {pid} --duration 10"));
                    hints.Add(new NextActionHint("collect", $"Inspect thread states without assuming the wait is I/O: collect --kind thread-snapshot --pid {pid}"));
                    break;
            }
        }

        if (hints.Count == 0 && triage.GetHighestPriorityObservedSignal() is { } prioritySignal)
        {
            var kind = prioritySignal.Name switch
            {
                "threadpool.queue" => "threadpool",
                "exceptions.rate" => "exceptions",
                "http.request-duration-p95" => "activities",
                _ => "counters",
            };
            hints.Add(new NextActionHint(
                "collect",
                $"Observed {prioritySignal.Name}, but the current window is inconclusive. Extend the capture: collect --kind {kind} --pid {pid} --duration 10"));
        }

        if (hints.Count == 0)
        {
            hints.Add(new NextActionHint(
                "collect",
                $"No salient signal crossed a triage threshold. If the symptom persists, extend counters: collect --kind counters --pid {pid} --duration 10"));
        }

        return hints;
    }

    private static List<NextActionHint> BuildCliRuntimeConfigHints(RuntimeConfigView cfg)
    {
        if (cfg.ThreadPool is { HillClimbingEnabled: false })
        {
            return
            [
                new NextActionHint(
                    "collect",
                    $"ThreadPool hill-climbing is disabled; capture threadpool events before investigating starvation: collect --kind threadpool --pid {cfg.ProcessId} --duration 6"),
            ];
        }

        return
        [
            new NextActionHint(
                "collect",
                $"Use runtime counters as the next cheap signal after confirming the startup configuration: collect --kind counters --pid {cfg.ProcessId} --duration 5"),
        ];
    }

    /// <summary>
    /// Phase 13 / G1 — environment self-diagnosis. Target-optional: with <c>--pid</c> it validates
    /// readiness against that target (diagnostic-socket UID match); without one it diagnoses the host.
    /// Exits non-zero (via <see cref="CliCommandResult.IsError"/>) when a hard blocker is present, so
    /// CI can gate on it. The diagnostic envelope itself stays a success envelope — the findings are
    /// data, not an error.
    /// </summary>
    private static CliCommandResult Doctor(IServiceProvider services, CliOptions options)
    {
        var inspector = services.GetRequiredService<IPreflightInspector>();
        var result = ProcessInspectionUseCases.Preflight(inspector, options.Pid);
        var report = result.Data!;
        var human = RenderDoctor(result, report);

        // Blocker => non-zero exit for CI gating. Envelope stays Ok (IsError=false on the wire).
        return new CliCommandResult(IsError: report.HasBlocker, Cancelled: false, Envelope: result, Human: human);
    }

    private static string RenderDoctor(DiagnosticResult<PreflightReport> result, PreflightReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.Summary);
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"  OS: {report.Os}   target: {(report.ProcessId is int pid ? pid.ToString(CultureInfo.InvariantCulture) : "<none>")}");
        sb.AppendLine();

        foreach (var check in report.Checks)
        {
            var glyph = check.Status switch
            {
                PreflightStatus.Ok => "OK  ",
                PreflightStatus.Degraded => "WARN",
                PreflightStatus.Blocked => "FAIL",
                _ => "n/a ",
            };
            sb.AppendLine(CultureInfo.InvariantCulture, $"  [{glyph}] {check.Title}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"         {check.Reason}");
            if (!string.IsNullOrWhiteSpace(check.Remediation))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"         fix: {check.Remediation}");
            }

            if (check.AffectedTools is { Count: > 0 } tools)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"         affects: {string.Join(", ", tools)}");
            }
        }

        return sb.ToString().TrimEnd();
    }

}
