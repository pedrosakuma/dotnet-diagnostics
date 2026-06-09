using System.Collections.Immutable;
using Microsoft.Diagnostics.Runtime;

namespace DotnetDiagnostics.Core.Dump;

public sealed record GcHandlesView(
    int TotalHandles,
    ImmutableArray<GcHandleBucket> ByKind,
    ImmutableArray<string> Notes);

public sealed record GcHandleBucket(
    string Kind,
    int Count,
    long RetainedBytes,
    ImmutableArray<GcHandleTypeStat> TopTypes);

public sealed record GcHandleTypeStat(
    string TypeFullName,
    int Count,
    long RetainedBytes,
    TypeIdentity? Identity);

internal static class GcHandleAggregation
{
    private const string UnknownTypeName = "<collected-or-unresolved>";
    private static readonly ImmutableArray<string> BucketOrder =
    [
        "Pinned",
        "Normal",
        "Weak",
        "WeakTrackResurrection",
        "Dependent",
        "AsyncPinned",
    ];

    internal static GcHandlesView Aggregate(IEnumerable<GcHandleSample> handles, int topTypesPerBucket = 5)
    {
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topTypesPerBucket);

        var totalHandles = 0;
        var buckets = BucketOrder.ToDictionary(kind => kind, static kind => new RawGcHandleBucket(kind), StringComparer.Ordinal);
        var unsupportedKinds = new Dictionary<ClrHandleKind, RawUnsupportedHandleKind>();

        foreach (var handle in handles)
        {
            totalHandles++;
            var bucketKind = MapBucketKind(handle.Kind);
            if (bucketKind is null)
            {
                if (!unsupportedKinds.TryGetValue(handle.Kind, out var unsupported))
                {
                    unsupported = new RawUnsupportedHandleKind();
                    unsupportedKinds[handle.Kind] = unsupported;
                }

                unsupported.Count++;
                unsupported.RetainedBytes += handle.RetainedBytes;
                continue;
            }

            var bucket = buckets[bucketKind];
            bucket.Count++;
            bucket.RetainedBytes += handle.RetainedBytes;

            var typeFullName = string.IsNullOrWhiteSpace(handle.TypeFullName)
                ? UnknownTypeName
                : handle.TypeFullName;
            var typeKey = new GcHandleTypeKey(
                typeFullName,
                handle.Identity?.ModuleVersionId,
                handle.Identity?.MetadataToken,
                handle.Identity?.ModuleName,
                handle.Identity?.ModulePath);

            if (!bucket.Types.TryGetValue(typeKey, out var typeStat))
            {
                typeStat = new RawGcHandleTypeStat(typeFullName, handle.Identity);
                bucket.Types[typeKey] = typeStat;
            }

            typeStat.Count++;
            typeStat.RetainedBytes += handle.RetainedBytes;
        }

        var byKind = BucketOrder
            .Select(kind =>
            {
                var bucket = buckets[kind];
                var topTypes = bucket.Types.Values
                    .OrderByDescending(static stat => stat.Count)
                    .ThenByDescending(static stat => stat.RetainedBytes)
                    .ThenBy(static stat => stat.TypeFullName, StringComparer.Ordinal)
                    .Take(topTypesPerBucket)
                    .Select(static stat => new GcHandleTypeStat(stat.TypeFullName, stat.Count, stat.RetainedBytes, stat.Identity))
                    .ToImmutableArray();

                return new GcHandleBucket(kind, bucket.Count, bucket.RetainedBytes, topTypes);
            })
            .ToImmutableArray();

        var notes = unsupportedKinds.Count == 0
            ? ImmutableArray<string>.Empty
            :
            [
                $"Encountered {string.Join(", ", unsupportedKinds.OrderBy(static kvp => kvp.Key.ToString(), StringComparer.Ordinal).Select(static kvp => $"{kvp.Value.Count:N0} {kvp.Key} ({kvp.Value.RetainedBytes:N0} bytes)"))} handle(s). These ClrMD-internal kinds are counted in TotalHandles but are omitted from byKind because they do not map to public GCHandleType values."
            ];

        return new GcHandlesView(totalHandles, byKind, notes);
    }

    private static string? MapBucketKind(ClrHandleKind kind) => kind switch
    {
        ClrHandleKind.Pinned => "Pinned",
        ClrHandleKind.Strong => "Normal",
        ClrHandleKind.WeakShort => "Weak",
        ClrHandleKind.WeakLong => "WeakTrackResurrection",
        ClrHandleKind.Dependent => "Dependent",
        ClrHandleKind.AsyncPinned => "AsyncPinned",
        _ => null,
    };

    internal readonly record struct GcHandleSample(
        ClrHandleKind Kind,
        string? TypeFullName,
        long RetainedBytes,
        TypeIdentity? Identity);

    private readonly record struct GcHandleTypeKey(
        string TypeFullName,
        Guid? ModuleVersionId,
        int? MetadataToken,
        string? ModuleName,
        string? ModulePath);

    private sealed class RawGcHandleBucket
    {
        public RawGcHandleBucket(string kind)
        {
            Kind = kind;
        }

        public string Kind { get; }
        public int Count;
        public long RetainedBytes;
        public Dictionary<GcHandleTypeKey, RawGcHandleTypeStat> Types { get; } = new();
    }

    private sealed class RawGcHandleTypeStat
    {
        public RawGcHandleTypeStat(string typeFullName, TypeIdentity? identity)
        {
            TypeFullName = typeFullName;
            Identity = identity;
        }

        public string TypeFullName { get; }
        public TypeIdentity? Identity { get; }
        public int Count;
        public long RetainedBytes;
    }

    private sealed class RawUnsupportedHandleKind
    {
        public int Count;
        public long RetainedBytes;
    }
}
