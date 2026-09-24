using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.Core.Captures;

internal sealed class StringSnapshotEncoding : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString();

    public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString() ?? throw new JsonException("Null string key.");

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        Validate(value);
        writer.WriteStringValue(value);
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        Validate(value);
        writer.WritePropertyName(value);
    }

    private static void Validate(ReadOnlySpan<char> value)
    {
        // Utf8JsonWriter otherwise replaces lone UTF-16 surrogates with U+FFFD silently.
        while (!value.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(value, out _, out var consumed) != OperationStatus.Done)
                throw new JsonException("Snapshot string contains an unpaired UTF-16 surrogate.");
            value = value[consumed..];
        }
    }
}
