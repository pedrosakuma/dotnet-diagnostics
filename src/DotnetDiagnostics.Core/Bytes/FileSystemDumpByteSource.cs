using DotnetDiagnostics.Core.Artifacts;

namespace DotnetDiagnostics.Core.Bytes;

public sealed class FileSystemDumpByteSource : IDumpByteSource
{
    private readonly IArtifactRootProvider _artifactRootProvider;
    private readonly FileChunkReader _chunkReader;

    public FileSystemDumpByteSource(
        IArtifactRootProvider artifactRootProvider,
        FileChunkReader? chunkReader = null)
    {
        _artifactRootProvider = artifactRootProvider ?? throw new ArgumentNullException(nameof(artifactRootProvider));
        _chunkReader = chunkReader ?? new FileChunkReader();
    }

    public async Task<ByteFetchEnvelope> FetchAsync(
        string dumpFilePath,
        long offset = 0,
        int maxBytes = FileChunkReader.DefaultChunkBytes,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = SafeArtifactPath.ResolvePath(
            _artifactRootProvider.Root,
            dumpFilePath,
            parameterName: nameof(dumpFilePath));

        var chunk = await _chunkReader.ReadAsync(resolvedPath, offset, maxBytes, cancellationToken).ConfigureAwait(false);
        return new ByteFetchEnvelope
        {
            Kind = "dump",
            Asset = "dump",
            Identifier = resolvedPath,
            SourcePath = resolvedPath,
            TotalSize = chunk.TotalSize,
            Sha256 = chunk.Sha256,
            Offset = chunk.Offset,
            ChunkSize = chunk.ChunkSize,
            Base64Chunk = chunk.Base64Chunk,
            NextOffset = chunk.NextOffset,
        };
    }
}
