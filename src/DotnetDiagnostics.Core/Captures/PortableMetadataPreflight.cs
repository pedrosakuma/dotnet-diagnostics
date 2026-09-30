using System.Text.Json;

namespace DotnetDiagnostics.Core.Captures;

internal static class PortableMetadataPreflight
{
    private sealed class Container(bool array, int limit)
    {
        internal bool Array { get; } = array;
        internal int Limit { get; } = limit;
        internal int Items { get; set; }
    }

    internal static void Check(ReadOnlySpan<byte> bytes, int depth, int entries)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = depth });
        var containers = new Stack<Container>();
        var arrayLimit = 64;
        var stringLimit = 1024;
        Span<char> text = stackalloc char[1024];
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = ReadText(ref reader, text, 1024);
                arrayLimit = name switch
                {
                    "entries" => entries, "members" => 3, "requiredFeatures" => 3, "RequiredFeatures" => 4, _ => 64
                };
                stringLimit = name switch
                {
                    "label" => 256,
                    "bundleId" or "entryId" or "sourceCaptureId" or "CaptureId" or "ArtifactId" or
                    "SourceArtifactId" or "EntryArtifactId" or "OriginArtifactId" or "LocalArtifactId" => 32,
                    "sha256" or "indexSha256" or "ManifestHash" or "DatabaseHash" or "Manifest" or "Database" or "Seal" => 64,
                    _ => 1024
                };
                continue;
            }
            if (reader.TokenType is JsonTokenType.EndArray or JsonTokenType.EndObject)
            {
                containers.Pop();
                continue;
            }
            if (containers.TryPeek(out var parent) && parent.Array)
                PortableBounds.Check("MetadataArrayItems", ++parent.Items, parent.Limit);
            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                containers.Push(new(reader.TokenType == JsonTokenType.StartArray, arrayLimit));
            else if (reader.TokenType == JsonTokenType.String)
                _ = ReadText(ref reader, text, stringLimit);
        }
    }

    private static string ReadText(ref Utf8JsonReader reader, scoped Span<char> buffer, int maximum)
    {
        int count;
        try { count = reader.CopyString(buffer[..maximum]); }
        catch (ArgumentException ex)
        {
            throw CapturePackage.Error(CaptureErrorCode.CapacityExceeded, "MetadataTextBytes: decoded text exceeds its bounded workspace.", ex);
        }
        PortableBounds.Check("MetadataTextBytes", CapturePackage.Utf8.GetByteCount(buffer[..count]), maximum);
        return new string(buffer[..count]);
    }
}
