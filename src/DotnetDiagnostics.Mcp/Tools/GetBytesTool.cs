using System.ComponentModel;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Bytes;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Safety;
using DotnetDiagnostics.Core.Tools.Dispatch;
using DotnetDiagnostics.Mcp.Security;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Tools;

/// <summary>
/// Canonical byte-fetch surface. It dispatches module, dump, trace, inventory, and
/// deletion operations on a <c>kind</c> discriminator while preserving the shared
/// <see cref="ByteFetchEnvelope"/> and chunking contract used by downstream consumers.
/// </summary>
/// <remarks>
/// <para>Implementation delegates to the existing internal
/// <see cref="DiagnosticTools.GetModuleBytes"/> and
/// <see cref="DiagnosticTools.GetDumpBytes"/> entrypoints. Those methods are implementation
/// details rather than registered MCP tools; compatibility is asserted by
/// <c>GetBytesCompatibilityTests</c>.</para>
/// <para>Migration history: removed get_module_bytes/get_dump_bytes aliases map to kind=module/dump.</para>
/// </remarks>
[McpServerToolType]
public sealed class GetBytesTool
{
    internal const string ToolName = DiagnosticOperationCatalog.GetBytes;
    internal const string KindModule = DiagnosticOperationCatalog.ByteKinds.Module;
    internal const string KindDump = DiagnosticOperationCatalog.ByteKinds.Dump;
    internal const string KindTrace = DiagnosticOperationCatalog.ByteKinds.Trace;
    internal const string KindList = DiagnosticOperationCatalog.ByteKinds.List;
    internal const string KindDelete = DiagnosticOperationCatalog.ByteKinds.Delete;
    internal const string KindCaptures = DiagnosticOperationCatalog.ByteKinds.Captures;

    internal const string DeleteArtifactScope = ToolInvocationScopeResolver.DeleteArtifactScope;

    internal static readonly IReadOnlyList<string> AllowedKinds =
        DiagnosticOperationCatalog.ByteKinds.All;

    [RequireScope("module-bytes-read")]
    [McpServerTool(
        Name = ToolName,
        Title = "Fetch bytes; manage artifacts and durable captures",
        Destructive = true,
        ReadOnly = false,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Fetch PE/PDB, dump or .nettrace chunks; manage artifacts and captures. Paths remain under MCP_ARTIFACT_ROOT. " +
        "maxBytes: 4 MiB default, 16 MiB cap; artifact cap 256 MiB. " +
        "captures supports lifecycle and bounded portable transfers via captureAction/captureTransfer; no SQL or client-selected roots. " +
        "Requires literal module-bytes-read; captures also investigation-export; deletion literal delete-artifact. Raw TTL excludes captures.")]
    public static async Task<DiagnosticResult<object>> GetBytes(
        IModuleByteSource moduleByteSource,
        IDumpByteSource dumpByteSource,
        IProcessContextResolver resolver,
        IPrincipalAccessor principalAccessor,
        IArtifactLifecycle artifactLifecycle,
        [Description("module|dump|trace: byte streaming; list|delete: raw artifact lifecycle; captures: private durable capture lifecycle, selected by captureAction.")] string kind,
        [Description("Module-only required MVID, GUID 'D' format.")] string? moduleVersionId = null,
        [Description("Module-only artifact: 'pe' (default) or 'pdb'.")] string asset = "pe",
        [Description("Dump-only path. Relative or absolute paths must resolve under MCP_ARTIFACT_ROOT.")] string? dumpFilePath = null,
        [Description("Trace-only path. Relative or absolute paths must resolve under MCP_ARTIFACT_ROOT.")] string? traceFilePath = null,
        [Description("Delete-only path relative to MCP_ARTIFACT_ROOT; traversal/absolute/symlink escapes are rejected.")] string? artifactPath = null,
        [Description("Chunk byte offset; default 0.")] long offset = 0,
        [Description("Response byte limit: default 4 MiB, cap 16 MiB.")] int maxBytes = FileChunkReader.DefaultChunkBytes,
        [Description("Module-only target PID. Omit to auto-select the lone visible .NET process.")] int? processId = null,
        [Description("attach_to_pod handle; routes through its attached Pod instead of the current MCP session binding.")]
        string? investigationHandleId = null,
        ILoggerFactory? loggerFactory = null,
        [Description("captures: list|describe|delete|recover; export-start|download-chunk|import-start|upload-chunk|import-commit|transfer-status|import-result|transfer-cancel.")]
        string captureAction = "list",
        [Description("Opaque durable capture ID for describe/delete/recover. No paths or SQL accepted.")]
        string? captureId = null,
        [Description("kind='captures', action='list': maximum catalog entries, 1..100 (default 25).")]
        int capturePageSize = 25,
        [Description("kind='captures', action='list': continuation from nextAfterCaptureId.")]
        string? afterCaptureId = null,
        DurableCaptureTools? durableCaptures = null,
        [Description("Portable action fields: operationId/requestedUtc, entries, transferId, offset/count, base64/sha256, archiveBytes/archiveSha256, afterEntry/pageSize. See tool reference.")]
        JsonElement? captureTransfer = null,
        PortableCaptureTools? portableCaptures = null,
        McpServer? server = null,
        CancellationToken cancellationToken = default)
    {
        if (!ToolDispatchGuards.TryValidateDiscriminator<object>(
                kind,
                AllowedKinds,
                nameof(kind),
                out var canonicalKind,
                out var dispatchFailure))
        {
            return dispatchFailure!;
        }

        return canonicalKind switch
        {
            KindCaptures when captureAction.Contains('-', StringComparison.Ordinal) => portableCaptures is null
                ? DurableCaptureTools.Unavailable<object>()
                : await portableCaptures.InvokeAsync(principalAccessor, server, captureAction, captureTransfer, cancellationToken).ConfigureAwait(false),
            KindCaptures => durableCaptures is null
                ? DurableCaptureTools.Unavailable<object>()
                : await durableCaptures.LifecycleAsync(
                    principalAccessor, captureAction, captureId, capturePageSize, afterCaptureId,
                    cancellationToken).ConfigureAwait(false),
            KindModule => AsObject(await DiagnosticTools.GetModuleBytes(
                moduleByteSource,
                resolver,
                principalAccessor,
                moduleVersionId ?? string.Empty,
                asset,
                offset,
                maxBytes,
                processId,
                loggerFactory,
                cancellationToken).ConfigureAwait(false)),
            KindDump => AsObject(await DiagnosticTools.GetDumpBytes(
                dumpByteSource,
                principalAccessor,
                dumpFilePath ?? string.Empty,
                offset,
                maxBytes,
                loggerFactory,
                cancellationToken).ConfigureAwait(false)),
            KindTrace => AsObject(await DiagnosticTools.GetTraceBytes(
                dumpByteSource,
                principalAccessor,
                traceFilePath ?? string.Empty,
                offset,
                maxBytes,
                loggerFactory,
                cancellationToken).ConfigureAwait(false)),
            KindList => ListArtifacts(artifactLifecycle),
            KindDelete => DeleteArtifact(artifactLifecycle, principalAccessor, artifactPath, loggerFactory),
            _ => throw new InvalidOperationException(
                $"DiscriminatorDispatch returned an unexpected canonical kind '{canonicalKind}'."),
        };
    }

    private static DiagnosticResult<object> AsObject<T>(DiagnosticResult<T> source) where T : class
        => new(source.Summary, source.Hints, source.Error)
        {
            Data = source.Data,
            Handle = source.Handle,
            HandleExpiresAt = source.HandleExpiresAt,
            ResolvedProcess = source.ResolvedProcess,
        };

    private static DiagnosticResult<object> ListArtifacts(IArtifactLifecycle lifecycle)
    {
        var artifacts = lifecycle.List();
        var total = artifacts.Sum(a => a.SizeBytes);
        var envelope = new ArtifactListingEnvelope
        {
            Root = lifecycle.Root,
            Count = artifacts.Count,
            TotalSizeBytes = total,
            Artifacts = artifacts,
        };
        return DiagnosticResult.Ok<object>(
            envelope,
            $"{artifacts.Count} artifact(s) under {lifecycle.Root} ({total} bytes).",
            new NextActionHint("get_bytes",
                "Delete an aged artifact with kind='delete' (needs 'delete-artifact' scope), or stream one with kind='dump'/'trace'."));
    }

    private static DiagnosticResult<object> DeleteArtifact(
        IArtifactLifecycle lifecycle,
        IPrincipalAccessor principalAccessor,
        string? artifactPath,
        ILoggerFactory? loggerFactory)
    {
        var logger = loggerFactory?.CreateLogger("DotnetDiagnostics.Mcp.Tools.GetBytes.Delete");
        if (string.IsNullOrWhiteSpace(artifactPath))
        {
            return DiagnosticResult.Fail<object>(
                "Argument 'artifactPath' is required when kind='delete'.",
                new DiagnosticError("InvalidArgument", "Argument 'artifactPath' is required when kind='delete'.", nameof(artifactPath)));
        }

        if (!ToolDispatchGuards.RequireExplicitScope(
                principalAccessor.Current,
                DeleteArtifactScope,
                () => $"get_bytes(delete) requires the literal scope '{DeleteArtifactScope}'. Root or wildcard tokens do not auto-grant this scope.",
                out DiagnosticResult<object>? scopeFailure,
                new NextActionHint("get_bytes", $"Retry with a bearer token that explicitly includes '{DeleteArtifactScope}'."), 
                errorTarget: principalAccessor.Current?.Name,
                errorDetailFactory: () => $"Grant the bearer principal the literal scope '{DeleteArtifactScope}'."))
        {
            logger?.LogWarning("get_bytes(delete) denied: explicit '{Scope}' scope required. tokenName={TokenName} path={Path}",
                DeleteArtifactScope, principalAccessor.Current?.Name, artifactPath);
            return scopeFailure!;
        }

        try
        {
            var deleted = lifecycle.Delete(artifactPath);
            var envelope = new ArtifactDeletionEnvelope { Root = lifecycle.Root, Deleted = deleted };
            return DiagnosticResult.Ok<object>(envelope, $"Deleted artifact '{deleted.RelativePath}' ({deleted.SizeBytes} bytes).");
        }
        catch (ArtifactPathException ex)
        {
            return DiagnosticResult.Fail<object>(
                $"get_bytes(delete) rejected the path: {ex.Message}",
                new DiagnosticError("InvalidArtifactPath", ex.Message, ex.ParameterName),
                new NextActionHint("get_bytes", "Supply a path relative to the artifact root; '..', absolute, and symlink escapes are rejected."));
        }
        catch (FileNotFoundException ex)
        {
            return DiagnosticResult.Fail<object>(
                $"get_bytes(delete) found no artifact at '{artifactPath}'.",
                new DiagnosticError("ArtifactNotFound", ex.Message, artifactPath),
                new NextActionHint("get_bytes", "List artifacts with kind='list' to confirm the relative path."));
        }
    }
}
