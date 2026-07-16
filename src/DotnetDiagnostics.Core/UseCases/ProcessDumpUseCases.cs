using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.ProcessDiscovery;
using Microsoft.Extensions.Logging;
using static DotnetDiagnostics.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>
/// Host-neutral process-dump use case (issue #288 PR3b). Owns the docs/authorization.md#per-call-confirmation confirmation gate,
/// process resolution, attach guarding and the dump-write orchestration for <c>collect_process_dump</c>.
/// Depends on Core abstractions only, so the MCP <c>collect_process_dump</c> tool and the standalone
/// <c>dotnet-diagnostics dump</c> CLI share one behavior.
/// </summary>
/// <remarks>
/// The transport seams: instead of the Server <c>IPrincipalAccessor</c> / <c>ILoggerFactory</c> this
/// takes the precomputed audit <c>principalName</c> and an already-categorized <see cref="ILogger"/>.
/// The MCP Server keeps a thin <c>DiagnosticTools.CollectProcessDump</c> wrapper that creates the
/// logger with its existing category and passes the principal name, so the audit log and the
/// confirmation-required envelope stay byte-identical.
/// </remarks>
public static class ProcessDumpUseCases
{
    /// <summary>
    /// Writes a process dump to disk (gated by <paramref name="confirm"/>). When
    /// <paramref name="confirm"/> is <see langword="false"/> returns a <c>confirmation_required</c>
    /// preview WITHOUT resolving the process or touching the target.
    /// </summary>
    public static async Task<DiagnosticResult<DumpToolResult>> CollectProcessDump(
        IProcessDumper dumper,
        IProcessContextResolver resolver,
        ILogger? logger,
        string? principalName,
        int? processId = null,
        ProcessDumpType dumpType = ProcessDumpType.Mini,
        string? outputDirectory = null,
        bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            // docs/authorization.md#per-call-confirmation: confirmation-required is a misuse signal, not an attack.
            // Log at Information level with the token name (never the bearer value), the
            // tool name and the reason as structured properties so audit consumers can
            // filter on the structured `tool` / `reason` audit fields.
            //
            // The confirmation gate runs BEFORE process-context resolution (#187 review):
            // ResolveContextAsync would otherwise open an EventPipe session via
            // CapabilityDetector to probe the target — that already counts as a process
            // attach for the purpose of "writes NOTHING / no process attach". When the
            // caller relied on auto-resolution we therefore echo a null TargetPid in the
            // preview instead of touching the target.
            logger?.LogInformation(
                "collect_process_dump rejected: confirmation_required. tokenName={TokenName} tool={Tool} reason={Reason} requestedPid={RequestedPid} dumpType={DumpType}",
                principalName ?? "(none)",
                "collect_process_dump",
                "ConfirmationRequired",
                processId,
                dumpType);

            var preview = new DumpToolResult
            {
                Kind = DumpToolResultKinds.ConfirmationRequired,
                Message = processId is null
                    ? "collect_process_dump writes a heap dump to disk. Pass confirm=true to proceed. processId was not supplied — the server will auto-select a .NET process when you re-issue with confirm=true."
                    : "collect_process_dump writes a heap dump to disk. Pass confirm=true to proceed.",
                TargetPid = processId,
                DumpType = dumpType,
                OutputDirectory = outputDirectory,
            };

            var retryArgs = new Dictionary<string, object?>
            {
                ["dumpType"] = dumpType.ToString(),
                ["outputDirectory"] = outputDirectory,
                ["confirm"] = true,
            };
            if (processId is not null) retryArgs["processId"] = processId;

            var retryHint = new NextActionHint(
                "collect_process_dump",
                "Re-issue the call with confirm=true after explicit human approval. Required scopes: dump-write + ptrace.",
                retryArgs);

            var summary = processId is null
                ? $"confirmation_required: collect_process_dump would write a {dumpType} dump for the auto-selected .NET process. Pass confirm=true to proceed."
                : $"confirmation_required: collect_process_dump would write a {dumpType} dump for pid {processId}. Pass confirm=true to proceed.";
            return DiagnosticResult.Ok(preview, summary, retryHint);
        }

        var resolved = await ResolveContextAsync<DumpToolResult>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;
        var ctx = resolved.Context;

        return await AttachGuard.GuardAttachAsync("collect_process_dump", pid, async () =>
        {
            var dump = await dumper.WriteDumpAsync(pid, dumpType, outputDirectory, cancellationToken).ConfigureAwait(false);
            var hint = dumpType == ProcessDumpType.Mini
                ? new NextActionHint("inspect_heap",
                    "Mini dump captured — heap walk unavailable. Re-capture with dumpType='WithHeap' for full inspection.",
                    new Dictionary<string, object?> { ["source"] = "dump", ["dumpFilePath"] = dump.FilePath })
                : new NextActionHint("inspect_heap",
                    "Inspect the dump's managed heap for top-retained types + handoff payload to dotnet-assembly-mcp.",
                    new Dictionary<string, object?>
                    {
                        ["source"] = "dump",
                        ["dumpFilePath"] = dump.FilePath,
                        ["topTypes"] = 20,
                    });
            var payload = new DumpToolResult
            {
                Kind = DumpToolResultKinds.DumpWritten,
                TargetPid = dump.ProcessId,
                DumpType = dump.DumpType,
                OutputDirectory = outputDirectory,
                Dump = dump,
            };
            return WithContext(DiagnosticResult.Ok(
                payload,
                $"Wrote {dumpType} dump for pid {dump.ProcessId} to {dump.FilePath} ({dump.FileSizeBytes:N0} bytes).",
                hint), ctx);
        }, cancellationToken, retryArguments: new Dictionary<string, object?>
        {
            ["processId"] = pid,
            ["dumpType"] = dumpType.ToString(),
            ["outputDirectory"] = outputDirectory,
            ["confirm"] = true,
        }).ConfigureAwait(false);
    }
}
