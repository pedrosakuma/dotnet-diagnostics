using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace DotnetDiagnostics.Core.Captures;

internal sealed record PortableMember(string Name, long Bytes, string Sha256);
internal sealed record PortableEntry(string EntryId, string? Label, string SourceCaptureId,
    CaptureFormatVersions Format, PortableMember[] Members);
internal sealed record PortableIndex(int ArchiveVersion, int RequiredArchiveReaderVersion,
    string[] RequiredFeatures, string BundleId, DateTimeOffset CreatedUtc, PortableEntry[] Entries);
internal sealed record PortableIndexSeal(int ArchiveVersion, long IndexBytes, string IndexSha256);
internal sealed record PortableExportReceipt(string OwnerId, PortableOperationKey Operation, string Fingerprint,
    string BundleId, DateTimeOffset CreatedUtc, DateTimeOffset BytesExpireUtc, long ReservationBytes, PortableExportResult? Result);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PortableIndex))]
[JsonSerializable(typeof(PortableIndexSeal))]
[JsonSerializable(typeof(PortableExportReceipt))]
[JsonSerializable(typeof(CaptureExportSelection[]))]
internal sealed partial class PortableCaptureJson : JsonSerializerContext
{
    internal static byte[] Encode<T>(T value, JsonTypeInfo<T> type, int maximum)
    {
        try
        {
            using var buffer = new BoundedSnapshotEncoding(maximum);
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = 16 }))
                JsonSerializer.Serialize(writer, value, type);
            return buffer.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw CapturePackage.Error(CaptureErrorCode.CapacityExceeded,
                FormattableString.Invariant($"PortableJsonBytes: maximum={maximum}; bounded encoding rejected the payload."), ex);
        }
    }
}
