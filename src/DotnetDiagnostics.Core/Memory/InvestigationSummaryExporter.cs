using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Threads;

namespace DotnetDiagnostics.Core.Memory;

public enum SummaryFormat { Json, Markdown }

/// <summary>
/// Builds an <see cref="InvestigationSummary"/> from one or more supported drill-down artifacts
/// and renders it as JSON or markdown for the LLM to paste into a PR/ADR/ticket.
/// </summary>
public interface IInvestigationSummaryExporter
{
    ExportedInvestigationSummary Export(ExportRequest request);
}

public sealed record InvestigationEvidenceInput(
    string Handle,
    string Kind,
    object Artifact,
    string? Origin = null);

public sealed record ExportRequest(
    IReadOnlyList<InvestigationEvidenceInput> Evidence,
    int TopHotspots = 10,
    string? BuildAssemblyName = null,
    string? PreviousInvestigationId = null,
    InvestigationFixTarget? TargetsFix = null,
    string? Notes = null,
    SummaryFormat Format = SummaryFormat.Json)
{
    /// <summary>Compatibility constructor for the original CPU-only export contract.</summary>
    public ExportRequest(
        string Handle,
        CpuSampleTraceArtifact Artifact,
        int TopHotspots = 10,
        string? BuildAssemblyName = null,
        string? PreviousInvestigationId = null,
        InvestigationFixTarget? TargetsFix = null,
        string? Notes = null,
        SummaryFormat Format = SummaryFormat.Json)
        : this(
            [new InvestigationEvidenceInput(Handle, "cpu-sample", Artifact)],
            TopHotspots,
            BuildAssemblyName,
            PreviousInvestigationId,
            TargetsFix,
            Notes,
            Format)
    {
    }
}

public sealed record ExportedInvestigationSummary(
    InvestigationSummary Summary,
    SummaryFormat Format,
    string Rendered);

public sealed class EvidenceMetricConflictException : InvalidOperationException
{
    public EvidenceMetricConflictException(
        string metricName,
        string firstHandle,
        double firstValue,
        string secondHandle,
        double secondValue)
        : base(BuildMessage(metricName, firstHandle, firstValue, secondHandle, secondValue))
    {
        MetricName = metricName;
    }

    public string MetricName { get; }

    private static string BuildMessage(
        string metricName,
        string firstHandle,
        double firstValue,
        string secondHandle,
        double secondValue)
    {
        var first = (Handle: firstHandle, Value: firstValue);
        var second = (Handle: secondHandle, Value: secondValue);
        if (string.Compare(first.Handle, second.Handle, StringComparison.Ordinal) > 0)
        {
            (first, second) = (second, first);
        }

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Metric '{metricName}' has conflicting values: handle '{first.Handle}'={first.Value:R}, handle '{second.Handle}'={second.Value:R}. Remove one conflicting handle or export separately.");
    }
}

public sealed class InvestigationSummaryExporter : IInvestigationSummaryExporter
{
    private const int MaxEvidenceMetrics = 64;
    private const int MaxThreadFindings = 10;
    private const int MaxFindingFrames = 12;

    private readonly IProvenanceCollector _provenance;
    private readonly TimeProvider _clock;
    private readonly Func<string> _idFactory;

    public InvestigationSummaryExporter(
        IProvenanceCollector provenance,
        TimeProvider? clock = null,
        Func<string>? idFactory = null)
    {
        _provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        _clock = clock ?? TimeProvider.System;
        _idFactory = idFactory ?? (() => $"inv-{Guid.NewGuid():N}");
    }

    public ExportedInvestigationSummary Export(ExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Evidence);
        if (request.Evidence.Count == 0)
        {
            throw new ArgumentException("At least one evidence artifact is required.", nameof(request));
        }
        if (request.TopHotspots < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "TopHotspots must be >= 1.");
        }

        var projections = request.Evidence
            .Select(ProjectEvidence)
            .OrderBy(static projection => projection.Evidence.Handle, StringComparer.Ordinal)
            .ToArray();
        var processId = projections[0].ProcessId;
        if (projections.Any(projection => projection.ProcessId != processId))
        {
            throw new ArgumentException("All evidence artifacts must come from the same process.", nameof(request));
        }

        var cpuArtifacts = request.Evidence
            .Where(static evidence => evidence.Kind == "cpu-sample"
                && evidence.Artifact is CpuSampleTraceArtifact)
            .Select(static evidence => (CpuSampleTraceArtifact)evidence.Artifact)
            .ToArray();
        if (cpuArtifacts.Length > 1)
        {
            throw new ArgumentException(
                "An investigation summary can include at most one CPU sample handle.",
                nameof(request));
        }
        var cpuArtifact = cpuArtifacts.FirstOrDefault();
        var hotspots = cpuArtifact is null
            ? Array.Empty<HotspotSummary>()
            : ProjectHotspots(cpuArtifact, request.TopHotspots);
        var totalSamples = cpuArtifacts.Sum(static artifact => artifact.TotalSamples);
        var startedAt = projections.Min(static projection => projection.Evidence.ObservedAt);
        var endedAt = projections.Max(static projection => projection.Evidence.ObservedAt + projection.Evidence.Duration);
        var legacyCpuOnly = IsLegacyCpuOnly(request.Evidence);
        var keyMetrics = legacyCpuOnly ? [] : MergeMetrics(projections);

        var findings = new InvestigationFindings(
            TotalSamples: totalSamples,
            StartedAt: startedAt,
            Duration: endedAt - startedAt,
            TopHotspots: hotspots,
            KeyMetrics: keyMetrics.Count == 0 ? null : keyMetrics);

        var summary = new InvestigationSummary(
            Schema: InvestigationSummary.SchemaV1,
            InvestigationId: _idFactory(),
            CreatedAt: _clock.GetUtcNow(),
            ProcessId: processId,
            Provenance: _provenance.Collect(processId, request.BuildAssemblyName),
            Findings: findings,
            PreviousInvestigationId: request.PreviousInvestigationId,
            TargetsFix: request.TargetsFix,
            Notes: request.Notes)
        {
            Evidence = legacyCpuOnly ? null : projections.Select(static projection => projection.Evidence).ToArray(),
        };

        var rendered = request.Format switch
        {
            SummaryFormat.Markdown => RenderMarkdown(summary),
            _ => JsonSerializer.Serialize(summary, InvestigationSummaryJsonContext.Default.InvestigationSummary),
        };

        return new ExportedInvestigationSummary(summary, request.Format, rendered);
    }

    private static HotspotSummary[] ProjectHotspots(CpuSampleTraceArtifact artifact, int topHotspots)
    {
        var total = artifact.TotalSamples == 0 ? 1 : artifact.TotalSamples;
        return FlattenTree(artifact.Root)
            .Where(n => !string.Equals(n.Frame.Method, "<root>", StringComparison.Ordinal))
            .Where(n => n.ExclusiveSamples > 0 || n.InclusiveSamples > 0)
            .GroupBy(n => new SymbolRef(n.Frame.Module, n.Frame.Method))
            .Select(g => new
            {
                Symbol = g.Key,
                Exclusive = g.Sum(n => n.ExclusiveSamples),
                Inclusive = g.Max(n => n.InclusiveSamples),
                Running = g.Sum(n => n.SelfSamples?.RunningSamples ?? 0),
                Waiting = g.Sum(n => n.SelfSamples?.WaitingSamples ?? 0),
                HasSelfSampleClassification = g.Any(n => n.SelfSamples is not null),
            })
            .OrderByDescending(g => g.Exclusive)
            .ThenByDescending(g => g.Inclusive)
            .Take(topHotspots)
            .Select(g =>
            {
                artifact.ResolvedSources.TryGetValue(g.Symbol, out var src);
                artifact.MethodIdentities.TryGetValue(g.Symbol, out var id);
                return new HotspotSummary(
                    Symbol: g.Symbol,
                    InclusiveSamples: g.Inclusive,
                    ExclusiveSamples: g.Exclusive,
                    InclusivePercent: Math.Round(100.0 * g.Inclusive / total, 2),
                    ExclusivePercent: Math.Round(100.0 * g.Exclusive / total, 2),
                    Source: src,
                    Identity: id)
                {
                    SelfSamples = g.HasSelfSampleClassification
                        ? new SelfSampleBreakdown(g.Running, g.Waiting)
                        : null,
                };
            })
            .ToArray();
    }

    private static EvidenceProjection ProjectEvidence(InvestigationEvidenceInput input)
        => (input.Kind, input.Artifact) switch
        {
            ("cpu-sample", CpuSampleTraceArtifact cpu) => ProjectCpu(input, cpu),
            ("counters", CounterSnapshot counters) => ProjectCounters(input, counters),
            ("gc-events", GcSummary gc) => ProjectGc(input, gc),
            ("gc-datas", GcDatasSnapshot datas) => ProjectGcDatas(input, datas),
            ("thread-snapshot", ThreadSnapshotArtifact threads) => ProjectThreads(input, threads),
            _ => throw new ArgumentException(
                $"Handle '{input.Handle}' has unsupported or mismatched evidence pair kind='{input.Kind}', artifact='{input.Artifact.GetType().Name}'.",
                nameof(input)),
        };

    private static EvidenceProjection ProjectCpu(InvestigationEvidenceInput input, CpuSampleTraceArtifact artifact)
    {
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["cpu-samples"] = artifact.TotalSamples,
        };
        return Projection(
            input,
            artifact.ProcessId,
            "collect_sample",
            "cpu",
            artifact.StartedAt,
            artifact.Duration,
            metrics,
            []);
    }

    private static EvidenceProjection ProjectCounters(InvestigationEvidenceInput input, CounterSnapshot snapshot)
    {
        var candidates = new List<KeyValuePair<string, double>>();
        candidates.AddRange(snapshot.Counters.Select(static counter =>
            new KeyValuePair<string, double>(counter.Name, counter.Value)));
        foreach (var meter in snapshot.Meters)
        {
            if (meter.LastValue is double last)
            {
                candidates.Add(new KeyValuePair<string, double>(meter.Instrument, last));
            }
            if (meter.Rate is double rate)
            {
                candidates.Add(new KeyValuePair<string, double>($"{meter.Instrument}.rate", rate));
            }
            if (meter.Histogram is { } histogram)
            {
                candidates.Add(new KeyValuePair<string, double>($"{meter.Instrument}.p95", histogram.P95));
            }
        }

        var metrics = SelectMetrics(candidates);
        var findings = new[]
        {
            new InvestigationEvidenceFinding(
                "counter-snapshot",
                $"Captured {snapshot.Counters.Count} counter(s) and {snapshot.Meters.Count} meter time series.",
                snapshot.Counters.Count + snapshot.Meters.Count),
        };
        return Projection(
            input,
            snapshot.ProcessId,
            "collect_events",
            "counters",
            snapshot.StartedAt,
            snapshot.Duration,
            metrics,
            findings);
    }

    private static EvidenceProjection ProjectGc(InvestigationEvidenceInput input, GcSummary summary)
    {
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["gc-total-collections"] = summary.TotalCollections,
            ["gc-total-pause-ms"] = summary.TotalPauseTime.TotalMilliseconds,
            ["gc-max-pause-ms"] = summary.MaxPauseTime.TotalMilliseconds,
        };
        foreach (var generation in summary.Generations)
        {
            metrics[$"gc-gen-{generation.Generation}-collections"] = generation.Count;
        }

        var findings = new[]
        {
            new InvestigationEvidenceFinding(
                "gc-summary",
                $"{summary.TotalCollections} collection(s), {summary.TotalPauseTime.TotalMilliseconds:F2} ms total pause, {summary.MaxPauseTime.TotalMilliseconds:F2} ms max pause.",
                summary.TotalCollections),
        };
        return Projection(
            input,
            summary.ProcessId,
            "collect_events",
            "gc",
            summary.StartedAt,
            summary.Duration,
            metrics,
            findings);
    }

    private static EvidenceProjection ProjectGcDatas(InvestigationEvidenceInput input, GcDatasSnapshot snapshot)
    {
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["gc-datas-samples"] = snapshot.Samples.Count,
            ["gc-datas-tuning-events"] = snapshot.TuningEvents.Count,
            ["gc-datas-full-gc-events"] = snapshot.FullGcTuningEvents.Count,
        };
        if (snapshot.Samples.Count > 0)
        {
            metrics["gc-datas-mean-throughput-cost-percent"] =
                snapshot.Samples.Average(static sample => sample.ThroughputCostPercent);
        }

        var findings = new[]
        {
            new InvestigationEvidenceFinding(
                "gc-datas-summary",
                $"Captured {snapshot.Samples.Count} DATAS sample(s), {snapshot.TuningEvents.Count} tuning event(s), and {snapshot.FullGcTuningEvents.Count} full-GC tuning event(s).",
                snapshot.Samples.Count + snapshot.TuningEvents.Count + snapshot.FullGcTuningEvents.Count),
        };
        return Projection(
            input,
            snapshot.ProcessId,
            "collect_events",
            "datas",
            snapshot.StartedAt,
            snapshot.Duration,
            metrics,
            findings);
    }

    private static EvidenceProjection ProjectThreads(InvestigationEvidenceInput input, ThreadSnapshotArtifact snapshot)
    {
        var blocked = snapshot.Threads.Where(static thread => thread.IsLikelyBlocked).ToArray();
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["thread-count"] = snapshot.Threads.Count,
            ["blocked-thread-count"] = blocked.Length,
        };
        if (snapshot.ThreadPool is { } threadPool)
        {
            metrics["threadpool-queue-length"] = threadPool.Queues.GlobalQueueLength
                + threadPool.Queues.LocalQueues.Sum(static queue => queue.QueueLength);
            metrics["threadpool-pending-work-items"] = threadPool.PendingWorkItems;
            metrics["threadpool-thread-count"] = threadPool.Workers.Current;
            if (threadPool.HillClimbing is { } hillClimbing)
            {
                metrics["threadpool-throughput"] = hillClimbing.Throughput;
            }
        }

        var findings = blocked
            .GroupBy(static thread => string.Join(" -> ", thread.Frames.Take(MaxFindingFrames).Select(static frame => frame.DisplayName)))
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.Ordinal)
            .Take(MaxThreadFindings)
            .Select(static group =>
            {
                var thread = group.First();
                var frames = thread.Frames
                    .Take(MaxFindingFrames)
                    .Select(static frame => new InvestigationEvidenceFrame(frame.DisplayName, frame.ModuleName, frame.Identity))
                    .ToArray();
                return new InvestigationEvidenceFinding(
                    "blocking-stack",
                    group.Key,
                    group.Count(),
                    frames);
            })
            .ToArray();

        return Projection(
            input,
            snapshot.ProcessId,
            "collect_thread_snapshot",
            "thread-snapshot",
            snapshot.CapturedAt,
            snapshot.WalkDuration,
            metrics,
            findings);
    }

    private static EvidenceProjection Projection(
        InvestigationEvidenceInput input,
        int processId,
        string sourceTool,
        string sourceKind,
        DateTimeOffset observedAt,
        TimeSpan duration,
        IReadOnlyDictionary<string, double> metrics,
        IReadOnlyList<InvestigationEvidenceFinding> findings)
        => new(
            processId,
            new InvestigationEvidence(
                input.Handle,
                input.Kind,
                input.Origin ?? InferOrigin(input.Artifact),
                sourceTool,
                sourceKind,
                observedAt,
                duration,
                metrics,
                findings));

    private static string InferOrigin(object artifact)
        => artifact is ThreadSnapshotArtifact threads
            ? threads.Origin.ToString().ToLowerInvariant()
            : "live";

    private static Dictionary<string, double> SelectMetrics(
        IEnumerable<KeyValuePair<string, double>> candidates)
    {
        var selected = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var candidate in candidates
                     .Where(static candidate => !string.IsNullOrWhiteSpace(candidate.Key))
                     .OrderBy(static candidate => MetricPriority(candidate.Key))
                     .ThenBy(static candidate => candidate.Key, StringComparer.Ordinal)
                     .Take(MaxEvidenceMetrics))
        {
            var key = candidate.Key;
            var suffix = 2;
            while (!selected.TryAdd(key, candidate.Value))
            {
                key = $"{candidate.Key}#{suffix++}";
            }
        }
        return selected;
    }

    private static int MetricPriority(string name)
    {
        var normalized = name.ToLowerInvariant();
        return normalized.Contains("queue", StringComparison.Ordinal)
            || normalized.Contains("throughput", StringComparison.Ordinal)
            || normalized.Contains("request", StringComparison.Ordinal)
            || normalized.Contains("latency", StringComparison.Ordinal)
            || normalized.Contains("threadpool", StringComparison.Ordinal)
            ? 0
            : normalized.Contains("gc", StringComparison.Ordinal)
              || normalized.Contains("cpu", StringComparison.Ordinal)
              || normalized.Contains("working-set", StringComparison.Ordinal)
                ? 1
                : 2;
    }

    private static Dictionary<string, double> MergeMetrics(
        IReadOnlyList<EvidenceProjection> projections)
    {
        var merged = new Dictionary<string, double>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var projection in projections)
        {
            foreach (var metric in projection.Evidence.Metrics)
            {
                if (merged.TryAdd(metric.Key, metric.Value))
                {
                    sources.Add(metric.Key, projection.Evidence.Handle);
                    continue;
                }

                if (merged[metric.Key].Equals(metric.Value))
                {
                    continue;
                }

                throw new EvidenceMetricConflictException(
                    metric.Key,
                    sources[metric.Key],
                    merged[metric.Key],
                    projection.Evidence.Handle,
                    metric.Value);
            }
        }
        return merged;
    }

    private static IEnumerable<CallTreeNode> FlattenTree(CallTreeNode root)
    {
        var stack = new Stack<CallTreeNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var child in n.Children) stack.Push(child);
        }
    }

    private static bool IsLegacyCpuOnly(IReadOnlyList<InvestigationEvidenceInput> evidence)
        => evidence.Count == 1
            && evidence[0].Kind == "cpu-sample"
            && evidence[0].Artifact is CpuSampleTraceArtifact;

    private static string RenderMarkdown(InvestigationSummary s)
    {
        var sb = new StringBuilder();
        sb.Append("# Investigation `").Append(s.InvestigationId).AppendLine("`");
        sb.Append("- Created: `").Append(s.CreatedAt.ToString("u")).AppendLine("`");
        sb.Append("- PID: `").Append(s.ProcessId).AppendLine("`");
        if (s.PreviousInvestigationId is not null)
        {
            sb.Append("- Previous: `").Append(s.PreviousInvestigationId).AppendLine("`");
        }
        sb.AppendLine();

        sb.AppendLine("## Provenance");
        if (s.Provenance.Build is { } b)
        {
            sb.Append("- Build: `").Append(b.AssemblyName ?? "?").Append('`');
            if (b.InformationalVersion is not null) sb.Append(" · v`").Append(b.InformationalVersion).Append('`');
            if (b.GitSha is not null) sb.Append(" · git `").Append(b.GitSha).Append('`');
            sb.AppendLine();
        }
        if (s.Provenance.Container is { } c)
        {
            sb.Append("- Container: image=`").Append(c.Image ?? "?")
              .Append("` ns=`").Append(c.Namespace ?? "?")
              .Append("` pod=`").Append(c.PodName ?? "?")
              .Append("` node=`").Append(c.NodeName ?? "?").AppendLine("`");
        }
        if (s.Provenance.Hostname is not null) sb.Append("- Host: `").Append(s.Provenance.Hostname).AppendLine("`");
        sb.AppendLine();

        sb.AppendLine("## Findings");
        var f = s.Findings;
        if (f.TopHotspots.Count > 0)
        {
            sb.Append("- Samples: `").Append(f.TotalSamples).Append("` over `").Append(f.Duration.TotalSeconds).AppendLine("s`");
            sb.AppendLine();
            sb.AppendLine("| # | Method | Module | Incl % | Excl % | Self run/wait | Source | Handoff (mvid · token) |");
            sb.AppendLine("|---|---|---|---:|---:|---:|---|---|");
            var i = 1;
            foreach (var h in f.TopHotspots)
            {
                sb.Append("| ").Append(i++).Append(" | `").Append(h.Symbol.MethodFullName)
                  .Append("` | `").Append(h.Symbol.Module).Append("` | ")
                  .Append(h.InclusivePercent).Append(" | ")
                  .Append(h.ExclusivePercent).Append(" | ");
                if (h.SelfSamples is { } selfSamples)
                {
                    sb.Append(selfSamples.RunningSamples).Append('/').Append(selfSamples.WaitingSamples);
                }
                else
                {
                    sb.Append('?');
                }
                sb.Append(" | ");
                if (h.Source is { } src)
                {
                    if (!string.IsNullOrEmpty(src.SourceLink))
                    {
                        sb.Append('[').Append(src.File ?? "?");
                        if (src.StartLine is int ln) sb.Append(':').Append(ln);
                        sb.Append("](").Append(src.SourceLink).Append(')');
                    }
                    else if (src.File is not null)
                    {
                        sb.Append('`').Append(src.File);
                        if (src.StartLine is int ln) sb.Append(':').Append(ln);
                        sb.Append('`');
                    }
                }
                sb.Append(" | ");
                if (h.Identity is { } id && id.ModuleVersionId is Guid mvid && id.MetadataToken is int tok)
                {
                    sb.Append('`').Append(mvid.ToString("D")).Append("` · `0x")
                      .Append(tok.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))
                      .Append('`');
                }
                sb.AppendLine(" |");
            }
            sb.AppendLine();
        }

        if (s.Evidence is { Count: > 0 } evidence)
        {
            sb.AppendLine("### Evidence provenance");
            foreach (var item in evidence)
            {
                sb.Append("- `").Append(item.Handle).Append("`: `")
                  .Append(item.SourceTool).Append("(kind=\"").Append(item.SourceKind)
                  .Append("\")` at `").Append(item.ObservedAt.ToString("u")).AppendLine("`");
                foreach (var finding in item.Findings)
                {
                    sb.Append("  - ").Append(finding.Category).Append(" (`")
                      .Append(finding.Count).Append("`): ").AppendLine(finding.Summary);
                }
            }
            sb.AppendLine();
        }

        if (s.TargetsFix is { } fix)
        {
            sb.AppendLine("## Targets Fix");
            if (fix.PullRequestUrl is not null) sb.Append("- PR: ").AppendLine(fix.PullRequestUrl);
            if (fix.CommitSha is not null) sb.Append("- Commit: `").Append(fix.CommitSha).AppendLine("`");
            if (fix.Description is not null) sb.AppendLine(fix.Description);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(s.Notes))
        {
            sb.AppendLine("## Notes").AppendLine(s.Notes);
        }

        return sb.ToString();
    }

    private sealed record EvidenceProjection(int ProcessId, InvestigationEvidence Evidence);
}
