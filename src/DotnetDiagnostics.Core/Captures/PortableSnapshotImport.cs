using System.Text.Json;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.Captures;

internal static class PortableSnapshotImport
{
    internal static CaptureSnapshot ValidateAndMap(CaptureInfo source, CaptureArtifactInfo artifact,
        CaptureSnapshot original, IReadOnlyDictionary<string, string> mapping, CaptureStoreOptions store,
        PortableCaptureOptions limits, long metadataBytes, ref long totalTokens,
        out DurableCaptureRecordStreamInfo? stream, out IReadOnlyList<DurableCaptureChild>? sourceChildren)
    {
        var tokens = CheckJson(original.Utf8Json.Span, limits.MaxTokensPerSnapshot, 64);
        totalTokens = checked(totalTokens + tokens);
        PortableBounds.Check("MaxTokensPerCapture", totalTokens, limits.MaxTokensPerCapture);
        PortableBounds.Check("HostRetainedBytes", checked(metadataBytes + original.Utf8Json.Length * 8L + tokens * 256L),
            PortableBounds.HostBytes);
        stream = null;
        sourceChildren = null;
        var snapshot = original;
        if (snapshot.Version == DurableCaptureSnapshotMetadata.Version)
            (snapshot, stream) = DurableCaptureSnapshotMetadata.Decode(artifact.Kind, snapshot, store.MaxSnapshotBytes);
        if (snapshot.Version == DurableCaptureCompositionCodec.SnapshotVersion)
        {
            var composition = DurableCaptureCompositionCodec.Decode(artifact.Kind, artifact.ArtifactId, snapshot, source, store);
            sourceChildren = composition.Children;
            var children = composition.Children.Select(child => child with
            {
                ArtifactId = mapping[child.ArtifactId], ParentArtifactId = mapping[child.ParentArtifactId]
            }).ToArray();
            var metadata = composition.Metadata;
            if (metadata?.Sweep is { } sweep)
                metadata = metadata with { Sweep = sweep with
                {
                    ArtifactIds = sweep.ArtifactIds.ToDictionary(static pair => pair.Key,
                        pair => pair.Value is null ? null : mapping[pair.Value], StringComparer.Ordinal)
                } };
            if (metadata?.GcActivities is { } pair)
                metadata = metadata with { GcActivities = pair with
                {
                    Gc = pair.Gc with { ArtifactId = Map(pair.Gc.ArtifactId) },
                    Activities = pair.Activities with { ArtifactId = Map(pair.Activities.ArtifactId) }
                } };
            snapshot = snapshot with { Utf8Json = DurableCaptureCompositionCodec.Encode(artifact.Kind,
                new(children) { Metadata = metadata }, store.MaxSnapshotBytes) };
        }
        else if (snapshot.Version == 0)
        {
            using var document = JsonDocument.Parse(snapshot.Utf8Json);
            if (stream is null || document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().Any())
                throw Corrupt("Snapshot.MetadataOnly");
        }
        else
        {
            _ = CaptureArtifactCodec.Decode(artifact.Kind, snapshot.Version, snapshot.Utf8Json.Span, store.MaxSnapshotBytes, portable: true);
        }
        if (stream is not null)
            snapshot = new(DurableCaptureSnapshotMetadata.Version,
                DurableCaptureSnapshotMetadata.Encode(artifact.Kind, snapshot.Version, snapshot.Utf8Json.Span,
                    stream, store.MaxSnapshotBytes), artifact.Kind);
        return snapshot;

        string? Map(string? id) => id is null ? null : mapping[id];
    }

    internal static long CheckJson(ReadOnlySpan<byte> bytes, long maximum, int depth)
    {
        _ = CapturePackage.Utf8.GetCharCount(bytes);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = depth });
        var objects = new Stack<HashSet<string>?>();
        var charges = new Stack<long>();
        long count = 0, retained = 0;
        while (reader.Read())
        {
            PortableBounds.Check("JsonTokens", ++count, maximum);
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                objects.Push(reader.TokenType == JsonTokenType.StartObject ? new(StringComparer.Ordinal) : null);
                charges.Push(0);
            }
            else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
            {
                objects.Pop();
                retained -= charges.Pop();
            }
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var charge = checked(128 + 2L * reader.ValueSpan.Length);
                retained = checked(retained + charge);
                PortableBounds.Check("PortableMetadataBytes", retained, 4 * 1024 * 1024);
                var charged = charges.Pop();
                charges.Push(charged + charge);
                if (!objects.Peek()!.Add(reader.GetString()!)) throw Corrupt("Snapshot.DuplicateProperty");
            }
            else if (reader.TokenType == JsonTokenType.Number &&
                (!reader.TryGetDouble(out var number) || !double.IsFinite(number)))
                throw Corrupt("Snapshot.NonFinite");
        }
        if (count == 0 || objects.Count != 0) throw Corrupt("Snapshot.Json");
        return count;
    }

    private static CaptureStoreException Corrupt(string reason) =>
        CapturePackage.Error(CaptureErrorCode.CorruptPackage, reason);
}
