using System.Text.Json.Nodes;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed partial class AdvisoryLlmAssessmentTests
{
    [Theory]
    [InlineData("missing", "Missing")]
    [InlineData("null", "Null")]
    [InlineData("array", "Array")]
    public async Task ComparisonSubset_RecordsUnusedFieldWithoutFabricatingContent(
        string alternativeKind, string expectedKind)
    {
        using var files = new AssessmentTestFiles();
        var fixture = await CreateFollowupFixtureAsync(files);
        var plan = AdvisoryLlmAssessment.FreezeFollowupPlan(
            fixture.Protocol, fixture.DraftPath, files.Path("subset.plan.json"));
        var slot = fixture.Protocol.Slots[4];
        var projection = AdvisoryLlmAssessment.Project(fixture.Protocol, slot);
        var packet = CalibrationPackets.ReadPacket(slot.PacketPath);
        var node = JsonNode.Parse(FreshPhaseAJson(invalidId: false))!.AsObject();
        if (alternativeKind == "missing")
        {
            node.Remove("alternatives");
        }
        else if (alternativeKind == "null")
        {
            node["alternatives"] = null;
        }
        var raw = node.ToJsonString();
        var before = FollowupHashTree(fixture.SourceRoot);
        var subset = AdvisoryLlmAssessment.PrepareComparisonSubset(
            plan, projection, packet, raw, AdvisoryCandidateSource.Original);

        subset.UnusedFields.Should().ContainSingle(value =>
            value.SourcePointer == "/alternatives" && value.SourceKind == expectedKind);
        subset.Claims.Should().ContainSingle();
        subset.Claims[0].Text.Should().Be("Synthetic fresh hypothesis.");
        subset.Claims[0].TextPointer.Should().Be("/hypotheses/0/text");
        subset.UncertaintyPointer.Should().Be("/uncertainty");
        subset.NextQuestionPointer.Should().Be("/nextDiagnosticQuestion");
        subset.RawResponseSha256.Should().Be(Sha256(System.Text.Encoding.UTF8.GetBytes(raw)));
        subset.Prompt.Should().NotContain(AdvisoryLlmAssessment.ComparisonSubsetPolicyVersion);
        subset.Prompt.Should().NotContain("sourceKind");
        subset.Prompt.Should().NotContain("\"alternatives\"");
        FollowupHashTree(fixture.SourceRoot).Should().BeEquivalentTo(before);

        node["alternatives"] = JsonNode.Parse("""[{"text":"UNUSED-ANSWER-HINT"}]""");
        var withUnusedText = AdvisoryLlmAssessment.PrepareComparisonSubset(
            plan, projection, packet, node.ToJsonString(), AdvisoryCandidateSource.Original);
        withUnusedText.Prompt.Should().Be(subset.Prompt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ComparisonSubset_PreservesEmptyClaimsOrUnresolvedCitations(bool emptyClaims)
    {
        using var files = new AssessmentTestFiles();
        var fixture = await CreateFollowupFixtureAsync(files);
        var plan = AdvisoryLlmAssessment.FreezeFollowupPlan(
            fixture.Protocol, fixture.DraftPath, files.Path("citation-subset.plan.json"));
        var slot = fixture.Protocol.Slots[4];
        var projection = AdvisoryLlmAssessment.Project(fixture.Protocol, slot);
        var packet = CalibrationPackets.ReadPacket(slot.PacketPath);
        var node = JsonNode.Parse(FreshPhaseAJson(invalidId: false))!.AsObject();
        const string unresolved = "tool-result://evidence-01#/evidence/not-retained";
        if (emptyClaims)
        {
            node["hypotheses"] = new JsonArray();
        }
        else
        {
            node["hypotheses"]![0]!.AsObject().Remove("id");
            node["hypotheses"]![0]!["evidenceLocations"] = new JsonArray(unresolved);
        }
        var subset = AdvisoryLlmAssessment.PrepareComparisonSubset(
            plan, projection, packet, node.ToJsonString(), AdvisoryCandidateSource.Original);
        if (emptyClaims)
        {
            subset.Claims.Should().BeEmpty();
            subset.Candidates[1].Claims.Should().BeEmpty();
        }
        else
        {
            subset.Claims[0].ControllerId.Should().Be("followup-claim-01");
            subset.Claims[0].EvidenceLocations.Should().ContainSingle().Which.Should().Be(unresolved);
            subset.CitationResolutions.Should().ContainSingle().Which.ExistsInProjection.Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("missingHypotheses")]
    [InlineData("missingUncertainty")]
    [InlineData("missingNextQuestion")]
    [InlineData("invalidCitations")]
    [InlineData("oversizedText")]
    [InlineData("duplicateProperty")]
    public async Task ComparisonSubset_RejectsUnreadableSelectedFields(string defect)
    {
        using var files = new AssessmentTestFiles();
        var fixture = await CreateFollowupFixtureAsync(files);
        var plan = AdvisoryLlmAssessment.FreezeFollowupPlan(
            fixture.Protocol, fixture.DraftPath, files.Path("rejected-subset.plan.json"));
        var slot = fixture.Protocol.Slots[4];
        var projection = AdvisoryLlmAssessment.Project(fixture.Protocol, slot);
        var packet = CalibrationPackets.ReadPacket(slot.PacketPath);
        var node = JsonNode.Parse(FreshPhaseAJson(invalidId: false))!.AsObject();
        switch (defect)
        {
            case "missingHypotheses": node.Remove("hypotheses"); break;
            case "missingUncertainty": node.Remove("uncertainty"); break;
            case "missingNextQuestion": node.Remove("nextDiagnosticQuestion"); break;
            case "invalidCitations": node["hypotheses"]![0]!["evidenceLocations"] = 42; break;
            case "oversizedText":
                node["hypotheses"]![0]!["text"] = new string('x', plan.Limits.MaximumStringCharacters + 1);
                break;
        }
        var raw = node.ToJsonString();
        if (defect == "duplicateProperty")
        {
            raw = raw.Insert(1, "\"hypotheses\":[],");
        }
        FluentActions.Invoking(() => AdvisoryLlmAssessment.PrepareComparisonSubset(
            plan, projection, packet, raw, AdvisoryCandidateSource.Original))
            .Should().Throw<Exception>();
    }
}
