using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DotnetDiagnosticsMcp.Core.Bytes;

public sealed class FileChunkReader
{
    public const int DefaultChunkBytes = 4 * 1024 * 1024;
    public const int MaxChunkBytes = 16 * 1024 * 1024;
    public const long MaxArtifactBytes = 256L * 1024 * 1024;

    private readonly ConcurrentDictionary<ShaCacheKey, string> _shaCache = new();

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
        var key = new ShaCacheKey(fullPath, lastWriteTimeUtc.Ticks, totalSize);
        var sha256 = _shaCache.TryGetValue(key, out var cached)
            ? cached
            : await ComputeAndCacheSha256Async(stream, key, cancellationToken).ConfigureAwait(false);

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

    private async Task<string> ComputeAndCacheSha256Async(
        FileStream stream,
        ShaCacheKey key,
        CancellationToken cancellationToken)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var allBytes = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        var read = 0;
        while (read < allBytes.Length)
        {
            var n = await stream.ReadAsync(allBytes.AsMemory(read, allBytes.Length - read), cancellationToken).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }

        var hash = SHA256.HashData(read == allBytes.Length ? allBytes : allBytes[..read]);
        var digest = Convert.ToHexString(hash).ToLowerInvariant();
        _shaCache.TryAdd(key, digest);
        return digest;
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
    }

    private readonly record struct ShaCacheKey(string Path, long LastWriteUtcTicks, long Length);
}

public sealed record FileChunkReadResult(
    long TotalSize,
    string Sha256,
    long Offset,
    int ChunkSize,
    string Base64Chunk,
    long? NextOffset);
