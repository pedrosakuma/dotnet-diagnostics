using System.Security.Cryptography;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Bytes;
using DotnetDiagnostics.Core.UseCases;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Unit coverage for <see cref="ByteMaterializationUseCases.MaterializeDumpBytes"/> (issue #288 PR4).
/// Drives the chunk-streaming loop with an in-memory fake <see cref="IDumpByteSource"/> so the
/// staging-file / SHA-verification / atomic-move pipeline and the chunk-invariant guards are exercised
/// without a live process or a real dump file.
/// </summary>
public sealed class ByteMaterializationUseCasesTests
{
    [Fact]
    public async Task MaterializeDumpBytes_MultipleChunks_WritesVerifiedFile()
    {
        var payload = RandomBytes(10_000);
        var source = new FakeDumpByteSource(payload, chunkSize: 4096);
        var output = NewTempPath();
        try
        {
            var result = await ByteMaterializationUseCases.MaterializeDumpBytes(
                source, principalAllowsLiteralScope: true, "dump.dmp", output, maxBytes: 4096,
                logger: null, principalName: null, CancellationToken.None);

            result.IsError.Should().BeFalse(result.Summary);
            result.Data.Should().NotBeNull();
            result.Data!.TotalBytes.Should().Be(payload.Length);
            result.Data.OutputPath.Should().Be(Path.GetFullPath(output));
            result.Data.Sha256.Should().Be(Sha256Hex(payload));

            File.Exists(output).Should().BeTrue();
            (await File.ReadAllBytesAsync(output)).Should().Equal(payload);
            source.FetchCount.Should().BeGreaterThan(1);
        }
        finally
        {
            TryDelete(output);
        }
    }

    [Fact]
    public async Task MaterializeDumpBytes_SingleChunk_Succeeds()
    {
        var payload = RandomBytes(512);
        var source = new FakeDumpByteSource(payload, chunkSize: 4096);
        var output = NewTempPath();
        try
        {
            var result = await ByteMaterializationUseCases.MaterializeDumpBytes(
                source, principalAllowsLiteralScope: true, "dump.dmp", output, maxBytes: 4096,
                logger: null, principalName: null, CancellationToken.None);

            result.IsError.Should().BeFalse(result.Summary);
            (await File.ReadAllBytesAsync(output)).Should().Equal(payload);
            source.FetchCount.Should().Be(1);
        }
        finally
        {
            TryDelete(output);
        }
    }

    [Fact]
    public async Task MaterializeDumpBytes_LiteralScopeDenied_ReturnsForbidden_AndWritesNothing()
    {
        var source = new FakeDumpByteSource(RandomBytes(128), chunkSize: 4096);
        var output = NewTempPath();
        try
        {
            var result = await ByteMaterializationUseCases.MaterializeDumpBytes(
                source, principalAllowsLiteralScope: false, "dump.dmp", output, maxBytes: 4096,
                logger: null, principalName: null, CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Error!.Kind.Should().Be("Forbidden");
            source.FetchCount.Should().Be(0);
            File.Exists(output).Should().BeFalse();
        }
        finally
        {
            TryDelete(output);
        }
    }

    [Fact]
    public async Task MaterializeDumpBytes_ShaDriftMidStream_FailsIntegrity_AndLeavesNoPartialFile()
    {
        var payload = RandomBytes(10_000);
        var source = new FakeDumpByteSource(payload, chunkSize: 4096) { CorruptShaAfterFirstChunk = true };
        var output = NewTempPath();
        try
        {
            var result = await ByteMaterializationUseCases.MaterializeDumpBytes(
                source, principalAllowsLiteralScope: true, "dump.dmp", output, maxBytes: 4096,
                logger: null, principalName: null, CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Error!.Kind.Should().Be("IntegrityError");
            File.Exists(output).Should().BeFalse("a failed materialisation must not leave a partial artifact at the destination");
        }
        finally
        {
            TryDelete(output);
        }
    }

    [Fact]
    public async Task MaterializeDumpBytes_OverwritesExistingFileAtomically()
    {
        var payload = RandomBytes(8_000);
        var source = new FakeDumpByteSource(payload, chunkSize: 4096);
        var output = NewTempPath();
        await File.WriteAllBytesAsync(output, RandomBytes(3));
        try
        {
            var result = await ByteMaterializationUseCases.MaterializeDumpBytes(
                source, principalAllowsLiteralScope: true, "dump.dmp", output, maxBytes: 4096,
                logger: null, principalName: null, CancellationToken.None);

            result.IsError.Should().BeFalse(result.Summary);
            (await File.ReadAllBytesAsync(output)).Should().Equal(payload);
        }
        finally
        {
            TryDelete(output);
        }
    }

    [Fact]
    public async Task MaterializeDumpBytes_WritesOwnerOnlyPermissions_OnPosix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var payload = RandomBytes(5_000);
        var source = new FakeDumpByteSource(payload, chunkSize: 4096);
        var output = NewTempPath();
        try
        {
            var result = await ByteMaterializationUseCases.MaterializeDumpBytes(
                source, principalAllowsLiteralScope: true, "dump.dmp", output, maxBytes: 4096,
                logger: null, principalName: null, CancellationToken.None);

            result.IsError.Should().BeFalse(result.Summary);
            var mode = File.GetUnixFileMode(output);
            (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite))
                .Should().Be(UnixFileMode.None, "materialised artifacts must be owner-only (0600)");
        }
        finally
        {
            TryDelete(output);
        }
    }

    [Fact]
    public async Task MaterializeDumpBytes_UnwritableOutputDirectory_ReturnsStructuredError_NoCrash()
    {
        var payload = RandomBytes(2_000);
        var source = new FakeDumpByteSource(payload, chunkSize: 4096);
        // Point --out at a path whose parent is an existing FILE, so Directory.CreateDirectory throws
        // an IOException — exercising the output-write error path (the dump path is not AttachGuarded).
        var blockingFile = NewTempPath();
        await File.WriteAllBytesAsync(blockingFile, RandomBytes(4));
        var output = Path.Combine(blockingFile, "nested", "out.bin");
        try
        {
            var result = await ByteMaterializationUseCases.MaterializeDumpBytes(
                source, principalAllowsLiteralScope: true, "dump.dmp", output, maxBytes: 4096,
                logger: null, principalName: null, CancellationToken.None);

            result.IsError.Should().BeTrue();
            result.Error!.Kind.Should().Be("OutputWriteFailed");
        }
        finally
        {
            TryDelete(blockingFile);
        }
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string NewTempPath() => Path.Combine(Path.GetTempPath(), "byte-mat-" + Guid.NewGuid().ToString("N") + ".bin");

    private static void TryDelete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class FakeDumpByteSource : IDumpByteSource
    {
        private readonly byte[] _payload;
        private readonly int _chunkSize;
        private readonly string _sha;

        public FakeDumpByteSource(byte[] payload, int chunkSize)
        {
            _payload = payload;
            _chunkSize = chunkSize;
            _sha = Sha256Hex(payload);
        }

        public int FetchCount { get; private set; }

        public bool CorruptShaAfterFirstChunk { get; init; }

        public Task<ByteFetchEnvelope> FetchAsync(
            string dumpFilePath,
            long offset = 0,
            int maxBytes = FileChunkReader.DefaultChunkBytes,
            CancellationToken cancellationToken = default)
        {
            FetchCount++;
            var take = (int)Math.Min(Math.Min(_chunkSize, maxBytes), _payload.Length - offset);
            take = Math.Max(take, 0);
            var slice = new byte[take];
            Array.Copy(_payload, offset, slice, 0, take);
            var next = offset + take;
            var hasMore = next < _payload.Length;

            var sha = CorruptShaAfterFirstChunk && FetchCount > 1
                ? new string('0', 64)
                : _sha;

            return Task.FromResult(new ByteFetchEnvelope
            {
                Kind = "dump",
                Asset = "dump",
                Identifier = dumpFilePath,
                SourcePath = "/artifacts/" + dumpFilePath,
                TotalSize = _payload.Length,
                Sha256 = sha,
                Offset = offset,
                ChunkSize = take,
                Base64Chunk = Convert.ToBase64String(slice),
                NextOffset = hasMore ? next : null,
            });
        }
    }
}
