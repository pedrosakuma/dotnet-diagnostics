using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

/// <summary>
/// Exercises the #681 agent-response mapper prototype end-to-end: free-text-shaped
/// <see cref="AgentScenarioResponse"/> in, scored <see cref="ScenarioEvaluationReport"/> out, via the same
/// <see cref="ScenarioEvaluator"/> the hand-authored gold interpretations in <c>ScenarioReplayTests</c> use.
/// Unlike those tests -- which construct <see cref="StructuredInterpretation"/> directly from manifest ids
/// -- these feed natural phrasing through <see cref="ScenarioAgentResponseMapper"/> first, so the mapping
/// heuristic itself is under test, not just the (already-covered) scoring math.
/// </summary>
public sealed class ScenarioAgentResponseMapperTests
{
    [Fact]
    public void Map_LockStormAcceptedNarrative_ScoresAsSupportedDiagnosis()
    {
        var manifest = LoadManifest("lock-storm");
        var response = new AgentScenarioResponse(
            CitedEvidenceIds: manifest.ExpectedEvidence.Select(item => item.Id).ToArray(),
            Hypothesis: "A sleeping monitor owner serializes work while waiters queue behind it.",
            Attribution: "The thread holding the monitor is asleep -- owner thread sleep.",
            NextAction: "Next: query the lock graph.",
            CausalityStatement: "The owner and waiters are correlated; this remains a correlation, not a proven cause.",
            Conclusions: [],
            Narrative: "This strongly correlates the sleeping owner with the waiters; further investigation could add certainty, but current evidence is consistent with the pattern.");

        var mapped = ScenarioAgentResponseMapper.Map(manifest, response);

        mapped.UnmappedFields.Should().BeEmpty();
        mapped.Interpretation.HypothesisIds.Should().Equal("sleeping-monitor-owner-serializes-work");
        mapped.Interpretation.AttributionIds.Should().Equal("monitor-owner-thread-sleep");
        mapped.Interpretation.NextActionIds.Should().Equal("query-lock-graph");
        mapped.Interpretation.CausalityPosture.Should().Be("correlated-owner-and-waiters");
        mapped.Uncertainty.AcknowledgesLimits.Should().BeTrue();
        mapped.Uncertainty.OverclaimsCertainty.Should().BeFalse();

        var evidence = ScenarioJson.ReadEvidence(
            ScenarioManifestLoader.ScenarioPath("Fixtures", "lock-storm.windows.evidence.json"));
        var report = ScenarioEvaluator.CreateReport(manifest, evidence, mapped.Interpretation);

        report.Interpretation.Status.Should().Be(ScenarioStageStatus.Passed);
        report.InterpretationScore!.WeightedScore.Should().Be(1);
    }

    [Fact]
    public void Map_LockStormOverclaimingWrongNarrative_ScoresAsUnsupportedAndFlagsOverclaim()
    {
        var manifest = LoadManifest("lock-storm");
        var response = new AgentScenarioResponse(
            CitedEvidenceIds: [],
            Hypothesis: "This looks like external IO wait.",
            Attribution: "This is caused by a GC pause somewhere in the runtime.",
            NextAction: "We should scale out the deployment to add more instances.",
            CausalityStatement: "I am certain this is a proven causal root cause, not just correlation.",
            Conclusions: manifest.ForbiddenConclusions,
            Narrative: "This is definitely the cause: external IO wait is guaranteed to be the source, with no other possible explanation.");

        var mapped = ScenarioAgentResponseMapper.Map(manifest, response);

        mapped.Interpretation.HypothesisIds.Should().Equal("external-io-wait");
        mapped.UnmappedFields.Should().ContainKey("attribution");
        mapped.UnmappedFields.Should().ContainKey("nextAction");
        mapped.UnmappedFields.Should().ContainKey("causality");
        mapped.Interpretation.AttributionIds.Should().BeEmpty();
        mapped.Interpretation.NextActionIds.Should().BeEmpty();
        mapped.Uncertainty.OverclaimsCertainty.Should().BeTrue();
        mapped.Uncertainty.AcknowledgesLimits.Should().BeFalse();

        var evidence = ScenarioJson.ReadEvidence(
            ScenarioManifestLoader.ScenarioPath("Fixtures", "lock-storm.windows.evidence.json"));
        var report = ScenarioEvaluator.CreateReport(manifest, evidence, mapped.Interpretation);

        report.Interpretation.Status.Should().Be(ScenarioStageStatus.Failed);
        report.InterpretationScore!.WeightedScore.Should().BeLessThan(0.5);
        report.InterpretationScore.Dimensions
            .Single(dimension => dimension.Name == "attribution").Score.Should().Be(0);
        report.InterpretationScore.Dimensions
            .Single(dimension => dimension.Name == "next-action").Score.Should().Be(0);
        report.InterpretationScore.Dimensions
            .Single(dimension => dimension.Name == "correlation-versus-causality").Score.Should().Be(0);
        report.InterpretationScore.Dimensions
            .Single(dimension => dimension.Name == "unsupported-conclusions").Score.Should().Be(0);
    }

    [Theory]
    [InlineData("sync-over-async")]
    [InlineData("culture-lookup")]
    public void Map_AcceptedNarrativeAcrossOtherScenarios_ResolvesEveryControlledField(string scenarioId)
    {
        var manifest = LoadManifest(scenarioId);
        var response = new AgentScenarioResponse(
            CitedEvidenceIds: manifest.ExpectedEvidence.Select(item => item.Id).ToArray(),
            Hypothesis: manifest.AcceptableHypotheses[0].Replace('-', ' '),
            Attribution: manifest.AcceptableAttributions[0],
            NextAction: manifest.AcceptableNextActions[0].Replace('-', ' '),
            CausalityStatement: manifest.RequiredCausalityPosture.Replace('-', ' '),
            Conclusions: [],
            Narrative: "This appears likely but remains consistent with a correlation pending further investigation.");

        var mapped = ScenarioAgentResponseMapper.Map(manifest, response);

        mapped.UnmappedFields.Should().BeEmpty();
        mapped.Interpretation.HypothesisIds.Should().Equal(manifest.AcceptableHypotheses[0]);
        mapped.Interpretation.AttributionIds.Should().Equal(manifest.AcceptableAttributions[0]);
        mapped.Interpretation.NextActionIds.Should().Equal(manifest.AcceptableNextActions[0]);
        mapped.Interpretation.CausalityPosture.Should().Be(manifest.RequiredCausalityPosture);
    }

    [Fact]
    public void Map_UnrecognizableHypothesis_IsReportedAsUnmappedRatherThanGuessed()
    {
        var manifest = LoadManifest("lock-storm");
        var response = new AgentScenarioResponse(
            CitedEvidenceIds: [],
            Hypothesis: "The disk firmware is corrupted and needs a vendor patch.",
            Attribution: manifest.AcceptableAttributions[0],
            NextAction: manifest.AcceptableNextActions[0].Replace('-', ' '),
            CausalityStatement: manifest.RequiredCausalityPosture.Replace('-', ' '),
            Conclusions: [],
            Narrative: string.Empty);

        var mapped = ScenarioAgentResponseMapper.Map(manifest, response);

        mapped.UnmappedFields.Should().ContainSingle().Which.Key.Should().Be("hypothesis");
        mapped.Interpretation.HypothesisIds.Should().BeEmpty();
        mapped.Uncertainty.AcknowledgesLimits.Should().BeFalse();
        mapped.Uncertainty.OverclaimsCertainty.Should().BeFalse();
    }

    [Fact]
    public void Map_NegatedAcceptedHypothesis_DoesNotSilentlyMatch()
    {
        // "not <accepted phrase>" has near-perfect token overlap with the accepted hypothesis id --
        // without a negation guard this would silently map (and score) as the correct diagnosis.
        var manifest = LoadManifest("lock-storm");
        var response = new AgentScenarioResponse(
            CitedEvidenceIds: [],
            Hypothesis: "This is not a sleeping monitor owner serializes work situation.",
            Attribution: manifest.AcceptableAttributions[0],
            NextAction: manifest.AcceptableNextActions[0].Replace('-', ' '),
            CausalityStatement: manifest.RequiredCausalityPosture.Replace('-', ' '),
            Conclusions: [],
            Narrative: string.Empty);

        var mapped = ScenarioAgentResponseMapper.Map(manifest, response);

        mapped.UnmappedFields.Should().ContainKey("hypothesis");
        mapped.Interpretation.HypothesisIds.Should().BeEmpty();
    }

    [Fact]
    public void Map_AmbiguousHypothesisBetweenAcceptedAndWrong_IsReportedAsUnmappedRatherThanGuessed()
    {
        // The response's own wording clears the match threshold for both the accepted hypothesis
        // and a tempting-wrong one. Picking whichever scores higher would hide that the response
        // itself endorses a wrong hypothesis alongside the right one.
        var manifest = LoadManifest("lock-storm");
        var response = new AgentScenarioResponse(
            CitedEvidenceIds: [],
            Hypothesis: "Sleeping monitor owner serializes work or external IO wait.",
            Attribution: manifest.AcceptableAttributions[0],
            NextAction: manifest.AcceptableNextActions[0].Replace('-', ' '),
            CausalityStatement: manifest.RequiredCausalityPosture.Replace('-', ' '),
            Conclusions: [],
            Narrative: string.Empty);

        var mapped = ScenarioAgentResponseMapper.Map(manifest, response);

        mapped.UnmappedFields.Should().ContainKey("hypothesis");
        mapped.Interpretation.HypothesisIds.Should().BeEmpty();
    }

    [Fact]
    public void Map_ContrastiveNegationInAnotherClause_DoesNotFalselyNegateTheAffirmedHypothesis()
    {
        // "Not <wrong candidate>, but <accepted candidate>" negates only the wrong candidate's
        // clause. A negation guard that ignores clause boundaries would incorrectly treat the
        // affirmed accepted hypothesis as negated too, just because "not" appears a few tokens
        // earlier in the *other* clause.
        var manifest = LoadManifest("lock-storm");
        var response = new AgentScenarioResponse(
            CitedEvidenceIds: [],
            Hypothesis: "Not a GC pause, but a sleeping monitor owner serializes work.",
            Attribution: manifest.AcceptableAttributions[0],
            NextAction: manifest.AcceptableNextActions[0].Replace('-', ' '),
            CausalityStatement: manifest.RequiredCausalityPosture.Replace('-', ' '),
            Conclusions: [],
            Narrative: string.Empty);

        var mapped = ScenarioAgentResponseMapper.Map(manifest, response);

        mapped.UnmappedFields.Should().NotContainKey("hypothesis");
        mapped.Interpretation.HypothesisIds.Should().Equal("sleeping-monitor-owner-serializes-work");
    }

    [Fact]
    public void Map_NegatedContractionAcceptedHypothesis_DoesNotSilentlyMatch()
    {
        // "isn't" must be treated the same as "is not" -- naive tokenization turns it into
        // meaningless fragments ("isn", "t") that never match a configured negation marker,
        // which would let a contraction-negated wrong claim silently score as the right one.
        var manifest = LoadManifest("lock-storm");
        var response = new AgentScenarioResponse(
            CitedEvidenceIds: [],
            Hypothesis: "This isn't a sleeping monitor owner serializes work situation.",
            Attribution: manifest.AcceptableAttributions[0],
            NextAction: manifest.AcceptableNextActions[0].Replace('-', ' '),
            CausalityStatement: manifest.RequiredCausalityPosture.Replace('-', ' '),
            Conclusions: [],
            Narrative: string.Empty);

        var mapped = ScenarioAgentResponseMapper.Map(manifest, response);

        mapped.UnmappedFields.Should().ContainKey("hypothesis");
        mapped.Interpretation.HypothesisIds.Should().BeEmpty();
    }

    [Fact]
    public void Map_NegationSeparatedByExclamationMark_DoesNotCrossSentenceBoundary()
    {
        // "Not <wrong candidate>! <accepted candidate>." must behave like the comma/"but" case:
        // the "!" ends the negated clause, so the accepted hypothesis in the next sentence is
        // affirmed, not swept up by the earlier negation.
        var manifest = LoadManifest("lock-storm");
        var response = new AgentScenarioResponse(
            CitedEvidenceIds: [],
            Hypothesis: "Not a GC pause! A sleeping monitor owner serializes work.",
            Attribution: manifest.AcceptableAttributions[0],
            NextAction: manifest.AcceptableNextActions[0].Replace('-', ' '),
            CausalityStatement: manifest.RequiredCausalityPosture.Replace('-', ' '),
            Conclusions: [],
            Narrative: string.Empty);

        var mapped = ScenarioAgentResponseMapper.Map(manifest, response);

        mapped.UnmappedFields.Should().NotContainKey("hypothesis");
        mapped.Interpretation.HypothesisIds.Should().Equal("sleeping-monitor-owner-serializes-work");
    }

    [Fact]
    public void Map_ShortWrongCandidateAlongsideAcceptedViaOr_IsReportedAsUnmappedRatherThanGuessed()
    {
        // A short tempting-wrong candidate ("gc-pause", 2 tokens) mentioned alongside the longer
        // accepted candidate in the same sentence must still trigger ambiguity: scoring against
        // the *whole* flattened response token set would dilute "gc-pause" below the match
        // threshold (diluted by the unrelated accepted-hypothesis tokens) and hide that the
        // response explicitly floats a wrong alternative. Per-clause scoring (splitting on "or")
        // keeps each alternative's score undiluted.
        var manifest = LoadManifest("lock-storm");
        var response = new AgentScenarioResponse(
            CitedEvidenceIds: [],
            Hypothesis: "Sleeping monitor owner serializes work or GC pause.",
            Attribution: manifest.AcceptableAttributions[0],
            NextAction: manifest.AcceptableNextActions[0].Replace('-', ' '),
            CausalityStatement: manifest.RequiredCausalityPosture.Replace('-', ' '),
            Conclusions: [],
            Narrative: string.Empty);

        var mapped = ScenarioAgentResponseMapper.Map(manifest, response);

        mapped.UnmappedFields.Should().ContainKey("hypothesis");
        mapped.Interpretation.HypothesisIds.Should().BeEmpty();
    }

    [Fact]
    public void Map_PostCandidateNegation_DoesNotSilentlyMatch()
    {
        // A negation marker appearing shortly *after* the candidate phrase ("X is not the cause")
        // must be caught too, not just a leading "not X" -- otherwise a response that explicitly
        // rejects the accepted hypothesis at the end of the sentence would silently map as correct.
        var manifest = LoadManifest("lock-storm");
        var response = new AgentScenarioResponse(
            CitedEvidenceIds: [],
            Hypothesis: "A sleeping monitor owner serializes work is not the cause here.",
            Attribution: manifest.AcceptableAttributions[0],
            NextAction: manifest.AcceptableNextActions[0].Replace('-', ' '),
            CausalityStatement: manifest.RequiredCausalityPosture.Replace('-', ' '),
            Conclusions: [],
            Narrative: string.Empty);

        var mapped = ScenarioAgentResponseMapper.Map(manifest, response);

        mapped.UnmappedFields.Should().ContainKey("hypothesis");
        mapped.Interpretation.HypothesisIds.Should().BeEmpty();
    }

    [Fact]
    public void Map_NegationBeforeClosingQuoteAndSentenceEnd_StillEndsTheClause()
    {
        // A closing quote right after the sentence-ending "!" must not defeat the clause boundary --
        // "Not <wrong candidate>!" <accepted candidate>." should behave exactly like the unquoted
        // "Not X! Y." case: the accepted hypothesis in the next sentence stays affirmed.
        var manifest = LoadManifest("lock-storm");
        var response = new AgentScenarioResponse(
            CitedEvidenceIds: [],
            Hypothesis: "\"Not a GC pause!\" A sleeping monitor owner serializes work.",
            Attribution: manifest.AcceptableAttributions[0],
            NextAction: manifest.AcceptableNextActions[0].Replace('-', ' '),
            CausalityStatement: manifest.RequiredCausalityPosture.Replace('-', ' '),
            Conclusions: [],
            Narrative: string.Empty);

        var mapped = ScenarioAgentResponseMapper.Map(manifest, response);

        mapped.UnmappedFields.Should().NotContainKey("hypothesis");
        mapped.Interpretation.HypothesisIds.Should().Equal("sleeping-monitor-owner-serializes-work");
    }

    [Fact]
    public void Map_TypographicApostropheContraction_DoesNotSilentlyMatch()
    {
        // A Unicode "smart quote" apostrophe (U+2019) in "isn't" must be normalized before contraction
        // expansion runs, so it is caught by the same negation guard as the ASCII apostrophe form.
        var manifest = LoadManifest("lock-storm");
        var response = new AgentScenarioResponse(
            CitedEvidenceIds: [],
            Hypothesis: "This isn\u2019t a sleeping monitor owner serializes work situation.",
            Attribution: manifest.AcceptableAttributions[0],
            NextAction: manifest.AcceptableNextActions[0].Replace('-', ' '),
            CausalityStatement: manifest.RequiredCausalityPosture.Replace('-', ' '),
            Conclusions: [],
            Narrative: string.Empty);

        var mapped = ScenarioAgentResponseMapper.Map(manifest, response);

        mapped.UnmappedFields.Should().ContainKey("hypothesis");
        mapped.Interpretation.HypothesisIds.Should().BeEmpty();
    }

    [Fact]
    public void Map_NegatedOverclaimPhrase_IsNotFlaggedAsOverclaiming()
    {
        // The narrative explicitly rejects the overclaim phrase ("is not definitely the cause");
        // raw substring matching would still flag OverclaimsCertainty even though the sentence is
        // hedging, not overclaiming.
        var manifest = LoadManifest("lock-storm");
        var response = new AgentScenarioResponse(
            CitedEvidenceIds: manifest.ExpectedEvidence.Select(item => item.Id).ToArray(),
            Hypothesis: "A sleeping monitor owner serializes work while waiters queue behind it.",
            Attribution: "The thread holding the monitor is asleep -- owner thread sleep.",
            NextAction: "Next: query the lock graph.",
            CausalityStatement: "The owner and waiters are correlated; this remains a correlation, not a proven cause.",
            Conclusions: [],
            Narrative: "This is not definitely the cause -- further investigation could add certainty.");

        var mapped = ScenarioAgentResponseMapper.Map(manifest, response);

        mapped.Uncertainty.OverclaimsCertainty.Should().BeFalse();
        mapped.Uncertainty.AcknowledgesLimits.Should().BeTrue();
    }

    [Fact]
    public void Map_NegatedOverclaimPhraseWithTypographicApostrophe_IsNotFlaggedAsOverclaiming()
    {
        // Same intent as Map_NegatedOverclaimPhrase_IsNotFlaggedAsOverclaiming, but negated via a
        // Unicode "smart quote" contraction ("isn't" with U+2019) instead of "is not" -- the
        // narrative-side quote normalization must match the one already applied on the tokenization
        // side, or this would inconsistently flag an overclaim depending on which apostrophe glyph
        // the response happened to use.
        var manifest = LoadManifest("lock-storm");
        var response = new AgentScenarioResponse(
            CitedEvidenceIds: manifest.ExpectedEvidence.Select(item => item.Id).ToArray(),
            Hypothesis: "A sleeping monitor owner serializes work while waiters queue behind it.",
            Attribution: "The thread holding the monitor is asleep -- owner thread sleep.",
            NextAction: "Next: query the lock graph.",
            CausalityStatement: "The owner and waiters are correlated; this remains a correlation, not a proven cause.",
            Conclusions: [],
            Narrative: "This isn\u2019t definitely the cause -- further investigation could add certainty.");

        var mapped = ScenarioAgentResponseMapper.Map(manifest, response);

        mapped.Uncertainty.OverclaimsCertainty.Should().BeFalse();
        mapped.Uncertainty.AcknowledgesLimits.Should().BeTrue();
    }

    private static ScenarioManifest LoadManifest(string scenarioId)
        => ScenarioManifestLoader.LoadAll().Single(item => item.Id == scenarioId);
}
