namespace DotnetDiagnostics.Core.Bytes;

public interface IModuleByteSource
{
    Task<ByteFetchEnvelope> FetchAsync(
        int processId,
        Guid moduleVersionId,
        string asset = "pe",
        long offset = 0,
        int maxBytes = FileChunkReader.DefaultChunkBytes,
        CancellationToken cancellationToken = default);
}
