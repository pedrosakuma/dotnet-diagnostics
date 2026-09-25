using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Triage;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>Allowlisted parent-only evidence; child snapshots live in their referenced artifacts.</summary>
public sealed record DurableCaptureParentMetadata(
    int Version, DurableSweepMetadata? Sweep, DurableGcActivitiesMetadata? GcActivities);

public sealed record DurableSweepMetadata(
    int DurationSeconds, TriageResult Triage, ProcessResources? Resource,
    IReadOnlyDictionary<string, string?> ArtifactIds, IReadOnlyList<string> Failures);

public sealed record DurableCorrelatedSideMetadata(
    string Status, DateTimeOffset RequestedStart, DateTimeOffset RequestedEnd,
    DateTimeOffset? ObservedStart, DateTimeOffset? ObservedEnd, string? ArtifactId, string? UnavailableReason);

public sealed record DurableGcActivitiesMetadata(
    int ProcessId, DateTimeOffset? ProcessStartedAt, string Status,
    DurableCorrelatedSideMetadata Gc, DurableCorrelatedSideMetadata Activities,
    DateTimeOffset? IntersectionStart, DateTimeOffset? IntersectionEnd, double? StartupSkewMs,
    GcOverlayResult? Overlay, string? OverlayUnavailableReason, IReadOnlyList<string> Notes);

internal static class DurableCaptureParentMetadataCodec
{
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private static readonly JsonTypeInfo<DurableCaptureParentMetadata> TypeInfo =
        (JsonTypeInfo<DurableCaptureParentMetadata>)Options.GetTypeInfo(typeof(DurableCaptureParentMetadata));

    internal static DurableCaptureParentMetadata? Project(object? result,
        IReadOnlyDictionary<string, string> artifactByHandle, int maxArtifacts)
        => result switch
        {
            SweepResult sweep => new(1, ProjectSweep(sweep, artifactByHandle, maxArtifacts), null),
            GcActivitiesCapture pair => new(1, null, new(pair.ProcessId, pair.ProcessStartedAt, pair.Status,
                Side(pair.Gc, artifactByHandle), Side(pair.Activities, artifactByHandle),
                pair.IntersectionStart, pair.IntersectionEnd, pair.StartupSkewMs,
                pair.Overlay, pair.OverlayUnavailableReason, pair.Notes)),
            _ => null,
        };

    internal static void Write(Utf8JsonWriter writer, DurableCaptureParentMetadata? metadata)
    {
        if (metadata is null) { writer.WriteNullValue(); return; }
        Validate(metadata);
        JsonSerializer.Serialize(writer, metadata, TypeInfo);
    }

    internal static DurableCaptureParentMetadata? Read(JsonElement element, CaptureInfo capture,
        IReadOnlySet<string> descendants, int maxArtifacts)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        RejectDuplicateProperties(element);
        var metadata = element.Deserialize(TypeInfo) ?? throw new InvalidDataException("Parent metadata cannot be null.");
        Validate(metadata);
        if (metadata.Sweep is { } sweep)
        {
            if (sweep.ArtifactIds.Count > maxArtifacts)
                throw new InvalidDataException("Sweep metadata artifact count exceeds MaxArtifacts.");
            var ids = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var reference in sweep.ArtifactIds)
                ids.Add(reference.Key, Resolve(capture, descendants, reference.Value));
            return metadata with { Sweep = sweep with { ArtifactIds = ids } };
        }
        var pair = metadata.GcActivities!;
        return metadata with { GcActivities = pair with
        {
            Gc = pair.Gc with { ArtifactId = Resolve(capture, descendants, pair.Gc.ArtifactId) },
            Activities = pair.Activities with { ArtifactId = Resolve(capture, descendants, pair.Activities.ArtifactId) },
        } };
    }

    private static DurableSweepMetadata ProjectSweep(SweepResult sweep,
        IReadOnlyDictionary<string, string> artifactByHandle, int maxArtifacts)
    {
        if (sweep.Handles.Count > maxArtifacts)
            throw new InvalidDataException("Sweep metadata handle count exceeds MaxArtifacts.");
        var ids = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var handle in sweep.Handles)
        {
            if (ids.Count == maxArtifacts)
                throw new InvalidDataException("Sweep metadata handle count exceeds MaxArtifacts.");
            ids.Add(handle.Key, Map(handle.Value, artifactByHandle));
        }
        return new(sweep.DurationSeconds, sweep.Triage, sweep.Resource, ids, sweep.Failures);
    }

    private static DurableCorrelatedSideMetadata Side<T>(CorrelatedCaptureSide<T> side,
        IReadOnlyDictionary<string, string> artifactByHandle)
        => new(side.Status, side.RequestedStart, side.RequestedEnd, side.ObservedStart, side.ObservedEnd,
            Map(side.Handle?.Id, artifactByHandle), side.UnavailableReason);

    private static string? Map(string? handle, IReadOnlyDictionary<string, string> artifactByHandle)
        => handle is null ? null : artifactByHandle.TryGetValue(handle, out var id)
            ? id : throw new InvalidDataException("Parent metadata references a child handle without a retained artifact.");

    private static string? Resolve(CaptureInfo capture, IReadOnlySet<string> descendants, string? reference)
    {
        if (reference is null) return null;
        var resolved = DurableCaptureCompositionCodec.ResolveIdentity(capture, reference);
        return descendants.Contains(resolved) ? resolved
            : throw new InvalidDataException("Parent metadata must reference its own retained descendants.");
    }

    private static void Validate(DurableCaptureParentMetadata metadata)
    {
        if (metadata.Version != 1 || (metadata.Sweep is null) == (metadata.GcActivities is null))
            throw new InvalidDataException("Unsupported parent metadata version or discriminator.");
        if (metadata.Sweep is { } sweep &&
            (sweep.DurationSeconds < 0 || sweep.Triage is null || sweep.Triage.Evidence is null ||
            sweep.Triage.Verdict is null || sweep.Triage.Assessment is null ||
            sweep.ArtifactIds is null || sweep.Failures is null ||
            sweep.Resource is { Notes: null } || sweep.Resource?.Trend is { Samples: null }))
            throw new InvalidDataException("Required sweep parent metadata is missing or invalid.");
        if (metadata.GcActivities is { } pair &&
            (pair.ProcessId <= 0 || pair.Status is null || pair.Gc is null || pair.Activities is null || pair.Notes is null ||
            pair.Gc.Status is null || pair.Activities.Status is null))
            throw new InvalidDataException("Required correlated parent metadata is missing or invalid.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate parent metadata property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            MaxDepth = 32,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            TypeInfoResolver = DurableCaptureParentJsonContext.Default.WithAddedModifier(static info =>
            {
                foreach (var property in info.Properties)
                    if (property.Get is not null && property.Set is not null) property.IsRequired = true;
            }),
        };
        options.MakeReadOnly();
        return options;
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(DurableCaptureParentMetadata))]
internal sealed partial class DurableCaptureParentJsonContext : JsonSerializerContext;
