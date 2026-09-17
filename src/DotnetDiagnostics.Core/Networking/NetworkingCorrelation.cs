using System.Text.Json.Serialization;

namespace DotnetDiagnostics.Core.Networking;

/// <summary>
/// Accounting for observed identities, not proof of transport completeness or successful-operation
/// coverage. Absent metadata on older artifacts means unknown. Failed lifecycles remain excluded.
/// </summary>
[method: JsonConstructor]
public sealed record NetworkingCorrelation(IReadOnlyDictionary<string, NetworkingCorrelationCounts> ByKind)
{
    public NetworkingCorrelation(NetworkingCorrelationCounts http, NetworkingCorrelationCounts dns, NetworkingCorrelationCounts tls)
        : this(new Dictionary<string, NetworkingCorrelationCounts>(StringComparer.Ordinal)
        {
            ["http"] = http, ["dns"] = dns, ["tls"] = tls,
        }) { }

    [JsonIgnore]
    public NetworkingCorrelationCounts Http => ByKind["http"];
    [JsonIgnore]
    public NetworkingCorrelationCounts Dns => ByKind["dns"];
    [JsonIgnore]
    public NetworkingCorrelationCounts Tls => ByKind["tls"];
}

/// <summary>
/// Starts partition into Paired, EmptyStarts, AmbiguousStarts, Expired, Evicted,
/// FailureDiscarded, Unfinished and CapacitySuppressedStarts. Stop counts are independent.
/// Identity history is retained for the whole capture; saturation fails closed.
/// </summary>
[method: JsonConstructor]
public sealed record NetworkingCorrelationCounts(IReadOnlyDictionary<string, long> Counts, bool IdentityCapacityReached)
{
    public NetworkingCorrelationCounts(long started, long paired, long emptyStarts, long ambiguousStarts,
        long expired, long evicted, long failureDiscarded, long unfinished, long capacitySuppressedStarts,
        long unmatchedStops, long emptyStops, long invalidTimestampStops, bool identityCapacityReached,
        long unmatchedFailures = 0)
        : this(new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["started"] = started, ["paired"] = paired, ["emptyStarts"] = emptyStarts,
            ["ambiguousStarts"] = ambiguousStarts, ["expired"] = expired, ["evicted"] = evicted,
            ["failureDiscarded"] = failureDiscarded, ["unfinished"] = unfinished,
            ["capacitySuppressedStarts"] = capacitySuppressedStarts, ["unmatchedStops"] = unmatchedStops,
            ["emptyStops"] = emptyStops, ["invalidTimestampStops"] = invalidTimestampStops,
            ["unmatchedFailures"] = unmatchedFailures,
        }, identityCapacityReached) { }

    [JsonIgnore]
    public long Started => Counts["started"];
    [JsonIgnore]
    public long Paired => Counts["paired"];
    [JsonIgnore]
    public long EmptyStarts => Counts["emptyStarts"];
    [JsonIgnore]
    public long AmbiguousStarts => Counts["ambiguousStarts"];
    [JsonIgnore]
    public long Expired => Counts["expired"];
    [JsonIgnore]
    public long Evicted => Counts["evicted"];
    [JsonIgnore]
    public long FailureDiscarded => Counts["failureDiscarded"];
    [JsonIgnore]
    public long Unfinished => Counts["unfinished"];
    [JsonIgnore]
    public long CapacitySuppressedStarts => Counts["capacitySuppressedStarts"];
    [JsonIgnore]
    public long UnmatchedStops => Counts["unmatchedStops"];
    [JsonIgnore]
    public long EmptyStops => Counts["emptyStops"];
    [JsonIgnore]
    public long InvalidTimestampStops => Counts["invalidTimestampStops"];
    [JsonIgnore]
    public long UnmatchedFailures => Counts["unmatchedFailures"];

    /// <summary>Observed pairing gaps only; false does not establish complete acquisition.</summary>
    public bool HasLimitations => Started != Paired || UnmatchedStops != 0 || EmptyStops != 0
        || InvalidTimestampStops != 0 || UnmatchedFailures != 0 || IdentityCapacityReached;
}
