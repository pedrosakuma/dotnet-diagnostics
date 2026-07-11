using System.ComponentModel;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Diagnostics;
using DotnetDiagnostics.Mcp.Security;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Tools;

internal static class DiagnosticToolHeapDump
{
    internal const string HeapSnapshotKind = HeapInspectionUseCases.HeapSnapshotKind;

    public static async Task<DiagnosticResult<DumpToolResult>> CollectProcessDump(
        IProcessDumper dumper,
        IProcessContextResolver resolver,
        IPrincipalAccessor principalAccessor,
        ILoggerFactory? loggerFactory = null,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Dump type: 'Mini', 'Triage', 'WithHeap' or 'Full'. Defaults to Mini.")] ProcessDumpType dumpType = ProcessDumpType.Mini,
        [Description("Optional sub-path under the artifact root (MCP_ARTIFACT_ROOT, default <temp>/dotnet-diagnostics-mcp). MUST be relative — absolute paths and '..' traversal are rejected (InvalidArtifactPath). Dump files are written with POSIX mode 0600.")] string? outputDirectory = null,
        [Description("Defense-in-depth confirmation flag — fallback for clients WITHOUT the MCP elicitation capability. Must be true to write a dump file when elicitation is unavailable; without it the tool returns a `confirmation_required` envelope describing what would have been written. Elicitation-capable clients are ALWAYS prompted natively and this flag is ignored for them (a human decline cannot be bypassed with confirm=true). See docs/authorization.md#per-call-confirmation")] bool confirm = false,
        [Description("Optional orchestrator investigation handle returned by attach_to_pod. When supplied, the orchestrator routes this diagnostic call through that attached Pod instead of inferring routing from the current MCP session binding.")]
        string? investigationHandleId = null,
        RequestContext<CallToolRequestParams>? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        var outcome = await DumpApprovalElicitation.RequestAsync(
            requestContext, processId, dumpType, outputDirectory, cancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case DumpApprovalOutcome.Approved:
                confirm = true;
                break;
            case DumpApprovalOutcome.Declined:
                loggerFactory?.CreateLogger("DotnetDiagnostics.Mcp.Tools.CollectProcessDump")?.LogInformation(
                    "collect_process_dump declined via elicitation. tokenName={TokenName} tool={Tool} reason={Reason} requestedPid={RequestedPid} dumpType={DumpType}",
                    principalAccessor.Current?.Name ?? "(none)",
                    "collect_process_dump",
                    "ApprovalDeclined",
                    processId,
                    dumpType);
                return DumpApprovalDeclinedEnvelope(processId, dumpType, outputDirectory);
            case DumpApprovalOutcome.Failed:
                loggerFactory?.CreateLogger("DotnetDiagnostics.Mcp.Tools.CollectProcessDump")?.LogWarning(
                    "collect_process_dump elicitation failed. tokenName={TokenName} tool={Tool} reason={Reason} requestedPid={RequestedPid} dumpType={DumpType}",
                    principalAccessor.Current?.Name ?? "(none)",
                    "collect_process_dump",
                    "ElicitationFailed",
                    processId,
                    dumpType);
                return DiagnosticResult.Fail<DumpToolResult>(
                    "collect_process_dump: the human-approval elicitation request failed (client error or timeout). " +
                    "No dump was written. Retry once the elicitation channel is healthy.",
                    new DiagnosticError(
                        "ElicitationFailed",
                        "The dump-approval elicitation round-trip failed; the dump was not written.",
                        nameof(requestContext)));
            case DumpApprovalOutcome.NotSupported:
            default:
                break;
        }

        return await ProcessDumpUseCases.CollectProcessDump(
            dumper,
            resolver,
            loggerFactory?.CreateLogger("DotnetDiagnostics.Mcp.Tools.CollectProcessDump"),
            principalAccessor.Current?.Name,
            processId,
            dumpType,
            outputDirectory,
            confirm,
            cancellationToken).ConfigureAwait(false);
    }

    public static Task<DiagnosticResult<DumpInspection>> InspectDump(
        IDumpInspector inspector,
        IDiagnosticHandleStore handles,
        SymbolServerAllowlist symbolServerAllowlist,
        IPrincipalAccessor principalAccessor,
        [Description("Absolute path to a previously-captured .dmp file. Required.")] string dumpFilePath,
        [Description("Number of types to return in each top-N list (bytes / instances). Defaults to 20.")] int topTypes = 20,
        [Description("When true, walks a short GC retention chain for the top retained types. Off by default — slower.")] bool includeRetentionPaths = false,
        [Description("Cap on retention-chain depth when retention paths are enabled. Defaults to 8.")] int retentionPathLimit = 8,
        [Description("When true, enumerate every loaded type's static reference fields ranked by directly-referenced object size — surfaces 'singleton that grew forever' leaks. Off by default; adds an extra pass over AppDomains × Modules × Types.")] bool includeStaticFields = false,
        [Description("When true, detect MulticastDelegate instances during the heap walk and group their invocation list by (target type, method) — surfaces 'event handler never unsubscribed' leaks. Cheap (folded into the existing heap pass).")] bool includeDelegateTargets = false,
        [Description("When true, hash every System.String during the heap walk and rank by aggregate retained bytes — surfaces missing string-interning. Cheap (folded into the existing heap pass) but allocates one hash per unique string.")] bool includeDuplicateStrings = false,
        [Description("Optional NT_SYMBOL_PATH-style search path reserved for symbol-resolving heap drilldowns. Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on `Diagnostics:SymbolServerAllowlist`.")] string? symbolPath = null,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
        => HeapInspectionUseCases.InspectDump(
            inspector,
            handles,
            symbolServerAllowlist,
            principalAccessor?.Current?.HasExplicitScope("symbols-remote") == true,
            dumpFilePath,
            topTypes,
            includeRetentionPaths,
            retentionPathLimit,
            includeStaticFields,
            includeDelegateTargets,
            includeDuplicateStrings,
            symbolPath,
            deprecation,
            cancellationToken);

    public static Task<DiagnosticResult<LiveHeapInspection>> InspectLiveHeap(
        IDumpInspector inspector,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        SymbolServerAllowlist symbolServerAllowlist,
        IPrincipalAccessor principalAccessor,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Number of types to return in each top-N list (bytes / instances). Defaults to 20.")] int topTypes = 20,
        [Description("When true, walks a short GC retention chain for the top retained types. Off by default — slower and lengthens the suspend window.")] bool includeRetentionPaths = false,
        [Description("Cap on retention-chain depth when retention paths are enabled. Defaults to 8.")] int retentionPathLimit = 8,
        [Description("When true, enumerate every loaded type's static reference fields ranked by directly-referenced object size — surfaces 'singleton that grew forever' leaks. Off by default; lengthens the suspend window.")] bool includeStaticFields = false,
        [Description("When true, detect MulticastDelegate instances during the heap walk and group their invocation list by (target type, method) — surfaces 'event handler never unsubscribed' leaks. Cheap (folded into the existing heap pass).")] bool includeDelegateTargets = false,
        [Description("When true, hash every System.String during the heap walk and rank by aggregate retained bytes — surfaces missing string-interning. Cheap (folded into the existing heap pass) but allocates one hash per unique string.")] bool includeDuplicateStrings = false,
        [Description("Optional NT_SYMBOL_PATH-style search path reserved for symbol-resolving heap drilldowns. Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on `Diagnostics:SymbolServerAllowlist`.")] string? symbolPath = null,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
        => HeapInspectionUseCases.InspectLiveHeap(
            inspector,
            handles,
            resolver,
            symbolServerAllowlist,
            principalAccessor?.Current?.HasExplicitScope("symbols-remote") == true,
            processId,
            topTypes,
            includeRetentionPaths,
            retentionPathLimit,
            includeStaticFields,
            includeDelegateTargets,
            includeDuplicateStrings,
            symbolPath,
            deprecation,
            cancellationToken);

    public static Task<DiagnosticResult<LiveHeapInspection>> InspectGcDump(
        IGcDumpHeapSnapshotCollector collector,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        int? processId = null,
        int topTypes = 20,
        TimeSpan? timeout = null,
        bool exportTrace = false,
        CancellationToken cancellationToken = default)
        => HeapInspectionUseCases.InspectGcDump(
            collector,
            handles,
            resolver,
            processId,
            topTypes,
            timeout,
            exportTrace,
            cancellationToken);

    public static async Task<DiagnosticResult<HeapSnapshotQueryResult>> QueryHeapSnapshot(
        IDiagnosticHandleStore handles,
        IDumpInspector inspector,
        SensitiveDataRedactor redactor,
        SensitiveValueGate sensitiveGate,
        IPrincipalAccessor principalAccessor,
        [Description("Snapshot handle returned by inspect_dump or inspect_live_heap.")] string handle,
        [Description("Which slice of the snapshot to return: 'top-types', 'retention-paths', 'roots-by-kind', 'finalizer-queue', 'fragmentation', 'static-fields', 'delegate-targets', 'duplicate-strings', 'gchandles', 'timers', 'alc', 'object', 'gcroot', 'objsize' or 'async'.")] string view = "top-types",
        [Description("Maximum entries to return for any ranked view ('top-types', 'finalizer-queue', 'fragmentation', 'static-fields', 'delegate-targets', 'duplicate-strings', 'async', 'timers', 'alc'). Ignored by 'roots-by-kind', 'gchandles', 'retention-paths', 'object', 'gcroot' and 'objsize'.")] int topN = 50,
        [Description("For view='top-types': ranking — 'bytes' (default) or 'instances'.")] string rankBy = "bytes",
        [Description("For view='retention-paths': case-insensitive substring matched against TypeFullName to narrow the returned chains.")] string? typeFullName = null,
        [Description("For view='object', 'gcroot' and 'objsize': managed object address (decimal or 0x-prefixed hex).") ] string? address = null,
        [Description("Opt-in to return raw string content / field value previews on the 'duplicate-strings' and 'object' views (issue #165 / H4). Defaults to false — those fields are returned as metadata-only placeholders unless the server enables `Diagnostics:AllowSensitiveHeapValues=true` AND the caller passes `includeSensitiveValues=true`. Any string surfaced even in that mode still runs through the SensitiveDataRedactor (Bearer/PEM/JWT/connection-string/AWS-key patterns).") ] bool includeSensitiveValues = false,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<HeapSnapshotQueryResult>(nameof(handle), "is required");
        if (topN < 1) return InvalidArg<HeapSnapshotQueryResult>(nameof(topN), "must be >= 1");

        var snapshot = handles.TryGet<HeapSnapshotArtifact>(handle);
        if (snapshot is null)
        {
            return DiagnosticResult.Fail<HeapSnapshotQueryResult>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError("HandleExpired", "Heap snapshot handles live ~10min and are invalidated when the target process exits.", handle),
                new NextActionHint("inspect_heap", "Re-attach and re-walk to issue a fresh handle.",
                    new Dictionary<string, object?> { ["processId"] = "<pid>" }));
        }

        var principalUnlocksSensitive = principalAccessor.Current?.HasExplicitScope("sensitive-heap-read") == true;
        var emitSensitive = sensitiveGate.ShouldEmit(includeSensitiveValues, principalUnlocksSensitive);

        if (emitSensitive && !principalUnlocksSensitive && sensitiveGate.IsAllowedByServer)
        {
            deprecation?.NotifySensitiveHeapValuesFlagBypass();
        }

        var normalizedView = view.Trim().ToLowerInvariant();
        var projection = HeapSnapshotQueryDispatcher.Dispatch(snapshot, handle, normalizedView, topN, rankBy, typeFullName);
        if (projection.Result is { } projected)
        {
            return projected;
        }

        switch (normalizedView)
        {
            case "duplicate-strings":
                return QueryDuplicateStrings(snapshot, handle, topN, redactor, emitSensitive);
            case "object":
            case "gcroot":
            case "objsize":
                if (string.IsNullOrWhiteSpace(address)) return InvalidArg<HeapSnapshotQueryResult>(nameof(address), $"is required for view='{normalizedView}'");
                if (!TryParseUnsignedHexOrInt(address, out var parsedAddress) || parsedAddress == 0)
                {
                    return InvalidArg<HeapSnapshotQueryResult>(nameof(address), "must be a non-zero address (decimal or 0x-prefixed hex)");
                }

                return await GuardAttachAsync(
                    "query_heap_snapshot",
                    snapshot.Origin == HeapSnapshotOrigin.Live ? snapshot.ProcessId : null,
                    async () => normalizedView switch
                    {
                        "object" => QueryObject(snapshot, handle, await inspector.InspectObjectAsync(snapshot, parsedAddress, cancellationToken).ConfigureAwait(false), redactor, emitSensitive),
                        "gcroot" => QueryGcRoot(snapshot, handle, await inspector.InspectGcRootAsync(snapshot, parsedAddress, cancellationToken).ConfigureAwait(false)),
                        _ => QueryObjectSize(snapshot, handle, await inspector.InspectObjectSizeAsync(snapshot, parsedAddress, cancellationToken).ConfigureAwait(false)),
                    },
                    cancellationToken).ConfigureAwait(false);
            default:
                return InvalidArg<HeapSnapshotQueryResult>(nameof(view), $"must be 'top-types', 'retention-paths', 'roots-by-kind', 'finalizer-queue', 'fragmentation', 'static-fields', 'delegate-targets', 'duplicate-strings', 'gchandles', 'timers', 'alc', 'object', 'gcroot', 'objsize' or 'async' (got '{view}')");
        }
    }

    private static DiagnosticResult<DumpToolResult> DumpApprovalDeclinedEnvelope(
        int? processId,
        ProcessDumpType dumpType,
        string? outputDirectory)
    {
        var preview = new DumpToolResult
        {
            Kind = DumpToolResultKinds.ConfirmationRequired,
            Message = "A human declined the dump-approval request (MCP elicitation). No dump was written. " +
                      "Do not retry this dump — pursue a non-destructive collector instead.",
            TargetPid = processId,
            DumpType = dumpType,
            OutputDirectory = outputDirectory,
        };
        var hint = new NextActionHint(
            "inspect_process",
            "Dump approval was declined by a human. Continue with non-destructive live collectors (counters, cpu sample, gc, exceptions) instead of re-requesting a dump.",
            null);
        var summary = processId is null
            ? $"approval_declined: a human declined writing a {dumpType} dump for the auto-selected .NET process. Nothing was written."
            : $"approval_declined: a human declined writing a {dumpType} dump for pid {processId}. Nothing was written.";
        return DiagnosticResult.Ok(preview, summary, hint);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryObject(
        HeapSnapshotArtifact snapshot,
        string handle,
        HeapObjectInspection inspection,
        SensitiveDataRedactor redactor,
        bool emitSensitive)
    {
        var origin = snapshot.Origin.ToString();
        var sanitized = SanitizeObjectInspection(inspection, redactor, emitSensitive);
        var summary = $"Returning object 0x{sanitized.Address:x} from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}) — `{sanitized.TypeFullName}` ({sanitized.Size:N0} bytes, {sanitized.Generation}/{sanitized.SegmentKind}).";
        if (!emitSensitive && (inspection.IsString || (inspection.Fields is { Count: > 0 })))
        {
            summary += " String/field value previews are redacted (issue #165 / H4); pass includeSensitiveValues=true on a server with Diagnostics:AllowSensitiveHeapValues=true to opt in.";
        }
        if (snapshot.Origin == HeapSnapshotOrigin.Live)
        {
            summary += " Live-object addresses can move after a GC; re-run inspect_live_heap if this address stops resolving.";
        }

        var result = new HeapSnapshotQueryResult(handle, "object", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            Address = sanitized.Address,
            ObjectDetails = sanitized,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static HeapObjectInspection SanitizeObjectInspection(HeapObjectInspection inspection, SensitiveDataRedactor redactor, bool emitSensitive)
    {
        IReadOnlyList<HeapObjectField>? fields = inspection.Fields;
        if (fields is { Count: > 0 })
        {
            var sanitizedFields = new List<HeapObjectField>(fields.Count);
            foreach (var f in fields)
            {
                var value = emitSensitive ? (redactor.Redact(f.Value) ?? f.Value) : SensitiveDataRedactor.MetadataOnlyPlaceholder;
                sanitizedFields.Add(new HeapObjectField(f.Name, f.TypeFullName, value)
                {
                    ObjectAddress = f.ObjectAddress,
                    ReferencedTypeFullName = f.ReferencedTypeFullName,
                });
            }
            fields = sanitizedFields;
        }

        IReadOnlyList<HeapArrayElement>? array = inspection.ArraySample;
        if (array is { Count: > 0 })
        {
            var sanitizedArray = new List<HeapArrayElement>(array.Count);
            foreach (var a in array)
            {
                var value = emitSensitive ? (redactor.Redact(a.Value) ?? a.Value) : SensitiveDataRedactor.MetadataOnlyPlaceholder;
                sanitizedArray.Add(new HeapArrayElement(a.Index, a.TypeFullName, value)
                {
                    ObjectAddress = a.ObjectAddress,
                    ReferencedTypeFullName = a.ReferencedTypeFullName,
                });
            }
            array = sanitizedArray;
        }

        string? stringValue = inspection.StringValue;
        if (inspection.IsString)
        {
            stringValue = emitSensitive ? redactor.Redact(stringValue) : SensitiveDataRedactor.MetadataOnlyPlaceholder;
        }

        return new HeapObjectInspection(inspection.Address, inspection.TypeFullName, inspection.Size, inspection.SegmentKind, inspection.Generation)
        {
            IsArray = inspection.IsArray,
            ArrayLength = inspection.ArrayLength,
            ArraySample = array,
            IsString = inspection.IsString,
            StringValue = stringValue,
            StringValueTruncated = inspection.StringValueTruncated,
            Fields = fields,
            Warnings = inspection.Warnings,
        };
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryGcRoot(
        HeapSnapshotArtifact snapshot,
        string handle,
        HeapGcRootInspection inspection)
    {
        var origin = snapshot.Origin.ToString();
        var summary = $"Returning GC-root chain for 0x{inspection.Address:x} from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}) — `{inspection.TypeFullName}` with {inspection.Chain.Count:N0} frame(s).";
        if (inspection.Truncated)
        {
            summary += " Chain is truncated by the BFS/depth safety caps.";
        }

        var result = new HeapSnapshotQueryResult(handle, "gcroot", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            Address = inspection.Address,
            GcRoot = inspection,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryObjectSize(
        HeapSnapshotArtifact snapshot,
        string handle,
        HeapObjectSizeInspection inspection)
    {
        var origin = snapshot.Origin.ToString();
        var summary = $"Returning object graph size for 0x{inspection.Address:x} from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}) — `{inspection.TypeFullName}` retains {inspection.RetainedBytes:N0} bytes across {inspection.ObjectCount:N0} object(s).";
        if (inspection.Truncated)
        {
            summary += " Result is truncated by the safety cap and is therefore a lower bound.";
        }

        var result = new HeapSnapshotQueryResult(handle, "objsize", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            Address = inspection.Address,
            ObjectSize = inspection,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryDuplicateStrings(
        HeapSnapshotArtifact snapshot,
        string handle,
        int topN,
        SensitiveDataRedactor redactor,
        bool emitSensitive)
    {
        var origin = snapshot.Origin.ToString();
        if (snapshot.DuplicateStrings is null)
        {
            return DiagnosticResult.Fail<HeapSnapshotQueryResult>(
                $"Snapshot '{handle}' was captured without duplicate-string aggregation.",
                new DiagnosticError("ViewNotCaptured", "Re-run inspect_dump / inspect_live_heap with includeDuplicateStrings=true.", handle));
        }

        var slice = snapshot.DuplicateStrings.Take(topN).Select(s =>
        {
            string preview;
            if (emitSensitive)
            {
                preview = redactor.Redact(s.Preview) ?? s.Preview;
            }
            else
            {
                preview = SensitiveDataRedactor.MetadataOnlyPlaceholder;
            }
            return new DuplicateStringStat(preview, s.StringLength, s.InstanceCount, s.TotalBytes, s.PreviewTruncated);
        }).ToArray();
        var summary = slice.Length == 0
            ? $"Snapshot '{handle}' has no duplicated System.String contents."
            : (emitSensitive
                ? $"Returning {slice.Length} duplicated string(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Top waste: {slice[0].InstanceCount:N0} copies, {slice[0].TotalBytes:N0} bytes (length {slice[0].StringLength}). Previews pass through the SensitiveDataRedactor (Bearer/PEM/JWT/conn-string patterns) — consider string.Intern() / a cache for the hottest entries."
                : $"Returning {slice.Length} duplicated string(s) (metadata-only — string previews redacted per issue #165 / H4) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Top waste: {slice[0].InstanceCount:N0} copies, {slice[0].TotalBytes:N0} bytes (length {slice[0].StringLength}). Pass includeSensitiveValues=true on a server with Diagnostics:AllowSensitiveHeapValues=true to reveal previews.");
        var result = new HeapSnapshotQueryResult(handle, "duplicate-strings", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            DuplicateStrings = slice,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("inspect_process", "Re-issue with valid arguments. See tool schema for ranges and defaults."));

    private static Task<DiagnosticResult<T>> GuardAttachAsync<T>(
        string tool,
        int? processId,
        Func<Task<DiagnosticResult<T>>> body,
        CancellationToken cancellationToken)
        => AttachGuard.GuardAttachAsync(tool, processId, body, cancellationToken);

    private static bool TryParseUnsignedHexOrInt(string value, out ulong result)
    {
        var s = value.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("0X", StringComparison.Ordinal))
        {
            return ulong.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out result);
        }
        return ulong.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out result);
    }
}
