using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Tools.Dispatch;
using DotnetDiagnostics.Mcp.Orchestrator;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using DotnetDiagnostics.Mcp.Security;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Tools;

/// <summary>
/// Canonical orchestrator listing surface. The <c>kind</c> discriminator selects Pod
/// discovery or active-investigation inventory.
/// </summary>
/// <remarks>
/// <para><c>attach_to_pod</c> / <c>detach_from_pod</c> are deliberately NOT merged —
/// the orchestrator design treats them as distinct side-effect boundaries.
/// </para>
/// <para>Authorization is split by <c>kind</c>: <c>pods</c> keeps the
/// <c>orchestrator-list</c> scope, while <c>investigations</c> requires the more
/// privileged <c>orchestrator-attach</c> scope. The MCP filter only sees the
/// declared <see cref="RequireAnyScopeAttribute"/> union; the per-kind tightening
/// is enforced inside the tool body so callers cannot use a <c>list</c>-only token
/// to enumerate investigation handles.</para>
/// </remarks>
[McpServerToolType]
public sealed class ListOrchestratorTool
{
    public const string KindPods = "pods";
    public const string KindInvestigations = "investigations";

    private static readonly IReadOnlyList<string> AllowedKinds = new[] { KindPods, KindInvestigations };

    [RequireAnyScope("orchestrator-list", "orchestrator-attach")]
    [McpServerTool(
        Name = "list_orchestrator",
        Title = "List orchestrator entities (Pods or active investigations)",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Pass kind='pods' to enumerate " +
        "candidate Pods in allowed namespaces (supports namespace/labelSelector/" +
        "fieldSelector/containerName/preparedOnly/includeNotReady/limit/cursor); pass kind='investigations' " +
        "to enumerate investigation handles minted on behalf of this MCP session (supports " +
        "includeTerminal/includeAllSessions). Read-only; never injects " +
        "an ephemeral container and never returns bearer tokens. attach_to_pod / detach_from_pod are " +
        "intentionally NOT folded in — they remain explicit per the orchestrator design.")]
    public static async Task<DiagnosticResult<ListOrchestratorResult>> ListOrchestrator(
        IPodInventory inventory,
        IInvestigationStore store,
        OrchestratorOptions options,
        IPrincipalAccessor principalAccessor,
        IKubeconfigContext kubeconfigContext,
        IKubeconfigHandleStore kubeconfigStore,
        McpServer? server = null,
        ILoggerFactory? loggerFactory = null,
        [Description("Discriminator: 'pods' (candidate Pods for attach) or 'investigations' (handles minted by this session). Case-sensitive.")]
        string kind = KindPods,
        // ---- kind=pods ---------------------------------------------------------------
        [Description("kind=pods: Kubernetes namespace to list from. When omitted, the orchestrator's DefaultNamespace is used.")]
        string? @namespace = null,
        [Description("kind=pods: Optional Kubernetes label selector (e.g. 'app=api,env=prod').")]
        string? labelSelector = null,
        [Description("kind=pods: Optional Kubernetes field selector (e.g. 'status.phase=Running').")]
        string? fieldSelector = null,
        [Description("kind=pods: Optional container name. Defaults to the first container in each Pod's spec.")]
        string? containerName = null,
        [Description("kind=pods: When true (default), only Pods that are diagnostically prepared are returned.")]
        bool preparedOnly = true,
        [Description("kind=pods: When false (default), Pods that are not Ready are filtered out.")]
        bool includeNotReady = false,
        [Description("kind=pods: Max rows per page (default 100, clamped to the orchestrator's MaxListLimit).")]
        int limit = 100,
        [Description("kind=pods: Opaque continuation cursor from a prior call's nextCursor. Null for the first page.")]
        string? cursor = null,
        [Description("kind=pods: AKS handoff (#234) — opaque kubeconfig handle minted by discover_azure(kind=aksclusters, includeKubeconfig=true). When set, this listing targets the AKS cluster identified by the handle instead of the orchestrator's default in-cluster / kubeconfig context.")]
        string? kubeconfigHandle = null,
        // ---- kind=investigations ----------------------------------------------------
        [Description("kind=investigations: When true, includes handles in terminal states (Closed/Expired/Failed). Default false — only Active/Attaching.")]
        bool includeTerminal = false,
        [Description("kind=investigations: Operator-only opt-in (requires Orchestrator:AllowCrossSessionAdmin=true OR the 'orchestrator-admin' scope). When true, returns handles minted by other MCP sessions.")]
        bool includeAllSessions = false,
        CancellationToken cancellationToken = default)
    {
        if (!ToolDispatchGuards.TryValidateDiscriminator<ListOrchestratorResult>(
                kind, AllowedKinds, parameterName: "kind",
                out var canonicalKind, out var failure))
        {
            return failure!;
        }

        if (!options.Enabled)
        {
            // The orchestrator gate keeps the tool unregistered when disabled, but a host that
            // wires the type up manually (or a future per-request toggle) must still see a
            // structured failure rather than a partial answer.
            var msg = "list_orchestrator: orchestrator mode is disabled (Orchestrator:Enabled=false).";
            return DiagnosticResult.Fail<ListOrchestratorResult>(
                msg,
                new DiagnosticError(OrchestratorErrorKinds.OrchestratorDisabled, msg),
                new NextActionHint(
                    "list_orchestrator",
                    "Set Orchestrator:Enabled=true on the MCP server and re-deploy."));
        }

        // Per-kind scope tightening. The [RequireAnyScope] filter at dispatch
        // accepts callers holding either listing scope; these guards make sure neither kind
        // becomes a back-door to the other's data by switching the discriminator.
        if (canonicalKind == KindInvestigations)
        {
            if (!ToolDispatchGuards.RequireScope(
                    principalAccessor.Current,
                    "orchestrator-attach",
                    () => "list_orchestrator(kind=investigations) requires the 'orchestrator-attach' scope.",
                    out DiagnosticResult<ListOrchestratorResult>? scopeFailure,
                    new NextActionHint(
                        "list_orchestrator",
                        "Use kind='pods' (orchestrator-list scope) or grant the token 'orchestrator-attach'.",
                        new Dictionary<string, object?> { ["kind"] = KindPods }),
                    errorKind: OrchestratorErrorKinds.PermissionDenied,
                    defaultErrorTargetToScope: false))
            {
                return scopeFailure!;
            }
        }
        else if (canonicalKind == KindPods)
        {
            if (!ToolDispatchGuards.RequireScope(
                    principalAccessor.Current,
                    "orchestrator-list",
                    () => "list_orchestrator(kind=pods) requires the 'orchestrator-list' scope.",
                    out DiagnosticResult<ListOrchestratorResult>? scopeFailure,
                    new NextActionHint(
                        "list_orchestrator",
                        "Grant the token 'orchestrator-list', or use kind='investigations' (orchestrator-attach scope).",
                        new Dictionary<string, object?> { ["kind"] = KindInvestigations }),
                    errorKind: OrchestratorErrorKinds.PermissionDenied,
                    defaultErrorTargetToScope: false))
            {
                return scopeFailure!;
            }
        }

        if (canonicalKind == KindPods)
        {
            // #234 — when the caller supplies a kubeconfigHandle, push it on the ambient
            // context so the downstream IKubernetesClientFactory resolves to an ephemeral
            // client built from the in-memory bytes. Validate up front so an expired /
            // unknown handle is surfaced as a structured error envelope and never reaches
            // a network call. The handle value is NEVER echoed back in the error.
            IDisposable? kubeconfigScope = null;
            if (!string.IsNullOrEmpty(kubeconfigHandle))
            {
                var probe = kubeconfigStore.TryResolve(kubeconfigHandle);
                if (probe is null)
                {
                    const string msg = "list_orchestrator(kind=pods): kubeconfigHandle is unknown or has expired.";
                    return DiagnosticResult.Fail<ListOrchestratorResult>(
                        msg,
                        new DiagnosticError(OrchestratorErrorKinds.KubeconfigHandleNotFound, msg),
                        new NextActionHint(
                            "discover_azure",
                            "Re-run discover_azure(kind=aksclusters, includeKubeconfig=true) to mint a fresh handle, then retry list_orchestrator with the new value.",
                            new Dictionary<string, object?> { ["kind"] = "aksclusters", ["includeKubeconfig"] = true }));
                }
                // Zero the defensive copy immediately — the real client build happens
                // inside the factory which re-resolves the handle through the store.
                System.Array.Clear(probe, 0, probe.Length);
                kubeconfigScope = kubeconfigContext.Push(kubeconfigHandle);
            }

            try
            {
                var inner = await OrchestratorTools.ListPods(
                    inventory,
                    @namespace,
                    labelSelector,
                    fieldSelector,
                    containerName,
                    preparedOnly,
                    includeNotReady,
                    limit,
                    cursor,
                    cancellationToken).ConfigureAwait(false);

                return Project(inner, KindPods, page => new ListOrchestratorResult(KindPods, Pods: page, Investigations: null));
            }
            finally
            {
                kubeconfigScope?.Dispose();
            }
        }
        else
        {
            var inner = await OrchestratorTools.ListActiveInvestigations(
                store,
                options,
                principalAccessor,
                server,
                loggerFactory,
                includeTerminal,
                includeAllSessions,
                cancellationToken).ConfigureAwait(false);

            return Project(inner, KindInvestigations, page => new ListOrchestratorResult(KindInvestigations, Pods: null, Investigations: page));
        }
    }

    private static DiagnosticResult<ListOrchestratorResult> Project<TInner>(
        DiagnosticResult<TInner> inner,
        string kind,
        System.Func<TInner, ListOrchestratorResult> wrap)
    {
        if (inner.IsError)
        {
            return new DiagnosticResult<ListOrchestratorResult>(inner.Summary, inner.Hints, inner.Error)
            {
                Data = null,
                Handle = inner.Handle,
                HandleExpiresAt = inner.HandleExpiresAt,
                ResolvedProcess = inner.ResolvedProcess,
            };
        }

        var data = inner.Data is null ? new ListOrchestratorResult(kind, null, null) : wrap(inner.Data);
        return new DiagnosticResult<ListOrchestratorResult>(inner.Summary, inner.Hints, inner.Error)
        {
            Data = data,
            Handle = inner.Handle,
            HandleExpiresAt = inner.HandleExpiresAt,
            ResolvedProcess = inner.ResolvedProcess,
        };
    }
}

/// <summary>
/// Discriminated payload for <see cref="ListOrchestratorTool.ListOrchestrator"/>. Exactly
/// one of <see cref="Pods"/> / <see cref="Investigations"/> is populated, matching the
/// requested <see cref="Kind"/>; the other is always <c>null</c> so JSON consumers can
/// branch without re-running the tool.
/// </summary>
public sealed record ListOrchestratorResult(
    string Kind,
    PodCandidatePage? Pods,
    InvestigationListPage? Investigations);
