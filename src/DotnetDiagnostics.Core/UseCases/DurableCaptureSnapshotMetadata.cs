using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>Producer-declared or observed record availability, independent of compatibility snapshots.</summary>
public sealed record DurableCaptureRecordStreamInfo(
    bool Available, long Offered, long Accepted, long? SourceRejected,
    IReadOnlyDictionary<string, long?> Sources, long SourceReportsRejected);

internal static class DurableCaptureSnapshotMetadata
{
    internal const int Version = 3;
    private const int MaxDepth = CaptureArtifactCodec.MaximumDepth + 2;

    internal static DurableCaptureRecordStreamInfo Stream(CaptureRecordingNode? node)
        => node is null
            ? new(false, 0, 0, null, new Dictionary<string, long?>(StringComparer.Ordinal), 0)
            : new(node.RecordStreamAvailable, node.Offered, node.Accepted,
                node.SourceRejected, node.Sources, node.SourceReportsRejected);

    internal static byte[] Encode(string kind, int snapshotVersion, ReadOnlySpan<byte> snapshot,
        DurableCaptureRecordStreamInfo stream, int maxBytes)
    {
        using var buffer = new BoundedSnapshotEncoding(maxBytes);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = MaxDepth }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("metadataVersion", 1);
            writer.WriteString("kind", kind);
            writer.WriteNumber("snapshotVersion", snapshotVersion);
            writer.WriteStartObject("recordStream");
            writer.WriteBoolean("available", stream.Available);
            writer.WriteNumber("offered", stream.Offered);
            writer.WriteNumber("accepted", stream.Accepted);
            Nullable(writer, "sourceRejected", stream.SourceRejected);
            writer.WriteNumber("sourceReportsRejected", stream.SourceReportsRejected);
            writer.WriteStartObject("sources");
            foreach (var source in stream.Sources) Nullable(writer, source.Key, source.Value);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WritePropertyName("snapshot");
            writer.WriteRawValue(snapshot, skipInputValidation: true);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    internal static (CaptureSnapshot Snapshot, DurableCaptureRecordStreamInfo Stream) Decode(
        string kind, CaptureSnapshot snapshot, int maxBytes)
    {
        if (snapshot.Version != Version || snapshot.Utf8Json.Length > maxBytes)
            throw new InvalidDataException("Unsupported or oversized snapshot metadata.");
        using var document = JsonDocument.Parse(snapshot.Utf8Json, new JsonDocumentOptions { MaxDepth = MaxDepth });
        var root = document.RootElement;
        Properties(root, "metadataVersion", "kind", "snapshotVersion", "recordStream", "snapshot");
        var version = root.GetProperty("snapshotVersion").GetInt32();
        if (root.GetProperty("metadataVersion").GetInt32() != 1 ||
            root.GetProperty("kind").GetString() != kind ||
            version is not (0 or CaptureArtifactCodec.FormatVersion or DurableCaptureCompositionCodec.SnapshotVersion))
            throw new InvalidDataException("Snapshot metadata kind or version is unsupported.");
        var data = root.GetProperty("recordStream");
        Properties(data, "available", "offered", "accepted", "sourceRejected", "sources", "sourceReportsRejected");
        var available = data.GetProperty("available").GetBoolean();
        var offered = Count(data.GetProperty("offered"));
        var accepted = Count(data.GetProperty("accepted"));
        var sourceRejected = NullableCount(data.GetProperty("sourceRejected"));
        var sourceReportsRejected = Count(data.GetProperty("sourceReportsRejected"));
        var sources = new Dictionary<string, long?>(StringComparer.Ordinal);
        var sourceData = data.GetProperty("sources");
        if (sourceData.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Source metadata must be an object.");
        foreach (var source in sourceData.EnumerateObject())
            if (sources.Count == 64 || string.IsNullOrWhiteSpace(source.Name) || Encoding.UTF8.GetByteCount(source.Name) > 1024 ||
                !sources.TryAdd(source.Name, NullableCount(source.Value)))
                throw new InvalidDataException("Source metadata exceeds its bound or contains duplicate names.");
        if (accepted > offered || !available && (offered != 0 || sourceRejected is not null || sources.Count != 0 || sourceReportsRejected != 0))
            throw new InvalidDataException("Snapshot stream availability or admission counts are inconsistent.");
        var payload = root.GetProperty("snapshot");
        if (payload.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Snapshot object expected.");
        return (new(version, Encoding.UTF8.GetBytes(payload.GetRawText()), kind),
            new(available, offered, accepted, sourceRejected, sources, sourceReportsRejected));
    }

    private static void Nullable(Utf8JsonWriter writer, string name, long? count)
    {
        if (count is { } value) writer.WriteNumber(name, value);
        else writer.WriteNull(name);
    }

    private static long? NullableCount(JsonElement element)
        => element.ValueKind == JsonValueKind.Null ? null : Count(element);

    private static long Count(JsonElement element)
    {
        var value = element.GetInt64();
        return value >= 0 ? value : throw new InvalidDataException("Snapshot metadata counts cannot be negative.");
    }

    private static void Properties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Snapshot metadata object expected.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new InvalidDataException("Unknown or duplicate snapshot metadata field.");
        if (seen.Count != expected.Length) throw new InvalidDataException("Missing snapshot metadata field.");
    }
}
