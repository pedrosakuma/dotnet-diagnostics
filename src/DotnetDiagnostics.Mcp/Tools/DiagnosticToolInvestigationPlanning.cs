using System.ComponentModel;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Investigation;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Mcp.Security;
using static DotnetDiagnostics.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnostics.Mcp.Tools;

internal static class DiagnosticToolInvestigationPlanning
{
    private const int MaxEvidenceHandles = 8;

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
        IPrincipalAccessor principalAccessor,
        [Description("Primary evidence handle from collect_sample(kind='cpu'), collect_events(kind='counters'|'gc'|'datas'), or collect_thread_snapshot.")] string handle,
        [Description("Optional additional supported evidence handles from the same process. Up to 7; duplicates are ignored.")] string[]? additionalHandles = null,
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
        if (additionalHandles is { Length: > MaxEvidenceHandles - 1 })
        {
            return InvalidArg<ExportedInvestigationSummary>(nameof(additionalHandles), $"must contain at most {MaxEvidenceHandles - 1} handles");
        }
        if (topHotspots < 1) return InvalidArg<ExportedInvestigationSummary>(nameof(topHotspots), "must be >= 1");

        var requestedHandles = new[] { handle }
            .Concat(additionalHandles ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var evidence = new List<InvestigationEvidenceInput>(requestedHandles.Length);
        int? processId = null;
        foreach (var requestedHandle in requestedHandles)
        {
            var lookup = handles.TryGetWithKind(requestedHandle);
            if (lookup is null)
            {
                return DiagnosticResult.Fail<ExportedInvestigationSummary>(
                    $"Handle '{requestedHandle}' is unknown or expired.",
                    new DiagnosticError("HandleExpired", "Drill-down handles live ~10min and may be invalidated when the target process exits.", requestedHandle),
                    new NextActionHint("collect_events", "Re-run the evidence collector on the same pid to issue a fresh handle.",
                        new Dictionary<string, object?> { ["kind"] = "counters", ["durationSeconds"] = 5 }));
            }

            if (!QuerySnapshotTool.AuthorizeHandleKind(
                    principalAccessor.Current,
                    lookup.Value.Kind,
                    view: null,
                    toolName: "export_investigation_summary",
                    failure: out var authorizationFailure))
            {
                return ConvertFailure<ExportedInvestigationSummary>(authorizationFailure!);
            }

            if (!IsSupportedEvidencePair(lookup.Value.Kind, lookup.Value.Artifact))
            {
                return DiagnosticResult.Fail<ExportedInvestigationSummary>(
                    $"Handle '{requestedHandle}' has unsupported or mismatched kind '{lookup.Value.Kind}'.",
                    new DiagnosticError(
                        "HandleKindMismatch",
                        "Supported canonical summary evidence pairs are cpu-sample/CpuSampleTraceArtifact, counters/CounterSnapshot, gc-events/GcSummary, gc-datas/GcDatasSnapshot, and thread-snapshot/ThreadSnapshotArtifact.",
                        requestedHandle),
                    new NextActionHint("collect_events", "Collect a supported counters or GC artifact.",
                        new Dictionary<string, object?> { ["kind"] = "counters", ["durationSeconds"] = 5 }));
            }

            processId ??= lookup.Value.Handle.ProcessId;
            if (lookup.Value.Handle.ProcessId != processId)
            {
                return DiagnosticResult.Fail<ExportedInvestigationSummary>(
                    "All evidence handles must belong to the same process.",
                    new DiagnosticError("EvidenceProcessMismatch", $"Handle '{requestedHandle}' belongs to PID {lookup.Value.Handle.ProcessId}; expected PID {processId}.", requestedHandle),
                    new NextActionHint("export_investigation_summary", "Re-issue with handles collected from one process."));
            }

            evidence.Add(new InvestigationEvidenceInput(
                requestedHandle,
                lookup.Value.Kind,
                lookup.Value.Artifact,
                lookup.Value.Handle.Origin.ToString().ToLowerInvariant()));
        }

        if (evidence.Count(static item => item.Kind == "cpu-sample") > 1)
        {
            return DiagnosticResult.Fail<ExportedInvestigationSummary>(
                "An investigation summary can include at most one CPU sample handle.",
                new DiagnosticError(
                    "HandleCombinationUnsupported",
                    "Use one CPU sample plus complementary counters, GC, or thread evidence; compare separate CPU windows with query_snapshot(view='diff')."),
                new NextActionHint("query_snapshot", "Compare the two CPU handles directly with the snapshot diff view."));
        }

        var fix = (fixCommitSha is null && fixPullRequestUrl is null && fixDescription is null)
            ? null
            : new InvestigationFixTarget(fixCommitSha, fixPullRequestUrl, fixDescription);

        ExportedInvestigationSummary exported;
        try
        {
            exported = exporter.Export(new ExportRequest(
                Evidence: evidence,
                TopHotspots: topHotspots,
                BuildAssemblyName: buildAssemblyName,
                PreviousInvestigationId: previousInvestigationId,
                TargetsFix: fix,
                Notes: notes,
                Format: format));
        }
        catch (EvidenceMetricConflictException ex)
        {
            return DiagnosticResult.Fail<ExportedInvestigationSummary>(
                ex.Message,
                new DiagnosticError("EvidenceMetricConflict", ex.Message, ex.MetricName),
                new NextActionHint(
                    "export_investigation_summary",
                    "Remove one handle that reports the conflicting metric, or export the captures separately."));
        }

        telemetry.Emit(exported.Summary, string.Join(",", requestedHandles));

        var bytes = exported.Rendered.Length;
        return DiagnosticResult.Ok(
            exported,
            $"Exported investigation {exported.Summary.InvestigationId} from {evidence.Count} evidence handle(s) ({exported.Summary.Findings.TopHotspots.Count} CPU hotspots, {bytes} chars {format}). Paste `rendered` into your PR/ADR; re-supply this JSON via compare_to_baseline on the next investigation.");
    }

    private static bool IsSupportedEvidencePair(string kind, object artifact)
        => (kind, artifact) switch
        {
            ("cpu-sample", CpuSampleTraceArtifact) => true,
            (DotnetDiagnostics.Core.Collection.CollectionHandleKinds.Counters, CounterSnapshot) => true,
            (DotnetDiagnostics.Core.Collection.CollectionHandleKinds.GcEvents, GcSummary) => true,
            (DotnetDiagnostics.Core.Collection.CollectionHandleKinds.GcDatas, GcDatasSnapshot) => true,
            (DotnetDiagnostics.Core.UseCases.SamplerUseCases.ThreadSnapshotKind, ThreadSnapshotArtifact) => true,
            _ => false,
        };

    private static DiagnosticResult<T> ConvertFailure<T>(DiagnosticResult<object> failure)
        => new(failure.Summary, failure.Hints, failure.Error)
        {
            Handle = failure.Handle,
            HandleExpiresAt = failure.HandleExpiresAt,
            ResolvedProcess = failure.ResolvedProcess,
        };

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("inspect_process", "Re-issue with valid arguments. See tool schema for ranges and defaults."));
}
