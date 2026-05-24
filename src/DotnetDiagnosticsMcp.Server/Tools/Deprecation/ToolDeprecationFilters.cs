using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Tools.Deprecation;

/// <summary>
/// MCP request filters that turn a <see cref="ToolDeprecationRegistry"/> into observable
/// behavior:
/// <list type="number">
///   <item><description><b>list_tools</b> — appends the deprecation notice to the matching
///   tool's description so callers see it without having to invoke the tool first.</description></item>
///   <item><description><b>call_tool</b> — records the invocation against the registry,
///   which emits the once-per-process audit-log warning.</description></item>
/// </list>
/// Both filters are no-ops when the registry has no entries — sub-issues #205–#213 are the
/// ones that will populate it.
/// </summary>
public static class ToolDeprecationFilters
{
    /// <summary>Filter that augments <c>tools/list</c> responses by appending the deprecation
    /// notice to every deprecated tool's description.</summary>
    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> CreateListToolsFilter(
        ToolDeprecationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return next => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken).ConfigureAwait(false);
            ApplyDeprecationNotices(registry, result);
            return result;
        };
    }

    /// <summary>Filter that records every <c>tools/call</c> invocation against the registry so
    /// the once-per-process deprecation warning fires on the first call to a deprecated tool.
    /// The invocation itself is delegated unchanged — this filter never blocks or rewrites
    /// the result.</summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CreateCallToolFilter(
        ToolDeprecationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        return next => async (request, cancellationToken) =>
        {
            var toolName = request.Params?.Name;
            if (!string.IsNullOrEmpty(toolName))
            {
                registry.NotifyInvocation(toolName);
            }
            return await next(request, cancellationToken).ConfigureAwait(false);
        };
    }

    /// <summary>
    /// Pure helper that mutates <paramref name="result"/> in place, appending each deprecated
    /// tool's notice to its description. Decoupled from <c>RequestContext</c> so tests can
    /// exercise the contract without constructing an <c>McpServer</c> — the filter wrapper
    /// above just calls it.
    /// </summary>
    public static void ApplyDeprecationNotices(ToolDeprecationRegistry registry, ListToolsResult result)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(result);
        if (registry.Entries.Count == 0 || result.Tools is null || result.Tools.Count == 0)
        {
            return;
        }

        for (int i = 0; i < result.Tools.Count; i++)
        {
            var tool = result.Tools[i];
            var entry = registry.TryGet(tool.Name);
            if (entry is null) continue;

            var existing = tool.Description;
            var suffix = entry.Notice;
            tool.Description = string.IsNullOrEmpty(existing)
                ? suffix
                : $"{existing}\n\n{suffix}";
        }
    }
}

