using System.ComponentModel;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Investigation;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.ProcessDiscovery;
using static DotnetDiagnostics.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnostics.Mcp.Tools;

internal static class DiagnosticToolInvestigationPlanning
{
    public static async Task<DiagnosticResult<InvestigationPlan>> StartInvestigation(
        IInvestigationPlanner planner,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Plain-language symptom, e.g. 'high latency on /checkout since v2025.10'. Required for cold mode; optional for warm/hypothesis.")] string? symptom = null,
        [Description("Specific hypothesis to test, e.g. 'lock contention on Cart.Checkout'. Triggers hypothesis mode.")] string? hypothesis = null,
        [Description("Baseline snapshot from a prior investigation (JSON of BaselineHandle). Triggers warm mode.")] BaselineHandle? baseline = null,
        [Description("Optional hard limit on tool calls before forcing summarization. Defaults to 8.")] int maxToolCalls = 8,
        [Description("If true, collect_process_dump steps are marked approval-gated. Defaults to true.")] bool dumpRequiresApproval = true,
        [Description("Optional orchestrator investigation handle returned by attach_to_pod. When supplied, the orchestrator routes this diagnostic call through that attached Pod instead of inferring routing from the current MCP session binding.")]
        string? investigationHandleId = null,
        CancellationToken cancellationToken = default)
    {
        if (maxToolCalls < 1) return InvalidArg<InvestigationPlan>(nameof(maxToolCalls), "must be >= 1");

        var resolved = await ResolveContextAsync<InvestigationPlan>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var request = new InvestigationRequest(
            ProcessId: pid,
            Symptom: symptom,
            Hypothesis: hypothesis,
            Baseline: baseline,
            Constraints: new InvestigationConstraints(
                MaxToolCalls: maxToolCalls,
                DumpRequiresApproval: dumpRequiresApproval));

        var plan = planner.Plan(request);
        var summary = $"Mode={plan.Mode}. Next step #{plan.NextStep.StepNumber}: {plan.NextStep.ToolName}. " +
                      $"{plan.AllSteps.Count} total step(s), {plan.EarlyStopConditions.Count} early-stop condition(s). " +
                      $"Playbook: {(plan.Playbook?.Count ?? 0)} chained call(s) ready to execute. " +
                      $"Honor MaxToolCalls={plan.Constraints.MaxToolCalls}.";

        var hints = (plan.Playbook is { Count: > 0 } playbook
                ? playbook
                : new[] { new NextActionHint(plan.NextStep.ToolName, plan.NextStep.Rationale, plan.NextStep.ToolParams) })
            .ToArray();
        return WithContext(DiagnosticResult.Ok(plan, summary, hints), resolved.Context);
    }

    public static DiagnosticResult<ExportedInvestigationSummary> ExportInvestigationSummary(
        IInvestigationSummaryExporter exporter,
        IDiagnosticHandleStore handles,
        DotnetDiagnostics.Mcp.Observability.IInvestigationTelemetryEmitter telemetry,
        [Description("Handle returned by a prior collect_sample(kind='cpu') call.")] string handle,
        [Description("Output format: 'json' (default — portable, machine-readable) or 'markdown' (human-readable for PRs).") ] SummaryFormat format = SummaryFormat.Json,
        [Description("Max hotspots to include in the summary. Defaults to 10.")] int topHotspots = 10,
        [Description("Optional managed assembly name for the target (from inspect_process(view='list')).") ] string? buildAssemblyName = null,
        [Description("Optional investigation id from the previous summary, to link lineage.")] string? previousInvestigationId = null,
        [Description("Optional commit SHA being proposed as the fix.")] string? fixCommitSha = null,
        [Description("Optional PR URL being proposed as the fix.")] string? fixPullRequestUrl = null,
        [Description("Optional short description of the proposed fix.")] string? fixDescription = null,
        [Description("Optional free-form notes appended to the summary.")] string? notes = null,
        [Description("Optional orchestrator investigation handle returned by attach_to_pod. When supplied, the orchestrator routes this diagnostic call through that attached Pod instead of inferring routing from the current MCP session binding.")]
        string? investigationHandleId = null)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<ExportedInvestigationSummary>(nameof(handle), "is required");
        if (topHotspots < 1) return InvalidArg<ExportedInvestigationSummary>(nameof(topHotspots), "must be >= 1");

        var artifact = handles.TryGet<CpuSampleTraceArtifact>(handle);
        if (artifact is null)
        {
            return DiagnosticResult.Fail<ExportedInvestigationSummary>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError("HandleExpired", "Drill-down handles live ~10min and are invalidated when the target process exits.", handle),
                new NextActionHint("collect_sample", "Re-run the sampler on the same pid to issue a fresh handle.",
                    new Dictionary<string, object?> { ["kind"] = "cpu", ["durationSeconds"] = 10 }));
        }

        var fix = (fixCommitSha is null && fixPullRequestUrl is null && fixDescription is null)
            ? null
            : new InvestigationFixTarget(fixCommitSha, fixPullRequestUrl, fixDescription);

        var exported = exporter.Export(new ExportRequest(
            Handle: handle,
            Artifact: artifact,
            TopHotspots: topHotspots,
            BuildAssemblyName: buildAssemblyName,
            PreviousInvestigationId: previousInvestigationId,
            TargetsFix: fix,
            Notes: notes,
            Format: format));

        telemetry.Emit(exported.Summary, handle);

        var bytes = exported.Rendered.Length;
        return DiagnosticResult.Ok(
            exported,
            $"Exported investigation {exported.Summary.InvestigationId} ({exported.Summary.Findings.TopHotspots.Count} hotspots, {bytes} chars {format}). Paste `rendered` into your PR/ADR; re-supply this JSON via compare_to_baseline on the next investigation.");
    }

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("inspect_process", "Re-issue with valid arguments. See tool schema for ranges and defaults."));
}
