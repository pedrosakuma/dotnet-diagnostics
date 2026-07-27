using System.Reflection;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using DotnetDiagnostics.Mcp.Security;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Tools;

internal static class InvestigationProxyReadResourceFilter
{
    public static McpRequestFilter<ReadResourceRequestParams, ReadResourceResult> Create(
        IInvestigationSessionBinder sessionBinder,
        Func<ILogger?> loggerAccessor,
        Func<RequestContext<ReadResourceRequestParams>, string?>? sessionIdResolver = null)
    {
        ArgumentNullException.ThrowIfNull(sessionBinder);
        ArgumentNullException.ThrowIfNull(loggerAccessor);
        sessionIdResolver ??= static context => TryGetServerSessionId(context.Server);

        return next => (request, cancellationToken) =>
            InvokeAsync(
                request.Params,
                sessionIdResolver(request),
                (_, ct) => next(request, ct),
                sessionBinder,
                loggerAccessor,
                cancellationToken);
    }

    internal static ValueTask<ReadResourceResult> InvokeAsync(
        ReadResourceRequestParams? requestParams,
        string? sessionId,
        Func<ReadResourceRequestParams?, CancellationToken, ValueTask<ReadResourceResult>> next,
        IInvestigationSessionBinder sessionBinder,
        Func<ILogger?> loggerAccessor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(sessionBinder);
        ArgumentNullException.ThrowIfNull(loggerAccessor);

        var handleId = sessionBinder.TryGetHandleId(sessionId);
        if (string.IsNullOrWhiteSpace(handleId) ||
            InvestigationProxyResourcePolicy.CanTraverseProxy(requestParams?.Uri))
        {
            return next(requestParams, cancellationToken);
        }

        loggerAccessor()?.LogWarning(
            "Blocked Resource URI '{ResourceUri}' for MCP session bound to investigation {HandleId}.",
            requestParams?.Uri,
            handleId);
        throw new McpException(
            $"Resource '{requestParams?.Uri}' cannot be read through an investigation-bound session. " +
            "Use query_snapshot with the scopes required by the underlying diagnostic handle.");
    }

    private static string? TryGetServerSessionId(McpServer? server)
        => server?.GetType()
            .GetProperty("SessionId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(server) as string;
}
