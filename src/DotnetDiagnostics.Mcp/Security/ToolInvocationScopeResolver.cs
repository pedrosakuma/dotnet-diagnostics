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
        => NormalizeDiscriminator(kind) switch
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
        => IsDiscriminator(view, "requests-now")
            ? PtraceScope
            : ReadCountersScope;

    internal static string GetListOrchestratorKindScope(string? kind)
        => IsDiscriminator(kind, "investigations")
            ? OrchestratorAttachScope
            : OrchestratorListScope;

    private static void ResolveCollectEvents(
        IDictionary<string, JsonElement>? arguments,
        ToolScopeResolutionPolicies? policies,
        ImmutableArray<string>.Builder additional,
        ImmutableArray<string>.Builder modifiers)
    {
        var kind = NormalizeDiscriminator(GetString(arguments, "kind")) ?? "counters";
        Add(additional, GetCollectEventsKindScope(kind));

        if (kind is "distributed_trace" or "replica_counters")
        {
            Add(additional, OrchestratorAttachScope);
        }

        if (IsDiscriminator(kind, "counters") &&
            (!string.IsNullOrWhiteSpace(GetString(arguments, "triggerWhen")) ||
             !string.IsNullOrWhiteSpace(GetString(arguments, "captureKind"))))
        {
            foreach (var scope in GetGatedCaptureScopes(GetString(arguments, "captureKind")))
            {
                Add(additional, scope);
            }
        }

        if (IsDiscriminator(kind, "event_source") &&
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
        var kind = NormalizeDiscriminator(GetString(arguments, "kind")) ?? "cpu";
        if (IsDiscriminator(kind, "method-params"))
        {
            Add(modifiers, SensitiveParameterReadScope);
        }

        if (IsDiscriminator(kind, "cpu") &&
            GetBoolean(arguments, "resolveMethodInstantiations"))
        {
            Add(additional, PtraceScope);
        }

        var usesSymbols = IsDiscriminator(kind, "off_cpu") ||
            (IsDiscriminator(kind, "cpu") &&
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

            var tool = NormalizeDiscriminator(GetString(request, "tool"));
            var kind = NormalizeDiscriminator(GetString(request, "kind"));
            if (IsDiscriminator(tool, "collect_sample"))
            {
                Add(additional, EventPipeScope);
            }
            else if (IsDiscriminator(tool, "collect_events"))
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
        var source = NormalizeDiscriminator(GetString(arguments, "source"));
        if (IsDiscriminator(source, "live"))
        {
            Add(additional, PtraceScope);
        }

        if (GetBoolean(arguments, "includeRetentionPaths"))
        {
            Add(modifiers, SensitiveHeapReadScope);
        }

        if (!IsDiscriminator(source, "gcdump") &&
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
        // Pod-local handles are opaque to the orchestrator. The delegation layer therefore forwards
        // every concrete primary scope the caller actually holds; the pod resolves the handle kind
        // and applies the existing kind-specific guard. Never require or synthesize the complete union.
        _ = proxyInvocation;

        var view = NormalizeDiscriminator(GetString(arguments, "view"));
        if (IsDiscriminator(view, "frame-vars"))
        {
            Add(additional, PtraceScope);
            Add(additional, HeapReadScope);
        }
        else if (IsDiscriminator(view, "retention-paths") ||
                 IsDiscriminator(view, "growth"))
        {
            Add(additional, HeapReadScope);
            Add(modifiers, SensitiveHeapReadScope);
        }

        if (!GetBoolean(arguments, "includeSensitiveValues"))
        {
            return;
        }

        if (IsDiscriminator(view, "events"))
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
        => IsDiscriminator(GetString(arguments, name), expected);

    private static string? NormalizeDiscriminator(string? value)
        => value?.Trim().ToLowerInvariant();

    private static bool IsDiscriminator(string? value, string expected)
        => string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static string? GetString(IDictionary<string, JsonElement>? arguments, string name)
        => TryGet(arguments, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static string? GetString(JsonElement value, string name)
    {
        if (value.TryGetProperty(name, out var exact))
        {
            return exact.ValueKind == JsonValueKind.String
                ? exact.GetString()?.Trim()
                : null;
        }

        foreach (var candidate in value.EnumerateObject())
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.Value.ValueKind == JsonValueKind.String
                    ? candidate.Value.GetString()?.Trim()
                    : null;
            }
        }

        return null;
    }

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
        if (arguments is null)
        {
            value = default;
            return false;
        }

        if (arguments.TryGetValue(name, out value))
        {
            return true;
        }

        foreach (var candidate in arguments)
        {
            if (string.Equals(candidate.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate.Value;
                return true;
            }
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
