using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Triage;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliSweepProjectionTests
{
    [Fact]
    public void ProjectSweepSummary_SerializedCliEnvelopePointsToDirectFailuresField()
    {
        var projected = CliHintProjection.ProjectSweepSummary(CreateResult());
        var json = JsonSerializer.SerializeToNode(
            projected,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        projected.Summary.Should().Contain("data.failures").And.NotContain("data.sweep.failures");
        json!["data"]!["failures"]!.AsArray().Should().ContainSingle();
        json["data"]!["sweep"].Should().BeNull();
    }

    private static DiagnosticResult<SweepResult> CreateResult()
    {
        var sweep = new SweepResult(
            6,
            new TriageResult(
                TriageClassifier.Inconclusive,
                TriageSeverity.Healthy,
                new TriageEvidence(null, null, null, null, null, null, null, null, null)),
            null,
            null,
            null,
            null,
            null,
            new Dictionary<string, string?>(),
            ["exceptions: CollectorFailed"]);

        return DiagnosticResult.Ok(sweep, "Sweep over 6s. 1 collector(s) failed.");
    }
}
