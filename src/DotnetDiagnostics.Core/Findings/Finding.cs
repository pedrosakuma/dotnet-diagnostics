using System.Text.Json.Serialization;

namespace DotnetDiagnostics.Core.Findings;

/// <summary>
/// A single engine-derived diagnostic conclusion: the server cross-references the signals it has
/// already collected and surfaces a compact, ranked statement of "here is what is likely wrong",
/// so the LLM / human does not have to re-derive it from the raw payload.
/// </summary>
/// <remarks>
/// Findings are <b>transparent grouping / cross-signal aggregation</b>, never a trained model — the
/// ground-truth label ("was this the real problem?") only ever comes from the very consumer that
/// already saw the finding, so any accumulated dataset would be contaminated by the suggestion
/// (consumer-side leakage). They are advisory: <see cref="Evidence"/> points back at artifact
/// handles / frames / counters, and the consumer can always drill and disagree.
/// </remarks>
/// <param name="Pattern">Stable machine identifier for the detected pattern (e.g. "regex-backtracking").</param>
/// <param name="Severity">How serious the pattern is when present.</param>
/// <param name="Confidence">How confident the detector is that the pattern is real, in <c>[0, 1]</c>.</param>
/// <param name="Title">One-line, human/LLM-readable statement of the conclusion.</param>
/// <param name="Evidence">The signals that support the conclusion, each referencing a handle / frame / counter.</param>
/// <param name="SuggestedFix">Optional short remediation guidance.</param>
/// <param name="NextAction">Optional pointer at the next tool call to confirm or drill into the finding.</param>
public sealed record Finding(
    string Pattern,
    FindingSeverity Severity,
    double Confidence,
    string Title,
    IReadOnlyList<FindingEvidence> Evidence,
    string? SuggestedFix = null,
    NextActionHint? NextAction = null);

/// <summary>How serious a <see cref="Finding"/> is when the pattern is present.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FindingSeverity>))]
public enum FindingSeverity
{
    [JsonStringEnumMemberName("critical")]
    Critical,

    [JsonStringEnumMemberName("high")]
    High,

    [JsonStringEnumMemberName("medium")]
    Medium,

    [JsonStringEnumMemberName("low")]
    Low,

    [JsonStringEnumMemberName("info")]
    Info,
}

/// <summary>
/// One supporting signal for a <see cref="Finding"/>. Keeps the payload small by referencing a
/// drill-down <see cref="Handle"/> instead of inlining the underlying blob.
/// </summary>
/// <param name="Kind">Signal category (e.g. "frame", "counter", "handle").</param>
/// <param name="Description">Human-readable description of the evidence.</param>
/// <param name="Handle">Optional drill-down handle the evidence was derived from.</param>
/// <param name="Value">Optional numeric magnitude (e.g. a percentage or a count).</param>
/// <param name="Unit">Optional unit for <see cref="Value"/> (e.g. "%", "samples").</param>
public sealed record FindingEvidence(
    string Kind,
    string Description,
    string? Handle = null,
    double? Value = null,
    string? Unit = null);
