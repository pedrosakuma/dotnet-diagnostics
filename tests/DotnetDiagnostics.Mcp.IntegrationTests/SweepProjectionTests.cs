using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Triage;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class SweepProjectionTests
{
    [Fact]
    public void Project_SerializedMcpEnvelopePointsToNestedSweepFailuresField()
    {
        var projected = CollectEventsTool.Project(
            CreateResult(),
            "sweep",
            static (envelope, sweep) => envelope with { Sweep = sweep });
        var json = JsonSerializer.SerializeToNode(
            projected,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        projected.Summary.Should().Contain("data.sweep.failures");
        json!["data"]!["sweep"]!["failures"]!.AsArray().Should().ContainSingle();
        json["data"]!["failures"].Should().BeNull();
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
