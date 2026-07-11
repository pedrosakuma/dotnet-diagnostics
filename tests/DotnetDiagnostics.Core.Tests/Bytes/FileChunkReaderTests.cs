using System.Security.Cryptography;
using DotnetDiagnostics.Core.Bytes;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.Bytes;

public sealed class FileChunkReaderTests : IDisposable
{
    private readonly string _root;

    public FileChunkReaderTests()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "test-artifacts", nameof(FileChunkReaderTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task ReadAsync_HandlesChunkMathEdges()
    {
        var path = WriteBytes("math.bin", Enumerable.Range(0, 10).Select(static i => (byte)i).ToArray());
        var reader = new FileChunkReader();

        var first = await reader.ReadAsync(path, offset: 0, maxBytes: 4);
        first.TotalSize.Should().Be(10);
        first.Offset.Should().Be(0);
        first.ChunkSize.Should().Be(4);
        first.NextOffset.Should().Be(4);
        Convert.FromBase64String(first.Base64Chunk).Should().Equal(0, 1, 2, 3);

        var tail = await reader.ReadAsync(path, offset: 8, maxBytes: 10);
        tail.ChunkSize.Should().Be(2);
        tail.NextOffset.Should().BeNull();
        Convert.FromBase64String(tail.Base64Chunk).Should().Equal(8, 9);

        var full = await reader.ReadAsync(path, offset: 0, maxBytes: 100);
        full.ChunkSize.Should().Be(10);
        full.NextOffset.Should().BeNull();

        var eof = await reader.ReadAsync(path, offset: 10, maxBytes: 4);
        eof.ChunkSize.Should().Be(0);
        eof.NextOffset.Should().BeNull();
        eof.Base64Chunk.Should().BeEmpty();

        var beyond = await reader.ReadAsync(path, offset: 25, maxBytes: 4);
        beyond.ChunkSize.Should().Be(0);
        beyond.NextOffset.Should().BeNull();
        beyond.TotalSize.Should().Be(10);
    }

    [Fact]
    public async Task ReadAsync_CapsChunkSizeAt16MiB()
    {
        var path = Path.Combine(_root, "large.bin");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.SetLength(20L * 1024 * 1024);
        }

        var reader = new FileChunkReader();
        var chunk = await reader.ReadAsync(path, offset: 0, maxBytes: 20 * 1024 * 1024);

        chunk.ChunkSize.Should().Be(FileChunkReader.MaxChunkBytes);
        chunk.NextOffset.Should().Be(FileChunkReader.MaxChunkBytes);
        chunk.TotalSize.Should().Be(20L * 1024 * 1024);
    }

    [Fact]
    public async Task ReadAsync_RejectsArtifactsLargerThan256MiB()
    {
        var path = Path.Combine(_root, "too-large.bin");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            stream.SetLength(FileChunkReader.MaxArtifactBytes + 1);
        }

        var reader = new FileChunkReader();
        var act = () => reader.ReadAsync(path, offset: 0, maxBytes: 1024);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*256 MiB streaming ceiling*");
    }


    [Fact]
    public async Task ReadAsync_ComputesSha256ForTheEntireFile()
    {
        var bytes = Enumerable.Range(0, 256 * 1024).Select(static i => (byte)(i % 251)).ToArray();
        var path = WriteBytes("digest.bin", bytes);
        var reader = new FileChunkReader();

        var chunk = await reader.ReadAsync(path, offset: 128, maxBytes: 4096);

        chunk.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [Fact]
    public async Task ReadAsync_EvictsShaCacheEntriesWhenCapacityIsExceeded()
    {
        var clock = new FakeTimeProvider();
        var reader = new FileChunkReader(clock, maxShaCacheEntries: 1, shaCacheTtl: TimeSpan.FromMinutes(5));
        var first = WriteBytes("first.bin", new byte[] { 1, 2, 3 });
        var second = WriteBytes("second.bin", new byte[] { 4, 5, 6 });

        await reader.ReadAsync(first, offset: 0, maxBytes: 3);
        reader.ShaCacheEntryCount.Should().Be(1);

        clock.Advance(TimeSpan.FromSeconds(1));
        await reader.ReadAsync(second, offset: 0, maxBytes: 3);

        reader.ShaCacheEntryCount.Should().Be(1);
    }

    [Fact]
    public async Task ReadAsync_InvalidatesShaCacheWhenLastWriteChanges()
    {
        var path = WriteBytes("hash.bin", new byte[] { 1, 2, 3, 4 });
        var reader = new FileChunkReader();

        var first = await reader.ReadAsync(path, offset: 0, maxBytes: 4);
        File.WriteAllBytes(path, new byte[] { 4, 3, 2, 1 });
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        var second = await reader.ReadAsync(path, offset: 0, maxBytes: 4);

        first.Sha256.Should().NotBe(second.Sha256);
        second.Sha256.Should().Be(Convert.ToHexString(SHA256.HashData(new byte[] { 4, 3, 2, 1 })).ToLowerInvariant());
    }

    [Fact]
    public async Task ReadAsync_IsSafeForConcurrentReaders()
    {
        var bytes = Enumerable.Range(0, 64 * 1024).Select(static i => (byte)(i % 251)).ToArray();
        var path = WriteBytes("concurrent.bin", bytes);
        var reader = new FileChunkReader();

        var tasks = Enumerable.Range(0, 16)
            .Select(i => reader.ReadAsync(path, offset: i * 128, maxBytes: 4096))
            .ToArray();

        var chunks = await Task.WhenAll(tasks);

        chunks.Should().OnlyContain(chunk => chunk.ChunkSize > 0);
        chunks.Select(chunk => chunk.Sha256).Distinct().Should().ContainSingle();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    private string WriteBytes(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
