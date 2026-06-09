using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using DotnetDiagnostics.Core.CpuSampling;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.Bytes;

public sealed class ClrMdModuleByteSource : IModuleByteSource
{
    private readonly FileChunkReader _chunkReader;
    private readonly MvidReader _mvidReader;
    private readonly ILogger<ClrMdModuleByteSource> _logger;
    private readonly ConcurrentDictionary<Guid, EmbeddedPdbCacheEntry> _embeddedPdbCache = new();

    public ClrMdModuleByteSource(
        FileChunkReader? chunkReader = null,
        MvidReader? mvidReader = null,
        ILogger<ClrMdModuleByteSource>? logger = null)
    {
        _chunkReader = chunkReader ?? new FileChunkReader();
        _mvidReader = mvidReader ?? new MvidReader();
        _logger = logger ?? NullLogger<ClrMdModuleByteSource>.Instance;
    }

    public async Task<ByteFetchEnvelope> FetchAsync(
        int processId,
        Guid moduleVersionId,
        string asset = "pe",
        long offset = 0,
        int maxBytes = FileChunkReader.DefaultChunkBytes,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId), "Process id must be positive.");
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be >= 0.");
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes), "maxBytes must be > 0.");

        var normalizedAsset = NormalizeAsset(asset);
        var assemblyPath = await Task.Run(() => ResolveModulePath(processId, moduleVersionId, cancellationToken), cancellationToken).ConfigureAwait(false);
        var pdbAvailability = ProbePdbAvailability(assemblyPath);

        if (normalizedAsset == "pe")
        {
            var chunk = await _chunkReader.ReadAsync(assemblyPath, offset, maxBytes, cancellationToken).ConfigureAwait(false);
            return new ByteFetchEnvelope
            {
                Kind = "module",
                Asset = "pe",
                Identifier = moduleVersionId.ToString("D"),
                SourcePath = assemblyPath,
                TotalSize = chunk.TotalSize,
                Sha256 = chunk.Sha256,
                Offset = chunk.Offset,
                ChunkSize = chunk.ChunkSize,
                Base64Chunk = chunk.Base64Chunk,
                NextOffset = chunk.NextOffset,
                CompanionPdbPath = pdbAvailability.SiblingPdbPath,
                PdbIsEmbedded = pdbAvailability.HasEmbeddedPortablePdb ? true : null,
                ProcessId = processId,
            };
        }

        if (pdbAvailability.SiblingPdbPath is { } siblingPdbPath)
        {
            var chunk = await _chunkReader.ReadAsync(siblingPdbPath, offset, maxBytes, cancellationToken).ConfigureAwait(false);
            return new ByteFetchEnvelope
            {
                Kind = "module",
                Asset = "pdb",
                Identifier = moduleVersionId.ToString("D"),
                SourcePath = siblingPdbPath,
                TotalSize = chunk.TotalSize,
                Sha256 = chunk.Sha256,
                Offset = chunk.Offset,
                ChunkSize = chunk.ChunkSize,
                Base64Chunk = chunk.Base64Chunk,
                NextOffset = chunk.NextOffset,
                PdbIsEmbedded = false,
                ProcessId = processId,
            };
        }

        if (!pdbAvailability.HasEmbeddedPortablePdb)
        {
            throw new FileNotFoundException(
                $"Module '{assemblyPath}' does not publish a sibling or embedded portable PDB.",
                assemblyPath);
        }

        var embedded = GetOrCreateEmbeddedPdb(moduleVersionId, assemblyPath);
        var memoryChunk = ReadMemoryChunk(embedded.Bytes, offset, maxBytes);
        return new ByteFetchEnvelope
        {
            Kind = "module",
            Asset = "pdb",
            Identifier = moduleVersionId.ToString("D"),
            SourcePath = assemblyPath,
            TotalSize = memoryChunk.TotalSize,
            Sha256 = embedded.Sha256,
            Offset = memoryChunk.Offset,
            ChunkSize = memoryChunk.ChunkSize,
            Base64Chunk = memoryChunk.Base64Chunk,
            NextOffset = memoryChunk.NextOffset,
            PdbIsEmbedded = true,
            ProcessId = processId,
        };
    }

    private string ResolveModulePath(int processId, Guid moduleVersionId, CancellationToken cancellationToken)
    {
        using var target = DataTarget.AttachToProcess(processId, suspend: true);
        var clrInfo = target.ClrVersions.FirstOrDefault()
            ?? throw new InvalidOperationException("Target does not expose a CoreCLR runtime.");
        using var runtime = clrInfo.CreateRuntime();

        foreach (var module in runtime.EnumerateModules())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = module.Name;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            var mvid = _mvidReader.TryRead(path);
            if (mvid == moduleVersionId)
            {
                return Path.GetFullPath(path);
            }
        }

        throw new InvalidOperationException(
            $"No loaded module with mvid {moduleVersionId:D} was found in pid {processId}.");
    }

    private PdbAvailability ProbePdbAvailability(string assemblyPath)
    {
        var siblingPdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        return new PdbAvailability(
            File.Exists(siblingPdbPath) ? siblingPdbPath : null,
            HasEmbeddedPortablePdb(assemblyPath));
    }

    private bool HasEmbeddedPortablePdb(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            return peReader.ReadDebugDirectory().Any(entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to probe embedded PDB availability for {AssemblyPath}", assemblyPath);
            return false;
        }
    }

    private EmbeddedPdbCacheEntry GetOrCreateEmbeddedPdb(Guid moduleVersionId, string assemblyPath)
    {
        var info = new FileInfo(assemblyPath);
        var normalizedPath = NormalizePath(assemblyPath);
        if (_embeddedPdbCache.TryGetValue(moduleVersionId, out var cached) &&
            cached.AssemblyPath == normalizedPath &&
            cached.LastWriteTimeUtcTicks == info.LastWriteTimeUtc.Ticks &&
            cached.AssemblyLength == info.Length)
        {
            return cached;
        }

        var extracted = ExtractEmbeddedPdb(normalizedPath, info.LastWriteTimeUtc.Ticks, info.Length);
        _embeddedPdbCache[moduleVersionId] = extracted;
        return extracted;
    }

    private static EmbeddedPdbCacheEntry ExtractEmbeddedPdb(string assemblyPath, long lastWriteTimeUtcTicks, long assemblyLength)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var entry = peReader.ReadDebugDirectory().FirstOrDefault(static e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
        if (entry.Type != DebugDirectoryEntryType.EmbeddedPortablePdb)
        {
            throw new FileNotFoundException($"Module '{assemblyPath}' does not contain an embedded portable PDB.", assemblyPath);
        }

        using var provider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
        var reader = provider.GetMetadataReader();
        var bytes = CopyMetadataBytes(reader);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new EmbeddedPdbCacheEntry(assemblyPath, lastWriteTimeUtcTicks, assemblyLength, bytes, sha256);
    }

    private static unsafe byte[] CopyMetadataBytes(MetadataReader reader)
    {
        var length = reader.MetadataLength;
        if (length > FileChunkReader.MaxArtifactBytes)
        {
            throw new InvalidOperationException(
                $"Embedded portable PDB is {length:N0} bytes, which exceeds the 256 MiB streaming ceiling.");
        }

        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        var bytes = GC.AllocateUninitializedArray<byte>(length);
        fixed (byte* destination = bytes)
        {
            Buffer.MemoryCopy(reader.MetadataPointer, destination, length, length);
        }

        return bytes;
    }

    private static FileChunkReadResult ReadMemoryChunk(byte[] bytes, long offset, int maxBytes)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be >= 0.");
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes), "maxBytes must be > 0.");

        var totalSize = bytes.LongLength;
        if (totalSize > FileChunkReader.MaxArtifactBytes)
        {
            throw new InvalidOperationException(
                $"Embedded portable PDB is {totalSize:N0} bytes, which exceeds the 256 MiB streaming ceiling.");
        }

        var effectiveMaxBytes = Math.Min(maxBytes, FileChunkReader.MaxChunkBytes);
        if (offset >= totalSize)
        {
            return new FileChunkReadResult(totalSize, string.Empty, offset, 0, string.Empty, null);
        }

        var remaining = totalSize - offset;
        var bytesToRead = (int)Math.Min(remaining, effectiveMaxBytes);
        var chunk = new byte[bytesToRead];
        Buffer.BlockCopy(bytes, (int)offset, chunk, 0, bytesToRead);
        long? nextOffset = offset + bytesToRead >= totalSize ? null : offset + bytesToRead;
        return new FileChunkReadResult(totalSize, string.Empty, offset, bytesToRead, Convert.ToBase64String(chunk), nextOffset);
    }

    private static string NormalizeAsset(string? asset)
    {
        if (string.IsNullOrWhiteSpace(asset)) return "pe";
        var normalized = asset.Trim().ToLowerInvariant();
        return normalized is "pe" or "pdb"
            ? normalized
            : throw new ArgumentException("asset must be 'pe' or 'pdb'.", nameof(asset));
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
    }

    private sealed record PdbAvailability(string? SiblingPdbPath, bool HasEmbeddedPortablePdb);

    private sealed record EmbeddedPdbCacheEntry(
        string AssemblyPath,
        long LastWriteTimeUtcTicks,
        long AssemblyLength,
        byte[] Bytes,
        string Sha256);
}
