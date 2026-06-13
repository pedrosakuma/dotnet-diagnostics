using System.Diagnostics;
using System.Globalization;
using DotnetDiagnostics.Core.Memory;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Mcp.Observability;

/// <summary>
/// Opt-in (#426) emitter that publishes an <see cref="InvestigationSummary"/> as an
/// OpenTelemetry span when <c>export_investigation_summary</c> runs, so a diagnostic run
/// leaves a durable, queryable trail in any OTLP backend (Grafana/Tempo, Honeycomb, or
/// Azure Application Insights via its OTLP endpoint). It reuses the existing OTel tracing
/// pipeline (the observability registration adds
/// <see cref="InvestigationTelemetry.ActivitySourceName"/> as a source) and is a strict
/// no-op unless explicitly enabled — the portable-JSON-owned-by-the-LLM model is unchanged.
/// </summary>
public interface IInvestigationTelemetryEmitter
{
    /// <summary>
    /// Emits <paramref name="summary"/> as a short-lived span when telemetry is enabled.
    /// Does nothing (and allocates no span) when disabled or when no tracing listener is
    /// attached. Never throws — telemetry must not perturb the diagnostic result.
    /// </summary>
    /// <param name="summary">The exported summary to record.</param>
    /// <param name="sourceHandle">The drill-down handle the summary was exported from.</param>
    void Emit(InvestigationSummary summary, string sourceHandle);
}

/// <summary>Configuration for <see cref="InvestigationTelemetry"/>. Bound from the
/// <c>Observability:InvestigationTelemetry</c> config section and the
/// <c>MCP_INVESTIGATION_OTEL</c> environment flag. Disabled by default.</summary>
public sealed class InvestigationTelemetryOptions
{
    public const string SectionName = "Observability:InvestigationTelemetry";

    /// <summary>When false (default) <see cref="InvestigationTelemetry.Emit"/> is a no-op.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Hard cap on the number of per-hotspot attribute groups attached to the span, keeping
    /// span cardinality bounded regardless of the requested <c>topHotspots</c>. Must be >= 0.
    /// </summary>
    public int MaxHotspotAttributes { get; set; } = 5;
}

/// <inheritdoc />
public sealed class InvestigationTelemetry : IInvestigationTelemetryEmitter, IDisposable
{
    /// <summary>ActivitySource name registered with the OTel tracing pipeline.</summary>
    public const string ActivitySourceName = "DotnetDiagnostics.Mcp.Investigations";

    /// <summary>Span name emitted for each exported summary.</summary>
    public const string ActivityName = "investigation.summary";

    private readonly InvestigationTelemetryOptions _options;
    private readonly ILogger<InvestigationTelemetry>? _logger;
    private readonly ActivitySource _activitySource = new(ActivitySourceName);

    public InvestigationTelemetry(InvestigationTelemetryOptions options, ILogger<InvestigationTelemetry>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public void Emit(InvestigationSummary summary, string sourceHandle)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (!_options.Enabled)
        {
            return;
        }

        // Telemetry is best-effort and must never perturb the diagnostic result: a faulty
        // tracing listener / OTLP exporter can throw from StartActivity, tag population, or
        // disposal. Swallow everything and surface it only as a debug log.
        try
        {
            EmitCore(summary, sourceHandle);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to emit investigation telemetry for {InvestigationId}.", summary.InvestigationId);
        }
    }

    private void EmitCore(InvestigationSummary summary, string sourceHandle)
    {
        using var activity = _activitySource.StartActivity(ActivityName, ActivityKind.Internal);
        if (activity is null)
        {
            // No tracing listener / exporter is attached — nothing to record.
            return;
        }

        activity.SetTag("investigation.id", summary.InvestigationId);
        activity.SetTag("investigation.schema", summary.Schema);
        activity.SetTag("investigation.source_handle", sourceHandle);
        activity.SetTag("process.pid", summary.ProcessId);
        if (summary.PreviousInvestigationId is { } prev)
        {
            activity.SetTag("investigation.previous_id", prev);
        }

        var findings = summary.Findings;
        activity.SetTag("investigation.total_samples", findings.TotalSamples);
        activity.SetTag("investigation.duration_seconds", findings.Duration.TotalSeconds);
        activity.SetTag("investigation.hotspot_count", findings.TopHotspots.Count);

        SetProvenanceTags(activity, summary.Provenance);
        SetHotspotTags(activity, findings.TopHotspots);

        if (summary.TargetsFix is { } fix)
        {
            if (fix.PullRequestUrl is { } pr) activity.SetTag("investigation.fix.pull_request", pr);
            if (fix.CommitSha is { } sha) activity.SetTag("investigation.fix.commit", sha);
        }
    }

    private static void SetProvenanceTags(Activity activity, InvestigationProvenance provenance)
    {
        if (provenance.Hostname is { } host) activity.SetTag("host.name", host);
        if (provenance.Build is { } build)
        {
            if (build.AssemblyName is { } asm) activity.SetTag("service.name", asm);
            if (build.InformationalVersion is { } ver) activity.SetTag("service.version", ver);
            if (build.GitSha is { } sha) activity.SetTag("vcs.revision", sha);
        }

        if (provenance.Container is { } container)
        {
            if (container.Image is { } image) activity.SetTag("container.image.name", image);
            if (container.Namespace is { } ns) activity.SetTag("k8s.namespace.name", ns);
            if (container.PodName is { } pod) activity.SetTag("k8s.pod.name", pod);
            if (container.NodeName is { } node) activity.SetTag("k8s.node.name", node);
        }
    }

    private void SetHotspotTags(Activity activity, IReadOnlyList<HotspotSummary> hotspots)
    {
        var cap = Math.Min(hotspots.Count, Math.Max(0, _options.MaxHotspotAttributes));
        for (var i = 0; i < cap; i++)
        {
            var h = hotspots[i];
            var prefix = string.Create(CultureInfo.InvariantCulture, $"investigation.hotspot.{i}.");
            activity.SetTag(prefix + "method", h.Symbol.MethodFullName);
            activity.SetTag(prefix + "module", h.Symbol.Module);
            activity.SetTag(prefix + "exclusive_percent", h.ExclusivePercent);
            activity.SetTag(prefix + "inclusive_percent", h.InclusivePercent);
        }
    }

    public void Dispose() => _activitySource.Dispose();
}
