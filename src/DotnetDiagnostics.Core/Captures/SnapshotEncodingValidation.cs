using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Memory;

namespace DotnetDiagnostics.Core.Captures;

/// <summary>
/// Enforces the allowlisted DTOs' reference nullability even on .NET 8. The serializer already
/// enforces value types and required fields. This prevents constructor defaults from laundering
/// malformed null collections into apparently empty observations.
/// </summary>
internal static class SnapshotEncodingValidation
{
    private static readonly ConcurrentDictionary<(Type Type, string Property), NullabilityInfo> Nullability = new();

    internal static void Validate(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options, bool portable = false)
        => ValidateValue(ref reader, type, options, nullable: false, annotation: null, portable, nonnegative: false);

    private static void ValidateValue(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options,
        bool nullable, NullabilityInfo? annotation, bool portable, bool nonnegative)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            if (!nullable && Nullable.GetUnderlyingType(type) is null)
                throw new JsonException($"Null is not valid for snapshot field of type {type.Name}.");
            return;
        }
        if (portable && reader.TokenType == JsonTokenType.Number &&
            (!reader.TryGetDouble(out var number) || !double.IsFinite(number) || nonnegative && number < 0))
            throw new JsonException("Portable snapshot numbers must be finite and typed counts cannot be negative.");
        type = StorageType(type);
        var info = options.GetTypeInfo(type);
        if (info.Kind == JsonTypeInfoKind.Object && reader.TokenType == JsonTokenType.StartObject)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected snapshot property.");
                var name = reader.GetString()!;
                var property = info.Properties.FirstOrDefault(p => p.Name == name);
                if (property?.Get is null || property.Set is null)
                    throw new JsonException($"Unknown or non-stored snapshot property '{name}'.");
                // Types come exclusively from source-generated metadata, never from input strings.
                // Reflection reads annotations only; it neither resolves names nor constructs objects.
                var propertyAnnotation = Nullability.GetOrAdd((type, name), static key =>
                    new NullabilityInfoContext().Create(key.Type.GetProperty(key.Property)
                        ?? throw new JsonException("Snapshot property metadata is unavailable.")));
                if (!reader.Read()) throw new JsonException("Missing snapshot value.");
                ValidateValue(ref reader, property.PropertyType, options,
                    propertyAnnotation.ReadState == NullabilityState.Nullable, propertyAnnotation, portable,
                    portable && IsCount(property.Name));
            }
        }
        else if (info.Kind == JsonTypeInfoKind.Enumerable && reader.TokenType == JsonTokenType.StartArray)
        {
            var elementType = type.IsArray ? type.GetElementType()! : type.GetGenericArguments()[0];
            var elementAnnotation = annotation?.ElementType ?? annotation?.GenericTypeArguments.FirstOrDefault();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                ValidateValue(ref reader, elementType, options,
                    elementAnnotation?.ReadState == NullabilityState.Nullable, elementAnnotation, portable, nonnegative);
        }
        else if (info.Kind == JsonTypeInfoKind.Dictionary && reader.TokenType == JsonTokenType.StartObject)
        {
            var valueType = type.GetGenericArguments()[1];
            var valueAnnotation = annotation?.GenericTypeArguments.ElementAtOrDefault(1);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName || !reader.Read())
                    throw new JsonException("Invalid snapshot dictionary.");
                ValidateValue(ref reader, valueType, options,
                    valueAnnotation?.ReadState == NullabilityState.Nullable, valueAnnotation, portable, nonnegative);
            }

        }
        else reader.Skip();
    }

    private static bool IsCount(string property) =>
        property.EndsWith("Count", StringComparison.Ordinal) || property.EndsWith("Counts", StringComparison.Ordinal) ||
        property.EndsWith("Samples", StringComparison.Ordinal) ||
        property is "Offered" or "Accepted" or "Persisted" or "SourceRejected" or "RecordRejected" or
            "QueueRejected" or "StorageRejected" or "Pending" or "SnapshotRejected";

    private static Type StorageType(Type type)
    {
        if (type == typeof(CallTreeNode)) return typeof(List<CallTreeSnapshotRow>);
        if (type == typeof(IReadOnlyDictionary<SymbolRef, SourceLocation>))
            return typeof(List<SymbolSnapshotRow<SourceLocation>>);
        if (type == typeof(IReadOnlyDictionary<SymbolRef, MethodIdentity>))
            return typeof(List<SymbolSnapshotRow<MethodIdentity>>);
        return type;
    }
}
