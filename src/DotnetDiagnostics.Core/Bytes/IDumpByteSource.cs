namespace DotnetDiagnostics.Core.Bytes;

public interface IDumpByteSource
{
    Task<ByteFetchEnvelope> FetchAsync(
        string dumpFilePath,
        long offset = 0,
        int maxBytes = FileChunkReader.DefaultChunkBytes,
        CancellationToken cancellationToken = default);
}
