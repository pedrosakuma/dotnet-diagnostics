using System.Collections.Immutable;
using System.Text.Json;
using DotnetDiagnostics.Core.GatedCapture;
using DotnetDiagnostics.Core.Security;

namespace DotnetDiagnostics.Mcp.Security;

/// <summary>
/// Resolves argument-dependent authorization requirements for one concrete tool invocation.
/// This is the single map consumed by local MCP dispatch and both investigation proxy paths.
/// </summary>
internal static class ToolInvocationScopeResolver
{
    internal const string DeleteArtifactScope = "delete-artifact";
    internal const string EventPipeScope = "eventpipe";
    internal const string EventSourceAnyScope = "eventsource-any";
    internal const string HeapReadScope = "heap-read";
    internal const string InvestigationExportScope = "investigation-export";
    internal const string ModuleBytesReadScope = "module-bytes-read";
    internal const string OrchestratorAdminScope = "orchestrator-admin";
    internal const string OrchestratorAttachScope = "orchestrator-attach";
    internal const string OrchestratorListScope = "orchestrator-list";
    internal const string PtraceScope = "ptrace";
    internal const string ReadCountersScope = "read-counters";
    internal const string SensitiveHeapReadScope = "sensitive-heap-read";
    internal const string SensitiveParameterReadScope = "sensitive-parameter-read";
    internal const string SymbolsRemoteScope = "symbols-remote";
    internal const string DumpWriteScope = "dump-write";

    internal readonly record struct Requirements(
        ImmutableArray<string> AdditionalScopes,
        ImmutableArray<string> ExplicitModifierScopes);

    internal static Requirements Resolve(
        string toolName,
        IDictionary<string, JsonElement>? arguments,
        bool proxyInvocation,
        ToolScopeResolutionPolicies? policies)
    {
        var additional = ImmutableArray.CreateBuilder<string>();
        var modifiers = ImmutableArray.CreateBuilder<string>();

        switch (toolName)
        {
            case "inspect_process":
                Add(additional, GetInspectProcessViewScope(GetString(arguments, "view") ?? "list"));
                break;

            case "list_orchestrator":
                Add(additional, GetListOrchestratorKindScope(GetString(arguments, "kind") ?? "pods"));
                if (GetBoolean(arguments, "includeAllSessions") &&
                    policies?.OrchestratorOptions?.AllowCrossSessionAdmin != true)
                {
                    Add(modifiers, OrchestratorAdminScope);
                }
                break;

            case "collect_events":
                ResolveCollectEvents(arguments, policies, additional, modifiers);
                break;

            case "collect_sample":
                ResolveCollectSample(arguments, policies, additional, modifiers);
                break;

            case "collect_batch":
                ResolveCollectBatch(arguments, additional);
                break;

            case "inspect_heap":
                ResolveInspectHeap(arguments, policies, additional, modifiers);
                break;

            case "query_snapshot":
                ResolveQuerySnapshot(arguments, proxyInvocation, policies, additional, modifiers);
                break;

            case "get_bytes":
                Add(modifiers, ModuleBytesReadScope);
                if (Matches(arguments, "kind", "delete"))
                {
                    Add(modifiers, DeleteArtifactScope);
                }
                break;

            case "collect_thread_snapshot":
                if (RequiresRemoteSymbolsScope(arguments, policies))
                {
                    Add(modifiers, SymbolsRemoteScope);
                }
                break;
        }

        return new Requirements(additional.ToImmutable(), modifiers.ToImmutable());
    }

    internal static string? GetCollectEventsKindScope(string? kind)
        => kind?.Trim() switch
        {
            "counters" or "replica_counters" => ReadCountersScope,
            "exceptions" or "crash-guard" or "gc" or "datas" or "catalog" or
            "event_source" or "activities" or "logs" or "jit" or "threadpool" or
            "contention" or "db" or "kestrel" or "networking" or "requests" or
            "startup" or "sweep" or "distributed_trace" => EventPipeScope,
            _ => null,
        };

    internal static ImmutableArray<string> GetGatedCaptureScopes(string? captureKind)
    {
        if (!GatedCaptureKinds.TryParse(captureKind, out var parsed))
        {
            return ImmutableArray<string>.Empty;
        }

        return parsed.Value switch
        {
            GatedCaptureKind.CpuSample => ImmutableArray.Create(EventPipeScope),
            GatedCaptureKind.Heap => ImmutableArray.Create(HeapReadScope, PtraceScope),
            GatedCaptureKind.ThreadSnapshot => ImmutableArray.Create(PtraceScope),
            GatedCaptureKind.Dump => ImmutableArray.Create(DumpWriteScope, PtraceScope),
            _ => ImmutableArray<string>.Empty,
        };
    }

    internal static string GetInspectProcessViewScope(string? view)
        => string.Equals(view?.Trim(), "requests-now", StringComparison.Ordinal)
            ? PtraceScope
            : ReadCountersScope;

    internal static string GetListOrchestratorKindScope(string? kind)
        => string.Equals(kind?.Trim(), "investigations", StringComparison.Ordinal)
            ? OrchestratorAttachScope
            : OrchestratorListScope;

    private static void ResolveCollectEvents(
        IDictionary<string, JsonElement>? arguments,
        ToolScopeResolutionPolicies? policies,
        ImmutableArray<string>.Builder additional,
        ImmutableArray<string>.Builder modifiers)
    {
        var kind = GetString(arguments, "kind") ?? "counters";
        Add(additional, GetCollectEventsKindScope(kind));

        if (kind is "distributed_trace" or "replica_counters")
        {
            Add(additional, OrchestratorAttachScope);
        }

        if (string.Equals(kind, "counters", StringComparison.Ordinal) &&
            (!string.IsNullOrWhiteSpace(GetString(arguments, "triggerWhen")) ||
             !string.IsNullOrWhiteSpace(GetString(arguments, "captureKind"))))
        {
            foreach (var scope in GetGatedCaptureScopes(GetString(arguments, "captureKind")))
            {
                Add(additional, scope);
            }
        }

        if (string.Equals(kind, "event_source", StringComparison.Ordinal) &&
            GetBoolean(arguments, "unsafeProvider") &&
            RequiresEventSourceAnyScope(arguments, policies))
        {
            Add(modifiers, EventSourceAnyScope);
        }
    }

    private static void ResolveCollectSample(
        IDictionary<string, JsonElement>? arguments,
        ToolScopeResolutionPolicies? policies,
        ImmutableArray<string>.Builder additional,
        ImmutableArray<string>.Builder modifiers)
    {
        var kind = GetString(arguments, "kind") ?? "cpu";
        if (string.Equals(kind, "method-params", StringComparison.Ordinal))
        {
            Add(modifiers, SensitiveParameterReadScope);
        }

        if (string.Equals(kind, "cpu", StringComparison.Ordinal) &&
            GetBoolean(arguments, "resolveMethodInstantiations"))
        {
            Add(additional, PtraceScope);
        }

        var usesSymbols = string.Equals(kind, "off_cpu", StringComparison.Ordinal) ||
            (string.Equals(kind, "cpu", StringComparison.Ordinal) &&
             GetBoolean(arguments, "resolveSourceLines", defaultValue: true));
        if (usesSymbols && RequiresRemoteSymbolsScope(arguments, policies))
        {
            Add(modifiers, SymbolsRemoteScope);
        }
    }

    private static void ResolveCollectBatch(
        IDictionary<string, JsonElement>? arguments,
        ImmutableArray<string>.Builder additional)
    {
        if (!TryGet(arguments, "requests", out var requests) ||
            requests.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var request in requests.EnumerateArray())
        {
            if (request.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var tool = GetString(request, "tool");
            var kind = GetString(request, "kind");
            if (string.Equals(tool, "collect_sample", StringComparison.Ordinal))
            {
                Add(additional, EventPipeScope);
            }
            else if (string.Equals(tool, "collect_events", StringComparison.Ordinal))
            {
                Add(additional, GetCollectEventsKindScope(kind));
            }
        }
    }

    private static void ResolveInspectHeap(
        IDictionary<string, JsonElement>? arguments,
        ToolScopeResolutionPolicies? policies,
        ImmutableArray<string>.Builder additional,
        ImmutableArray<string>.Builder modifiers)
    {
        var source = GetString(arguments, "source");
        if (string.Equals(source, "live", StringComparison.Ordinal))
        {
            Add(additional, PtraceScope);
        }

        if (GetBoolean(arguments, "includeRetentionPaths"))
        {
            Add(modifiers, SensitiveHeapReadScope);
        }

        if (!string.Equals(source, "gcdump", StringComparison.Ordinal) &&
            RequiresRemoteSymbolsScope(arguments, policies))
        {
            Add(modifiers, SymbolsRemoteScope);
        }
    }

    private static void ResolveQuerySnapshot(
        IDictionary<string, JsonElement>? arguments,
        bool proxyInvocation,
        ToolScopeResolutionPolicies? policies,
        ImmutableArray<string>.Builder additional,
        ImmutableArray<string>.Builder modifiers)
    {
        // A pod-local diagnostic handle is opaque to the orchestrator. Local dispatch resolves its
        // exact kind from IDiagnosticHandleStore, but neither proxy path can safely infer whether an
        // arbitrary handle is counters, EventPipe, heap, thread, or an exported sample. Require the
        // complete primary union before forwarding rather than letting the pod's root bearer widen
        // the original caller. View-specific tightening below still applies.
        if (proxyInvocation)
        {
            Add(additional, ReadCountersScope);
            Add(additional, EventPipeScope);
            Add(additional, HeapReadScope);
            Add(additional, PtraceScope);
            Add(additional, InvestigationExportScope);
        }

        var view = GetString(arguments, "view");
        if (string.Equals(view, "frame-vars", StringComparison.Ordinal))
        {
            Add(additional, PtraceScope);
            Add(additional, HeapReadScope);
        }
        else if (string.Equals(view, "retention-paths", StringComparison.Ordinal))
        {
            Add(additional, HeapReadScope);
            Add(modifiers, SensitiveHeapReadScope);
        }

        if (!GetBoolean(arguments, "includeSensitiveValues"))
        {
            return;
        }

        if (string.Equals(view, "events", StringComparison.Ordinal))
        {
            Add(modifiers, SensitiveParameterReadScope);
        }
        else if (view is "duplicate-strings" or "object" or "frame-vars" &&
                 policies?.SensitiveValueGate?.IsAllowedByServer != true)
        {
            Add(modifiers, SensitiveHeapReadScope);
        }
    }

    private static bool RequiresRemoteSymbolsScope(
        IDictionary<string, JsonElement>? arguments,
        ToolScopeResolutionPolicies? policies)
    {
        var symbolPath = GetString(arguments, "symbolPath");
        return SymbolServerAllowlist.ContainsRemoteUrl(symbolPath) &&
            policies?.SymbolServerAllowlist?.Validate(symbolPath).IsAllowed != true;
    }

    private static bool RequiresEventSourceAnyScope(
        IDictionary<string, JsonElement>? arguments,
        ToolScopeResolutionPolicies? policies)
    {
        var providerName = GetString(arguments, "providerName");
        if (!string.IsNullOrWhiteSpace(providerName) &&
            policies?.EventSourceAllowlist?.IsAllowed(providerName) == true)
        {
            return false;
        }

        return policies?.SensitiveValueGate?.IsAllowedByServer != true;
    }

    private static bool Matches(
        IDictionary<string, JsonElement>? arguments,
        string name,
        string expected)
        => string.Equals(GetString(arguments, name), expected, StringComparison.Ordinal);

    private static string? GetString(IDictionary<string, JsonElement>? arguments, string name)
        => TryGet(arguments, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static string? GetString(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;

    private static bool GetBoolean(
        IDictionary<string, JsonElement>? arguments,
        string name,
        bool defaultValue = false)
        => TryGet(arguments, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;

    private static bool TryGet(
        IDictionary<string, JsonElement>? arguments,
        string name,
        out JsonElement value)
    {
        if (arguments is not null && arguments.TryGetValue(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static void Add(ImmutableArray<string>.Builder builder, string? scope)
    {
        if (!string.IsNullOrEmpty(scope) && !builder.Contains(scope, StringComparer.Ordinal))
        {
            builder.Add(scope);
        }
    }
}
