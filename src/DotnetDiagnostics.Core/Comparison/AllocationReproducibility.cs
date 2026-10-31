using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;

namespace DotnetDiagnostics.Core.Comparison;

public static class AllocationReproducibilityBounds
{
    public const int MinimumCaptures = 2;
    public const int MaximumCaptures = 8;
    public const int DefaultTopK = 3;
    public const int MaximumTopK = 16;
}

[JsonConverter(typeof(JsonStringEnumConverter<AllocationReproducibilityAvailability>))]
public enum AllocationReproducibilityAvailability
{
    [JsonStringEnumMemberName("available")]
    Available,

    [JsonStringEnumMemberName("insufficient_capture_count")]
    InsufficientCaptureCount,

    [JsonStringEnumMemberName("incompatible")]
    Incompatible,
}

[JsonConverter(typeof(JsonStringEnumConverter<AllocationAttributionState>))]
public enum AllocationAttributionState
{
    [JsonStringEnumMemberName("raw_empty")]
    RawEmpty,

    [JsonStringEnumMemberName("no_stack")]
    NoStack,

    [JsonStringEnumMemberName("zero_weight")]
    ZeroWeight,

    [JsonStringEnumMemberName("partial")]
    Partial,

    [JsonStringEnumMemberName("complete")]
    Complete,

    [JsonStringEnumMemberName("unavailable")]
    Unavailable,

    [JsonStringEnumMemberName("unavailable_handle")]
    UnavailableHandle,
}

public sealed record AllocationReproducibilityReason(string Code, string Field, string Message);

public sealed record AllocationReproducibilityCompatibility(
    string Status,
    IReadOnlyList<AllocationReproducibilityReason> Reasons);

public sealed record AllocationRankedItem(
    string Key,
    string DisplayName,
    int Rank,
    long EstimatedBytes,
    long AllocationTicks);

public sealed record AllocationReproducibilityCapture(
    string Handle,
    string HandleStatus,
    string? UnavailableReason,
    int? ProcessId,
    DateTimeOffset? ProcessStartedAt,
    DateTimeOffset? StartedAt,
    double? DurationSeconds,
    long? EstimatedBytes,
    long? AllocationTicks,
    long? AttributedEstimatedBytes,
    long? UnattributedEstimatedBytes,
    long? AttributedAllocationTicks,
    long? UnattributedAllocationTicks,
    double? AttributedByteShare,
    double? UnattributedByteShare,
    double? AttributedTickShare,
    double? UnattributedTickShare,
    AllocationAttributionState AttributionState,
    AllocationReproducibilityCaptureMetadata? CompatibilityIdentity,
    IReadOnlyList<AllocationRankedItem> AllocatedTypes,
    IReadOnlyList<AllocationRankedItem> AllocationStacks);

public sealed record AllocationDescriptiveSummary(
    double? Median,
    double? RawMedianAbsoluteDeviation,
    string Unit,
    string? MedianUnavailableReason,
    string? RawMedianAbsoluteDeviationUnavailableReason);

public sealed record AllocationRankStability(
    int PairCount,
    int MembershipComparisonCount,
    double? MedianMembershipJaccard,
    double? MinimumMembershipJaccard,
    int SharedRankComparisonCount,
    double? MedianSharedKeySpearman,
    double? MinimumSharedKeySpearman,
    string? MembershipUnavailableReason,
    string? SharedRankUnavailableReason);

public sealed record AllocationReproducibilityResult(
    string Schema,
    string Method,
    AllocationReproducibilityAvailability Availability,
    int RequestedCaptureCount,
    int AvailableCaptureCount,
    string IndependenceUnit,
    int TopK,
    AllocationReproducibilityCompatibility Compatibility,
    AllocationDescriptiveSummary EstimatedBytes,
    AllocationDescriptiveSummary AllocationTicks,
    AllocationRankStability AllocatedTypeStability,
    AllocationRankStability AllocationStackStability,
    IReadOnlyList<AllocationReproducibilityCapture> Captures,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Limitations)
{
    public const string SchemaV1 = "dotnet-diagnostics/allocation-reproducibility/v1";
    public const string MethodV1 = "bounded-top-k-jaccard-shared-key-spearman-v1";
}

public sealed record AllocationReproducibilityInput(
    string Handle,
    DiagnosticHandleLookupStatus HandleStatus,
    DiagnosticHandle? HandleMetadata,
    AllocationSampleArtifact? Artifact)
{
    public static AllocationReproducibilityInput FromLookup(
        string handle,
        DiagnosticHandleLookupResult lookup)
        => new(
            handle,
            lookup.Status,
            lookup.Lookup?.Handle,
            lookup.Lookup?.Artifact as AllocationSampleArtifact);
}

/// <summary>
/// Produces bounded, descriptive reproducibility evidence from separately started allocation
/// captures. Allocation totals remain runtime sampling weights. Type and stack inventories are
/// evaluated independently and no causal, severity, regression or health verdict is produced.
/// </summary>
public static class AllocationReproducibility
{
    private const string Compatible = "compatible";
    private const string Incompatible = "incompatible";

    public static DiagnosticResult<AllocationReproducibilityResult> Analyze(
        IReadOnlyList<AllocationReproducibilityInput> inputs,
        int topK = AllocationReproducibilityBounds.DefaultTopK)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count is < 1 or > AllocationReproducibilityBounds.MaximumCaptures)
        {
            return Invalid(
                nameof(inputs),
                $"must contain between 1 and {AllocationReproducibilityBounds.MaximumCaptures} capture handles");
        }
        if (topK is < 1 or > AllocationReproducibilityBounds.MaximumTopK)
        {
            return Invalid(
                nameof(topK),
                $"must be between 1 and {AllocationReproducibilityBounds.MaximumTopK}");
        }
        if (inputs.Select(static input => input.Handle).Distinct(StringComparer.Ordinal).Count() != inputs.Count)
        {
            return Invalid(nameof(inputs), "must not contain duplicate capture handles");
        }
        foreach (var input in inputs.Where(static input =>
            input.HandleStatus == DiagnosticHandleLookupStatus.Found))
        {
            if (input.HandleMetadata is null)
            {
                return Invalid(
                    nameof(inputs),
                    $"contains available handle '{input.Handle}' without handle metadata");
            }
            if (!string.Equals(input.HandleMetadata.Kind, "allocation-sample", StringComparison.Ordinal))
            {
                return Invalid(
                    nameof(inputs),
                    $"contains handle '{input.Handle}' of kind '{input.HandleMetadata.Kind}'; every available handle must be allocation-sample");
            }
            if (input.Artifact is null)
            {
                return Invalid(
                    nameof(inputs),
                    $"contains allocation handle '{input.Handle}' whose artifact payload is unavailable or has the wrong type");
            }
        }

        var captures = inputs.Select(input => Project(input, topK))
            .OrderBy(static capture => capture.StartedAt is null)
            .ThenBy(static capture => capture.StartedAt)
            .ThenBy(static capture => capture.Handle, StringComparer.Ordinal)
            .ToArray();
        var available = captures
            .Where(static capture => capture.HandleStatus == "available"
                && capture.CompatibilityIdentity is not null)
            .ToArray();
        var compatibilityReasons = CompatibilityReasons(captures, available);
        var compatibility = new AllocationReproducibilityCompatibility(
            compatibilityReasons.Count == 0 ? Compatible : Incompatible,
            compatibilityReasons);
        var enough = available.Length >= AllocationReproducibilityBounds.MinimumCaptures;
        var availability = compatibilityReasons.Count > 0
            ? AllocationReproducibilityAvailability.Incompatible
            : enough
                ? AllocationReproducibilityAvailability.Available
                : AllocationReproducibilityAvailability.InsufficientCaptureCount;
        var pool = availability == AllocationReproducibilityAvailability.Incompatible
            ? Array.Empty<AllocationReproducibilityCapture>()
            : available;
        var totalsUnavailable = availability switch
        {
            AllocationReproducibilityAvailability.Incompatible => "incompatible-capture-identity",
            AllocationReproducibilityAvailability.InsufficientCaptureCount => "insufficient-capture-count",
            _ => null,
        };
        var bytes = Describe(
            pool.Select(static capture => capture.EstimatedBytes!.Value).ToArray(),
            "estimated-bytes",
            totalsUnavailable);
        var ticks = Describe(
            pool.Select(static capture => capture.AllocationTicks!.Value).ToArray(),
            "ticks",
            totalsUnavailable);
        var typeStability = Stability(
            pool.Select(static capture => capture.AllocatedTypes).ToArray(),
            totalsUnavailable);
        var stackStability = Stability(
            pool.Select(static capture => capture.AllocationStacks).ToArray(),
            totalsUnavailable);
        var limitations = new List<string>
        {
            "GCAllocationTick estimated bytes are runtime sampling weights, not exact allocated bytes, object counts, retained bytes or CPU time.",
            "Allocated types and allocation stack sites are independent ranked inventories; no type-to-stack relationship is inferred or serialized.",
            "Capture-level medians and raw median absolute deviations are descriptive only. No confidence interval is computed over weighted allocation ticks.",
            "Workload equivalence is not observed by the collector and remains a caller-held assumption.",
            "Scenario validation thresholds are intentionally absent; this result emits no causal, severity, regression or health verdict.",
        };
        if (captures.Any(static capture => capture.HandleStatus != "available"))
        {
            limitations.Add("Unavailable, expired or capacity-evicted handles remain in the requested denominator and do not become zero-valued captures.");
        }
        if (captures.Any(static capture => capture.CompatibilityIdentity is null
            && capture.HandleStatus == "available"))
        {
            limitations.Add("Historical allocation artifacts without reproducibility metadata remain readable but are not backfilled from the current host.");
        }

        var result = new AllocationReproducibilityResult(
            AllocationReproducibilityResult.SchemaV1,
            AllocationReproducibilityResult.MethodV1,
            availability,
            captures.Length,
            available.Length,
            "capture",
            topK,
            compatibility,
            bytes,
            ticks,
            typeStability,
            stackStability,
            captures,
            [
                "Each input is one separately started and stopped allocation capture.",
                "Compatible captures used the same runtime semantics, EventPipe allocation backend, requested duration and retained top-N bounds.",
                "The workload shape and offered work were stable across captures.",
            ],
            limitations);
        var summary = availability switch
        {
            AllocationReproducibilityAvailability.Available
                => $"Analyzed {available.Length} compatible allocation captures using bounded independent type and stack top-{topK} stability; no performance verdict was produced.",
            AllocationReproducibilityAvailability.Incompatible
                => $"Allocation captures are incompatible; capture-level evidence was retained but no pooled reproducibility calculation was produced.",
            _ => $"Allocation reproducibility requires at least {AllocationReproducibilityBounds.MinimumCaptures} compatible available captures; {available.Length} were available.",
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static AllocationReproducibilityCapture Project(
        AllocationReproducibilityInput input,
        int topK)
    {
        if (input.HandleStatus != DiagnosticHandleLookupStatus.Found
            || input.HandleMetadata is null
            || input.Artifact is null)
        {
            return new(
                Handle: input.Handle,
                HandleStatus: HandleStatus(input.HandleStatus),
                UnavailableReason: HandleUnavailableReason(input.HandleStatus),
                ProcessId: input.HandleMetadata?.ProcessId,
                ProcessStartedAt: null,
                StartedAt: null,
                DurationSeconds: null,
                EstimatedBytes: null,
                AllocationTicks: null,
                AttributedEstimatedBytes: null,
                UnattributedEstimatedBytes: null,
                AttributedAllocationTicks: null,
                UnattributedAllocationTicks: null,
                AttributedByteShare: null,
                UnattributedByteShare: null,
                AttributedTickShare: null,
                UnattributedTickShare: null,
                AttributionState: AllocationAttributionState.UnavailableHandle,
                CompatibilityIdentity: null,
                AllocatedTypes: [],
                AllocationStacks: []);
        }

        var artifact = input.Artifact;
        var sample = artifact.Summary;
        if (!string.Equals(input.HandleMetadata.Kind, "allocation-sample", StringComparison.Ordinal))
        {
            return new(
                Handle: input.Handle,
                HandleStatus: "available",
                UnavailableReason: $"handle-kind-{input.HandleMetadata.Kind}",
                ProcessId: input.HandleMetadata.ProcessId,
                ProcessStartedAt: artifact.Reproducibility?.ProcessStartedAt,
                StartedAt: sample.StartedAt,
                DurationSeconds: Math.Round(sample.Duration.TotalSeconds, 4),
                EstimatedBytes: sample.TotalBytes,
                AllocationTicks: sample.TotalEvents,
                AttributedEstimatedBytes: null,
                UnattributedEstimatedBytes: null,
                AttributedAllocationTicks: null,
                UnattributedAllocationTicks: null,
                AttributedByteShare: null,
                UnattributedByteShare: null,
                AttributedTickShare: null,
                UnattributedTickShare: null,
                AttributionState: AllocationAttributionState.Unavailable,
                CompatibilityIdentity: artifact.Reproducibility,
                AllocatedTypes: [],
                AllocationStacks: []);
        }

        var coverage = Coverage(artifact);
        var state = AttributionState(sample, coverage);
        var attributedBytes = coverage?.Bytes;
        var attributedTicks = coverage?.Ticks;
        var typeRows = sample.TotalBytes > 0
            ? RankTypes(sample.TopByBytes, topK)
            : [];
        var stackRows = attributedBytes > 0
            ? RankStacks(sample.TopBySite, topK)
            : [];
        return new(
            input.Handle,
            "available",
            coverage?.UnavailableReason,
            sample.ProcessId,
            artifact.Reproducibility?.ProcessStartedAt,
            sample.StartedAt,
            Math.Round(sample.Duration.TotalSeconds, 4),
            sample.TotalBytes,
            sample.TotalEvents,
            attributedBytes,
            attributedBytes is null ? null : sample.TotalBytes - attributedBytes,
            attributedTicks,
            attributedTicks is null ? null : sample.TotalEvents - attributedTicks,
            Share(attributedBytes, sample.TotalBytes),
            UnattributedShare(attributedBytes, sample.TotalBytes),
            Share(attributedTicks, sample.TotalEvents),
            UnattributedShare(attributedTicks, sample.TotalEvents),
            state,
            artifact.Reproducibility,
            typeRows,
            stackRows);
    }

    private static List<AllocationReproducibilityReason> CompatibilityReasons(
        AllocationReproducibilityCapture[] allCaptures,
        AllocationReproducibilityCapture[] captures)
    {
        var reasons = new List<AllocationReproducibilityReason>();
        if (allCaptures.Any(static capture => capture.HandleStatus == "available"
            && capture.CompatibilityIdentity is null))
        {
            reasons.Add(new(
                "CaptureIdentityUnavailable",
                "compatibilityIdentity",
                "One or more available allocation artifacts predate the reproducibility identity block; identity is not backfilled from the current host."));
        }
        if (captures.Length == 0)
        {
            return reasons;
        }

        var reference = captures[0].CompatibilityIdentity!;
        Compare("schema", static identity => identity.Schema);
        Compare("runtimeFlavor", static identity => identity.RuntimeFlavor);
        Compare("runtimeVersion", static identity => identity.RuntimeVersion);
        Compare("operatingSystem", static identity => identity.OperatingSystem);
        Compare("architecture", static identity => identity.Architecture);
        Compare("backend", static identity => identity.Backend);
        Compare("quantity", static identity => identity.Quantity);
        Compare("unit", static identity => identity.Unit);
        Compare("aggregation", static identity => identity.Aggregation);
        Compare("normalization", static identity => identity.Normalization);
        Compare("requestedDurationSeconds", static identity => identity.RequestedDurationSeconds);
        Compare("requestedTopN", static identity => identity.RequestedTopN);

        return reasons;

        void Compare<T>(
            string field,
            Func<AllocationReproducibilityCaptureMetadata, T> selector)
        {
            var values = captures.Select(capture => selector(capture.CompatibilityIdentity!))
                .Distinct()
                .ToArray();
            if (values.Length <= 1)
            {
                return;
            }

            reasons.Add(new(
                "CaptureIdentityMismatch",
                field,
                $"Allocation capture compatibility field '{field}' differs across captures; pooled calculations are unavailable."));
        }
    }

    private static AllocationDescriptiveSummary Describe(
        long[] values,
        string unit,
        string? unavailableReason)
    {
        if (unavailableReason is not null)
        {
            return new(null, null, unit, unavailableReason, unavailableReason);
        }
        if (values.Length == 0)
        {
            return new(null, null, unit, "insufficient-capture-count", "insufficient-capture-count");
        }

        var median = Median(values.Select(static value => (double)value).ToArray());
        double? mad = values.Length < 2
            ? null
            : Median(values.Select(value => Math.Abs(value - median)).ToArray());
        return new(
            median,
            mad,
            unit,
            null,
            mad is null ? "insufficient-capture-count" : null);
    }

    private static AllocationRankStability Stability(
        IReadOnlyList<AllocationRankedItem>[] inventories,
        string? unavailableReason)
    {
        if (unavailableReason is not null)
        {
            return new(0, 0, null, null, 0, null, null, unavailableReason, unavailableReason);
        }

        var pairCount = 0;
        var jaccards = new List<double>();
        var correlations = new List<double>();
        for (var leftIndex = 0; leftIndex < inventories.Length; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < inventories.Length; rightIndex++)
            {
                pairCount++;
                var left = inventories[leftIndex];
                var right = inventories[rightIndex];
                var leftKeys = left.Select(static item => item.Key).ToHashSet(StringComparer.Ordinal);
                var rightKeys = right.Select(static item => item.Key).ToHashSet(StringComparer.Ordinal);
                var union = leftKeys.Union(rightKeys, StringComparer.Ordinal).Count();
                if (union > 0)
                {
                    jaccards.Add(
                        leftKeys.Intersect(rightKeys, StringComparer.Ordinal).Count() / (double)union);
                }

                var shared = leftKeys.Intersect(rightKeys, StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (shared.Length >= 2)
                {
                    correlations.Add(Spearman(left, right, shared));
                }
            }
        }

        return new(
            pairCount,
            jaccards.Count,
            jaccards.Count == 0 ? null : Median(jaccards),
            jaccards.Count == 0 ? null : jaccards.Min(),
            correlations.Count,
            correlations.Count == 0 ? null : Median(correlations),
            correlations.Count == 0 ? null : correlations.Min(),
            jaccards.Count == 0 ? "no-ranked-keys" : null,
            correlations.Count == 0 ? "fewer-than-two-shared-ranked-keys" : null);
    }

    private static double Spearman(
        IReadOnlyList<AllocationRankedItem> left,
        IReadOnlyList<AllocationRankedItem> right,
        string[] shared)
    {
        var leftRanks = ReRankShared(left, shared);
        var rightRanks = ReRankShared(right, shared);
        var sumSquared = shared.Sum(key =>
        {
            var difference = leftRanks[key] - rightRanks[key];
            return difference * difference;
        });
        var count = shared.Length;
        var value = 1d - ((6d * sumSquared) / (count * (count * count - 1d)));
        return Math.Clamp(value, -1d, 1d);
    }

    private static Dictionary<string, int> ReRankShared(
        IReadOnlyList<AllocationRankedItem> rows,
        IReadOnlyList<string> shared)
    {
        var set = shared.ToHashSet(StringComparer.Ordinal);
        return rows.Where(row => set.Contains(row.Key))
            .OrderBy(static row => row.Rank)
            .Select(static (row, index) => (row.Key, Rank: index + 1))
            .ToDictionary(static item => item.Key, static item => item.Rank, StringComparer.Ordinal);
    }

    private static AllocationRankedItem[] RankTypes(
        IReadOnlyList<AllocatedType> rows,
        int topK)
        => rows
            .GroupBy(TypeKey, StringComparer.Ordinal)
            .Select(static group => group
                .OrderByDescending(static row => row.TotalBytes)
                .ThenBy(static row => row.TypeName, StringComparer.Ordinal)
                .First())
            .OrderByDescending(static row => row.TotalBytes)
            .ThenByDescending(static row => row.EventCount)
            .ThenBy(static row => row.TypeName, StringComparer.Ordinal)
            .Take(topK)
            .Select(static (row, index) => new AllocationRankedItem(
                TypeKey(row),
                row.TypeName,
                index + 1,
                row.TotalBytes,
                row.EventCount))
            .ToArray();

    private static AllocationRankedItem[] RankStacks(
        IReadOnlyList<AllocationSite> rows,
        int topK)
        => rows
            .GroupBy(StackKey, StringComparer.Ordinal)
            .Select(static group => group
                .OrderByDescending(static row => row.TotalBytes)
                .ThenBy(static row => row.Frame.Module, StringComparer.Ordinal)
                .ThenBy(static row => row.Frame.Method, StringComparer.Ordinal)
                .First())
            .OrderByDescending(static row => row.TotalBytes)
            .ThenByDescending(static row => row.EventCount)
            .ThenBy(static row => row.Frame.Module, StringComparer.Ordinal)
            .ThenBy(static row => row.Frame.Method, StringComparer.Ordinal)
            .Take(topK)
            .Select(static (row, index) => new AllocationRankedItem(
                StackKey(row),
                string.IsNullOrWhiteSpace(row.Frame.Module)
                    ? row.Frame.Method
                    : $"{row.Frame.Module}!{row.Frame.Method}",
                index + 1,
                row.TotalBytes,
                row.EventCount))
            .ToArray();

    private static string TypeKey(AllocatedType row)
        => row.Identity is { ModuleVersionId: { } mvid, MetadataToken: { } token }
            ? $"type:{mvid:D}:{token:x8}"
            : $"type:{row.Identity?.ModuleName ?? string.Empty}:{row.TypeName}";

    private static string StackKey(AllocationSite row)
        => row.Identity is { ModuleVersionId: { } mvid, MetadataToken: { } token }
            ? $"method:{mvid:D}:{token:x8}"
            : $"method:{row.Frame.Module}:{row.Frame.Method}";

    private static CoverageResult? Coverage(AllocationSampleArtifact artifact)
    {
        if (artifact.EstimatedBytesTrace is null)
        {
            return new(null, null, "byte-weighted-trace-unavailable");
        }

        var ticks = artifact.TraceArtifact.Root.InclusiveSamples;
        var bytes = artifact.EstimatedBytesTrace.Root.InclusiveSamples;
        var sample = artifact.Summary;
        return ticks < 0 || ticks > sample.TotalEvents
            || bytes < 0 || bytes > sample.TotalBytes
            || (ticks == 0 && bytes > 0)
                ? new(null, null, "invalid-attribution-coverage")
                : new(ticks, bytes, null);
    }

    private static AllocationAttributionState AttributionState(
        AllocationSample sample,
        CoverageResult? coverage)
    {
        if (sample.TotalEvents == 0)
        {
            return AllocationAttributionState.RawEmpty;
        }
        if (coverage is null || coverage.Ticks is null || coverage.Bytes is null)
        {
            return AllocationAttributionState.Unavailable;
        }
        if (coverage.Ticks == 0)
        {
            return AllocationAttributionState.NoStack;
        }
        if (coverage.Bytes == 0)
        {
            return AllocationAttributionState.ZeroWeight;
        }
        return coverage.Ticks == sample.TotalEvents && coverage.Bytes == sample.TotalBytes
            ? AllocationAttributionState.Complete
            : AllocationAttributionState.Partial;
    }

    private static double? Share(long? numerator, long denominator)
        => numerator is null || denominator <= 0 ? null : numerator.Value / (double)denominator;

    private static double? UnattributedShare(long? numerator, long denominator)
        => Share(numerator, denominator) is { } share ? 1d - share : null;

    private static double Median(IReadOnlyList<double> values)
    {
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    private static string HandleStatus(DiagnosticHandleLookupStatus status)
        => status switch
        {
            DiagnosticHandleLookupStatus.Found => "available",
            DiagnosticHandleLookupStatus.Expired => "expired",
            DiagnosticHandleLookupStatus.CapacityEvicted => "capacity-evicted",
            _ => "unknown",
        };

    private static string HandleUnavailableReason(DiagnosticHandleLookupStatus status)
        => status switch
        {
            DiagnosticHandleLookupStatus.Expired => "handle-expired",
            DiagnosticHandleLookupStatus.CapacityEvicted => "handle-capacity-evicted",
            DiagnosticHandleLookupStatus.Found => "artifact-type-mismatch",
            _ => "handle-unknown",
        };

    private static DiagnosticResult<AllocationReproducibilityResult> Invalid(
        string field,
        string message)
        => DiagnosticResult.Fail<AllocationReproducibilityResult>(
            $"Invalid allocation reproducibility request: {field} {message}.",
            new DiagnosticError(
                "InvalidArgument",
                $"Invalid allocation reproducibility request: {field} {message}.",
                field));

    private sealed record CoverageResult(long? Ticks, long? Bytes, string? UnavailableReason);
}
