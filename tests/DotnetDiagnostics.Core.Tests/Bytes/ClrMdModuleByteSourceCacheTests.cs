using System.Threading;
using DotnetDiagnostics.Core.Bytes;
using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.Bytes;

public sealed class ClrMdModuleByteSourceCacheTests
{
    [Fact]
    public async Task FetchAsync_CachesResolvedModuleMetadataAcrossChunks()
    {
        var assemblyPath = typeof(ClrMdModuleByteSourceCacheTests).Assembly.Location;
        var mvid = new MvidReader().TryRead(assemblyPath);
        mvid.Should().NotBeNull();

        var resolverCalls = 0;
        var source = new ClrMdModuleByteSource(
            chunkReader: new FileChunkReader(),
            mvidReader: new MvidReader(),
            logger: null,
            clock: TimeProvider.System,
            modulePathResolver: (processId, moduleVersionId, cancellationToken) =>
            {
                Interlocked.Increment(ref resolverCalls);
                return assemblyPath;
            },
            maxModuleResolutionCacheEntries: ClrMdModuleByteSource.DefaultModuleResolutionCacheEntries,
            moduleResolutionCacheTtl: ClrMdModuleByteSource.DefaultModuleResolutionCacheTtl,
            maxEmbeddedPdbCacheBytes: ClrMdModuleByteSource.DefaultEmbeddedPdbCacheBytes,
            embeddedPdbCacheTtl: ClrMdModuleByteSource.DefaultEmbeddedPdbCacheTtl,
            processStartTimeResolver: static _ => 100);

        var first = await source.FetchAsync(1234, mvid!.Value, asset: "pe", offset: 0, maxBytes: 512);
        first.NextOffset.Should().NotBeNull();

        var second = await source.FetchAsync(1234, mvid.Value, asset: "pe", offset: first.NextOffset!.Value, maxBytes: 512);

        resolverCalls.Should().Be(1);
        source.ModuleResolutionCacheCount.Should().Be(1);
        first.SourcePath.ShouldMatchFileSystemPath(Path.GetFullPath(assemblyPath));
        second.SourcePath.ShouldMatchFileSystemPath(Path.GetFullPath(assemblyPath));
    }


    [Fact]
    public async Task FetchAsync_ReProbesWhenSiblingPdbAppearsAfterANegativeCacheHit()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-artifacts", nameof(ClrMdModuleByteSourceCacheTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var assemblyPath = Path.Combine(root, "sample.dll");
        await File.WriteAllBytesAsync(assemblyPath, new byte[] { 0x4d, 0x5a, 0x01, 0x02, 0x03, 0x04 });

        try
        {
            var resolverCalls = 0;
            var source = new ClrMdModuleByteSource(
                chunkReader: new FileChunkReader(),
                mvidReader: new MvidReader(),
                logger: null,
                clock: TimeProvider.System,
                modulePathResolver: (processId, moduleVersionId, cancellationToken) =>
                {
                    Interlocked.Increment(ref resolverCalls);
                    return assemblyPath;
                },
                maxModuleResolutionCacheEntries: ClrMdModuleByteSource.DefaultModuleResolutionCacheEntries,
                moduleResolutionCacheTtl: TimeSpan.FromMinutes(5),
                maxEmbeddedPdbCacheBytes: ClrMdModuleByteSource.DefaultEmbeddedPdbCacheBytes,
                embeddedPdbCacheTtl: ClrMdModuleByteSource.DefaultEmbeddedPdbCacheTtl,
            processStartTimeResolver: static _ => 100);

            var moduleVersionId = Guid.NewGuid();
            var first = await source.FetchAsync(1234, moduleVersionId, asset: "pe", offset: 0, maxBytes: 4);
            first.CompanionPdbPath.Should().BeNull();

            var siblingPdbPath = Path.ChangeExtension(assemblyPath, ".pdb")!;
            await File.WriteAllBytesAsync(siblingPdbPath, new byte[] { 1, 2, 3, 4 });

            var pdb = await source.FetchAsync(1234, moduleVersionId, asset: "pdb", offset: 0, maxBytes: 4);

            resolverCalls.Should().Be(2);
            pdb.SourcePath.ShouldMatchFileSystemPath(Path.GetFullPath(siblingPdbPath));
            pdb.PdbIsEmbedded.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }


    [Fact]
    public async Task FetchAsync_ReResolvesModuleMetadataWhenProcessStartTimeChanges()
    {
        var assemblyPath = typeof(ClrMdModuleByteSourceCacheTests).Assembly.Location;
        var mvid = new MvidReader().TryRead(assemblyPath);
        mvid.Should().NotBeNull();

        var resolverCalls = 0;
        var processStartTimeUtcTicks = 100L;
        var source = new ClrMdModuleByteSource(
            chunkReader: new FileChunkReader(),
            mvidReader: new MvidReader(),
            logger: null,
            clock: TimeProvider.System,
            modulePathResolver: (processId, moduleVersionId, cancellationToken) =>
            {
                Interlocked.Increment(ref resolverCalls);
                return assemblyPath;
            },
            maxModuleResolutionCacheEntries: 4,
            moduleResolutionCacheTtl: TimeSpan.FromMinutes(5),
            maxEmbeddedPdbCacheBytes: ClrMdModuleByteSource.DefaultEmbeddedPdbCacheBytes,
            embeddedPdbCacheTtl: ClrMdModuleByteSource.DefaultEmbeddedPdbCacheTtl,
            processStartTimeResolver: _ => processStartTimeUtcTicks);

        await source.FetchAsync(1234, mvid!.Value, asset: "pe", offset: 0, maxBytes: 128);
        processStartTimeUtcTicks = 200L;
        await source.FetchAsync(1234, mvid.Value, asset: "pe", offset: 0, maxBytes: 128);

        resolverCalls.Should().Be(2);
    }

    [Fact]
    public async Task FetchAsync_ReResolvesModuleMetadataAfterCacheExpiry()
    {
        var assemblyPath = typeof(ClrMdModuleByteSourceCacheTests).Assembly.Location;
        var mvid = new MvidReader().TryRead(assemblyPath);
        mvid.Should().NotBeNull();

        var clock = new FakeTimeProvider();
        var resolverCalls = 0;
        var source = new ClrMdModuleByteSource(
            chunkReader: new FileChunkReader(),
            mvidReader: new MvidReader(),
            logger: null,
            clock: clock,
            modulePathResolver: (processId, moduleVersionId, cancellationToken) =>
            {
                Interlocked.Increment(ref resolverCalls);
                return assemblyPath;
            },
            maxModuleResolutionCacheEntries: 4,
            moduleResolutionCacheTtl: TimeSpan.FromSeconds(1),
            maxEmbeddedPdbCacheBytes: ClrMdModuleByteSource.DefaultEmbeddedPdbCacheBytes,
            embeddedPdbCacheTtl: ClrMdModuleByteSource.DefaultEmbeddedPdbCacheTtl,
            processStartTimeResolver: static _ => 100);

        await source.FetchAsync(1234, mvid!.Value, asset: "pe", offset: 0, maxBytes: 128);
        clock.Advance(TimeSpan.FromSeconds(2));
        await source.FetchAsync(1234, mvid.Value, asset: "pe", offset: 0, maxBytes: 128);

        resolverCalls.Should().Be(2);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
