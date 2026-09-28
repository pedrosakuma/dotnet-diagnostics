using System.Security.Cryptography;

namespace DotnetDiagnostics.TestSupport.OperatingEnvelope;

public static class OperatingEnvelopeEvidenceHasher
{
    public static async Task<string> HashAsync(
        Stream stream,
        Action checkDeadline,
        CancellationToken cancellationToken,
        int chunkSize = 64 * 1024)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(checkDeadline);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The evidence stream must be readable.", nameof(stream));
        }
        if (chunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[chunkSize];
        while (true)
        {
            checkDeadline();
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            checkDeadline();
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
