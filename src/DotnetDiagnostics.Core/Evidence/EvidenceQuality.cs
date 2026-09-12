using System.Text.Json.Serialization;

namespace DotnetDiagnostics.Core.Evidence;

/// <summary>Stable, collector-independent categories for limitations on diagnostic evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EvidenceLimitationCategory>))]
public enum EvidenceLimitationCategory
{
    DetectedTransportLoss,
    ProcessingFailure,
    CollectorEviction,
    OutputProjection,
    Inference,
    CaptureWindow,
    Startup,
    MechanismUnavailable,
    MechanismUnobservable,
    LegacyUnknown,
}

/// <summary>Whether a class of conclusion is supported by the retained evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EvidenceConclusionSupport>))]
public enum EvidenceConclusionSupport
{
    Supported,
    Inconclusive,
    NotEstablished,
}

/// <summary>
/// One bounded limitation. Producers emit at most one row for a stable category/scope pair rather
/// than appending one message per event.
/// </summary>
public sealed record EvidenceLimitation(
    EvidenceLimitationCategory Category,
    string Scope,
    long? AffectedCount,
    string Detail);

/// <summary>Conclusion policy derived from evidence quality, not a universal completeness flag.</summary>
public sealed record EvidenceConclusionPolicy(
    EvidenceConclusionSupport RetainedExplicitPositiveEvidence,
    EvidenceConclusionSupport AbsenceOrExhaustiveCounts,
    EvidenceConclusionSupport RegressionOrHealthyControl);

/// <summary>
/// Bounded evidence-quality metadata shared by collector artifacts and portable comparisons.
/// A null value on an older serialized artifact means legacy-unknown, never clean.
/// </summary>
public sealed record EvidenceQuality(
    string Schema,
    IReadOnlyList<EvidenceLimitation> Limitations,
    EvidenceConclusionPolicy Conclusions)
{
    public const string SchemaV1 = "dotnet-diagnostics/evidence-quality/v1";

    public static EvidenceQuality LegacyUnknown { get; } = new(
        SchemaV1,
        [
            new EvidenceLimitation(
                EvidenceLimitationCategory.LegacyUnknown,
                "capture",
                null,
                "This artifact predates structured evidence-quality metadata; capture limitations are unknown."),
        ],
        new EvidenceConclusionPolicy(
            EvidenceConclusionSupport.NotEstablished,
            EvidenceConclusionSupport.Inconclusive,
            EvidenceConclusionSupport.Inconclusive));
}
