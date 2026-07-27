using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;

namespace DotnetDiagnostics.Core.Comparison;

/// <summary>Projects a heap snapshot into comparable rows and typed diff metrics.</summary>
public sealed class HeapSnapshotComparableProjector : IComparableProjector
{
    public string Kind => "heap-snapshot";

    public bool CanProject(object artifact) => artifact is HeapSnapshotArtifact;

    public ComparableSnapshot Project(object artifact, string label)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact is not HeapSnapshotArtifact snapshot)
        {
            throw new ArgumentException($"Expected {nameof(HeapSnapshotArtifact)}, got {artifact.GetType().Name}.", nameof(artifact));
        }

        var rows = ProjectRows(snapshot, Kind);
        return new ComparableSnapshot(
            Schema: ComparableSnapshot.SchemaV1,
            Kind: Kind,
            Label: label,
            CapturedAt: snapshot.CapturedAt,
            ProcessId: snapshot.ProcessId,
            Metrics: Array.Empty<MetricValue>(),
            Rows: rows);
    }

    public static Dictionary<TypeIdentity, HeapDiffMetric> ProjectTyped(HeapSnapshotArtifact snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var result = new Dictionary<TypeIdentity, HeapDiffMetric>();
        foreach (var row in ProjectTypedByAvailableIdentity(snapshot))
        {
            result[row.Identity] = row.Metric;
        }

        return result;
    }

    internal static IReadOnlyList<HeapComparableType> ProjectTypedByAvailableIdentity(HeapSnapshotArtifact snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var byBytes = AggregateRankingList(snapshot.TopTypesByBytes);
        var byInstances = AggregateRankingList(snapshot.TopTypesByInstances);
        var copies = new Dictionary<CopyIdentityKey, CopyMetric>();

        MergeRanking(copies, byBytes);
        MergeRanking(copies, byInstances);

        return copies.Values
            .GroupBy(static copy => GetCanonicalIdentityKey(copy.Identity))
            .Select(static group =>
            {
                var rows = group.ToArray();
                var identity = SelectRepresentativeIdentity(rows.Select(static row => row.Identity));
                return new HeapComparableType(
                    identity,
                    new HeapDiffMetric(
                        TotalBytes: rows.Sum(static row => row.Metric.TotalBytes),
                        InstanceCount: rows.Sum(static row => row.Metric.InstanceCount)));
            })
            .OrderBy(static row => row.Identity.TypeFullName, StringComparer.Ordinal)
            .ThenBy(static row => row.Identity.ModulePath, PathComparer)
            .ThenBy(static row => row.Identity.ModuleName, PathComparer)
            .ToArray();
    }

    private static ComparableRow[] ProjectRows(HeapSnapshotArtifact snapshot, string kind)
    {
        return ProjectTypedByAvailableIdentity(snapshot)
            .OrderByDescending(static row => row.Metric.TotalBytes)
            .ThenBy(static row => row.Identity.TypeFullName, StringComparer.Ordinal)
            .Select(row => new ComparableRow(
                ComparableKeyFactory.ForType(kind, row.Identity, row.Identity.TypeFullName, row.Identity.ModuleName),
                row.Identity.TypeFullName,
                new[]
                {
                    Metric("totalBytes", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Total, MetricNormalization.None, "bytes", row.Metric.TotalBytes),
                    Metric("instanceCount", MetricRole.Secondary, BetterDirection.Lower, MetricAggregation.Total, MetricNormalization.None, "count", row.Metric.InstanceCount),
                }))
            .ToArray();
    }

    private static MetricValue Metric(
        string name,
        MetricRole role,
        BetterDirection direction,
        MetricAggregation aggregation,
        MetricNormalization normalizedBy,
        string unit,
        double value)
        => new(new MetricDefinition(name, role, direction, aggregation, normalizedBy, unit), Math.Round(value, 4));

    internal static int GetMatchScore(TypeIdentity left, TypeIdentity right)
    {
        if (!string.Equals(left.TypeFullName, right.TypeFullName, StringComparison.Ordinal))
        {
            return 0;
        }

        if (left.MetadataToken is { } leftToken &&
            right.MetadataToken is { } rightToken &&
            leftToken != rightToken)
        {
            return 0;
        }

        if (left.ModuleVersionId is { } leftMvid && right.ModuleVersionId is { } rightMvid)
        {
            if (leftMvid != rightMvid)
            {
                return 0;
            }

            if (HasSharedPath(left, right, out var samePath))
            {
                return samePath ? 50 : 40;
            }

            if (HasSharedModuleName(left, right, out var sameModule))
            {
                return sameModule ? 45 : 40;
            }

            return 40;
        }

        if (HasSharedPath(left, right, out var pathsEqual))
        {
            if (!pathsEqual) return 0;
            return left.MetadataToken is not null && right.MetadataToken is not null ? 35 : 30;
        }

        if (HasSharedModuleName(left, right, out var modulesEqual))
        {
            if (!modulesEqual) return 0;
            return left.MetadataToken is not null && right.MetadataToken is not null ? 25 : 20;
        }

        return 10;
    }

    internal static MatchSelection FindUniqueBestMatch(
        TypeIdentity identity,
        IReadOnlyList<HeapComparableType> candidates)
    {
        var bestScore = 0;
        HeapComparableType? best = null;
        var bestCount = 0;
        foreach (var candidate in candidates)
        {
            var score = GetMatchScore(identity, candidate.Identity);
            if (score == 0 || score < bestScore) continue;
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
                bestCount = 1;
            }
            else
            {
                bestCount++;
            }
        }

        return new MatchSelection(bestCount == 1 ? best : null, bestScore, bestCount > 1);
    }

    private static Dictionary<CopyIdentityKey, CopyMetric> AggregateRankingList(IReadOnlyList<TypeStat> stats)
    {
        var result = new Dictionary<CopyIdentityKey, CopyMetric>();
        foreach (var stat in stats)
        {
            var identity = NormalizeIdentity(stat);
            var key = CopyIdentityKey.Create(identity, stat.ModuleImageBase);
            if (!result.TryGetValue(key, out var existing))
            {
                result[key] = new CopyMetric(
                    identity,
                    new HeapDiffMetric(TotalBytes: stat.TotalBytes, InstanceCount: stat.InstanceCount));
                continue;
            }

            result[key] = existing with
            {
                Metric = new HeapDiffMetric(
                    TotalBytes: Math.Max(existing.Metric.TotalBytes, stat.TotalBytes),
                    InstanceCount: Math.Max(existing.Metric.InstanceCount, stat.InstanceCount)),
            };
        }

        return result;
    }

    private static void MergeRanking(
        Dictionary<CopyIdentityKey, CopyMetric> destination,
        IReadOnlyDictionary<CopyIdentityKey, CopyMetric> source)
    {
        foreach (var pair in source)
        {
            if (!destination.TryGetValue(pair.Key, out var existing))
            {
                destination[pair.Key] = pair.Value;
                continue;
            }

            destination[pair.Key] = existing with
            {
                Metric = new HeapDiffMetric(
                    TotalBytes: Math.Max(existing.Metric.TotalBytes, pair.Value.Metric.TotalBytes),
                    InstanceCount: Math.Max(existing.Metric.InstanceCount, pair.Value.Metric.InstanceCount)),
            };
        }
    }

    private static TypeIdentity NormalizeIdentity(TypeStat stat)
    {
        var identity = stat.Identity;
        if (identity is null)
        {
            return new TypeIdentity(stat.TypeFullName)
            {
                ModuleName = stat.ModuleName,
            };
        }

        return identity with
        {
            TypeFullName = stat.TypeFullName,
            ModuleName = identity.ModuleName ?? stat.ModuleName,
        };
    }

    private static bool HasSharedPath(TypeIdentity left, TypeIdentity right, out bool equal)
    {
        if (string.IsNullOrWhiteSpace(left.ModulePath) || string.IsNullOrWhiteSpace(right.ModulePath))
        {
            equal = false;
            return false;
        }

        equal = PathComparer.Equals(left.ModulePath, right.ModulePath);
        return true;
    }

    private static bool HasSharedModuleName(TypeIdentity left, TypeIdentity right, out bool equal)
    {
        if (string.IsNullOrWhiteSpace(left.ModuleName) || string.IsNullOrWhiteSpace(right.ModuleName))
        {
            equal = false;
            return false;
        }

        equal = PathComparer.Equals(left.ModuleName, right.ModuleName);
        return true;
    }

    private static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string NormalizePath(string? value)
        => OperatingSystem.IsWindows() ? value?.ToUpperInvariant() ?? string.Empty : value ?? string.Empty;

    internal static HeapCanonicalIdentityKey GetCanonicalIdentityKey(TypeIdentity identity)
    {
        if (identity.ModuleVersionId is { } moduleVersionId)
        {
            return new HeapCanonicalIdentityKey(
                Kind: 3,
                identity.TypeFullName,
                moduleVersionId,
                identity.MetadataToken,
                Module: string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(identity.ModulePath))
        {
            return new HeapCanonicalIdentityKey(
                Kind: 2,
                identity.TypeFullName,
                ModuleVersionId: null,
                identity.MetadataToken,
                NormalizePath(identity.ModulePath));
        }

        if (!string.IsNullOrWhiteSpace(identity.ModuleName))
        {
            return new HeapCanonicalIdentityKey(
                Kind: 1,
                identity.TypeFullName,
                ModuleVersionId: null,
                identity.MetadataToken,
                NormalizePath(identity.ModuleName));
        }

        return new HeapCanonicalIdentityKey(
            Kind: 0,
            identity.TypeFullName,
            ModuleVersionId: null,
            identity.MetadataToken,
            Module: string.Empty);
    }

    internal static TypeIdentity SelectRepresentativeIdentity(IEnumerable<TypeIdentity> identities)
        => identities
            .OrderByDescending(static identity => identity.ModuleVersionId is not null)
            .ThenByDescending(static identity => identity.MetadataToken is not null)
            .ThenByDescending(static identity => !string.IsNullOrWhiteSpace(identity.ModulePath))
            .ThenBy(static identity => identity.ModulePath, PathComparer)
            .ThenBy(static identity => identity.ModuleName, PathComparer)
            .First();

    internal readonly record struct HeapCanonicalIdentityKey(
        int Kind,
        string TypeFullName,
        Guid? ModuleVersionId,
        int? MetadataToken,
        string Module);

    private readonly record struct CopyIdentityKey(HeapCanonicalIdentityKey Stable, ulong? ModuleImageBase)
    {
        public static CopyIdentityKey Create(TypeIdentity identity, ulong? moduleImageBase)
            => new(GetCanonicalIdentityKey(identity), moduleImageBase);
    }

    private sealed record CopyMetric(TypeIdentity Identity, HeapDiffMetric Metric);
}

internal sealed record HeapComparableType(TypeIdentity Identity, HeapDiffMetric Metric);

internal sealed record MatchSelection(
    HeapComparableType? Match,
    int Score,
    bool Ambiguous);
