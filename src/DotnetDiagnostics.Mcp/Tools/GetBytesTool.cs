using System.ComponentModel;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Bytes;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Tools.Dispatch;
using DotnetDiagnostics.Mcp.Security;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Tools;

/// <summary>
/// Successor for <c>get_module_bytes</c> and <c>get_dump_bytes</c>: a single
/// byte-fetch surface that dispatches on a <c>kind</c> discriminator. Both legacy tools
/// share the same <see cref="ByteFetchEnvelope"/>, chunking contract, and downstream
/// consumers (dotnet-assembly-mcp, dotnet-native-mcp, orchestrator proxy), so merging them
/// reduces the visible MCP surface without changing the wire shape.
/// </summary>
/// <remarks>
/// <para>Implementation delegates to the existing <see cref="DiagnosticTools.GetModuleBytes"/>
/// and <see cref="DiagnosticTools.GetDumpBytes"/> entrypoints so the legacy tools and
/// this successor stay byte-for-byte compatible (asserted by
/// <c>GetBytesCompatibilityTests</c>). When the legacy tools are eventually removed, the
/// implementations will move here and the call direction will flip.</para>
/// </remarks>
[McpServerToolType]
public sealed class GetBytesTool
{
    internal const string KindModule = "module";
    internal const string KindDump = "dump";
    internal const string KindTrace = "trace";
    internal const string KindList = "list";
    internal const string KindDelete = "delete";

    internal const string DeleteArtifactScope = "delete-artifact";

    internal static readonly IReadOnlyList<string> AllowedKinds = new[] { KindModule, KindDump, KindTrace, KindList, KindDelete };

    [RequireScope("module-bytes-read")]
    [McpServerTool(
        Name = "get_bytes",
        Title = "Fetch module/dump/trace bytes; list or delete artifacts",
        Destructive = true,
        ReadOnly = false,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Streams a managed module artifact (PE or PDB), a dump file, or a raw trace (.nettrace) as repeated CallTool chunks so sibling MCPs can materialise pod-local binaries through the orchestrator proxy; also lists and deletes artifacts under MCP_ARTIFACT_ROOT for lifecycle hygiene. " +
        "Dispatches on 'kind': 'module' (resolve by ModuleVersionId in a live process; asset defaults to 'pe'; optional processId — server auto-selects when omitted), 'dump' (path under MCP_ARTIFACT_ROOT, re-validated every call), 'trace' (a .nettrace exported by collect_sample(kind='cpu', exportTrace=true) or inspect_heap(source='gcdump', exportTrace=true), path under MCP_ARTIFACT_ROOT), 'list' (read-only inventory of all artifacts under the root, newest first), or 'delete' (remove one artifact — requires the literal 'delete-artifact' scope). " +
        "maxBytes defaults to 4 MiB and is capped at 16 MiB per response; total artifact size is capped at 256 MiB. A TTL reaper (MCP_ARTIFACT_TTL_HOURS, default 24h, 0=disabled) prunes aged artifacts automatically. " +
        "Successor to 'get_module_bytes' and 'get_dump_bytes'; both legacy tools remain available during the deprecation window and emit identical envelopes.")]
    public static async Task<DiagnosticResult<object>> GetBytes(
        IModuleByteSource moduleByteSource,
        IDumpByteSource dumpByteSource,
        IProcessContextResolver resolver,
        IPrincipalAccessor principalAccessor,
        IArtifactLifecycle artifactLifecycle,
        [Description("Artifact kind: 'module' (PE/PDB of a loaded module), 'dump' (dump file under the root), 'trace' (.nettrace under the root), 'list' (inventory all artifacts), or 'delete' (remove one artifact).")] string kind,
        [Description("Module MVID (GUID 'D' format). Required when kind='module'; ignored otherwise.")] string? moduleVersionId = null,
        [Description("Module artifact when kind='module': 'pe' (default) or 'pdb'. Ignored when kind='dump'.")] string asset = "pe",
        [Description("Dump path when kind='dump'. Relative paths resolve under MCP_ARTIFACT_ROOT; absolute paths must still resolve under that root. Ignored for other kinds.")] string? dumpFilePath = null,
        [Description("Trace path when kind='trace'. Relative paths resolve under MCP_ARTIFACT_ROOT; absolute paths must still resolve under that root. Ignored for other kinds.")] string? traceFilePath = null,
        [Description("Artifact path to delete when kind='delete'. Relative to MCP_ARTIFACT_ROOT; traversal/absolute/symlink escapes are rejected. Ignored for other kinds.")] string? artifactPath = null,
        [Description("Byte offset where this chunk starts. Defaults to 0.")] long offset = 0,
        [Description("Maximum bytes to return in this response. Defaults to 4 MiB and is capped at 16 MiB.")] int maxBytes = FileChunkReader.DefaultChunkBytes,
        [Description("Operating system process id of the target .NET process. Used only when kind='module'; optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        if (!DiscriminatorDispatch.TryValidate<object>(
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

        var principal = principalAccessor.Current;
        if (principal is not null && !principal.HasExplicitScope(DeleteArtifactScope))
        {
            logger?.LogWarning("get_bytes(delete) denied: explicit '{Scope}' scope required. tokenName={TokenName} path={Path}",
                DeleteArtifactScope, principal.Name, artifactPath);
            return DiagnosticResult.Fail<object>(
                $"get_bytes(delete) requires the literal scope '{DeleteArtifactScope}'. Root or wildcard tokens do not auto-grant this scope.",
                new DiagnosticError("Forbidden", $"Grant the bearer principal the literal scope '{DeleteArtifactScope}'.", principal.Name),
                new NextActionHint("get_bytes", $"Retry with a bearer token that explicitly includes '{DeleteArtifactScope}'."));
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
