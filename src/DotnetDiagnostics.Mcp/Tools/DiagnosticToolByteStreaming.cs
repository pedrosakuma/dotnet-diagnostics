using System.ComponentModel;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Bytes;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Security;
using Microsoft.Extensions.Logging;
using static DotnetDiagnostics.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnostics.Mcp.Tools;

internal static class DiagnosticToolByteStreaming
{
    public static async Task<DiagnosticResult<ByteFetchEnvelope>> GetModuleBytes(
        IModuleByteSource moduleByteSource,
        IProcessContextResolver resolver,
        IPrincipalAccessor principalAccessor,
        [Description("PE module MVID (GUID 'D' format) of the loaded module to stream. Required.")] string moduleVersionId,
        [Description("Artifact to stream: 'pe' (default) or 'pdb'.")] string asset = "pe",
        [Description("Byte offset where this chunk starts. Defaults to 0.")] long offset = 0,
        [Description("Maximum bytes to return in this response. Defaults to 4 MiB and is capped at 16 MiB.")] int maxBytes = FileChunkReader.DefaultChunkBytes,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(moduleVersionId)) return InvalidArg<ByteFetchEnvelope>(nameof(moduleVersionId), "is required");
        if (!Guid.TryParse(moduleVersionId, out var mvid)) return InvalidArg<ByteFetchEnvelope>(nameof(moduleVersionId), "must be a GUID in 'D' format");
        if (offset < 0) return InvalidArg<ByteFetchEnvelope>(nameof(offset), "must be >= 0");
        if (maxBytes <= 0) return InvalidArg<ByteFetchEnvelope>(nameof(maxBytes), "must be > 0");

        var logger = loggerFactory?.CreateLogger("DotnetDiagnostics.Mcp.Tools.GetModuleBytes");
        var explicitScopeFailure = RequireLiteralScope<ByteFetchEnvelope>(
            principalAccessor,
            logger,
            "get_module_bytes",
            identifierName: "mvid",
            identifierValue: mvid.ToString("D"),
            offset);
        if (explicitScopeFailure is not null)
        {
            return explicitScopeFailure;
        }

        var resolved = await ResolveContextAsync<ByteFetchEnvelope>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;

        return await GuardAttachAsync("get_module_bytes", resolved.ProcessId, async () =>
        {
            try
            {
                var envelope = await moduleByteSource.FetchAsync(resolved.ProcessId, mvid, asset, offset, maxBytes, cancellationToken).ConfigureAwait(false);
                AuditByteFetch(logger, principalAccessor.Current, "get_module_bytes", envelope.Identifier, null, envelope.Offset, envelope.ChunkSize, envelope.TotalSize);
                var result = BuildByteFetchResult(
                    envelope,
                    BuildByteFetchSummary(envelope),
                    BuildModuleByteFetchHint(envelope, resolved.ProcessId, asset, maxBytes));
                return WithContext(result, resolved.Context);
            }
            catch (FileNotFoundException ex)
            {
                return ArtifactNotFound<ByteFetchEnvelope>("get_module_bytes", ex.Message, ex.FileName ?? mvid.ToString("D"));
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    [Description(
        "Streams a dump file under the artifact root in repeated CallTool chunks so sibling MCPs can materialise pod-local dumps through the orchestrator proxy. dumpFilePath may be relative to MCP_ARTIFACT_ROOT or absolute when it still resolves under that root after symlink resolution. " +
        "Path hints are untrusted: the tool re-validates every call through the artifact-root sandbox. maxBytes defaults to 4 MiB and is capped at 16 MiB per response; total artifact size is capped at 256 MiB.")]
    public static async Task<DiagnosticResult<ByteFetchEnvelope>> GetDumpBytes(
        IDumpByteSource dumpByteSource,
        IPrincipalAccessor principalAccessor,
        [Description("Dump path to stream. Relative paths are resolved under the artifact root; absolute paths are allowed only when they still resolve under that root. Required.")] string dumpFilePath,
        [Description("Byte offset where this chunk starts. Defaults to 0.")] long offset = 0,
        [Description("Maximum bytes to return in this response. Defaults to 4 MiB and is capped at 16 MiB.")] int maxBytes = FileChunkReader.DefaultChunkBytes,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dumpFilePath)) return InvalidArg<ByteFetchEnvelope>(nameof(dumpFilePath), "is required");
        if (offset < 0) return InvalidArg<ByteFetchEnvelope>(nameof(offset), "must be >= 0");
        if (maxBytes <= 0) return InvalidArg<ByteFetchEnvelope>(nameof(maxBytes), "must be > 0");

        var logger = loggerFactory?.CreateLogger("DotnetDiagnostics.Mcp.Tools.GetDumpBytes");
        var explicitScopeFailure = RequireLiteralScope<ByteFetchEnvelope>(
            principalAccessor,
            logger,
            "get_dump_bytes",
            identifierName: "dumpPath",
            identifierValue: dumpFilePath,
            offset);
        if (explicitScopeFailure is not null)
        {
            return explicitScopeFailure;
        }

        try
        {
            var envelope = await dumpByteSource.FetchAsync(dumpFilePath, offset, maxBytes, cancellationToken).ConfigureAwait(false);
            AuditByteFetch(logger, principalAccessor.Current, "get_dump_bytes", null, envelope.Identifier, envelope.Offset, envelope.ChunkSize, envelope.TotalSize);
            return BuildByteFetchResult(envelope, BuildByteFetchSummary(envelope), BuildDumpByteFetchHint(envelope, maxBytes));
        }
        catch (DotnetDiagnostics.Core.Artifacts.ArtifactPathException artifactEx)
        {
            return DiagnosticResult.Fail<ByteFetchEnvelope>(
                $"get_dump_bytes rejected the request: {artifactEx.Message}",
                new DiagnosticError("InvalidArtifactPath", artifactEx.Message, artifactEx.ParameterName),
                new NextActionHint("get_bytes",
                    "Re-issue with a path under the artifact root; absolute paths must still resolve under that root after symlink resolution."));
        }
        catch (FileNotFoundException ex)
        {
            return ArtifactNotFound<ByteFetchEnvelope>("get_dump_bytes", ex.Message, ex.FileName ?? dumpFilePath);
        }
        catch (InvalidOperationException ex)
        {
            return DiagnosticResult.Fail<ByteFetchEnvelope>(
                $"get_dump_bytes rejected the request: {ex.Message}",
                new DiagnosticError("InvalidArgument", ex.Message, ex.GetType().FullName));
        }
    }

    [Description(
        "Streams a raw trace file (.nettrace) under the artifact root in repeated CallTool chunks so a sibling MCP / human can materialise an exported CPU or GC trace for offline PerfView/Speedscope/Perfetto analysis. traceFilePath may be relative to MCP_ARTIFACT_ROOT or absolute when it still resolves under that root after symlink resolution. " +
        "Path hints are untrusted: the tool re-validates every call through the artifact-root sandbox. maxBytes defaults to 4 MiB and is capped at 16 MiB per response; total artifact size is capped at 256 MiB.")]
    public static async Task<DiagnosticResult<ByteFetchEnvelope>> GetTraceBytes(
        IDumpByteSource traceByteSource,
        IPrincipalAccessor principalAccessor,
        [Description("Trace path to stream. Relative paths are resolved under the artifact root; absolute paths are allowed only when they still resolve under that root. Required.")] string traceFilePath,
        [Description("Byte offset where this chunk starts. Defaults to 0.")] long offset = 0,
        [Description("Maximum bytes to return in this response. Defaults to 4 MiB and is capped at 16 MiB.")] int maxBytes = FileChunkReader.DefaultChunkBytes,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(traceFilePath)) return InvalidArg<ByteFetchEnvelope>(nameof(traceFilePath), "is required");
        if (offset < 0) return InvalidArg<ByteFetchEnvelope>(nameof(offset), "must be >= 0");
        if (maxBytes <= 0) return InvalidArg<ByteFetchEnvelope>(nameof(maxBytes), "must be > 0");

        var logger = loggerFactory?.CreateLogger("DotnetDiagnostics.Mcp.Tools.GetTraceBytes");
        var explicitScopeFailure = RequireLiteralScope<ByteFetchEnvelope>(
            principalAccessor,
            logger,
            "get_trace_bytes",
            identifierName: "tracePath",
            identifierValue: traceFilePath,
            offset);
        if (explicitScopeFailure is not null)
        {
            return explicitScopeFailure;
        }

        try
        {
            var fetched = await traceByteSource.FetchAsync(traceFilePath, offset, maxBytes, cancellationToken).ConfigureAwait(false);
            var envelope = fetched with { Kind = "trace", Asset = "trace" };
            AuditByteFetch(logger, principalAccessor.Current, "get_trace_bytes", null, envelope.Identifier, envelope.Offset, envelope.ChunkSize, envelope.TotalSize);
            return BuildByteFetchResult(envelope, BuildByteFetchSummary(envelope), BuildTraceByteFetchHint(envelope, maxBytes));
        }
        catch (DotnetDiagnostics.Core.Artifacts.ArtifactPathException artifactEx)
        {
            return DiagnosticResult.Fail<ByteFetchEnvelope>(
                $"get_trace_bytes rejected the request: {artifactEx.Message}",
                new DiagnosticError("InvalidArtifactPath", artifactEx.Message, artifactEx.ParameterName),
                new NextActionHint("get_bytes",
                    "Re-issue with a path under the artifact root; absolute paths must still resolve under that root after symlink resolution."));
        }
        catch (FileNotFoundException ex)
        {
            return ArtifactNotFound<ByteFetchEnvelope>("get_trace_bytes", ex.Message, ex.FileName ?? traceFilePath);
        }
        catch (InvalidOperationException ex)
        {
            return DiagnosticResult.Fail<ByteFetchEnvelope>(
                $"get_trace_bytes rejected the request: {ex.Message}",
                new DiagnosticError("InvalidArgument", ex.Message, ex.GetType().FullName));
        }
    }

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("inspect_process", "Re-issue with valid arguments. See tool schema for ranges and defaults."));

    private static DiagnosticResult<T>? RequireLiteralScope<T>(
        IPrincipalAccessor principalAccessor,
        Microsoft.Extensions.Logging.ILogger? logger,
        string tool,
        string identifierName,
        string identifierValue,
        long offset)
    {
        var principal = principalAccessor.Current;
        if (principal?.HasExplicitScope("module-bytes-read") == true)
        {
            return null;
        }

        logger?.LogWarning(
            "{Tool} denied: explicit module-bytes-read scope required. tokenName={TokenName} {IdentifierName}={IdentifierValue} offset={Offset} chunkSize={ChunkSize} totalSize={TotalSize}",
            tool,
            principal?.Name ?? "(none)",
            identifierName,
            identifierValue,
            offset,
            0,
            0);

        return DiagnosticResult.Fail<T>(
            $"{tool} requires the literal scope 'module-bytes-read'. Root or wildcard tokens do not auto-grant this modifier scope.",
            new DiagnosticError(
                "Forbidden",
                "Grant the bearer principal the literal scope 'module-bytes-read'. Root ('*') is intentionally insufficient for this modifier scope.",
                principal?.Name),
            new NextActionHint(
                tool,
                "Retry with a bearer token that explicitly includes 'module-bytes-read'."));
    }

    private static void AuditByteFetch(
        Microsoft.Extensions.Logging.ILogger? logger,
        BearerPrincipal? principal,
        string tool,
        string? mvid,
        string? dumpPath,
        long offset,
        int chunkSize,
        long totalSize)
    {
        if (logger is null)
        {
            return;
        }

        if (mvid is not null)
        {
            logger.LogInformation(
                "{Tool} streamed bytes. tokenName={TokenName} mvid={Mvid} offset={Offset} chunkSize={ChunkSize} totalSize={TotalSize}",
                tool,
                principal?.Name ?? "(none)",
                mvid,
                offset,
                chunkSize,
                totalSize);
            return;
        }

        logger.LogInformation(
            "{Tool} streamed bytes. tokenName={TokenName} dumpPath={DumpPath} offset={Offset} chunkSize={ChunkSize} totalSize={TotalSize}",
            tool,
            principal?.Name ?? "(none)",
            dumpPath ?? "(none)",
            offset,
            chunkSize,
            totalSize);
    }

    private static DiagnosticResult<ByteFetchEnvelope> BuildByteFetchResult(
        ByteFetchEnvelope envelope,
        string summary,
        NextActionHint? hint)
        => hint is null
            ? DiagnosticResult.Ok(envelope, summary)
            : DiagnosticResult.Ok(envelope, summary, hint);

    private static string BuildByteFetchSummary(ByteFetchEnvelope envelope)
    {
        var streamed = envelope.ChunkSize == 0
            ? $"No bytes remain at offset {envelope.Offset:N0}"
            : $"Streamed {envelope.ChunkSize:N0} byte(s) from offset {envelope.Offset:N0}";
        var more = envelope.NextOffset is long next
            ? $" Next chunk starts at offset {next:N0}."
            : " Stream complete.";
        return $"{streamed} of {envelope.TotalSize:N0} total byte(s) for {envelope.Kind} {envelope.Identifier} ({envelope.Asset}).{more}";
    }

    private static NextActionHint? BuildModuleByteFetchHint(ByteFetchEnvelope envelope, int processId, string asset, int maxBytes)
        => envelope.NextOffset is long next
            ? new NextActionHint(
                "get_bytes",
                "Continue streaming the next chunk from the same module asset.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "module",
                    ["moduleVersionId"] = envelope.Identifier,
                    ["asset"] = string.IsNullOrWhiteSpace(asset) ? envelope.Asset : asset,
                    ["offset"] = next,
                    ["maxBytes"] = maxBytes,
                    ["processId"] = processId,
                })
            : null;

    private static NextActionHint? BuildDumpByteFetchHint(ByteFetchEnvelope envelope, int maxBytes)
        => envelope.NextOffset is long next
            ? new NextActionHint(
                "get_bytes",
                "Continue streaming the next chunk from the same dump artifact.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "dump",
                    ["dumpFilePath"] = envelope.Identifier,
                    ["offset"] = next,
                    ["maxBytes"] = maxBytes,
                })
            : null;

    private static NextActionHint? BuildTraceByteFetchHint(ByteFetchEnvelope envelope, int maxBytes)
        => envelope.NextOffset is long next
            ? new NextActionHint(
                "get_bytes",
                "Continue streaming the next chunk from the same trace artifact.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "trace",
                    ["traceFilePath"] = envelope.Identifier,
                    ["offset"] = next,
                    ["maxBytes"] = maxBytes,
                })
            : null;

    private static DiagnosticResult<T> ArtifactNotFound<T>(string tool, string message, string detail)
        => DiagnosticResult.Fail<T>(
            $"{tool} could not locate the requested artifact: {message}",
            new DiagnosticError("ArtifactNotFound", message, detail));
    private static Task<DiagnosticResult<T>> GuardAttachAsync<T>(
        string tool,
        int? processId,
        Func<Task<DiagnosticResult<T>>> body,
        CancellationToken cancellationToken)
        => AttachGuard.GuardAttachAsync(tool, processId, body, cancellationToken);

    private static DiagnosticResult<T> ClassifyAttachFailure<T>(string tool, int? processId, Exception ex)
        => AttachGuard.ClassifyAttachFailure<T>(tool, processId, ex);
}
