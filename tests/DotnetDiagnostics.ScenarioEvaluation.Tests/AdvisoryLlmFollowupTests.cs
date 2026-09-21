using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed partial class AdvisoryLlmAssessmentTests
{
    private static readonly JsonSerializerOptions FollowupTestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false),
        },
    };

    [Fact]
    public async Task Followup_AssessesSchemaInvalidRetainedTextAndRunsOnlyDeclaredCalls()
    {
        using var files = new AssessmentTestFiles();
        var fixture = await CreateFollowupFixtureAsync(files);
        var sourceHashes = FollowupHashTree(fixture.SourceRoot);
        var previousSourceHashes = FollowupHashTree(fixture.PreviousSourceRoot);
        var frozenPath = files.Path("followup.plan.json");
        var plan = AdvisoryLlmAssessment.FreezeFollowupPlan(
            fixture.Protocol,
            fixture.DraftPath,
            frozenPath);
        var output = files.Path("followup-output");
        var transport = new FollowupTransport(output, fixture.Protocol);
        var previous = Environment.GetEnvironmentVariable(
            AdvisoryLlmAssessment.FollowupAuthorizationVariable);
        Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.FollowupAuthorizationVariable, "1");
        try
        {
            var summary = await AdvisoryLlmAssessment.RunFollowupAsync(
                fixture.Protocol,
                plan,
                output,
                transport,
                CancellationToken.None);

            summary.TechnicallyComplete.Should().BeTrue();
            summary.ActualNewCalls.Should().Be(8);
            summary.ActualNewPhaseACalls.Should().Be(1);
            summary.ActualNewPhaseBCalls.Should().Be(7);
            summary.MeasurementGlossaryVersion.Should().Be(
                AdvisoryLlmAssessment.FollowupMeasurementGlossaryVersion);
            summary.MeasurementGlossarySha256.Should().Be(
                AdvisoryLlmAssessment.FollowupMeasurementGlossarySha256);
            summary.MeasurementGlossaryProvenance.Should().BeEquivalentTo(
                AdvisoryLlmAssessment.FollowupMeasurementGlossaryProvenance,
                options => options.WithStrictOrdering());
            summary.PreviousSourceSummarySha256.Should().Be(plan.PreviousSourceSummarySha256);
            transport.PhaseACalls.Should().Be(1);
            transport.PhaseBCalls.Should().Be(7);
            transport.Prompts.Should().OnlyContain(prompt =>
                !prompt.Contains(plan.PlanId, StringComparison.Ordinal)
                && !prompt.Contains(plan.PlanFingerprint, StringComparison.Ordinal)
                && !prompt.Contains("schemaInvalid", StringComparison.OrdinalIgnoreCase)
                && !prompt.Contains("source-run", StringComparison.Ordinal)
                && !prompt.Contains("continuation-source", StringComparison.Ordinal)
                && !prompt.Contains("case-0", StringComparison.Ordinal)
                && !prompt.Contains("PRIOR-B-VERDICT-MARKER", StringComparison.Ordinal)
                && !prompt.Contains("CONTROLLER-SOURCE-STATUS-MARKER", StringComparison.Ordinal)
                && !prompt.Contains("f9c2ef8e", StringComparison.Ordinal)
                && !prompt.Contains("CounterValue.cs", StringComparison.Ordinal)
                && prompt.Contains("value is the last observed sample", StringComparison.Ordinal)
                && prompt.Contains("not a capture-wide mean or total", StringComparison.Ordinal)
                && prompt.Contains("do not divide value by", StringComparison.Ordinal));
            transport.Prompts.Where(prompt =>
                    prompt.Contains("opaque candidate interpretations", StringComparison.Ordinal))
                .Should().OnlyContain(prompt =>
                    prompt.Contains("require a baseline", StringComparison.Ordinal)
                    && prompt.Contains("reference; otherwise", StringComparison.Ordinal)
                    && prompt.Contains("do not establish simultaneity or causation", StringComparison.Ordinal)
                    && prompt.Contains("Missing data supports uncertainty", StringComparison.Ordinal)
                    && prompt.Contains("Candidate agreement is not truth", StringComparison.Ordinal)
                    && prompt.Contains("Do not choose a winner", StringComparison.Ordinal)
                    && prompt.Contains("predates the measurement glossary", StringComparison.Ordinal)
                    && prompt.Contains("do not attribute a measurement-semantics mismatch solely",
                        StringComparison.Ordinal));

            var first = summary.Cases[0];
            first.PhaseAMode.Should().Be(AdvisoryFollowupPhaseAMode.RetainedSemanticExtraction);
            first.PhaseAReceivedMeasurementGlossary.Should().BeFalse();
            first.PhaseA.PromptSha256.Should().Be(
                plan.Cases[0].Source.SourcePhaseAPromptSha256);
            first.PhaseA.Status.Should().Be(AdvisoryCallStatus.InvalidResponse);
            first.Format.Compliance.Should().Be(AdvisoryFollowupFormatCompliance.Noncompliant);
            first.SemanticView!.Claims.Should().ContainSingle();
            first.SemanticView.Claims[0].ControllerId.Should().Be("followup-claim-01");
            first.SemanticView.Claims[0].TextPointer.Should().Be("/hypotheses/0/text");
            first.SemanticView.Claims[0].Text.Should().Be("Synthetic retained hypothesis.");
            first.SemanticView.CitationResolutions.Should().ContainSingle(value =>
                !value.ExistsInProjection && value.Location.EndsWith("/missing", StringComparison.Ordinal));
            first.PhaseA.RawResponse.Should().Contain("\"observationId\":\"hypothesis-01\"");
            first.PhaseA.RawResponse.Should().NotContain("\"hypothesisId\"");
            File.Exists(first.SemanticSealPath).Should().BeTrue();

            var fresh = summary.Cases[4];
            fresh.PhaseAMode.Should().Be(AdvisoryFollowupPhaseAMode.FreshEvidenceOnly);
            fresh.PhaseAReceivedMeasurementGlossary.Should().BeTrue();
            fresh.PhaseA.PromptSha256.Should().NotBe(
                plan.Cases[4].Source.SourcePhaseAPromptSha256);
            fresh.SourcePhaseAStatus.Should().Be(AdvisoryCallStatus.TransportFailed);
            fresh.SourcePhaseAResponseSha256.Should().BeNull();
            fresh.PhaseA.Status.Should().Be(AdvisoryCallStatus.Succeeded);
            fresh.Format.Compliance.Should().Be(AdvisoryFollowupFormatCompliance.Compliant);
            fresh.SemanticView!.Claims.Should().ContainSingle();
            fresh.SemanticView.Claims[0].ControllerId.Should().Be("followup-claim-01");
            File.Exists(fresh.SemanticSealPath).Should().BeTrue();

            summary.OrderControls.Should().HaveCount(2);
            summary.OrderControlResults.Should().HaveCount(2);
            foreach (var control in summary.OrderControls)
            {
                var primary = summary.Cases.Single(value => value.SlotId == control.SlotId).Primary;
                control.FirstCandidate.Should().NotBe(primary.FirstCandidate);
                control.CandidateContentSha256.Should().Be(primary.CandidateContentSha256);
            }
            summary.OrderControlResults.Should().OnlyContain(value =>
                value.Claims.All(claim => claim.PrimarySupport == claim.ControlSupport
                    && claim.PrimaryCertainty == claim.ControlCertainty)
                && value.Candidates.All(candidate =>
                    candidate.PrimaryUncertainty == candidate.ControlUncertainty
                    && candidate.PrimaryNextStep == candidate.ControlNextStep));
            FollowupHashTree(fixture.SourceRoot).Should().BeEquivalentTo(sourceHashes);
            FollowupHashTree(fixture.PreviousSourceRoot).Should().BeEquivalentTo(previousSourceHashes);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                AdvisoryLlmAssessment.FollowupAuthorizationVariable,
                previous);
        }
    }

    [Fact]
    public async Task Followup_FreshSchemaFailureRemainsVisibleButSafeSemanticViewContinues()
    {
        using var files = new AssessmentTestFiles();
        var fixture = await CreateFollowupFixtureAsync(files);
        var plan = AdvisoryLlmAssessment.FreezeFollowupPlan(
            fixture.Protocol,
            fixture.DraftPath,
            files.Path("format.plan.json"));
        var output = files.Path("format-output");
        var transport = new FollowupTransport(output, fixture.Protocol, invalidFreshId: true);
        var previous = Environment.GetEnvironmentVariable(
            AdvisoryLlmAssessment.FollowupAuthorizationVariable);
        Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.FollowupAuthorizationVariable, "1");
        try
        {
            var summary = await AdvisoryLlmAssessment.RunFollowupAsync(
                fixture.Protocol,
                plan,
                output,
                transport,
                CancellationToken.None);

            var fresh = summary.Cases[4];
            fresh.Format.Compliance.Should().Be(AdvisoryFollowupFormatCompliance.Noncompliant);
            fresh.SemanticView!.Claims.Should().ContainSingle();
            fresh.Primary.Response.Should().NotBeNull();
            summary.ActualNewCalls.Should().Be(8);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                AdvisoryLlmAssessment.FollowupAuthorizationVariable,
                previous);
        }
    }

    [Theory]
    [InlineData("sourceHash")]
    [InlineData("missingText")]
    [InlineData("duplicateProperty")]
    [InlineData("seal")]
    [InlineData("oldPrompt")]
    [InlineData("previousSummary")]
    [InlineData("wrapperPath")]
    public async Task FollowupPreparation_RejectsChangedOrUnreadableRetainedSource(string defect)
    {
        using var files = new AssessmentTestFiles();
        var fixture = await CreateFollowupFixtureAsync(files, defect);
        var output = files.Path("rejected.plan.json");

        FluentActions.Invoking(() => AdvisoryLlmAssessment.FreezeFollowupPlan(
                fixture.Protocol,
                fixture.DraftPath,
                output))
            .Should().Throw<Exception>();
        File.Exists(output).Should().BeFalse();
    }

    [Theory]
    [InlineData("nonemptyFingerprint")]
    [InlineData("duplicateProperty")]
    [InlineData("unknownProperty")]
    [InlineData("glossaryProvenance")]
    public async Task FollowupPreparation_StrictlyRejectsMalformedDraft(string defect)
    {
        using var files = new AssessmentTestFiles();
        var fixture = await CreateFollowupFixtureAsync(files);
        var json = File.ReadAllText(fixture.DraftPath);
        json = defect switch
        {
            "nonemptyFingerprint" => json.Replace(
                "\"planFingerprint\": \"\"",
                $"\"planFingerprint\": \"{new string('a', 64)}\"",
                StringComparison.Ordinal),
            "duplicateProperty" => json.Insert(1, "\"schemaVersion\":1,"),
            "glossaryProvenance" => json.Replace(
                "\"sourceRevision\": \"f9c2ef8e\"",
                "\"sourceRevision\": \"deadbeef\"",
                StringComparison.Ordinal),
            _ => json.Insert(1, "\"unexpected\":true,"),
        };
        var malformed = files.Path("malformed-followup.json");
        File.WriteAllText(malformed, json);
        var output = files.Path("malformed-frozen.json");

        FluentActions.Invoking(() => AdvisoryLlmAssessment.FreezeFollowupPlan(
                fixture.Protocol,
                malformed,
                output))
            .Should().Throw<Exception>();
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public async Task Followup_RequiresExplicitAuthorizationAndCreateNewOutput()
    {
        using var files = new AssessmentTestFiles();
        var fixture = await CreateFollowupFixtureAsync(files);
        var plan = AdvisoryLlmAssessment.FreezeFollowupPlan(
            fixture.Protocol,
            fixture.DraftPath,
            files.Path("authorization.plan.json"));
        var output = files.Path("existing-followup");
        Directory.CreateDirectory(output);
        var previous = Environment.GetEnvironmentVariable(
            AdvisoryLlmAssessment.FollowupAuthorizationVariable);
        Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.FollowupAuthorizationVariable, null);
        try
        {
            await FluentActions.Awaiting(() => AdvisoryLlmAssessment.RunFollowupAsync(
                    fixture.Protocol,
                    plan,
                    files.Path("not-authorized"),
                    new FollowupTransport(files.Path("not-authorized"), fixture.Protocol),
                    CancellationToken.None))
                .Should().ThrowAsync<InvalidOperationException>();

            Environment.SetEnvironmentVariable(
                AdvisoryLlmAssessment.FollowupAuthorizationVariable,
                "1");
            await FluentActions.Awaiting(() => AdvisoryLlmAssessment.RunFollowupAsync(
                    fixture.Protocol,
                    plan,
                    Path.Combine(fixture.SourceRoot, "forbidden-output"),
                    new FollowupTransport(
                        Path.Combine(fixture.SourceRoot, "forbidden-output"),
                        fixture.Protocol),
                    CancellationToken.None))
                .Should().ThrowAsync<InvalidDataException>();
            await FluentActions.Awaiting(() => AdvisoryLlmAssessment.RunFollowupAsync(
                    fixture.Protocol,
                    plan,
                    output,
                    new FollowupTransport(output, fixture.Protocol),
                    CancellationToken.None))
                .Should().ThrowAsync<IOException>();
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                AdvisoryLlmAssessment.FollowupAuthorizationVariable,
                previous);
        }
    }

    [Fact]
    public async Task Followup_GlobalPrerequisiteFailureAbortsRemainingCalls()
    {
        using var files = new AssessmentTestFiles();
        var fixture = await CreateFollowupFixtureAsync(files);
        var plan = AdvisoryLlmAssessment.FreezeFollowupPlan(
            fixture.Protocol,
            fixture.DraftPath,
            files.Path("abort.plan.json"));
        var output = files.Path("abort-output");
        var transport = new FollowupAuthenticationFailureTransport();
        var previous = Environment.GetEnvironmentVariable(
            AdvisoryLlmAssessment.FollowupAuthorizationVariable);
        Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.FollowupAuthorizationVariable, "1");
        try
        {
            var summary = await AdvisoryLlmAssessment.RunFollowupAsync(
                fixture.Protocol,
                plan,
                output,
                transport,
                CancellationToken.None);

            summary.AbortedForGlobalPrerequisite.Should().BeTrue();
            summary.ActualNewCalls.Should().Be(1);
            summary.ActualNewPhaseACalls.Should().Be(1);
            summary.ActualNewPhaseBCalls.Should().Be(0);
            summary.Cases.Should().OnlyContain(value =>
                value.Primary.PhaseB.Status == AdvisoryCallStatus.Skipped);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                AdvisoryLlmAssessment.FollowupAuthorizationVariable,
                previous);
        }
    }

    [Fact]
    public async Task Followup_PrimaryFormatFailureSkipsOnlyItsDependentControl()
    {
        using var files = new AssessmentTestFiles();
        var fixture = await CreateFollowupFixtureAsync(files);
        var plan = AdvisoryLlmAssessment.FreezeFollowupPlan(
            fixture.Protocol,
            fixture.DraftPath,
            files.Path("partial.plan.json"));
        var output = files.Path("partial-output");
        var transport = new FollowupTransport(
            output,
            fixture.Protocol,
            invalidFirstPhaseB: true);
        var previous = Environment.GetEnvironmentVariable(
            AdvisoryLlmAssessment.FollowupAuthorizationVariable);
        Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.FollowupAuthorizationVariable, "1");
        try
        {
            var summary = await AdvisoryLlmAssessment.RunFollowupAsync(
                fixture.Protocol,
                plan,
                output,
                transport,
                CancellationToken.None);

            summary.AbortedForGlobalPrerequisite.Should().BeFalse();
            summary.TechnicallyComplete.Should().BeFalse();
            summary.ActualNewCalls.Should().Be(7);
            summary.ActualNewPhaseACalls.Should().Be(1);
            summary.ActualNewPhaseBCalls.Should().Be(6);
            summary.Cases[0].Primary.PhaseB.Status.Should().Be(AdvisoryCallStatus.InvalidResponse);
            summary.OrderControls.Single(value => value.SlotId == summary.Cases[0].SlotId)
                .PhaseB.Status.Should().Be(AdvisoryCallStatus.Skipped);
            summary.OrderControls.Single(value => value.SlotId == summary.Cases[2].SlotId)
                .Response.Should().NotBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                AdvisoryLlmAssessment.FollowupAuthorizationVariable,
                previous);
        }
    }

    private static async Task<FollowupFixture> CreateFollowupFixtureAsync(
        AssessmentTestFiles files,
        string? defect = null)
    {
        var protocol = files.CreateProtocol("semantic-followup");
        var sourceRoot = files.Path("source-run");
        var previous = Environment.GetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable);
        Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable, "1");
        AdvisoryRunSummary source;
        try
        {
            source = await AdvisoryLlmAssessment.RunAsync(
                protocol,
                sourceRoot,
                new RecordingTransport(),
                CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable, previous);
        }

        var cases = source.Cases.ToArray();
        for (var index = 0; index < cases.Length; index++)
        {
            var sourceCase = cases[index];
            if (index < 4)
            {
                var retained = RetainedPhaseAJson(
                    invalidLegacyId: index < 2,
                    invalidCitation: index == 0);
                var raw = "```json\n" + retained + "\n```";
                sourceCase = sourceCase with
                {
                    PhaseA = sourceCase.PhaseA with
                    {
                        Status = AdvisoryCallStatus.InvalidResponse,
                        Detail = index < 2
                            ? "Legacy hypothesis ID property is invalid."
                            : "Legacy source status remains preserved.",
                        RawResponse = raw,
                        ResponseSha256 = Sha256(Encoding.UTF8.GetBytes(raw)),
                        ResponseFramingTransform = "unparsed",
                        NormalizedResponseSha256 = null,
                    },
                    SealedPhaseAPath = null,
                    SealedPhaseASha256 = null,
                    PhaseAResponse = null,
                    PhaseBResponse = null,
                    CandidateMapping = [],
                };
            }
            else if (index == 4)
            {
                sourceCase = sourceCase with
                {
                    PhaseA = sourceCase.PhaseA with
                    {
                        Status = AdvisoryCallStatus.TransportFailed,
                        Detail = "Source response exceeded its byte limit.",
                        RawResponse = null,
                        ResponseSha256 = null,
                        NormalizedResponseSha256 = null,
                    },
                    SealedPhaseAPath = null,
                    SealedPhaseASha256 = null,
                    PhaseAResponse = null,
                    PhaseBResponse = null,
                    CandidateMapping = [],
                };
            }
            cases[index] = sourceCase;
            WriteFollowupJson(Path.Combine(sourceRoot, sourceCase.SlotId, "case-result.json"), sourceCase);
        }
        source = source with { Cases = cases };
        WriteFollowupJson(Path.Combine(sourceRoot, "run-summary.json"), source);

        var continuationRoot = files.Path("continuation-source");
        var wrappers = new AdvisoryContinuationCaseResult[5];
        var wrapperBindings =
            new Dictionary<string, (string CaseSha, string? SealSha)>(StringComparer.Ordinal);
        for (var index = 0; index < 5; index++)
        {
            var sourceCase = cases[index];
            var slot = protocol.Slots[index];
            var projection = AdvisoryLlmAssessment.Project(protocol, slot);
            var normalized = sourceCase.PhaseA.RawResponse is null
                ? null
                : AdvisoryLlmAssessment.NormalizeJsonResponse(
                    sourceCase.PhaseA.RawResponse,
                    protocol.Limits.MaximumResponseBytes);
            var directory = Path.Combine(continuationRoot, slot.SlotId);
            Directory.CreateDirectory(directory);
            string? sealSha = null;
            var continuedCase = sourceCase;
            if (index is 2 or 3)
            {
                var response = AdvisoryLlmAssessment.ParsePhaseA(
                    sourceCase.PhaseA.RawResponse!,
                    projection,
                    protocol.Limits);
                var normalizedCall = sourceCase.PhaseA with
                {
                    ResponseFramingTransform = normalized!.Transform,
                    ResponseFramingVersion = normalized.Version,
                    NormalizedResponseSha256 = normalized.PayloadSha256,
                };
                var seal = new AdvisoryPhaseASeal(
                    2,
                    protocol.ProtocolId,
                    protocol.ProtocolFingerprint,
                    slot.SlotId,
                    slot.PacketFingerprint,
                    projection.ProjectionSha256,
                    normalizedCall.PromptSha256,
                    normalizedCall.ResponseSha256,
                    normalizedCall,
                    response);
                var sealPath = Path.Combine(directory, "phase-a.recovered.sealed.json");
                WriteFollowupJson(sealPath, seal);
                sealSha = Sha256(File.ReadAllBytes(sealPath));
                continuedCase = sourceCase with
                {
                    PhaseA = normalizedCall,
                    SealedPhaseAPath = sealPath,
                    SealedPhaseASha256 = sealSha,
                    PhaseAResponse = response,
                };
            }
            var priorVerdict = "PRIOR-B-VERDICT-MARKER";
            continuedCase = continuedCase with
            {
                PhaseB = continuedCase.PhaseB with
                {
                    Detail = priorVerdict,
                    RawResponse = priorVerdict,
                    ResponseSha256 = Sha256(Encoding.UTF8.GetBytes(priorVerdict)),
                },
                PhaseBResponse = null,
                CandidateMapping = [],
            };
            var wrapper = new AdvisoryContinuationCaseResult(
                slot.SlotId,
                index == 4
                    ? AdvisoryContinuationDisposition.Unavailable
                    : AdvisoryContinuationDisposition.RecoverRetainedPhaseA,
                "CONTROLLER-SOURCE-STATUS-MARKER",
                Sha256(File.ReadAllBytes(Path.Combine(sourceRoot, slot.SlotId, "case-result.json"))),
                sourceCase.PhaseA.Status,
                sourceCase.PhaseB.Status,
                sourceCase.PhaseA.ResponseSha256,
                normalized?.PayloadSha256,
                normalized?.Transform,
                AdvisoryPhaseOrigin.SourceFailurePreserved,
                AdvisoryPhaseOrigin.NotRun,
                continuedCase);
            var casePath = Path.Combine(directory, "continuation-case-result.json");
            WriteFollowupJson(casePath, wrapper);
            wrapperBindings.Add(
                slot.SlotId,
                (Sha256(File.ReadAllBytes(casePath)), sealSha));
            wrappers[index] = wrapper;
        }
        var previousSummarySha = Sha256(File.ReadAllBytes(Path.Combine(sourceRoot, "run-summary.json")));
        var continuationSummary = new AdvisoryContinuationSummary(
            2,
            "synthetic-continuation-source",
            new string('c', 64),
            protocol.ProtocolFingerprint,
            previousSummarySha,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            0,
            0,
            false,
            false,
            null,
            wrappers);
        Directory.CreateDirectory(continuationRoot);
        WriteFollowupJson(Path.Combine(continuationRoot, "run-summary.json"), continuationSummary);

        var followupLimits = protocol.Limits with
        {
            MaximumCases = 5,
            MaximumCalls = 8,
            MaximumCallsPerCase = 3,
        };
        var sourceDefinition = new AdvisoryFollowupSource(
            "continuation-source",
            AdvisoryFollowupSourceKind.ContinuationRun,
            Path.GetFullPath(continuationRoot),
            Sha256(File.ReadAllBytes(Path.Combine(continuationRoot, "run-summary.json"))));
        var primaryOrders = new[]
        {
            AdvisoryCandidateSource.Original,
            AdvisoryCandidateSource.Reanalysis,
            AdvisoryCandidateSource.Original,
            AdvisoryCandidateSource.Reanalysis,
            AdvisoryCandidateSource.Original,
        };
        var plans = protocol.Slots.Take(5).Select((slot, index) =>
        {
            var sourceCase = wrappers[index].Result;
            var normalized = sourceCase.PhaseA.RawResponse is null
                ? null
                : AdvisoryLlmAssessment.NormalizeJsonResponse(
                    sourceCase.PhaseA.RawResponse,
                    protocol.Limits.MaximumResponseBytes);
            return new AdvisoryFollowupCasePlan(
                slot.SlotId,
                index == 4
                    ? AdvisoryFollowupPhaseAMode.FreshEvidenceOnly
                    : AdvisoryFollowupPhaseAMode.RetainedSemanticExtraction,
                primaryOrders[index],
                new AdvisoryFollowupRetainedBinding(
                    sourceDefinition.SourceId,
                    $"{slot.SlotId}/continuation-case-result.json",
                    wrapperBindings[slot.SlotId].CaseSha,
                    sourceCase.PhaseA.PromptSha256,
                    sourceCase.PhaseA.ResponseSha256,
                    normalized?.PayloadSha256,
                    sourceCase.ProjectionSha256,
                    index is 2 or 3 ? sourceDefinition.SourceId : null,
                    index is 2 or 3 ? wrapperBindings[slot.SlotId].CaseSha : null,
                    wrapperBindings[slot.SlotId].SealSha),
                new AdvisoryFollowupSemanticBindings(
                    index == 4
                        ? []
                        :
                        [
                            new AdvisoryFollowupClaimBinding(
                                "followup-claim-01",
                                "/hypotheses/0/text",
                                "/hypotheses/0/evidenceLocations"),
                        ],
                    "/uncertainty",
                    "/abstained",
                    "/nextDiagnosticQuestion"));
        }).ToArray();
        var draft = new AdvisoryFollowupPlan(
            1,
            "synthetic-semantic-followup",
            string.Empty,
            protocol.ProtocolFingerprint,
            previousSummarySha,
            AdvisoryLlmAssessment.FollowupRubricVersion,
            AdvisoryLlmAssessment.FollowupRubricSha256,
            AdvisoryLlmAssessment.FollowupMeasurementGlossaryVersion,
            AdvisoryLlmAssessment.FollowupMeasurementGlossarySha256,
            AdvisoryLlmAssessment.FollowupMeasurementGlossaryProvenance,
            AdvisoryLlmAssessment.FollowupPhaseAPromptFingerprint,
            AdvisoryLlmAssessment.FollowupPhaseBPromptFingerprint,
            protocol.PhaseAModel,
            protocol.PhaseBModel,
            followupLimits,
            8,
            1,
            7,
            [sourceDefinition],
            plans,
            [
                new AdvisoryFollowupOrderControlPlan("control-01", protocol.Slots[0].SlotId),
                new AdvisoryFollowupOrderControlPlan("control-03", protocol.Slots[2].SlotId),
            ]);

        if (defect == "sourceHash")
        {
            draft = draft with
            {
                Cases = draft.Cases.Select((value, index) => index == 0
                    ? value with
                    {
                        Source = value.Source with
                        {
                            SourceCaseResultSha256 = new string('a', 64),
                        },
                    }
                    : value).ToArray(),
            };
        }
        else if (defect == "oldPrompt")
        {
            draft = draft with
            {
                Cases = draft.Cases.Select((value, index) => index == 0
                    ? value with
                    {
                        Source = value.Source with
                        {
                            SourcePhaseAPromptSha256 = new string('b', 64),
                        },
                    }
                    : value).ToArray(),
            };
        }
        else if (defect == "previousSummary")
        {
            draft = draft with { PreviousSourceSummarySha256 = new string('d', 64) };
        }
        else if (defect == "wrapperPath")
        {
            draft = draft with
            {
                Cases = draft.Cases.Select((value, index) => index == 0
                    ? value with
                    {
                        Source = value.Source with
                        {
                            SourceCaseRelativePath = "case-01/other.json",
                        },
                    }
                    : value).ToArray(),
            };
        }
        else if (defect == "missingText")
        {
            draft = draft with
            {
                Cases = draft.Cases.Select((value, index) => index == 0
                    ? value with
                    {
                        SemanticBindings = value.SemanticBindings with
                        {
                            Claims =
                            [
                                value.SemanticBindings.Claims[0] with
                                {
                                    TextPointer = "/hypotheses/0/missing",
                                },
                            ],
                        },
                    }
                    : value).ToArray(),
            };
        }
        else if (defect == "duplicateProperty")
        {
            var changedWrapper = wrappers[0];
            var changed = changedWrapper.Result;
            var raw = changed.PhaseA.RawResponse!.Replace(
                "\"uncertainty\":",
                "\"uncertainty\":\"duplicate\",\"uncertainty\":",
                StringComparison.Ordinal);
            changed = changed with
            {
                PhaseA = changed.PhaseA with
                {
                    RawResponse = raw,
                    ResponseSha256 = Sha256(Encoding.UTF8.GetBytes(raw)),
                },
            };
            changedWrapper = changedWrapper with { Result = changed };
            wrappers[0] = changedWrapper;
            var wrapperPath = Path.Combine(
                continuationRoot,
                changed.SlotId,
                "continuation-case-result.json");
            WriteFollowupJson(wrapperPath, changedWrapper);
            continuationSummary = continuationSummary with { Cases = wrappers };
            WriteFollowupJson(
                Path.Combine(continuationRoot, "run-summary.json"),
                continuationSummary);
            sourceDefinition = sourceDefinition with
            {
                SummarySha256 = Sha256(
                    File.ReadAllBytes(Path.Combine(continuationRoot, "run-summary.json"))),
            };
            var changedBinding = draft.Cases[0].Source with
            {
                SourceCaseResultSha256 = Sha256(File.ReadAllBytes(wrapperPath)),
                SourcePhaseAResponseSha256 = changed.PhaseA.ResponseSha256,
                NormalizedPayloadSha256 = AdvisoryLlmAssessment.NormalizeJsonResponse(
                    raw,
                    protocol.Limits.MaximumResponseBytes).PayloadSha256,
            };
            draft = draft with
            {
                Sources = [sourceDefinition],
                Cases = draft.Cases.Select((value, index) => index == 0
                    ? value with { Source = changedBinding }
                    : value).ToArray(),
            };
        }
        else if (defect == "seal")
        {
            File.AppendAllText(
                Path.Combine(
                    continuationRoot,
                    protocol.Slots[2].SlotId,
                    "phase-a.recovered.sealed.json"),
                " ");
        }

        var draftPath = files.Path("followup.draft.json");
        WriteFollowupJson(draftPath, draft);
        return new FollowupFixture(protocol, continuationRoot, sourceRoot, draftPath);
    }

    private static string RetainedPhaseAJson(bool invalidLegacyId, bool invalidCitation)
    {
        var idProperty = invalidLegacyId ? "observationId" : "hypothesisId";
        var location = invalidCitation
            ? "tool-result://evidence-01#/evidence/counters/0/missing"
            : "tool-result://evidence-01#/evidence/counters/0/value";
        return $$"""
          {"observations":[{"observationId":"observation-01","text":"Synthetic observation.","evidenceLocations":["tool-result://evidence-01#/evidence/counters/0/value"]}],"hypotheses":[{"{{idProperty}}":"hypothesis-01","text":"Synthetic retained hypothesis.","confidence":"low","evidenceLocations":["{{location}}"]}],"alternatives":[],"uncertainty":"Synthetic retained uncertainty.","abstained":false,"nextDiagnosticQuestion":"What additional measurement is available?"}
          """;
    }

    private static string FreshPhaseAJson(bool invalidId)
        => $$"""
          {"observations":[{"{{(invalidId ? "observationId" : "id")}}":"observation-01","text":"Synthetic fresh observation.","evidenceLocations":["tool-result://evidence-01#/evidence/counters/0/value"]}],"hypotheses":[{"id":"hypothesis-01","text":"Synthetic fresh hypothesis.","evidenceLocations":["tool-result://evidence-01#/evidence/counters/0/value"]}],"alternatives":[],"uncertainty":"Synthetic fresh uncertainty.","abstained":false,"nextDiagnosticQuestion":"What additional measurement is available?"}
          """;

    private static string FollowupPhaseBJson(string prompt)
    {
        const string marker = "\n\nEvidence and candidates:";
        var offset = prompt.IndexOf(marker, StringComparison.Ordinal);
        offset.Should().BeGreaterThanOrEqualTo(0);
        using var payload = JsonDocument.Parse(prompt[(offset + marker.Length)..]);
        var candidates = payload.RootElement.GetProperty("candidates")
            .EnumerateArray()
            .Select(candidate => new
            {
                candidateId = candidate.GetProperty("candidateId").GetString(),
                claims = candidate.GetProperty("claims").EnumerateArray().Select(claim => new
                {
                    claimId = claim.GetProperty("claimId").GetString(),
                    support = "supported",
                    certainty = "appropriate",
                    rationale = "Synthetic deterministic claim judgment.",
                }).ToArray(),
                abstentionAndUncertainty = "useful",
                nextStepUsefulness = "useful",
                rationale = "Synthetic deterministic candidate judgment.",
            }).ToArray();
        return JsonSerializer.Serialize(new
        {
            candidates,
            disagreements = Array.Empty<string>(),
            overallLimitations = "Synthetic deterministic limitations.",
        });
    }

    private static Dictionary<string, string> FollowupHashTree(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => Sha256(File.ReadAllBytes(path)),
                StringComparer.Ordinal);

    private static void WriteFollowupJson<T>(string path, T value)
        => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, FollowupTestJsonOptions));

    private sealed record FollowupFixture(
        AdvisoryLlmProtocol Protocol,
        string SourceRoot,
        string PreviousSourceRoot,
        string DraftPath);

    private sealed class FollowupTransport(
        string outputRoot,
        AdvisoryLlmProtocol protocol,
        bool invalidFreshId = false,
        bool invalidFirstPhaseB = false) : IAdvisoryStructuredTransport
    {
        public int PhaseACalls { get; private set; }

        public int PhaseBCalls { get; private set; }

        public List<string> Prompts { get; } = [];

        public Task<AdvisoryStructuredInvocation> CompleteAsync(
            AdvisoryLlmModel model,
            string prompt,
            int maximumResponseBytes,
            CancellationToken cancellationToken)
        {
            Prompts.Add(prompt);
            if (model.Model == protocol.PhaseAModel.Model)
            {
                PhaseACalls++;
                PhaseACalls.Should().Be(1);
                Directory.EnumerateFiles(
                        outputRoot,
                        "phase-a.semantic.sealed.json",
                        SearchOption.AllDirectories)
                    .Should().HaveCount(4);
                return Task.FromResult(new AdvisoryStructuredInvocation(
                    FreshPhaseAJson(invalidFreshId)));
            }

            model.Model.Should().Be(protocol.PhaseBModel.Model);
            PhaseBCalls++;
            Directory.EnumerateFiles(
                    outputRoot,
                    "phase-a.semantic.sealed.json",
                    SearchOption.AllDirectories)
                .Should().HaveCount(5, "all semantic views must be sealed before comparison calls");
            if (invalidFirstPhaseB && PhaseBCalls == 1)
            {
                return Task.FromResult(new AdvisoryStructuredInvocation("{"));
            }
            return Task.FromResult(new AdvisoryStructuredInvocation(FollowupPhaseBJson(prompt)));
        }
    }

    private sealed class FollowupAuthenticationFailureTransport : IAdvisoryStructuredTransport
    {
        public Task<AdvisoryStructuredInvocation> CompleteAsync(
            AdvisoryLlmModel model,
            string prompt,
            int maximumResponseBytes,
            CancellationToken cancellationToken)
            => throw new AgentTransportException("Authentication is required.");
    }
}
