using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Bytes;
using DotnetDiagnostics.Core.ProcessDiscovery;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>
/// Host-neutral use cases that <b>materialise</b> a byte-fetch artifact (a managed module PE/PDB, or a
/// dump file) to a local file (issue #288). The MCP <c>get_bytes</c> tool streams base64 chunks for a
/// sibling MCP to reassemble through the orchestrator proxy; the standalone <c>dotnet-diagnostics</c>
/// CLI instead loops that same chunk stream itself, writes the whole artifact to disk, verifies it, and
/// returns a <see cref="ByteMaterialization"/> pointer. This is net-new behaviour (nothing is forwarded
/// from the Server), but it lives in Core so the engine stays host-neutral and reuses the shared
/// <see cref="IModuleByteSource"/> / <see cref="IDumpByteSource"/> chunk readers.
/// </summary>
public static class ByteMaterializationUseCases
{
    /// <summary>
    /// Streams every chunk of a loaded module's PE (or PDB) out of a live process and writes it to
    /// <paramref name="outputPath"/>, returning a verified pointer. <paramref name="processId"/> is
    /// optional — when omitted the resolver auto-selects the lone visible .NET process.
    /// </summary>
    public static async Task<DiagnosticResult<ByteMaterialization>> MaterializeModuleBytes(
        IModuleByteSource moduleByteSource,
        IProcessContextResolver resolver,
        bool principalAllowsLiteralScope,
        string moduleVersionId,
        string asset,
        int? processId,
        string outputPath,
        int maxBytes,
        ILogger? logger,
        string? principalName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(moduleByteSource);
        ArgumentNullException.ThrowIfNull(resolver);

        if (string.IsNullOrWhiteSpace(moduleVersionId)) return InvalidArg(nameof(moduleVersionId), "is required");
        if (!Guid.TryParse(moduleVersionId, out var mvid)) return InvalidArg(nameof(moduleVersionId), "must be a GUID in 'D' format");
        if (string.IsNullOrWhiteSpace(outputPath)) return InvalidArg(nameof(outputPath), "is required");
        if (maxBytes <= 0) return InvalidArg(nameof(maxBytes), "must be > 0");

        var normalizedAsset = NormalizeAsset(asset);
        if (normalizedAsset is null) return InvalidArg(nameof(asset), "must be 'pe' or 'pdb'");

        if (!principalAllowsLiteralScope) return LiteralScopeForbidden("get-bytes", principalName);

        var resolved = await ProcessResolutionHelpers.ResolveContextAsync<ByteMaterialization>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;

        var fullOutputPath = Path.GetFullPath(outputPath);

        var result = await AttachGuard.GuardAttachAsync<ByteMaterialization>("get-bytes", resolved.ProcessId, async () =>
        {
            try
            {
                var last = await MaterializeAsync(
                    fullOutputPath,
                    (offset, ct) => moduleByteSource.FetchAsync(resolved.ProcessId, mvid, normalizedAsset, offset, maxBytes, ct),
                    cancellationToken).ConfigureAwait(false);

                logger?.LogInformation(
                    "get-bytes materialised module bytes. tokenName={TokenName} mvid={Mvid} totalSize={TotalSize} output={Output}",
                    principalName ?? "(none)",
                    mvid.ToString("D"),
                    last.TotalSize,
                    fullOutputPath);

                var pointer = new ByteMaterialization
                {
                    Kind = last.Kind,
                    Asset = last.Asset,
                    Identifier = last.Identifier,
                    SourcePath = last.SourcePath,
                    OutputPath = fullOutputPath,
                    TotalBytes = last.TotalSize,
                    Sha256 = last.Sha256,
                    ProcessId = last.ProcessId ?? resolved.ProcessId,
                    CompanionPdbPath = last.CompanionPdbPath,
                    PdbIsEmbedded = last.PdbIsEmbedded,
                };
                return DiagnosticResult.Ok(pointer, BuildSummary(pointer), BuildHint(pointer));
            }
            catch (FileNotFoundException ex)
            {
                return ArtifactNotFound(ex.Message, ex.FileName ?? mvid.ToString("D"));
            }
            catch (ByteIntegrityException ex)
            {
                return IntegrityFailure(ex.Message);
            }
        }, cancellationToken, retryArguments: new Dictionary<string, object?>
        {
            ["kind"] = "module",
            ["moduleVersionId"] = moduleVersionId,
            ["asset"] = normalizedAsset,
            ["processId"] = resolved.ProcessId,
            ["outputPath"] = outputPath,
            ["maxBytes"] = maxBytes,
        }).ConfigureAwait(false);

        return ProcessResolutionHelpers.WithContext(result, resolved.Context);
    }

    /// <summary>
    /// Streams every chunk of a dump file (resolved under the artifact root) and writes it to
    /// <paramref name="outputPath"/>, returning a verified pointer.
    /// </summary>
    public static async Task<DiagnosticResult<ByteMaterialization>> MaterializeDumpBytes(
        IDumpByteSource dumpByteSource,
        bool principalAllowsLiteralScope,
        string dumpFilePath,
        string outputPath,
        int maxBytes,
        ILogger? logger,
        string? principalName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dumpByteSource);

        if (string.IsNullOrWhiteSpace(dumpFilePath)) return InvalidArg(nameof(dumpFilePath), "is required");
        if (string.IsNullOrWhiteSpace(outputPath)) return InvalidArg(nameof(outputPath), "is required");
        if (maxBytes <= 0) return InvalidArg(nameof(maxBytes), "must be > 0");

        if (!principalAllowsLiteralScope) return LiteralScopeForbidden("get-bytes", principalName);

        var fullOutputPath = Path.GetFullPath(outputPath);

        try
        {
            var last = await MaterializeAsync(
                fullOutputPath,
                (offset, ct) => dumpByteSource.FetchAsync(dumpFilePath, offset, maxBytes, ct),
                cancellationToken).ConfigureAwait(false);

            logger?.LogInformation(
                "get-bytes materialised dump bytes. tokenName={TokenName} dumpPath={DumpPath} totalSize={TotalSize} output={Output}",
                principalName ?? "(none)",
                last.Identifier,
                last.TotalSize,
                fullOutputPath);

            var pointer = new ByteMaterialization
            {
                Kind = last.Kind,
                Asset = last.Asset,
                Identifier = last.Identifier,
                SourcePath = last.SourcePath,
                OutputPath = fullOutputPath,
                TotalBytes = last.TotalSize,
                Sha256 = last.Sha256,
            };
            return DiagnosticResult.Ok(pointer, BuildSummary(pointer), BuildHint(pointer));
        }
        catch (ArtifactPathException ex)
        {
            return DiagnosticResult.Fail<ByteMaterialization>(
                $"get-bytes rejected the dump request: {ex.Message}",
                new DiagnosticError("InvalidArtifactPath", ex.Message, ex.ParameterName),
                new NextActionHint("get-bytes", "Re-issue with a dump path that resolves under the artifact root."));
        }
        catch (FileNotFoundException ex)
        {
            return ArtifactNotFound(ex.Message, ex.FileName ?? dumpFilePath);
        }
        catch (InvalidOperationException ex)
        {
            return DiagnosticResult.Fail<ByteMaterialization>(
                $"get-bytes rejected the dump request: {ex.Message}",
                new DiagnosticError("InvalidArgument", ex.Message, ex.GetType().FullName));
        }
        catch (ByteIntegrityException ex)
        {
            return IntegrityFailure(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The dump path is not wrapped in AttachGuard (no live attach), so output-path I/O
            // failures (unwritable / missing --out directory, permission denied, move conflict) must be
            // mapped to a structured envelope here rather than escaping as an unhandled crash.
            return OutputWriteFailure(fullOutputPath, ex);
        }
    }

    /// <summary>
    /// Streams every chunk of a trace file (.nettrace, resolved under the artifact root) and writes it
    /// to <paramref name="outputPath"/>, returning a verified pointer. Mirrors
    /// <see cref="MaterializeDumpBytes"/> exactly; only the audit/label differs.
    /// </summary>
    public static async Task<DiagnosticResult<ByteMaterialization>> MaterializeTraceBytes(
        IDumpByteSource traceByteSource,
        bool principalAllowsLiteralScope,
        string traceFilePath,
        string outputPath,
        int maxBytes,
        ILogger? logger,
        string? principalName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(traceByteSource);

        if (string.IsNullOrWhiteSpace(traceFilePath)) return InvalidArg(nameof(traceFilePath), "is required");
        if (string.IsNullOrWhiteSpace(outputPath)) return InvalidArg(nameof(outputPath), "is required");
        if (maxBytes <= 0) return InvalidArg(nameof(maxBytes), "must be > 0");

        if (!principalAllowsLiteralScope) return LiteralScopeForbidden("get-bytes", principalName);

        var fullOutputPath = Path.GetFullPath(outputPath);

        try
        {
            var last = await MaterializeAsync(
                fullOutputPath,
                (offset, ct) => traceByteSource.FetchAsync(traceFilePath, offset, maxBytes, ct),
                cancellationToken).ConfigureAwait(false);

            logger?.LogInformation(
                "get-bytes materialised trace bytes. tokenName={TokenName} tracePath={TracePath} totalSize={TotalSize} output={Output}",
                principalName ?? "(none)",
                last.Identifier,
                last.TotalSize,
                fullOutputPath);

            var pointer = new ByteMaterialization
            {
                Kind = "trace",
                Asset = "trace",
                Identifier = last.Identifier,
                SourcePath = last.SourcePath,
                OutputPath = fullOutputPath,
                TotalBytes = last.TotalSize,
                Sha256 = last.Sha256,
            };
            return DiagnosticResult.Ok(pointer, BuildSummary(pointer), BuildHint(pointer));
        }
        catch (ArtifactPathException ex)
        {
            return DiagnosticResult.Fail<ByteMaterialization>(
                $"get-bytes rejected the trace request: {ex.Message}",
                new DiagnosticError("InvalidArtifactPath", ex.Message, ex.ParameterName),
                new NextActionHint("get-bytes", "Re-issue with a trace path that resolves under the artifact root."));
        }
        catch (FileNotFoundException ex)
        {
            return ArtifactNotFound(ex.Message, ex.FileName ?? traceFilePath);
        }
        catch (InvalidOperationException ex)
        {
            return DiagnosticResult.Fail<ByteMaterialization>(
                $"get-bytes rejected the trace request: {ex.Message}",
                new DiagnosticError("InvalidArgument", ex.Message, ex.GetType().FullName));
        }
        catch (ByteIntegrityException ex)
        {
            return IntegrityFailure(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OutputWriteFailure(fullOutputPath, ex);
        }
    }

    /// <summary>
    /// Loops the chunked <paramref name="fetch"/> from offset 0 following <c>NextOffset</c>, writing the
    /// decoded bytes to a sibling staging file, verifying invariants on every chunk and the final
    /// SHA-256 end-to-end, then atomically replacing <paramref name="fullOutputPath"/>. The staging file
    /// is always removed on failure / cancellation so a partial artifact never lands at the destination.
    /// Returns the final chunk's envelope (carrying whole-artifact metadata).
    /// </summary>
    private static async Task<ByteFetchEnvelope> MaterializeAsync(
        string fullOutputPath,
        Func<long, CancellationToken, Task<ByteFetchEnvelope>> fetch,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var staging = fullOutputPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        var moved = false;
        try
        {
            string? expectedSha = null;
            var expectedTotal = -1L;
            ByteFetchEnvelope? last = null;
            var offset = 0L;

            var streamOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            };
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Materialised artifacts can carry sensitive dump memory or proprietary module/PDB
                // bytes. Create the staging file 0600 (owner-only) so it never exists at a permissive
                // umask-derived mode; File.Move preserves the mode onto the destination. Mirrors
                // SafeArtifactPath.CreateRestrictedFile.
                streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var output = new FileStream(staging, streamOptions))
            {
                while (true)
                {
                    var env = await fetch(offset, cancellationToken).ConfigureAwait(false);

                    if (env.Offset != offset)
                    {
                        throw new ByteIntegrityException($"byte source returned offset {env.Offset:N0}, expected {offset:N0}.");
                    }

                    if (expectedSha is null)
                    {
                        expectedSha = env.Sha256;
                        expectedTotal = env.TotalSize;
                    }
                    else if (!string.Equals(expectedSha, env.Sha256, StringComparison.Ordinal) || expectedTotal != env.TotalSize)
                    {
                        throw new ByteIntegrityException("artifact changed while it was being materialised (sha256/size drift between chunks).");
                    }

                    var decoded = env.ChunkSize == 0 ? Array.Empty<byte>() : Convert.FromBase64String(env.Base64Chunk);
                    if (decoded.Length != env.ChunkSize)
                    {
                        throw new ByteIntegrityException($"decoded chunk length {decoded.Length:N0} != declared chunkSize {env.ChunkSize:N0}.");
                    }

                    if (decoded.Length > 0)
                    {
                        await output.WriteAsync(decoded, cancellationToken).ConfigureAwait(false);
                    }

                    last = env;

                    if (env.NextOffset is not long next)
                    {
                        break;
                    }

                    if (next <= offset)
                    {
                        throw new ByteIntegrityException($"byte source did not advance (nextOffset {next:N0} <= offset {offset:N0}).");
                    }

                    offset = next;
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var writtenSha = await ComputeSha256Async(staging, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(writtenSha, expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new ByteIntegrityException($"materialised file sha256 {writtenSha} != source sha256 {expectedSha}.");
            }

            File.Move(staging, fullOutputPath, overwrite: true);
            moved = true;
            return last!;
        }
        finally
        {
            if (!moved)
            {
                TryDelete(staging);
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string? NormalizeAsset(string? asset)
    {
        if (string.IsNullOrWhiteSpace(asset))
        {
            return "pe";
        }

        var normalized = asset.Trim().ToLowerInvariant();
        return normalized is "pe" or "pdb" ? normalized : null;
    }

    private static string BuildSummary(ByteMaterialization m)
        => $"Materialised {m.TotalBytes:N0} byte(s) of {m.Kind} {m.Identifier} ({m.Asset}) to {m.OutputPath}. sha256={m.Sha256}.";

    private static NextActionHint BuildHint(ByteMaterialization m)
        => new(
            "get-bytes",
            $"The {m.Asset} artifact is materialised at {m.OutputPath}; decompile or inspect it offline.");

    private static DiagnosticResult<ByteMaterialization> InvalidArg(string parameterName, string requirement)
        => DiagnosticResult.Fail<ByteMaterialization>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("get-bytes", "Re-issue with valid arguments."));

    private static DiagnosticResult<ByteMaterialization> ArtifactNotFound(string message, string detail)
        => DiagnosticResult.Fail<ByteMaterialization>(
            $"The requested artifact could not be located: {message}",
            new DiagnosticError("ArtifactNotFound", message, detail),
            new NextActionHint("processes", "Confirm the module/dump exists and the target is still alive."));

    private static DiagnosticResult<ByteMaterialization> IntegrityFailure(string message)
        => DiagnosticResult.Fail<ByteMaterialization>(
            $"Byte materialisation failed an integrity check: {message}",
            new DiagnosticError("IntegrityError", message),
            new NextActionHint("get-bytes", "Re-run the materialisation; the source may have changed mid-stream."));

    private static DiagnosticResult<ByteMaterialization> OutputWriteFailure(string outputPath, Exception ex)
        => DiagnosticResult.Fail<ByteMaterialization>(
            $"Failed to write the materialised artifact to '{outputPath}': {ex.Message}",
            new DiagnosticError("OutputWriteFailed", ex.Message, ex.GetType().FullName),
            new NextActionHint("get-bytes", "Re-issue with --out pointing at a writable destination."));

    private static DiagnosticResult<ByteMaterialization> LiteralScopeForbidden(string tool, string? principalName)
        => DiagnosticResult.Fail<ByteMaterialization>(
            $"{tool} requires the literal scope 'module-bytes-read'. Root or wildcard tokens do not auto-grant this modifier scope.",
            new DiagnosticError(
                "Forbidden",
                "Grant the bearer principal the literal scope 'module-bytes-read'. Root ('*') is intentionally insufficient for this modifier scope.",
                principalName),
            new NextActionHint(tool, "Retry with a principal that explicitly includes 'module-bytes-read'."));

    private sealed class ByteIntegrityException : Exception
    {
        public ByteIntegrityException(string message)
            : base(message)
        {
        }
    }
}
