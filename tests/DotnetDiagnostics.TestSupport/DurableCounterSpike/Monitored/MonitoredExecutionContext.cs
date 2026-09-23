namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

internal enum MonitoredAdmissionStage
{
    PrevalidationComponentProof,
    ScoredCampaign,
}

// Execution mechanics do not carry a campaign admission or a decision.
internal interface IMonitoredExecutionManifest
{
    MonitoredBinaryIdentity RuntimeBinary { get; }
    MonitoredBinaryIdentity ToolBinary { get; }
    MonitoredBinaryIdentity SampleBinary { get; }
    MonitoredSourceCommits SourceCommits { get; }
    string FixtureManifestSha256 { get; }
    string HistoryRoot { get; }
    string WorkspaceRoot { get; }
    string OutputRoot { get; }
}

internal sealed record MonitoredExecutionContext(
    IMonitoredExecutionManifest Manifest,
    string RepositoryRoot,
    string ManifestPath,
    string ManifestSha256,
    string CampaignRoot,
    MonitoredAttributionMap Attribution,
    MonitoredEvidenceEncoding Encoding,
    MonitoredComponentEvidence ComponentEvidence,
    PrevalidationObservationScope? Prevalidation = null)
{
    internal static MonitoredExecutionContext FromCampaign(MonitoredValidatedManifest validated)
        => new(validated.Manifest, validated.RepositoryRoot, validated.ManifestPath,
            validated.ManifestSha256, validated.CampaignRoot, validated.Attribution,
            validated.Encoding, validated.ComponentEvidence);
}

internal sealed record PrevalidationObservationScope(
    IReadOnlyList<string> CompletedRoots,
    MonitoredProcessIdentity Coordinator,
    string? OwnershipPath = null,
    IReadOnlyDictionary<ApparentFileIdentity, PrevalidationDescriptorFixture>? DescriptorFixtures = null,
    string? CurrentHistoryRoot = null,
    bool GeometryFixtures = false)
{
    internal PrevalidationMonitorClient? Monitor { get; init; }
}

internal sealed record PrevalidationDescriptorFixture(
    MonitoredProcessIdentity Owner, string Target, long Length);
