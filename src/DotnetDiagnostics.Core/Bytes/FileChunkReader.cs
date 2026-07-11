using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DotnetDiagnostics.Core.Bytes;

public sealed class FileChunkReader
{
    public const int DefaultChunkBytes = 4 * 1024 * 1024;
    public const int MaxChunkBytes = 16 * 1024 * 1024;
    public const long MaxArtifactBytes = 256L * 1024 * 1024;

    internal const int DefaultShaCacheEntries = 128;
    internal static readonly TimeSpan DefaultShaCacheTtl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<ShaCacheKey, ShaCacheEntry> _shaCache = new();
    private readonly TimeProvider _clock;
    private readonly int _maxShaCacheEntries;
    private readonly TimeSpan _shaCacheTtl;

    public FileChunkReader()
        : this(TimeProvider.System, DefaultShaCacheEntries, DefaultShaCacheTtl)
    {
    }

    internal FileChunkReader(TimeProvider clock, int maxShaCacheEntries, TimeSpan shaCacheTtl)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (maxShaCacheEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxShaCacheEntries), "Must be >= 1.");
        }

        if (shaCacheTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(shaCacheTtl), "TTL must be positive.");
        }

        _clock = clock;
        _maxShaCacheEntries = maxShaCacheEntries;
        _shaCacheTtl = shaCacheTtl;
    }

    internal int ShaCacheEntryCount => _shaCache.Count;

    public async Task<FileChunkReadResult> ReadAsync(
        string path,
        long offset = 0,
        int maxBytes = DefaultChunkBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be >= 0.");
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes), "maxBytes must be > 0.");

        var fullPath = NormalizePath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("File not found.", fullPath);
        }

        await using var stream = new FileStream(
            fullPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });

        var totalSize = stream.Length;
        if (totalSize > MaxArtifactBytes)
        {
            throw new InvalidOperationException(
                $"Artifact '{fullPath}' is {totalSize:N0} bytes, which exceeds the 256 MiB streaming ceiling.");
        }

        var effectiveMaxBytes = Math.Min(maxBytes, MaxChunkBytes);
        var lastWriteTimeUtc = info.LastWriteTimeUtc;
        var now = _clock.GetUtcNow();
        EvictExpiredShaEntries(now);
        var key = new ShaCacheKey(fullPath, lastWriteTimeUtc.Ticks, totalSize);
        var sha256 = TryGetCachedSha256(key, now, out var cached)
            ? cached
            : await ComputeAndCacheSha256Async(stream, key, now, cancellationToken).ConfigureAwait(false);

        if (offset >= totalSize)
        {
            return new FileChunkReadResult(totalSize, sha256, offset, 0, string.Empty, null);
        }

        stream.Seek(offset, SeekOrigin.Begin);
        var remaining = totalSize - offset;
        var bytesToRead = (int)Math.Min(remaining, effectiveMaxBytes);
        var buffer = GC.AllocateUninitializedArray<byte>(bytesToRead);
        var read = 0;
        while (read < bytesToRead)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, bytesToRead - read), cancellationToken).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }

        var chunk = read == buffer.Length ? buffer : buffer[..read];
        long? nextOffset = offset + read >= totalSize ? null : offset + read;
        return new FileChunkReadResult(totalSize, sha256, offset, read, Convert.ToBase64String(chunk), nextOffset);
    }

    private bool TryGetCachedSha256(ShaCacheKey key, DateTimeOffset now, out string digest)
    {
        if (!_shaCache.TryGetValue(key, out var cached))
        {
            digest = string.Empty;
            return false;
        }

        if (cached.ExpiresAt <= now)
        {
            _shaCache.TryRemove(key, out _);
            digest = string.Empty;
            return false;
        }

        var refreshed = cached with
        {
            ExpiresAt = now + _shaCacheTtl,
            LastAccessUtc = now,
        };
        _shaCache[key] = refreshed;
        digest = cached.Digest;
        return true;
    }

    private async Task<string> ComputeAndCacheSha256Async(
        FileStream stream,
        ShaCacheKey key,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        stream.Seek(0, SeekOrigin.Begin);
        using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                incrementalHash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var digest = Convert.ToHexString(incrementalHash.GetHashAndReset()).ToLowerInvariant();
        _shaCache[key] = new ShaCacheEntry(digest, now + _shaCacheTtl, now);
        EnforceShaCacheCapacity();
        return digest;
    }

    private void EvictExpiredShaEntries(DateTimeOffset now)
    {
        foreach (var kv in _shaCache)
        {
            if (kv.Value.ExpiresAt <= now)
            {
                _shaCache.TryRemove(kv.Key, out _);
            }
        }
    }

    private void EnforceShaCacheCapacity()
    {
        while (_shaCache.Count > _maxShaCacheEntries)
        {
            var oldest = _shaCache
                .OrderBy(kv => kv.Value.LastAccessUtc)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(oldest.Key.Path) || !_shaCache.TryRemove(oldest.Key, out _))
            {
                return;
            }
        }
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
    }

    private readonly record struct ShaCacheKey(string Path, long LastWriteUtcTicks, long Length);

    private readonly record struct ShaCacheEntry(string Digest, DateTimeOffset ExpiresAt, DateTimeOffset LastAccessUtc);
}

public sealed record FileChunkReadResult(
    long TotalSize,
    string Sha256,
    long Offset,
    int ChunkSize,
    string Base64Chunk,
    long? NextOffset);
