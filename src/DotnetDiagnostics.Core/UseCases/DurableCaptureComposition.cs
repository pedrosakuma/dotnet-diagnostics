using System.Text.Json;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>Explicit child routes and completion evidence; these rows are not raw observations.</summary>
public sealed record DurableCaptureComposition(IReadOnlyList<DurableCaptureChild> Children)
{
    public DurableCaptureParentMetadata? Metadata { get; init; }
}

public sealed record DurableCaptureChild(
    string ArtifactId, string Kind, string Name, string ParentArtifactId,
    long Offered, long Accepted, long? SourceRejected, IReadOnlyDictionary<string, long?> Sources,
    long SourceReportsRejected, DiagnosticError? Error, bool Cancelled, bool SnapshotAvailable);

internal static class DurableCaptureCompositionCodec
{
    internal const int SnapshotVersion = 2;
    internal const string HandleKind = "capture-group";

    internal static byte[] Encode(string kind, DurableCaptureComposition composition, int maxBytes)
    {
        using var buffer = new BoundedSnapshotEncoding(maxBytes);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            writer.WriteNumber("compositionVersion", 2);
            writer.WriteStartArray("children");
            foreach (var child in composition.Children)
            {
                writer.WriteStartObject();
                writer.WriteString("artifactId", child.ArtifactId);
                writer.WriteString("kind", child.Kind);
                writer.WriteString("name", child.Name);
                writer.WriteString("parentArtifactId", child.ParentArtifactId);
                writer.WriteNumber("offered", child.Offered);
                writer.WriteNumber("accepted", child.Accepted);
                WriteNullable(writer, "sourceRejected", child.SourceRejected);
                writer.WriteNumber("sourceReportsRejected", child.SourceReportsRejected);
                writer.WriteStartObject("sources");
                foreach (var source in child.Sources) WriteNullable(writer, source.Key, source.Value);
                writer.WriteEndObject();
                if (child.Error is { } error)
                {
                    writer.WriteStartObject("error");
                    writer.WriteString("kind", error.Kind);
                    writer.WriteString("message", error.Message);
                    writer.WriteString("detail", error.Detail);
                    writer.WriteEndObject();
                }
                else writer.WriteNull("error");
                writer.WriteBoolean("cancelled", child.Cancelled);
                writer.WriteBoolean("snapshotAvailable", child.SnapshotAvailable);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("metadata");
            DurableCaptureParentMetadataCodec.Write(writer, composition.Metadata);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    internal static DurableCaptureComposition Decode(string kind, string artifactId, CaptureSnapshot snapshot,
        CaptureInfo capture, CaptureStoreOptions options)
    {
        if (snapshot.Version != SnapshotVersion || snapshot.Utf8Json.Length > options.MaxSnapshotBytes)
            throw new InvalidDataException("Unsupported or oversized composition snapshot.");
        using var document = JsonDocument.Parse(snapshot.Utf8Json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("compositionVersion", out var versionJson) ||
            versionJson.ValueKind != JsonValueKind.Number || !versionJson.TryGetInt32(out var version))
            throw new InvalidDataException("Missing or invalid composition version.");
        if (version == 1) RequireProperties(root, "kind", "compositionVersion", "children");
        else RequireProperties(root, "kind", "compositionVersion", "children", "metadata");
        if (root.GetProperty("kind").GetString() != kind || version is not (1 or 2))
            throw new InvalidDataException("Composition kind or version does not match.");
        var children = root.GetProperty("children");
        if (children.ValueKind != JsonValueKind.Array || children.GetArrayLength() > options.MaxArtifacts)
            throw new InvalidDataException("Composition child count exceeds MaxArtifacts.");
        var result = new List<DurableCaptureChild>(children.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in children.EnumerateArray())
        {
            RequireProperties(child, "artifactId", "kind", "name", "parentArtifactId", "offered", "accepted",
                "sourceRejected", "sourceReportsRejected", "sources", "error", "cancelled", "snapshotAvailable");
            var id = ResolveIdentity(capture, Text(child, "artifactId"));
            var childKind = Text(child, "kind");
            var name = Text(child, "name");
            var parent = ResolveIdentity(capture, Text(child, "parentArtifactId"));
            if (id == artifactId) throw new InvalidDataException("Composition cannot reference itself as a child.");
            if (!ids.Add(id)) throw new InvalidDataException("Composition contains a duplicate child identity.");
            if (!capture.Artifacts.Any(a => a.ArtifactId == id && a.Kind == childKind && a.Name == name))
                throw new InvalidDataException("Composition child identity, kind or name differs from artifact metadata.");
            var offered = Nonnegative(child.GetProperty("offered"));
            var accepted = Nonnegative(child.GetProperty("accepted"));
            if (accepted > offered) throw new InvalidDataException("Invalid composition admission accounting.");
            var sourcesJson = child.GetProperty("sources");
            if (sourcesJson.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Composition sources must be an object.");
            var sources = new Dictionary<string, long?>(StringComparer.Ordinal);
            foreach (var source in sourcesJson.EnumerateObject())
            {
                if (sources.Count == 64 || System.Text.Encoding.UTF8.GetByteCount(source.Name) > 1024 ||
                    !sources.TryAdd(source.Name, NullableNonnegative(source.Value)))
                    throw new InvalidDataException("Composition source count, name or uniqueness is invalid.");
            }
            DiagnosticError? error = null;
            var errorJson = child.GetProperty("error");
            if (errorJson.ValueKind != JsonValueKind.Null)
            {
                RequireProperties(errorJson, "kind", "message", "detail");
                error = new(Text(errorJson, "kind"), Text(errorJson, "message"), errorJson.GetProperty("detail").GetString());
            }
            result.Add(new(id, childKind, name, parent, offered, accepted,
                NullableNonnegative(child.GetProperty("sourceRejected")), sources,
                Nonnegative(child.GetProperty("sourceReportsRejected")), error,
                child.GetProperty("cancelled").GetBoolean(), child.GetProperty("snapshotAvailable").GetBoolean()));
        }
        foreach (var child in result)
        {
            var parent = child.ParentArtifactId;
            var depth = 0;
            while (parent != artifactId)
            {
                if (++depth > result.Count)
                    throw new InvalidDataException("Composition child references contain a cycle.");
                parent = result.FirstOrDefault(candidate => candidate.ArtifactId == parent)?.ParentArtifactId
                    ?? throw new InvalidDataException("Composition parent reference is unavailable.");
            }
        }
        return new(result.AsReadOnly())
        {
            Metadata = version == 1 ? null : DurableCaptureParentMetadataCodec.Read(
                root.GetProperty("metadata"), capture, ids, options.MaxArtifacts),
        };
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } known) writer.WriteNumber(name, known);
        else writer.WriteNull(name);
    }

    internal static string ResolveIdentity(CaptureInfo capture, string reference)
    {
        CaptureArtifactInfo? found = null;
        foreach (var artifact in capture.Artifacts)
        {
            if (artifact.ArtifactId != reference && artifact.SourceArtifactId != reference) continue;
            if (found is not null) throw new InvalidDataException("Composition artifact identity is ambiguous.");
            found = artifact;
        }
        return found?.ArtifactId
            ?? throw new InvalidDataException("Composition references are unavailable; open retained child artifacts individually.");
    }

    private static string Text(JsonElement element, string property)
        => element.GetProperty(property).GetString()
            ?? throw new InvalidDataException("Composition string fields cannot be null.");

    private static long? NullableNonnegative(JsonElement element)
        => element.ValueKind == JsonValueKind.Null ? null : Nonnegative(element);

    private static long Nonnegative(JsonElement element)
    {
        var value = element.GetInt64();
        return value >= 0 ? value : throw new InvalidDataException("Composition counts cannot be negative.");
    }

    private static void RequireProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Composition object expected.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || !found.Add(property.Name))
                throw new InvalidDataException("Unknown or duplicate composition property.");
        if (found.Count != expected.Length) throw new InvalidDataException("Missing composition property.");
    }
}
