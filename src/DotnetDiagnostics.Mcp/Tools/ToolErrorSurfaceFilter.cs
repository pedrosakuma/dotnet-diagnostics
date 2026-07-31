using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Safety;
using DotnetDiagnostics.Mcp.Safety;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Tools;

/// <summary>
/// MCP CallTool filter that marks structured diagnostic failure envelopes with
/// <c>IsError=true</c>, and converts any unhandled tool exception to an error
/// <see cref="CallToolResult"/> with a diagnostic text block.
/// </summary>
/// <remarks>
/// <para>
/// Without this filter the MCP SDK's terminal stage swallows the original exception and
/// emits the generic <c>"An error occurred invoking 'X'."</c> message (see
/// <c>McpServer.ConfigureTools</c> in ModelContextProtocol.Core 1.3.0). That leaves the
/// LLM blind to the actual failure — PTRACE permission denied, FileNotFound, ClrMD
/// version mismatch, etc. all look identical. Issues #62, #63 surfaced this as a hard
/// blocker during dogfood.
/// </para>
/// <para>
/// The filter sits OUTSIDE the SDK's terminal try/catch (filters wrap the inner handler
/// while the terminal stage wraps the whole filter pipeline), so it observes exceptions
/// raised by the tool body before the SDK gets a chance to mask them. Tools that classify
/// failures with <see cref="DiagnosticResult{T}"/> keep their structured content and
/// human-readable summary; the filter only corrects the MCP error bit. Cancellation and
/// protocol exceptions are rethrown so the SDK can perform the canonical close-up.
/// </para>
/// <para>
/// The exception response intentionally carries no <c>StructuredContent</c>: the output
/// schema is the tool's success-path schema (e.g. <c>LiveHeapInspection</c>), and strict
/// clients (Copilot CLI, Claude Code) validate <c>structuredContent</c> against it. A
/// text-only error result honours <c>isError=true</c> without triggering schema
/// validation failures — same reasoning behind issue #61.
/// </para>
/// </remarks>
internal static class ToolErrorSurfaceFilter
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create(Func<ILogger?> loggerAccessor)
        => next => async (request, cancellationToken) =>
        {
            try
            {
                var result = await next(request, cancellationToken).ConfigureAwait(false);
                return MarkStructuredFailure(result);
            }
            catch (Exception ex) when (!IsRethrow(ex, cancellationToken))
            {
                var toolName = request.Params?.Name ?? "(unknown tool)";

                loggerAccessor()?.LogWarning(
                    ex,
                    "Tool '{ToolName}' threw {ExceptionType}; surfacing structured error to client.",
                    toolName,
                    ex.GetType().FullName ?? ex.GetType().Name);

                return new CallToolResult
                {
                    IsError = true,
                    Content = new List<ContentBlock>
                    {
                        new TextContentBlock { Text = BuildErrorText(toolName, ex) },
                    },
                };
            }
        };

    /// <summary>
    /// Sets the MCP error bit when <paramref name="result"/> carries the repository's
    /// standard structured failure envelope, and returns the same result instance.
    /// </summary>
    internal static CallToolResult MarkStructuredFailure(CallToolResult result)
    {
        if (IsStructuredFailure(result))
        {
            result.IsError = true;
        }

        return result;
    }

    /// <summary>
    /// Returns true when a tool produced the repository's standard
    /// <see cref="DiagnosticResult{T}"/> failure envelope.
    /// </summary>
    internal static bool IsStructuredFailure(CallToolResult result)
    {
        if (result.StructuredContent is not { ValueKind: JsonValueKind.Object } structured)
        {
            return false;
        }

        return structured.TryGetProperty("error", out var error)
               && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
    }

    private static bool IsRethrow(Exception ex, CancellationToken cancellationToken)
        => IsRethrowable(ex, cancellationToken);

    /// <summary>Exposed for tests — same predicate the filter uses to decide rethrow vs surface.</summary>
    internal static bool IsRethrowable(Exception ex, CancellationToken cancellationToken)
        => (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
           || ex is McpProtocolException;

    /// <summary>Exposed for tests — formats the error block surfaced as the text content.</summary>
    internal static string BuildErrorText(string toolName, Exception ex)
    {
        var topMessage = string.IsNullOrWhiteSpace(ex.Message) ? "(no message)" : ex.Message;
        var sb = new StringBuilder();
        sb.Append(toolName)
          .Append(" failed: ")
          .Append(ex.GetType().Name)
          .Append(": ")
          .Append(topMessage);

        sb.Append("\n\nException chain:");
        var depth = 0;
        for (Exception? cur = ex; cur is not null && depth < 8; cur = cur.InnerException, depth++)
        {
            sb.Append("\n  ")
              .Append(new string(' ', depth * 2))
              .Append(cur.GetType().FullName ?? cur.GetType().Name)
              .Append(": ")
              .Append(string.IsNullOrWhiteSpace(cur.Message) ? "(no message)" : cur.Message);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Applies structured-failure classification at the tool primitive boundary. MCP task
/// execution invokes <see cref="McpServerTool.InvokeAsync"/> directly and bypasses request
/// filters, so this decorator must run before the SDK chooses the task's terminal status.
/// </summary>
internal sealed class StructuredErrorMcpServerTool(
    McpServerTool innerTool,
    IDiagnosticHandleStore? handles)
    : DelegatingMcpServerTool(innerTool)
{
    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var result = await base.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        result = ToolErrorSurfaceFilter.MarkStructuredFailure(result);

        var parameters = request.Params;
        if (parameters is null
            || !InvocationSafetyRegistry.TryGet(parameters.Name, out _))
        {
            return result;
        }

        try
        {
            var assessment = McpInvocationSafety.ResolveAssessment(
                parameters.Name,
                parameters.Arguments,
                handles);
            return McpInvocationSafetyFilter.Decorate(result, assessment);
        }
        catch (InvocationSafetyResolutionException)
        {
            // The request filter resolves and fails closed before normal/task scheduling.
            // This decorator exists to annotate the task terminal result; never mask an
            // already-produced tool result if a direct internal invocation skipped filters.
            return result;
        }
    }
}
