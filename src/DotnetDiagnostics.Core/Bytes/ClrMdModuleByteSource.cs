using System.Collections.Concurrent;
using System.Diagnostics;
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
    internal const int DefaultModuleResolutionCacheEntries = 128;
    internal const long DefaultEmbeddedPdbCacheBytes = 64L * 1024 * 1024;
    internal static readonly TimeSpan DefaultModuleResolutionCacheTtl = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan DefaultEmbeddedPdbCacheTtl = TimeSpan.FromMinutes(10);

    private readonly FileChunkReader _chunkReader;
    private readonly MvidReader _mvidReader;
    private readonly ILogger<ClrMdModuleByteSource> _logger;
    private readonly TimeProvider _clock;
    private readonly Func<int, Guid, CancellationToken, string> _modulePathResolver;
    private readonly Func<int, long?> _processStartTimeResolver;
    private readonly ConcurrentDictionary<ModuleCacheKey, ModuleResolutionCacheEntry> _moduleResolutionCache = new();
    private readonly ConcurrentDictionary<Guid, EmbeddedPdbCacheEntry> _embeddedPdbCache = new();
    private readonly object _embeddedPdbCacheLock = new();
    private readonly int _maxModuleResolutionCacheEntries;
    private readonly TimeSpan _moduleResolutionCacheTtl;
    private readonly long _maxEmbeddedPdbCacheBytes;
    private readonly TimeSpan _embeddedPdbCacheTtl;
    private long _embeddedPdbCacheBytes;

    public ClrMdModuleByteSource(
        FileChunkReader? chunkReader = null,
        MvidReader? mvidReader = null,
        ILogger<ClrMdModuleByteSource>? logger = null)
        : this(
            chunkReader,
            mvidReader,
            logger,
            TimeProvider.System,
            modulePathResolver: null,
            DefaultModuleResolutionCacheEntries,
            DefaultModuleResolutionCacheTtl,
            DefaultEmbeddedPdbCacheBytes,
            DefaultEmbeddedPdbCacheTtl,
            processStartTimeResolver: null)
    {
    }

    internal ClrMdModuleByteSource(
        FileChunkReader? chunkReader,
        MvidReader? mvidReader,
        ILogger<ClrMdModuleByteSource>? logger,
        TimeProvider clock,
        Func<int, Guid, CancellationToken, string>? modulePathResolver,
        int maxModuleResolutionCacheEntries,
        TimeSpan moduleResolutionCacheTtl,
        long maxEmbeddedPdbCacheBytes,
        TimeSpan embeddedPdbCacheTtl,
        Func<int, long?>? processStartTimeResolver)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (maxModuleResolutionCacheEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxModuleResolutionCacheEntries), "Must be >= 1.");
        }

        if (moduleResolutionCacheTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(moduleResolutionCacheTtl), "TTL must be positive.");
        }

        if (maxEmbeddedPdbCacheBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEmbeddedPdbCacheBytes), "Must be positive.");
        }

        if (embeddedPdbCacheTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(embeddedPdbCacheTtl), "TTL must be positive.");
        }

        _chunkReader = chunkReader ?? new FileChunkReader();
        _mvidReader = mvidReader ?? new MvidReader();
        _logger = logger ?? NullLogger<ClrMdModuleByteSource>.Instance;
        _clock = clock;
        _modulePathResolver = modulePathResolver ?? ResolveModulePath;
        _processStartTimeResolver = processStartTimeResolver ?? TryGetProcessStartTimeUtcTicks;
        _maxModuleResolutionCacheEntries = maxModuleResolutionCacheEntries;
        _moduleResolutionCacheTtl = moduleResolutionCacheTtl;
        _maxEmbeddedPdbCacheBytes = maxEmbeddedPdbCacheBytes;
        _embeddedPdbCacheTtl = embeddedPdbCacheTtl;
    }

    internal int ModuleResolutionCacheCount => _moduleResolutionCache.Count;
    internal int EmbeddedPdbCacheCount => _embeddedPdbCache.Count;

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
        var module = await Task.Run(
                () => ResolveModuleMetadataCached(processId, moduleVersionId, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        var assemblyPath = module.AssemblyPath;
        var pdbAvailability = module.PdbAvailability;

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

    private ModuleResolutionCacheEntry ResolveModuleMetadataCached(int processId, Guid moduleVersionId, CancellationToken cancellationToken)
    {
        var key = new ModuleCacheKey(processId, moduleVersionId);
        var now = _clock.GetUtcNow();
        var processStartTimeUtcTicks = _processStartTimeResolver(processId);
        EvictExpiredModuleEntries(now);
        if (TryGetCachedModuleResolution(key, processStartTimeUtcTicks, now, out var cached))
        {
            return cached;
        }

        var assemblyPath = NormalizePath(_modulePathResolver(processId, moduleVersionId, cancellationToken));
        var resolved = CreateModuleResolutionCacheEntry(assemblyPath, processStartTimeUtcTicks, now);
        _moduleResolutionCache[key] = resolved;
        EnforceModuleResolutionCacheCapacity();
        return resolved;
    }

    private bool TryGetCachedModuleResolution(ModuleCacheKey key, long? processStartTimeUtcTicks, DateTimeOffset now, out ModuleResolutionCacheEntry cached)
    {
        if (!_moduleResolutionCache.TryGetValue(key, out var existing))
        {
            cached = default!;
            return false;
        }

        cached = existing;

        if (cached.ExpiresAt <= now || !ProcessStillMatches(cached, processStartTimeUtcTicks) || !AssemblyStillMatches(cached) || !SiblingPdbStillMatches(cached))
        {
            _moduleResolutionCache.TryRemove(key, out _);
            cached = default!;
            return false;
        }

        cached = cached with
        {
            ExpiresAt = now + _moduleResolutionCacheTtl,
            LastAccessUtc = now,
        };
        _moduleResolutionCache[key] = cached;
        return true;
    }

    private ModuleResolutionCacheEntry CreateModuleResolutionCacheEntry(string assemblyPath, long? processStartTimeUtcTicks, DateTimeOffset now)
    {
        var info = new FileInfo(assemblyPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Resolved module file not found.", assemblyPath);
        }

        return new ModuleResolutionCacheEntry(
            assemblyPath,
            ProbePdbAvailability(assemblyPath),
            processStartTimeUtcTicks,
            info.LastWriteTimeUtc.Ticks,
            info.Length,
            now + _moduleResolutionCacheTtl,
            now);
    }

    private void EvictExpiredModuleEntries(DateTimeOffset now)
    {
        foreach (var kv in _moduleResolutionCache)
        {
            if (kv.Value.ExpiresAt <= now)
            {
                _moduleResolutionCache.TryRemove(kv.Key, out _);
            }
        }
    }

    private void EnforceModuleResolutionCacheCapacity()
    {
        while (_moduleResolutionCache.Count > _maxModuleResolutionCacheEntries)
        {
            var oldest = _moduleResolutionCache
                .OrderBy(kv => kv.Value.LastAccessUtc)
                .FirstOrDefault();
            if (oldest.Key.ProcessId == 0 || !_moduleResolutionCache.TryRemove(oldest.Key, out _))
            {
                return;
            }
        }
    }

    private static bool AssemblyStillMatches(ModuleResolutionCacheEntry entry)
    {
        var info = new FileInfo(entry.AssemblyPath);
        return info.Exists &&
            info.LastWriteTimeUtc.Ticks == entry.LastWriteTimeUtcTicks &&
            info.Length == entry.AssemblyLength;
    }

    private static bool ProcessStillMatches(ModuleResolutionCacheEntry entry, long? processStartTimeUtcTicks) =>
        entry.ProcessStartTimeUtcTicks is not null && processStartTimeUtcTicks == entry.ProcessStartTimeUtcTicks;

    private static bool SiblingPdbStillMatches(ModuleResolutionCacheEntry entry)
    {
        if (entry.PdbAvailability.SiblingPdbPath is { } siblingPdbPath)
        {
            return File.Exists(siblingPdbPath);
        }

        return !File.Exists(Path.ChangeExtension(entry.AssemblyPath, ".pdb"));
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
        var now = _clock.GetUtcNow();

        lock (_embeddedPdbCacheLock)
        {
            EvictExpiredEmbeddedPdbEntries(now);
            if (TryGetCachedEmbeddedPdb(moduleVersionId, normalizedPath, info, now, out var cached))
            {
                return cached;
            }
        }

        var extracted = ExtractEmbeddedPdb(normalizedPath, info.LastWriteTimeUtc.Ticks, info.Length, now + _embeddedPdbCacheTtl, now);

        lock (_embeddedPdbCacheLock)
        {
            EvictExpiredEmbeddedPdbEntries(now);
            if (TryGetCachedEmbeddedPdb(moduleVersionId, normalizedPath, info, now, out var cached))
            {
                return cached;
            }

            if (_embeddedPdbCache.TryGetValue(moduleVersionId, out var replaced))
            {
                _embeddedPdbCacheBytes -= replaced.Bytes.LongLength;
            }

            _embeddedPdbCache[moduleVersionId] = extracted;
            _embeddedPdbCacheBytes += extracted.Bytes.LongLength;
            EnforceEmbeddedPdbCacheCapacity();
            return extracted;
        }
    }

    private bool TryGetCachedEmbeddedPdb(
        Guid moduleVersionId,
        string normalizedPath,
        FileInfo info,
        DateTimeOffset now,
        out EmbeddedPdbCacheEntry cached)
    {
        if (!_embeddedPdbCache.TryGetValue(moduleVersionId, out var existing))
        {
            cached = default!;
            return false;
        }

        cached = existing;

        if (cached.ExpiresAt <= now ||
            cached.AssemblyPath != normalizedPath ||
            cached.LastWriteTimeUtcTicks != info.LastWriteTimeUtc.Ticks ||
            cached.AssemblyLength != info.Length)
        {
            if (_embeddedPdbCache.TryRemove(moduleVersionId, out var removed))
            {
                _embeddedPdbCacheBytes -= removed.Bytes.LongLength;
            }

            cached = default!;
            return false;
        }

        cached = cached with
        {
            ExpiresAt = now + _embeddedPdbCacheTtl,
            LastAccessUtc = now,
        };
        _embeddedPdbCache[moduleVersionId] = cached;
        return true;
    }

    private void EvictExpiredEmbeddedPdbEntries(DateTimeOffset now)
    {
        foreach (var kv in _embeddedPdbCache)
        {
            if (kv.Value.ExpiresAt <= now && _embeddedPdbCache.TryRemove(kv.Key, out var removed))
            {
                _embeddedPdbCacheBytes -= removed.Bytes.LongLength;
            }
        }
    }

    private void EnforceEmbeddedPdbCacheCapacity()
    {
        while (_embeddedPdbCacheBytes > _maxEmbeddedPdbCacheBytes)
        {
            var oldest = _embeddedPdbCache
                .OrderBy(kv => kv.Value.LastAccessUtc)
                .FirstOrDefault();
            if (oldest.Key == Guid.Empty || !_embeddedPdbCache.TryRemove(oldest.Key, out var removed))
            {
                return;
            }

            _embeddedPdbCacheBytes -= removed.Bytes.LongLength;
        }
    }

    private static EmbeddedPdbCacheEntry ExtractEmbeddedPdb(
        string assemblyPath,
        long lastWriteTimeUtcTicks,
        long assemblyLength,
        DateTimeOffset expiresAt,
        DateTimeOffset lastAccessUtc)
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
        return new EmbeddedPdbCacheEntry(assemblyPath, lastWriteTimeUtcTicks, assemblyLength, bytes, sha256, expiresAt, lastAccessUtc);
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

    private static long? TryGetProcessStartTimeUtcTicks(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private readonly record struct ModuleCacheKey(int ProcessId, Guid ModuleVersionId);

    private sealed record PdbAvailability(string? SiblingPdbPath, bool HasEmbeddedPortablePdb);

    private sealed record ModuleResolutionCacheEntry(
        string AssemblyPath,
        PdbAvailability PdbAvailability,
        long? ProcessStartTimeUtcTicks,
        long LastWriteTimeUtcTicks,
        long AssemblyLength,
        DateTimeOffset ExpiresAt,
        DateTimeOffset LastAccessUtc);

    private sealed record EmbeddedPdbCacheEntry(
        string AssemblyPath,
        long LastWriteTimeUtcTicks,
        long AssemblyLength,
        byte[] Bytes,
        string Sha256,
        DateTimeOffset ExpiresAt,
        DateTimeOffset LastAccessUtc);
}
