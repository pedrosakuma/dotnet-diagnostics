using System.ComponentModel;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.JitCapture;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Signals;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Mcp.Security;
using static DotnetDiagnostics.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnostics.Mcp.Tools;

internal static class DiagnosticToolThreadingAndJit
{
    private static readonly TimeSpan ThreadSnapshotHandleTtl = TimeSpan.FromMinutes(10);
    internal const string ThreadSnapshotKind = "thread-snapshot";

    public static async Task<DiagnosticResult<ThreadSnapshotQueryResult>> CollectThreadSnapshot(
        IThreadSnapshotInspector inspector,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        SymbolServerAllowlist symbolServerAllowlist,
        IPrincipalAccessor principalAccessor,
        [Description("Operating system process id of the target .NET process. Mutually exclusive with dumpFilePath. Optional — when both processId and dumpFilePath are null/empty the server auto-selects a live .NET process.")] int? processId = null,
        [Description("Absolute path to a previously-captured .dmp file. Mutually exclusive with processId.")] string? dumpFilePath = null,
        [Description("Maximum stack frames captured per thread. Defaults to 64.")] int maxFramesPerThread = 64,
        [Description("Include runtime frames (PInvoke trampolines, etc.) without an associated managed method. Off by default.")] bool includeRuntimeFrames = false,
        [Description("Include pure native frames where ClrMD cannot resolve a method. Off by default.")] bool includeNativeFrames = false,
        [Description("Optional NT_SYMBOL_PATH-style search path forwarded to symbol-resolving backends. Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on `Diagnostics:SymbolServerAllowlist`.")] string? symbolPath = null,
        [Description("Verbosity (summary|detail|raw). Default 'summary' returns only the top-3 blocked threads inline and drops the SyncBlock lock-graph (use query_snapshot(view='lock-graph') for the full graph). 'detail' returns the historical top-25 threads + top-25 locks. 'raw' is equivalent to detail. The full snapshot is always retained behind the issued handle.")]
        SamplingDepth depth = SamplingDepth.Summary,
        [Description("Optional orchestrator investigation handle returned by attach_to_pod. When supplied, the orchestrator routes this diagnostic call through that attached Pod instead of inferring routing from the current MCP session binding.")]
        string? investigationHandleId = null,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
    {
        var hasExplicitPid = processId.HasValue && processId.Value != 0;
        var hasDump = !string.IsNullOrWhiteSpace(dumpFilePath);
        if (hasExplicitPid && hasDump)
        {
            return InvalidArg<ThreadSnapshotQueryResult>(nameof(dumpFilePath), "processId and dumpFilePath are mutually exclusive");
        }
        if (maxFramesPerThread < 1) return InvalidArg<ThreadSnapshotQueryResult>(nameof(maxFramesPerThread), "must be >= 1");
        if (maxFramesPerThread > ClrMdThreadSnapshotInspector.MaxFramesPerThreadHardCap) return InvalidArg<ThreadSnapshotQueryResult>(nameof(maxFramesPerThread), $"must be <= {ClrMdThreadSnapshotInspector.MaxFramesPerThreadHardCap} (bounds the live-attach suspend window)");

        var symbolDenial = ValidateSymbolPath<ThreadSnapshotQueryResult>(symbolServerAllowlist, symbolPath, principalAccessor, deprecation);
        if (symbolDenial is not null) return symbolDenial;

        int livePid = 0;
        ProcessContext? liveCtx = null;
        if (!hasDump)
        {
            var resolved = await ResolveContextAsync<ThreadSnapshotQueryResult>(resolver, processId, cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null) return resolved.Failure;
            livePid = resolved.ProcessId;
            liveCtx = resolved.Context;
        }

        return await GuardAttachAsync("collect_thread_snapshot", hasDump ? (int?)null : livePid, async () =>
        {
            var opts = new ThreadSnapshotOptions(maxFramesPerThread, includeRuntimeFrames, includeNativeFrames, symbolPath);
            ThreadSnapshotArtifact snapshot;
            if (hasDump)
            {
                snapshot = await inspector.InspectDumpAsync(dumpFilePath!, opts, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                snapshot = await inspector.InspectLiveAsync(livePid, opts, cancellationToken).ConfigureAwait(false);
            }

            var handle = handles.Register(
                snapshot.ProcessId,
                ThreadSnapshotKind,
                snapshot,
                ThreadSnapshotHandleTtl,
                evictWhenProcessExits: false,
                origin: snapshot.Origin == ThreadSnapshotOrigin.Live ? HandleOrigin.Live : HandleOrigin.Dump);
            var origin = snapshot.Origin.ToString().ToLowerInvariant();
            var blocked = snapshot.Threads.Count(t => t.IsLikelyBlocked);
            var contended = snapshot.Locks.Count(l => l.IsContended);
            var signals = ThreadWaitSignals.Detect(snapshot, handle.Id);

            ThreadSnapshotQueryResult summaryView;
            string summary;
            if (depth == SamplingDepth.Summary)
            {
                var topBlocked = snapshot.Threads
                    .OrderByDescending(t => t.IsLikelyBlocked)
                    .ThenByDescending(t => t.LockCount)
                    .Take(3)
                    .ToArray();
                summaryView = new ThreadSnapshotQueryResult(handle.Id, "top-blocked", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
                {
                    Threads = topBlocked,
                    Locks = Array.Empty<MonitorLockState>(),
                };
                var droppedThreads = snapshot.Threads.Count - topBlocked.Length;
                summary = $"{origin} thread snapshot of pid {snapshot.ProcessId}: {snapshot.Threads.Count} thread(s) ({blocked} likely blocked), {snapshot.Locks.Count} SyncBlock(s) ({contended} contended). Showing top {topBlocked.Length} blocked inline (dropped {droppedThreads} thread(s) and {snapshot.Locks.Count} lock(s); handle has all). Walk {snapshot.WalkDuration.TotalMilliseconds:N0} ms. Handle `{handle.Id}` (~10 min). Use query_snapshot(handle=\"{handle.Id}\", view=top-blocked|threads-summary|stack|lock-graph|deadlocks|unique-stacks|async-stalls|threadpool).";
            }
            else
            {
                summaryView = new ThreadSnapshotQueryResult(handle.Id, "threads-summary", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
                {
                    Threads = snapshot.Threads.Take(25).ToArray(),
                    Locks = snapshot.Locks.Take(25).ToArray(),
                };
                summary = $"{origin} thread snapshot of pid {snapshot.ProcessId}: {snapshot.Threads.Count} thread(s) ({blocked} likely blocked), {snapshot.Locks.Count} SyncBlock(s) ({contended} contended). Walk {snapshot.WalkDuration.TotalMilliseconds:N0} ms. Handle `{handle.Id}` (~10 min). Use query_snapshot(handle=\"{handle.Id}\", view=top-blocked|threads-summary|stack|lock-graph|deadlocks|unique-stacks|async-stalls|threadpool).";
            }

            if (snapshot.SnapshotKind is not "exact")
            {
                summary += $" SnapshotKind={snapshot.SnapshotKind}";
                if (snapshot.WindowSeconds is int w)
                {
                    summary += $" over {w}s window";
                }
                summary += ".";
            }
            if (snapshot.Warnings is { Count: > 0 })
            {
                summary += $" Caveats: {string.Join(" ", snapshot.Warnings.Take(3))}";
            }

            var hint = contended > 0
                ? new NextActionHint("query_snapshot",
                    "Check the captured lock graph for wait-for cycles before drilling into individual stacks.",
                    new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "deadlocks" })
                : blocked > 0
                    ? new NextActionHint("query_snapshot",
                        "Drill into the top blocked threads.",
                        new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "top-blocked" })
                    : null;

            var result = hint is null
                ? DiagnosticResult.Ok(summaryView, summary)
                : DiagnosticResult.Ok(summaryView, summary, hint);
            return WithContext(result with { Signals = signals.Count > 0 ? signals : null }, liveCtx);
        }, cancellationToken, hasDump
            ? null
            : new Dictionary<string, object?>
            {
                ["processId"] = livePid,
                ["maxFramesPerThread"] = maxFramesPerThread,
                ["includeRuntimeFrames"] = includeRuntimeFrames,
                ["includeNativeFrames"] = includeNativeFrames,
                ["symbolPath"] = symbolPath,
                ["depth"] = depth,
                ["investigationHandleId"] = investigationHandleId,
            }).ConfigureAwait(false);
    }

    public static async Task<DiagnosticResult<CapturedMethodBytes>> CaptureMethodBytes(
        IJitMethodCapturer capturer,
        IProcessContextResolver resolver,
        [Description("PE module MVID (D format, e.g. '6f5c9bf0-1e0b-4f3b-9a8e-…') of the assembly that declares the method. Required.")] string moduleVersionId,
        [Description("IL method-def metadata token (table 0x06). Accepts decimal or hex (0x06000142). Required.")] string metadataToken,
        [Description("Operating system process id of the target .NET process. Mutually exclusive with dumpFilePath. Optional — when both processId and dumpFilePath are null/empty the server auto-selects a live .NET process.")] int? processId = null,
        [Description("Absolute path to a previously-captured .dmp file. Mutually exclusive with processId.")] string? dumpFilePath = null,
        [Description("Optional fast-path: a code address already observed for this method (e.g. MethodCodeStart from MethodLoad_V2). Hex (with or without 0x prefix) or decimal. Mismatches with (moduleVersionId, metadataToken) surface as a warning, not a hard error.")] string? codeAddress = null,
        [Description("Optional tier label echoed back on the result (e.g. 'Tier0', 'Tier1', 'Tier1OSR'). ClrMD does not expose the JIT OptimizationTier directly; this field is informational. The authoritative MethodCompilationType (None/Jit/Ngen) is always returned.")] string? tier = null,
        [Description("Optional sub-path under the artifact root (MCP_ARTIFACT_ROOT, default <temp>/dotnet-diagnostics-mcp). Defaults to 'method-bytes/{pid}'. MUST be relative — absolute paths and '..' traversal are rejected (InvalidArtifactPath). .bin files are written with POSIX mode 0600.")] string? outputDirectory = null,
        [Description("Optional orchestrator investigation handle returned by attach_to_pod. When supplied, the orchestrator routes this diagnostic call through that attached Pod instead of inferring routing from the current MCP session binding.")]
        string? investigationHandleId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(moduleVersionId)) return InvalidArg<CapturedMethodBytes>(nameof(moduleVersionId), "is required");
        if (!Guid.TryParse(moduleVersionId, out var mvid)) return InvalidArg<CapturedMethodBytes>(nameof(moduleVersionId), "must be a GUID in 'D' format");
        if (string.IsNullOrWhiteSpace(metadataToken)) return InvalidArg<CapturedMethodBytes>(nameof(metadataToken), "is required");
        if (!TryParseHexOrInt(metadataToken, out var tokenValue) || tokenValue <= 0)
        {
            return InvalidArg<CapturedMethodBytes>(nameof(metadataToken), "must be a positive integer (decimal or 0x-prefixed hex)");
        }

        ulong? codeAddressValue = null;
        if (!string.IsNullOrWhiteSpace(codeAddress))
        {
            if (!TryParseUnsignedHexOrInt(codeAddress, out var addr) || addr == 0)
            {
                return InvalidArg<CapturedMethodBytes>(nameof(codeAddress), "must be a non-zero address (decimal or 0x-prefixed hex)");
            }
            codeAddressValue = addr;
        }

        var hasExplicitPid = processId.HasValue && processId.Value != 0;
        var hasDump = !string.IsNullOrWhiteSpace(dumpFilePath);
        if (hasExplicitPid && hasDump)
        {
            return InvalidArg<CapturedMethodBytes>(nameof(dumpFilePath), "processId and dumpFilePath are mutually exclusive");
        }

        int livePid = 0;
        ProcessContext? liveCtx = null;
        if (!hasDump)
        {
            var resolved = await ResolveContextAsync<CapturedMethodBytes>(resolver, processId, cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null) return resolved.Failure;
            livePid = resolved.ProcessId;
            liveCtx = resolved.Context;
        }

        var request = new MethodCaptureRequest(
            ModuleVersionId: mvid,
            MetadataToken: tokenValue,
            CodeAddress: codeAddressValue,
            Tier: tier,
            OutputDirectory: outputDirectory);

        return await GuardAttachAsync("capture_method_bytes", hasDump ? (int?)null : livePid, async () =>
        {
            var captured = hasDump
                ? await capturer.CaptureFromDumpAsync(dumpFilePath!, request, cancellationToken).ConfigureAwait(false)
                : await capturer.CaptureLiveAsync(livePid, request, cancellationToken).ConfigureAwait(false);

            var summary = BuildCaptureSummary(captured);
            var hint = BuildDisassembleHint(captured);
            var result = hint is null
                ? DiagnosticResult.Ok(captured, summary)
                : DiagnosticResult.Ok(captured, summary, hint);
            return WithContext(result, liveCtx);
        }, cancellationToken, hasDump
            ? null
            : BuildCaptureMethodBytesRetryArguments(
                moduleVersionId,
                metadataToken,
                livePid,
                codeAddress,
                tier,
                outputDirectory,
                investigationHandleId)).ConfigureAwait(false);
    }

    public static DiagnosticResult<ThreadSnapshotQueryResult> QueryThreadSnapshot(
        IDiagnosticHandleStore handles,
        [Description("Snapshot handle returned by collect_thread_snapshot.")] string handle,
        [Description("Which slice to return: 'threads-summary', 'stack', 'lock-graph', 'deadlocks', 'top-blocked', 'unique-stacks', 'async-stalls', 'wait-chains' or 'threadpool'.")] string view = "top-blocked",
        [Description("For view='stack': thread id key to return frames for. CoreCLR snapshots use ManagedThreadId; linux-native-stack snapshots use OSThreadId (TID). Ignored by other views.")] int? threadId = null,
        [Description("Maximum entries returned by ranked-list views ('threads-summary', 'top-blocked', 'lock-graph', 'unique-stacks') or the number of deadlock cycles returned by 'deadlocks'. Defaults to 50.")] int topN = 50,
        [Description("For view='unique-stacks': number of top frames folded into the signature hash. Defaults to 20. Ignored by other views.")] int framesToHash = ThreadSnapshotUniqueStackGrouper.DefaultFramesToHash,
        [Description("For view='unique-stacks': drop groups with fewer than this many threads. Defaults to 1. Ignored by other views.")] int minCount = 1)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<ThreadSnapshotQueryResult>(nameof(handle), "is required");
        if (topN < 1) return InvalidArg<ThreadSnapshotQueryResult>(nameof(topN), "must be >= 1");

        var snapshot = handles.TryGet<ThreadSnapshotArtifact>(handle);
        if (snapshot is null)
        {
            return DiagnosticResult.Fail<ThreadSnapshotQueryResult>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError("HandleExpired", "Thread snapshot handles live ~10min and expire by TTL.", handle),
                new NextActionHint("collect_thread_snapshot", "Re-capture to issue a fresh handle."));
        }

        return ThreadSnapshotQueryDispatcher.Dispatch(snapshot, handle, view, threadId, topN, framesToHash, minCount);
    }

    private static string BuildCaptureSummary(CapturedMethodBytes captured)
    {
        var typeFqn = captured.Method.TypeFullName ?? "<unknown type>";
        var methodName = captured.Method.MethodName;
        var origin = captured.Origin.ToString().ToLowerInvariant();
        if (captured.Regions.Count == 0)
        {
            return $"{origin} capture of {typeFqn}.{methodName} on pid {captured.ProcessId}: no JIT-emitted code present (method not yet JITted, abstract/extern, or ReadyToRun-only). " +
                   "Check warnings on the payload for details.";
        }
        var sizes = string.Join(", ", captured.Regions.Select(r => $"{r.Region}={r.Size:N0} B"));
        var first = captured.Regions[0];
        return $"{origin} capture of {typeFqn}.{methodName} on pid {captured.ProcessId} ({captured.Architecture}, {first.CompilationType ?? "?"}): " +
               $"{sizes}. Wrote {captured.Regions.Count} file(s) under {captured.OutputDirectory}.";
    }

    private static NextActionHint? BuildDisassembleHint(CapturedMethodBytes captured)
    {
        if (captured.Regions.Count == 0) return null;
        var hot = captured.Regions.FirstOrDefault(r => r.Region == "Hot") ?? captured.Regions[0];
        return new NextActionHint(
            "dotnet-native-mcp.disassemble",
            $"Disassemble the captured {hot.Region} region. The file is a raw blob with no PE/ELF/Mach-O header — pass rawBlob=true so the disassembler skips header validation.",
            new Dictionary<string, object?>
            {
                ["imagePath"] = hot.FilePath,
                ["rawBlob"] = true,
                ["rva"] = 0,
                ["size"] = hot.Size,
                ["architecture"] = hot.Architecture,
                ["baseAddress"] = hot.BaseAddress,
            });
    }

    private static bool TryParseHexOrInt(string value, out int result)
    {
        var s = value.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("0X", StringComparison.Ordinal))
        {
            return int.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out result);
        }
        return int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParseUnsignedHexOrInt(string value, out ulong result)
    {
        var s = value.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("0X", StringComparison.Ordinal))
        {
            return ulong.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out result);
        }
        return ulong.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("inspect_process", "Re-issue with valid arguments. See tool schema for ranges and defaults."));

    private static DiagnosticResult<T>? ValidateSymbolPath<T>(
        SymbolServerAllowlist allowlist,
        string? symbolPath,
        IPrincipalAccessor? principalAccessor = null,
        LegacyDiagnosticsFlagDeprecation? deprecation = null)
        => SymbolPathValidation.Validate<T>(
            allowlist,
            symbolPath,
            principalAccessor?.Current?.HasExplicitScope("symbols-remote") == true,
            deprecation);

    internal static IReadOnlyDictionary<string, object?> BuildCaptureMethodBytesRetryArguments(
        string moduleVersionId,
        string metadataToken,
        int processId,
        string? codeAddress,
        string? tier,
        string? outputDirectory,
        string? investigationHandleId)
        => new Dictionary<string, object?>
        {
            ["moduleVersionId"] = moduleVersionId,
            ["metadataToken"] = metadataToken,
            ["processId"] = processId,
            ["codeAddress"] = codeAddress,
            ["tier"] = tier,
            ["outputDirectory"] = outputDirectory,
            ["investigationHandleId"] = investigationHandleId,
        };

    private static Task<DiagnosticResult<T>> GuardAttachAsync<T>(
        string tool,
        int? processId,
        Func<Task<DiagnosticResult<T>>> body,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? retryArguments = null)
        => AttachGuard.GuardAttachAsync(
            tool,
            processId,
            body,
            cancellationToken,
            retryArguments: retryArguments);
}
