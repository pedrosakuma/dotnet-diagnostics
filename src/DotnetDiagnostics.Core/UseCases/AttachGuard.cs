using DotnetDiagnostics.Core;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>
/// Host-neutral classification of process-attach failures (ClrMD / EventPipe / dotnet-diagnostics)
/// into a structured <see cref="DiagnosticResult{T}"/>. Extracted from the MCP Server in #288 so the
/// standalone CLI and the MCP tools share one source of truth for permission/endpoint/process-exit
/// distinctions. Without this, uncaught exceptions surface as an opaque "An error occurred invoking
/// 'X'." — leaving a low-context caller blind to PTRACE / permission / process-exit causes. See #32.
/// </summary>
public static class AttachGuard
{
    /// <summary>
    /// Wraps a tool body that attaches to a live process and translates known failure shapes into a
    /// structured <see cref="DiagnosticResult{T}"/>. Cancellation requested via
    /// <paramref name="cancellationToken"/> propagates; every other exception is classified.
    /// Callers that want a replayable busy hint must supply complete schema-valid
    /// <paramref name="retryArguments"/>; Core never infers tool parameters.
    /// </summary>
    public static async Task<DiagnosticResult<T>> GuardAttachAsync<T>(
        string tool,
        int? processId,
        Func<Task<DiagnosticResult<T>>> body,
        CancellationToken cancellationToken,
        AttachConcurrencyLimiter? limiter = null,
        IReadOnlyDictionary<string, object?>? retryArguments = null)
    {
        // Per-pid concurrency gate (#452, D2): two live attaches against the same target collide
        // because only one attacher can suspend it at a time. Serialize live attaches per pid;
        // dump-based work (no live pid) is never gated.
        IDisposable? permit = null;
        if (processId is int gatedPid && gatedPid > 0)
        {
            limiter ??= AttachConcurrencyLimiter.Shared;
            try
            {
                permit = await limiter.TryAcquireAsync(gatedPid, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            if (permit is null)
            {
                return BusyResult<T>(tool, gatedPid, retryArguments);
            }
        }

        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ClassifyAttachFailure<T>(tool, processId, ex);
        }
        finally
        {
            permit?.Dispose();
        }
    }

    /// <summary>
    /// Retriable "busy" envelope returned when another attach already holds the per-pid gate.
    /// The hint nudges the LLM to back off and retry the same tool rather than fail the run.
    /// <paramref name="retryArguments"/> must be complete and schema-valid; pass
    /// <see langword="null"/> for an informational, non-replayable hint.
    /// </summary>
    public static DiagnosticResult<T> BusyResult<T>(
        string tool,
        int processId,
        IReadOnlyDictionary<string, object?>? retryArguments = null)
        => DiagnosticResult.Fail<T>(
            $"{tool} is busy: another attach against pid {processId} is in progress.",
            new DiagnosticError("Busy", "Only one process-attach can suspend a target at a time; this pid already has an attach in flight.", "AttachConcurrencyLimiter"),
            new NextActionHint(tool,
                "Wait a moment and retry — the concurrent attach is expected to finish shortly.",
                BuildRetryArguments(retryArguments))
            { Priority = NextActionHintPriority.High });

    private static Dictionary<string, object?>? BuildRetryArguments(
        IReadOnlyDictionary<string, object?>? retryArguments)
    {
        if (retryArguments is null)
        {
            return null;
        }

        // Tool schemas are intentionally unknown to Core. Callers must provide a complete,
        // schema-valid replay bag; never infer that a live attach pid is accepted by the tool.
        return new Dictionary<string, object?>(retryArguments);
    }

    /// <summary>
    /// Maps a known attach failure (<paramref name="ex"/>) for <paramref name="tool"/> into a
    /// structured failure envelope with tool-specific next-action hints.
    /// </summary>
    public static DiagnosticResult<T> ClassifyAttachFailure<T>(string tool, int? processId, Exception ex)
    {
        var typeName = ex.GetType().FullName ?? ex.GetType().Name;
        var message = ex.Message ?? "(no message)";
        var pidHint = processId is int p && p > 0 ? $" (pid {p})" : string.Empty;

        if (ex is Microsoft.Diagnostics.NETCore.Client.ServerNotAvailableException)
        {
            return DiagnosticResult.Fail<T>(
                $"{tool} could not reach the diagnostic socket{pidHint}.",
                new DiagnosticError("EndpointUnavailable", message, typeName),
                new NextActionHint("inspect_process", "Re-list processes. Common cause: sidecar UID mismatch with target, or process has exited."));
        }

        // ClrMD wraps Linux ptrace failures (errno EPERM/ESRCH) in ClrDiagnosticsException.
        // Match on type-name-suffix to avoid taking a hard reference here. "operation not
        // permitted" is the canonical EPERM wording; also walk inner exceptions because
        // ClrMD often nests a Win32Exception with NativeErrorCode==1.
        var isPtraceFailure = (typeName.EndsWith("ClrDiagnosticsException", StringComparison.Ordinal)
                               && (message.Contains("PTRACE", StringComparison.OrdinalIgnoreCase)
                                   || message.Contains("permission", StringComparison.OrdinalIgnoreCase)
                                   || message.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase)))
                              || HasEpermInChain(ex);
        if (isPtraceFailure)
        {
            // Probe the live host now so the error envelope carries the exact mitigation
            // (e.g. "ptrace_scope=1 and sidecar lacks CAP_SYS_PTRACE") rather than the
            // generic "check ptrace_scope/CAP_SYS_PTRACE/UID" boilerplate. The probe is
            // cheap (two /proc reads on Linux) and pure on hot failure paths.
            var ptrace = DotnetDiagnostics.Core.Capabilities.PtraceProbe.Detect();
            var headline = ptrace.CanAttach
                ? $"{tool} could not attach{pidHint}: ptrace was denied even though the sidecar's static capability probe expected attach to succeed ({ptrace.Reason}). " +
                  "Likely cause: target process exited, runs under a different UID, or containers are in separate PID namespaces (use Docker '--pid=container:target' or K8s 'shareProcessNamespace: true')."
                : $"{tool} could not attach{pidHint}: ptrace was denied — {ptrace.Reason}";

            var hints = new List<NextActionHint>
            {
                new("inspect_process",
                    "Re-check sidecar capabilities (CanAttachClrMD, AttachClrMdReason). " +
                    "If CAP_SYS_PTRACE is granted, containers may be in separate PID namespaces — " +
                    "use Docker '--pid=container:target' or K8s 'shareProcessNamespace: true'.",
                    processId is int pidForCap && pidForCap > 0
                        ? new Dictionary<string, object?> { ["view"] = "capabilities", ["processId"] = pidForCap }
                        : new Dictionary<string, object?> { ["view"] = "capabilities" })
                { Priority = NextActionHintPriority.High },
                new("collect_process_dump",
                    "Fall back to dump-based workflow (collect_process_dump, then inspect_heap(source=\"dump\")). " +
                    "Dumps use the diagnostic IPC socket (no ptrace) and work across PID namespaces.",
                    processId is int pp && pp > 0 ? new Dictionary<string, object?> { ["processId"] = pp } : null)
                { Priority = NextActionHintPriority.Low },
            };

            if (string.Equals(tool, "collect_thread_snapshot", StringComparison.Ordinal))
            {
                hints.Add(new NextActionHint(
                    "collect_sample",
                    "If ptrace cannot be granted, use the perf-replay fallback path tracked in issue #92 (short capture + thread-state inference).",
                    processId is int pidForReplay && pidForReplay > 0
                        ? new Dictionary<string, object?> { ["kind"] = "off_cpu", ["processId"] = pidForReplay, ["durationSeconds"] = 5 }
                        : new Dictionary<string, object?> { ["kind"] = "off_cpu", ["durationSeconds"] = 5 }));
            }

            return DiagnosticResult.Fail<T>(
                headline,
                new DiagnosticError("PermissionDenied", message, typeName),
                hints.ToArray());
        }

        if (ex is UnauthorizedAccessException)
        {
            // Enhanced hints covering both capability and PID namespace issues.
            var accessHints = new List<NextActionHint>
            {
                new("inspect_process",
                    "Verify UID alignment and capabilities. If CAP_SYS_PTRACE is granted but attach still fails, " +
                    "containers may be in separate PID namespaces — use Docker '--pid=container:target' or K8s 'shareProcessNamespace: true'.",
                    processId is int pid && pid > 0
                        ? new Dictionary<string, object?> { ["view"] = "capabilities", ["processId"] = pid }
                        : new Dictionary<string, object?> { ["view"] = "capabilities" })
                { Priority = NextActionHintPriority.High },
                new("collect_process_dump",
                    "Fall back to dump-based workflow: collect_process_dump then inspect_heap(source=\"dump\"). " +
                    "Dumps use the diagnostic IPC socket (no ptrace) and work across PID namespaces.",
                    processId is int pidForDump && pidForDump > 0
                        ? new Dictionary<string, object?> { ["processId"] = pidForDump }
                        : null)
                { Priority = NextActionHintPriority.Low },
            };

            if (string.Equals(tool, "collect_thread_snapshot", StringComparison.Ordinal))
            {
                accessHints.Add(new NextActionHint(
                    "collect_sample",
                    "When ptrace cannot be granted, use the perf-replay fallback tracked in issue #92.",
                    processId is int pidForReplay && pidForReplay > 0
                        ? new Dictionary<string, object?> { ["kind"] = "off_cpu", ["processId"] = pidForReplay, ["durationSeconds"] = 5 }
                        : new Dictionary<string, object?> { ["kind"] = "off_cpu", ["durationSeconds"] = 5 }));
            }

            return DiagnosticResult.Fail<T>(
                $"{tool} was denied access{pidHint}.",
                new DiagnosticError("PermissionDenied", message, typeName),
                accessHints.ToArray());
        }

        if (ex is ExternalToolNotFoundException missingTool)
        {
            if (string.Equals(tool, "collect_thread_snapshot", StringComparison.Ordinal))
            {
                return DiagnosticResult.Fail<T>(
                    $"{tool} cannot run{pidHint}: required external tool '{missingTool.ToolName}' is missing.",
                    new DiagnosticError("ToolNotFound", message, typeName),
                    new NextActionHint("inspect_process",
                        "Re-check sidecar capabilities after installing elfutils (eu-stack).",
                        processId is int pidForCap && pidForCap > 0 ? new Dictionary<string, object?> { ["processId"] = pidForCap } : null));
            }

            return DiagnosticResult.Fail<T>(
                $"{tool} cannot run{pidHint}: required external tool '{missingTool.ToolName}' is missing.",
                new DiagnosticError("ToolNotFound", message, typeName));
        }

        if (ex is DotnetDiagnostics.Core.Artifacts.ArtifactPathException artifactEx)
        {
            return DiagnosticResult.Fail<T>(
                $"{tool} rejected the request: {artifactEx.Message}",
                new DiagnosticError("InvalidArtifactPath", artifactEx.Message, artifactEx.ParameterName),
                new NextActionHint(tool,
                    "Re-issue with a relative sub-path under the artifact root, or omit outputDirectory to use the default."));
        }

        if (ex is ArgumentException or InvalidOperationException)
        {
            return DiagnosticResult.Fail<T>(
                $"{tool} rejected the request{pidHint}: {message}",
                new DiagnosticError("InvalidArgument", message, typeName));
        }

        return DiagnosticResult.Fail<T>(
            $"{tool} failed{pidHint}: {message}",
            new DiagnosticError("Internal", message, typeName));
    }

    private static bool HasEpermInChain(Exception ex)
    {
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is System.ComponentModel.Win32Exception w32 && w32.NativeErrorCode == 1 /* EPERM */)
            {
                return true;
            }
            if (cur.Message is string m
                && m.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
