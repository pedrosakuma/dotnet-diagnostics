using System.Text.Json;
using DotnetDiagnostics.Core.Dump;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Diagnostics;

/// <summary>
/// Issue #425 — native MCP <b>Elicitation</b> for the <c>collect_process_dump</c> human-approval gate.
/// When the connected client advertises the elicitation capability, the server requests a structured
/// approve/deny decision (PID / dump type / output path / disk-cost preview) <i>within the single
/// tool call's lifecycle</i> instead of relying on the ad-hoc <c>confirm=true</c> round-trip.
/// </summary>
/// <remarks>
/// <para>
/// Capability-gated and stateless by construction: if the client did not negotiate
/// <c>elicitation</c> (older clients, stdio hosts that don't proxy it), or if the round-trip
/// throws for any transport reason, the helper reports <see cref="DumpApprovalOutcome.NotSupported"/>
/// and the caller falls back to the existing <c>confirmation_required</c> preview/retry contract.
/// No server-side pending-approval store is created — the decision lives and dies with the request.
/// </para>
/// </remarks>
internal static class DumpApprovalElicitation
{
    /// <summary>Schema/content field carrying the operator's boolean decision.</summary>
    internal const string ApproveField = "approve";

    /// <summary>
    /// Requests human approval to write a process dump via MCP elicitation.
    /// Returns <see cref="DumpApprovalOutcome.NotSupported"/> (without contacting the client) when the
    /// request context is absent or the client did not advertise the elicitation capability.
    /// </summary>
    public static async Task<DumpApprovalOutcome> RequestAsync(
        RequestContext<CallToolRequestParams>? request,
        int? processId,
        ProcessDumpType dumpType,
        string? outputDirectory,
        CancellationToken cancellationToken)
    {
        var server = request?.Server;
        if (server is null || server.ClientCapabilities?.Elicitation is null)
        {
            return DumpApprovalOutcome.NotSupported;
        }

        var pidText = processId is null ? "the auto-selected .NET process" : $"pid {processId}";
        var outputText = string.IsNullOrWhiteSpace(outputDirectory)
            ? "the default artifact root"
            : $"'{outputDirectory}' (under the artifact root)";
        var message =
            $"Approve writing a {dumpType} process dump for {pidText} to {outputText}? " +
            "A process dump is irreversible, can be large (Mini < Triage < WithHeap < Full), and may " +
            "contain heap contents (secrets, PII). It will remain on the server's filesystem until " +
            "deleted. Approve only after confirming this is intended.";

        var elicit = new ElicitRequestParams
        {
            Message = message,
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                {
                    [ApproveField] = new ElicitRequestParams.BooleanSchema
                    {
                        Title = "Write the dump",
                        Description = $"Set true to write the {dumpType} dump for {pidText}.",
                        Default = false,
                    },
                },
                Required = new List<string> { ApproveField },
            },
        };

        try
        {
            var result = await server.ElicitAsync(elicit, cancellationToken).ConfigureAwait(false);

            // Only an explicit accept carrying approve=true authorizes the write. A decline/cancel
            // action, or an accept with approve=false, is a deliberate denial — never escalate it
            // to a confirm=true preview that would invite the LLM to retry around the human.
            if (result.IsAccepted
                && result.Content is not null
                && result.Content.TryGetValue(ApproveField, out var value)
                && value.ValueKind == JsonValueKind.True)
            {
                return DumpApprovalOutcome.Approved;
            }

            return DumpApprovalOutcome.Declined;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The whole tool call is being cancelled — propagate so the tool returns its
            // cancellation envelope rather than silently writing/falling back.
            throw;
        }
        catch
        {
            // The client ADVERTISED elicitation but the round-trip failed (handler error, timeout,
            // transport fault). Fail CLOSED: report Failed so the caller surfaces a structured error
            // and does NOT fall back to honouring confirm=true — a capable client must not have its
            // human-approval gate silently bypassed by a flaky elicitation channel.
            return DumpApprovalOutcome.Failed;
        }
    }
}

/// <summary>Result of a dump-approval elicitation round-trip.</summary>
internal enum DumpApprovalOutcome
{
    /// <summary>Client cannot be elicited (no capability advertised, no round-trip made) — fall back
    /// to the <c>confirm=true</c> preview/retry contract.</summary>
    NotSupported,

    /// <summary>A human explicitly approved the dump write.</summary>
    Approved,

    /// <summary>A human explicitly declined (or supplied <c>approve=false</c>) — write nothing, do not retry.</summary>
    Declined,

    /// <summary>The client advertised elicitation but the round-trip failed (handler error, timeout,
    /// transport fault). Fail CLOSED — surface a structured error; do NOT honour <c>confirm=true</c>.</summary>
    Failed,
}
