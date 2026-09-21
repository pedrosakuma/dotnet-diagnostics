using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class AdvisoryLlmAssessmentTests
{
    [Fact]
    public void FreezeProtocol_UsesCanonicalFingerprintWithoutOverwritingFiles()
    {
        using var files = new AssessmentTestFiles();
        var protocol = files.CreateProtocol();
        var sourcePath = files.Path("source-protocol.json");
        AdvisoryLlmAssessment.WriteProtocol(sourcePath, protocol);
        var draftJson = File.ReadAllText(sourcePath)
            .Replace(protocol.ProtocolFingerprint, string.Empty, StringComparison.Ordinal);
        var draftPath = files.Path("draft.json");
        File.WriteAllText(draftPath, draftJson);
        var outputPath = files.Path("frozen.json");

        var frozen = AdvisoryLlmAssessment.FreezeProtocol(draftPath, outputPath);

        frozen.ProtocolFingerprint.Should().Be(protocol.ProtocolFingerprint);
        AdvisoryLlmAssessment.LoadProtocol(outputPath).Should().BeEquivalentTo(protocol);
        File.ReadAllText(draftPath).Should().Be(draftJson);
        var frozenBytes = File.ReadAllBytes(outputPath);
        FluentActions.Invoking(() => AdvisoryLlmAssessment.FreezeProtocol(draftPath, outputPath))
            .Should().Throw<IOException>();
        File.ReadAllBytes(outputPath).Should().Equal(frozenBytes);
    }

    [Theory]
    [InlineData("nonemptyFingerprint")]
    [InlineData("duplicateProperty")]
    [InlineData("unknownProperty")]
    [InlineData("invalidLimits")]
    public void FreezeProtocol_RejectsInvalidDraftBeforeCreatingOutput(string defect)
    {
        using var files = new AssessmentTestFiles();
        var protocol = files.CreateProtocol();
        var sourcePath = files.Path("source-protocol.json");
        AdvisoryLlmAssessment.WriteProtocol(sourcePath, protocol);
        var json = File.ReadAllText(sourcePath);
        if (defect != "nonemptyFingerprint")
        {
            json = json.Replace(protocol.ProtocolFingerprint, string.Empty, StringComparison.Ordinal);
        }

        json = defect switch
        {
            "duplicateProperty" => json.Insert(1, "\"schemaVersion\":2,"),
            "unknownProperty" => json.Insert(1, "\"unexpected\":true,"),
            "invalidLimits" => json.Replace(
                "\"maximumCalls\": 16", "\"maximumCalls\": 17", StringComparison.Ordinal),
            _ => json,
        };
        var draftPath = files.Path("draft.json");
        File.WriteAllText(draftPath, json);
        var outputPath = files.Path("frozen.json");
        var action = () => AdvisoryLlmAssessment.FreezeProtocol(draftPath, outputPath);

        if (defect == "unknownProperty")
        {
            action.Should().Throw<JsonException>();
        }
        else
        {
            action.Should().Throw<InvalidDataException>();
        }

        File.Exists(outputPath).Should().BeFalse();
    }

    [Fact]
    public void Prompts_DiscloseEnforcedResponseBounds()
    {
        using var files = new AssessmentTestFiles();
        var protocol = files.CreateProtocol();
        var projection = AdvisoryLlmAssessment.Project(protocol, protocol.Slots[0]);
        var phaseA = AdvisoryLlmAssessment.BuildPhaseAPrompt(protocol, projection);
        var packet = CalibrationPackets.ReadPacket(protocol.Slots[0].PacketPath);
        var candidates = CandidatePair(
            packet, AdvisoryLlmAssessment.ParsePhaseA(PhaseAJson(), projection, protocol.Limits));
        var phaseB = AdvisoryLlmAssessment.BuildPhaseBPrompt(protocol, projection, candidates);

        phaseA.Should().Contain("1-12 observations, 0-5 hypotheses, 0-4 alternatives");
        phaseA.Should().Contain("1-8 citations per item");
        phaseB.Should().Contain("0-16 disagreements");
        foreach (var prompt in new[] { phaseA, phaseB })
        {
            prompt.Should().Contain("nonblank and at most 2000 characters");
            prompt.Should().Contain("65536 UTF-8 bytes");
        }
    }

    [Theory]
    [InlineData("protocolId")]
    [InlineData("protocolFingerprint")]
    [InlineData("missingProtocolFingerprint")]
    [InlineData("rubricFingerprint")]
    [InlineData("productCommit")]
    public void Projection_RejectsSelfConsistentPacketFromDifferentSourceBaseline(string defect)
    {
        using var files = new AssessmentTestFiles();
        var source = AssessmentTestFiles.FrozenSource;
        var foreignSource = defect switch
        {
            "protocolId" => source with { ProtocolId = "other-protocol" },
            "protocolFingerprint" => source with { ProtocolFingerprint = new string('a', 64) },
            "rubricFingerprint" => source with { RubricFingerprint = new string('b', 64) },
            "productCommit" => source with { ProductCommit = new string('a', 40) },
            _ => source,
        };
        var protocol = files.CreateProtocol(
            packetSource: foreignSource,
            omitPacketProtocolFingerprint: defect == "missingProtocolFingerprint");

        FluentActions.Invoking(() => AdvisoryLlmAssessment.Project(protocol, protocol.Slots[0]))
            .Should().Throw<InvalidDataException>().WithMessage("*source baseline does not match*");
    }

    [Fact]
    public void ProjectionAndPhaseAPrompt_FailClosedAndExcludePriorInterpretation()
    {
        using var files = new AssessmentTestFiles();
        var protocol = files.CreateProtocol();
        var slot = protocol.Slots[0];

        var projection = AdvisoryLlmAssessment.Project(protocol, slot);
        var prompt = AdvisoryLlmAssessment.BuildPhaseAPrompt(protocol, projection);

        prompt.Should().Contain("evidence-01");
        prompt.Should().Contain("threadpool-queue-length");
        prompt.Should().NotContain("ORIGINAL DIAGNOSIS");
        prompt.Should().NotContain("ORIGINAL UNCERTAINTY");
        prompt.Should().NotContain("ORIGINAL NEXT STEP");
        prompt.Should().NotContain("ANSWER HINT");
        prompt.Should().NotContain(slot.SlotId);
        prompt.Should().NotContain(slot.PacketFingerprint);
        prompt.Should().NotContain(protocol.Source.ProtocolFingerprint);
        prompt.Should().NotContain(protocol.Source.RubricFingerprint);
        prompt.Should().NotContain(protocol.Source.ProductCommit);
        projection.PointersExcludedByProjection.Should().Contain(
            "tool-result://source-call#/evidence/notes/0");
        projection.OriginallyInvalidPointers.Should().Contain(
            "tool-result://source-call#/evidence/missing");
        projection.OriginalPointerResults.Should().Contain(value =>
            value.OriginalLocation == "tool-result://source-call#/evidence/counters/0/value"
            && value.ExistsInOriginalResult
            && value.IncludedInProjection
            && value.ProjectedLocation == "tool-result://evidence-01#/evidence/counters/0/value");
    }

    [Fact]
    public void SnapshotProjection_RemovesDerivedSignalsAndHeuristics()
    {
        using var files = new AssessmentTestFiles();
        var protocol = files.CreateProtocol();

        var projection = AdvisoryLlmAssessment.Project(protocol, protocol.Slots[1]);
        var json = JsonSerializer.Serialize(projection.Evidence);

        json.Should().Contain("Worker.Run");
        json.Should().Contain("isThreadPoolWorker");
        json.Should().NotContain("signals");
        json.Should().NotContain("nextAction");
        json.Should().NotContain("inferredWaitReason");
        json.Should().NotContain("isLikelyBlocked");
        json.Should().NotContain("isContendedLockOwner");
        json.Should().NotContain("isLockWaiter");
        json.Should().NotContain("\"isContended\"");
    }

    [Fact]
    public void ApprovedFact_IsBoundToExactSourceNote()
    {
        using var files = new AssessmentTestFiles();
        var protocol = files.CreateProtocol();
        var slot = protocol.Slots[5];
        var fact = new AdvisoryApprovedFact(
            0,
            "/evidence/notes/0",
            Sha256(Encoding.UTF8.GetBytes("ANSWER HINT: synthetic forbidden text")),
            "The retained counter note states that the capture was edited.");
        var changedSlot = slot with { ApprovedFacts = [fact] };
        var changed = AssessmentTestFiles.Refingerprint(protocol with
        {
            Slots = protocol.Slots.Select(value => value.SlotId == slot.SlotId ? changedSlot : value).ToArray(),
        });

        var projection = AdvisoryLlmAssessment.Project(changed, changedSlot);
        projection.Evidence[0].Evidence.GetRawText().Should().Contain("approvedFacts");
        projection.Evidence[0].Evidence.GetRawText().Should().NotContain("ANSWER HINT");
        projection.Evidence[0].Evidence.GetRawText().Should().NotContain(fact.SourceTextSha256);

        var staleFact = fact with { SourceTextSha256 = new string('a', 64) };
        var staleSlot = changedSlot with { ApprovedFacts = [staleFact] };
        var action = () => AdvisoryLlmAssessment.Project(changed, staleSlot);
        action.Should().Throw<InvalidDataException>().WithMessage("*binding changed*");
    }

    [Fact]
    public void StrictParsers_RejectDuplicatesUnknownPropertiesEnumsAndWrongClaimIds()
    {
        using var files = new AssessmentTestFiles();
        var protocol = files.CreateProtocol();
        var projection = AdvisoryLlmAssessment.Project(protocol, protocol.Slots[0]);
        var validA = PhaseAJson();

        AdvisoryLlmAssessment.ParsePhaseA(validA, projection, protocol.Limits)
            .Hypotheses.Should().ContainSingle();
        var duplicate = validA.Replace(
            "\"abstained\":false",
            "\"abstained\":false,\"abstained\":true",
            StringComparison.Ordinal);
        FluentActions.Invoking(() => AdvisoryLlmAssessment.ParsePhaseA(duplicate, projection, protocol.Limits))
            .Should().Throw<InvalidDataException>().WithMessage("*duplicate*");
        var unknown = validA.Replace(
            "\"nextDiagnosticQuestion\"",
            "\"unexpected\":1,\"nextDiagnosticQuestion\"",
            StringComparison.Ordinal);
        FluentActions.Invoking(() => AdvisoryLlmAssessment.ParsePhaseA(unknown, projection, protocol.Limits))
            .Should().Throw<JsonException>();

        var phaseA = AdvisoryLlmAssessment.ParsePhaseA(validA, projection, protocol.Limits);
        var packet = CalibrationPackets.ReadPacket(protocol.Slots[0].PacketPath);
        var candidates = CandidatePair(packet, phaseA);
        var badEnum = PhaseBJson().Replace("\"supported\"", "\"invented\"", StringComparison.Ordinal);
        FluentActions.Invoking(() => AdvisoryLlmAssessment.ParsePhaseB(badEnum, candidates, protocol.Limits))
            .Should().Throw<JsonException>();
        var wrongId = PhaseBJson().Replace(
            "candidate-02-claim-01",
            "candidate-02-claim-99",
            StringComparison.Ordinal);
        FluentActions.Invoking(() => AdvisoryLlmAssessment.ParsePhaseB(wrongId, candidates, protocol.Limits))
            .Should().Throw<InvalidDataException>().WithMessage("*exactly match*");
    }

    [Fact]
    public async Task Runner_SealsPhaseABeforeFreshPhaseBAndKeepsMappingControllerOnly()
    {
        using var files = new AssessmentTestFiles();
        var protocol = files.CreateProtocol();
        var transport = new RecordingTransport();
        var previous = Environment.GetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable);
        Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable, "1");
        try
        {
            var summary = await AdvisoryLlmAssessment.RunAsync(
                protocol,
                files.Path("run"),
                transport,
                CancellationToken.None);

            summary.Cases.Should().HaveCount(8);
            transport.Prompts.Should().HaveCount(16);
            transport.Prompts.Where((_, index) => index % 2 == 0)
                .Should().OnlyContain(prompt => !prompt.Contains("ORIGINAL DIAGNOSIS", StringComparison.Ordinal));
            transport.Prompts.Where((_, index) => index % 2 == 1)
                .Should().OnlyContain(prompt => prompt.Contains("candidate-01", StringComparison.Ordinal));
            transport.Prompts.Should().OnlyContain(prompt =>
                prompt.Contains("No tools are available", StringComparison.Ordinal));
            summary.Cases.Should().OnlyContain(value =>
                value.SealedPhaseASha256 != null
                && File.Exists(value.SealedPhaseAPath)
                && value.PhaseB.InputSha256.Length == 64);
            summary.Cases.Take(4).Should().OnlyContain(value =>
                value.CandidateMapping[0].Source == AdvisoryCandidateSource.Original);
            summary.Cases.Skip(4).Should().OnlyContain(value =>
                value.CandidateMapping[0].Source == AdvisoryCandidateSource.Reanalysis);
            transport.Prompts.Should().OnlyContain(prompt =>
                !prompt.Contains("\"source\":\"original\"", StringComparison.OrdinalIgnoreCase)
                && !prompt.Contains("\"source\":\"reanalysis\"", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable, previous);
        }
    }

    [Fact]
    public async Task Runner_PreservesMalformedResponseAndSkipsPhaseBWithoutRetry()
    {
        using var files = new AssessmentTestFiles();
        var protocol = files.CreateProtocol();
        var transport = new RecordingTransport(["{\"observations\":"]);
        var previous = Environment.GetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable);
        Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable, "1");
        try
        {
            var summary = await AdvisoryLlmAssessment.RunAsync(
                protocol,
                files.Path("partial"),
                transport,
                CancellationToken.None);

            summary.Cases[0].PhaseA.Status.Should().Be(AdvisoryCallStatus.InvalidResponse);
            summary.Cases[0].PhaseA.RawResponse.Should().Be("{\"observations\":");
            summary.Cases[0].PhaseB.Status.Should().Be(AdvisoryCallStatus.Skipped);
            transport.Prompts.Should().HaveCount(15);
            summary.Cases.Skip(1).Should().OnlyContain(value =>
                value.PhaseA.Status == AdvisoryCallStatus.Succeeded);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable, previous);
        }
    }

    [Fact]
    public async Task Runner_AbortsRemainingCallsAfterGlobalAuthenticationFailure()
    {
        using var files = new AssessmentTestFiles();
        var protocol = files.CreateProtocol();
        var transport = new AuthenticationFailureTransport();
        var previous = Environment.GetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable);
        Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable, "1");
        try
        {
            var summary = await AdvisoryLlmAssessment.RunAsync(
                protocol,
                files.Path("abort"),
                transport,
                CancellationToken.None);

            summary.AbortedForGlobalPrerequisite.Should().BeTrue();
            transport.Calls.Should().Be(1);
            summary.Cases[0].PhaseA.Status.Should().Be(AdvisoryCallStatus.TransportFailed);
            summary.Cases.Skip(1).Should().OnlyContain(value =>
                value.PhaseA.Status == AdvisoryCallStatus.Skipped
                && value.PhaseB.Status == AdvisoryCallStatus.Skipped);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable, previous);
        }
    }

    [Fact]
    public async Task Runner_AbortsAfterGlobalFailureInPhaseB()
    {
        using var files = new AssessmentTestFiles();
        var protocol = files.CreateProtocol();
        var transport = new AuthenticationFailureTransport(failOnCall: 2);
        var previous = Environment.GetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable);
        Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable, "1");
        try
        {
            var summary = await AdvisoryLlmAssessment.RunAsync(
                protocol,
                files.Path("abort-phase-b"),
                transport,
                CancellationToken.None);

            summary.AbortedForGlobalPrerequisite.Should().BeTrue();
            transport.Calls.Should().Be(2);
            summary.Cases[0].PhaseA.Status.Should().Be(AdvisoryCallStatus.Succeeded);
            summary.Cases[0].PhaseB.Status.Should().Be(AdvisoryCallStatus.TransportFailed);
            summary.Cases.Skip(1).Should().OnlyContain(value =>
                value.PhaseA.Status == AdvisoryCallStatus.Skipped);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable, previous);
        }
    }

    [Fact]
    public async Task Runner_CancellationPreservesTimeoutAndSkipsEveryPhaseB()
    {
        using var files = new AssessmentTestFiles();
        var protocol = files.CreateProtocol();
        using var cancellation = new CancellationTokenSource();
        var transport = new CancellingTransport(cancellation);
        var previous = Environment.GetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable);
        Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable, "1");
        try
        {
            var summary = await AdvisoryLlmAssessment.RunAsync(
                protocol,
                files.Path("timeout"),
                transport,
                cancellation.Token);

            summary.Cases.Should().OnlyContain(value =>
                value.PhaseA.Status == AdvisoryCallStatus.TimedOut
                && value.PhaseB.Status == AdvisoryCallStatus.Skipped);
            transport.Calls.Should().Be(8);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable, previous);
        }
    }

    [Fact]
    public void ChangedPacketAndNoOverwritePreparation_AreRejected()
    {
        using var files = new AssessmentTestFiles();
        var protocol = files.CreateProtocol();
        File.AppendAllText(protocol.Slots[0].PacketPath, " ");
        FluentActions.Invoking(() => AdvisoryLlmAssessment.Project(protocol, protocol.Slots[0]))
            .Should().Throw<InvalidDataException>().WithMessage("*hash changed*");

        protocol = files.CreateProtocol("second");
        var output = files.Path("prepared");
        AdvisoryLlmAssessment.Prepare(protocol, output);
        FluentActions.Invoking(() => AdvisoryLlmAssessment.Prepare(protocol, output))
            .Should().Throw<IOException>();
    }

    [Fact]
    public void HumanReviewImporter_RejectsAutomatedAssessmentRecord()
    {
        using var files = new AssessmentTestFiles();
        var protocol = files.CreateProtocol();
        var packet = CalibrationPackets.ReadPacket(protocol.Slots[0].PacketPath);
        var path = files.Path("llm-assessment.json");
        File.WriteAllText(path, PhaseBJson());

        FluentActions.Invoking(() => CalibrationPackets.ReadReview(path, packet))
            .Should().Throw<JsonException>();
    }

    [Fact]
    public void StructuredCliParser_RejectsToolActivityAndPreservesMalformedAssistantContent()
    {
        var malformedEvent =
            """{"type":"assistant.message","data":{"content":"{\"observations\":","toolRequests":[]}}""";
        CopilotCliAgentTransport.ParseStructuredJsonOutput(malformedEvent)
            .Should().Be("{\"observations\":");
        var toolEvent =
            """{"type":"tool.execution_start","data":{"content":"{}"}}""";
        FluentActions.Invoking(() => CopilotCliAgentTransport.ParseStructuredJsonOutput(toolEvent))
            .Should().Throw<JsonException>().WithMessage("*tool activity*");
    }

    private static IReadOnlyList<AdvisoryCandidate> CandidatePair(
        CalibrationPacket packet,
        AdvisoryPhaseAResponse phaseA)
        =>
        [
            new AdvisoryCandidate(
                "candidate-01",
                [new AdvisoryCandidateClaim("candidate-01-claim-01", packet.Claims[0].Text, [])],
                packet.Uncertainty,
                packet.NextSteps[0]),
            new AdvisoryCandidate(
                "candidate-02",
                [new AdvisoryCandidateClaim(
                    "candidate-02-claim-01",
                    phaseA.Hypotheses[0].Text,
                    phaseA.Hypotheses[0].EvidenceLocations)],
                phaseA.Uncertainty,
                phaseA.NextDiagnosticQuestion),
        ];

    private static string PhaseAJson(bool snapshot = false)
    {
        var location = snapshot
            ? "tool-result://evidence-01#/evidence/threads/0/frames/0"
            : "tool-result://evidence-01#/evidence/counters/0/value";
        return
        $$"""
        {
          "observations":[{"observationId":"observation-01","text":"The retained signal is observable.","evidenceLocations":["{{location}}"]}],
          "hypotheses":[{"hypothesisId":"hypothesis-01","text":"Work may be accumulating.","confidence":"low","evidenceLocations":["{{location}}"]}],
          "alternatives":[{"alternativeId":"alternative-01","text":"The sample may be transient.","evidenceLocations":["tool-result://evidence-01#/captureSeconds"]}],
          "uncertainty":"A single retained window cannot establish cause.",
          "abstained":false,
          "nextDiagnosticQuestion":"Does the queue remain elevated in another capture?"
        }
        """;
    }

    private static string PhaseBJson()
        =>
        """
        {
          "candidates":[
            {"candidateId":"candidate-01","claims":[{"claimId":"candidate-01-claim-01","support":"supported","certainty":"appropriate","rationale":"The retained counter supports the bounded statement."}],"abstentionAndUncertainty":"useful","nextStepUsefulness":"useful","rationale":"The interpretation stays bounded."},
            {"candidateId":"candidate-02","claims":[{"claimId":"candidate-02-claim-01","support":"partiallySupported","certainty":"appropriate","rationale":"Accumulation is plausible but not causal."}],"abstentionAndUncertainty":"useful","nextStepUsefulness":"useful","rationale":"The interpretation asks for another capture."}
          ],
          "disagreements":["The candidates differ in how directly they describe accumulation."],
          "overallLimitations":"This is retained historical evidence, not a fresh investigation."
        }
        """;

    private static string Sha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class RecordingTransport : IAdvisoryStructuredTransport
    {
        private readonly Queue<string> overrides;

        public RecordingTransport(IEnumerable<string>? overrides = null)
        {
            this.overrides = new Queue<string>(overrides ?? []);
        }

        public List<string> Prompts { get; } = [];

        public Task<AdvisoryStructuredInvocation> CompleteAsync(
            AdvisoryLlmModel model,
            string prompt,
            int maximumResponseBytes,
            CancellationToken cancellationToken)
        {
            Prompts.Add(prompt);
            var response = overrides.Count != 0
                ? overrides.Dequeue()
                : prompt.Contains("opaque candidate interpretations", StringComparison.Ordinal)
                    ? PhaseBJson()
                    : PhaseAJson(prompt.Contains(
                        "\"collector\": \"collect_thread_snapshot\"",
                        StringComparison.Ordinal));
            return Task.FromResult(new AdvisoryStructuredInvocation(response));
        }
    }

    private sealed class AuthenticationFailureTransport(int failOnCall = 1)
        : IAdvisoryStructuredTransport
    {
        public int Calls { get; private set; }

        public Task<AdvisoryStructuredInvocation> CompleteAsync(
            AdvisoryLlmModel model,
            string prompt,
            int maximumResponseBytes,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == failOnCall)
            {
                throw new AgentTransportException(
                    "Copilot CLI invocation exited: normal CLI authentication was unavailable.");
            }
            return Task.FromResult(new AdvisoryStructuredInvocation(
                prompt.Contains("opaque candidate interpretations", StringComparison.Ordinal)
                    ? PhaseBJson()
                    : PhaseAJson(prompt.Contains(
                        "\"collector\": \"collect_thread_snapshot\"",
                        StringComparison.Ordinal))));
        }
    }

    private sealed class CancellingTransport(CancellationTokenSource cancellation)
        : IAdvisoryStructuredTransport
    {
        public int Calls { get; private set; }

        public async Task<AdvisoryStructuredInvocation> CompleteAsync(
            AdvisoryLlmModel model,
            string prompt,
            int maximumResponseBytes,
            CancellationToken cancellationToken)
        {
            Calls++;
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class AssessmentTestFiles : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "advisory-llm-test-artifacts",
            Guid.NewGuid().ToString("n"));

        public string Path(string name)
        {
            Directory.CreateDirectory(directory);
            return System.IO.Path.Combine(directory, name);
        }

        public static AdvisorySourceBaseline FrozenSource { get; } = new(
            "advisory-calibration-v1",
            "83736e129e944f6e0481c5eff27b8e9a879669d73c700402c8c868596de923ad",
            "3b67a0737764cffefb58b0e1708a3e04428d8b75d1ede4d6c6cb42c0bc8dff35",
            "f9c2ef8e849155983ad2344ac9fc28d2b469d906");

        public AdvisoryLlmProtocol CreateProtocol(
            string suffix = "first",
            AdvisorySourceBaseline? packetSource = null,
            bool omitPacketProtocolFingerprint = false)
        {
            packetSource ??= FrozenSource;
            var slots = new List<AdvisoryAssessmentSlot>();
            for (var index = 0; index < 8; index++)
            {
                var provenance = index < 5
                    ? CalibrationProvenanceKind.LiveModel
                    : CalibrationProvenanceKind.AuthoredEditedReplay;
                var reportPath = Path($"{suffix}-report-{index}.json");
                BlindedAgentHarness.WriteReport(reportPath, CreateReport(index == 1, packetSource.ProductCommit));
                var packet = CalibrationPackets.CreatePacket(
                    reportPath,
                    new CalibrationCaseDescriptor(
                        packetSource.ProtocolId,
                        packetSource.RubricFingerprint,
                        $"synthetic-slot-{index + 1:00}",
                        CalibrationPartition.Development,
                        provenance,
                        $"synthetic-capture-{index + 1:00}",
                        new string('c', 64),
                        "SYNTHETIC TEST packet only.",
                        omitPacketProtocolFingerprint ? null : packetSource.ProtocolFingerprint));
                var packetPath = Path($"{suffix}-packet-{index}.json");
                CalibrationPackets.WritePacket(packetPath, packet);
                slots.Add(new AdvisoryAssessmentSlot(
                    $"slot-{index + 1:00}",
                    System.IO.Path.GetFullPath(packetPath),
                    Sha256(File.ReadAllBytes(packetPath)),
                    packet.Fingerprint,
                    provenance,
                    index < 4 ? AdvisoryCandidateSource.Original : AdvisoryCandidateSource.Reanalysis,
                    []));
            }

            var protocol = new AdvisoryLlmProtocol(
                AdvisoryLlmAssessment.CurrentSchemaVersion,
                "advisory-llm-v2",
                string.Empty,
                DateTimeOffset.Parse("2026-09-21T00:00:00Z", CultureInfo.InvariantCulture),
                FrozenSource,
                Model("claude-sonnet-5"),
                Model("gpt-5.6-sol"),
                new AdvisoryLlmLimits(
                    8,
                    16,
                    2,
                    120,
                    32 * 60,
                    5 * 60,
                    65_536,
                    65_536,
                    1024 * 1024,
                    5,
                    12,
                    4,
                    8,
                    16,
                    2000),
                slots);
            return Refingerprint(protocol);
        }

        public static AdvisoryLlmProtocol Refingerprint(AdvisoryLlmProtocol protocol)
            => protocol with
            {
                ProtocolFingerprint = AdvisoryLlmAssessment.ComputeProtocolFingerprint(
                    protocol with { ProtocolFingerprint = string.Empty }),
            };

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static AdvisoryLlmModel Model(string name)
            => new("github-copilot-cli", name, "unknown", "copilot-cli", "GitHub Copilot CLI synthetic-test");

        private static AgentHarnessReport CreateReport(bool snapshot, string productCommit)
        {
            var content = snapshot ? SnapshotContent() : CounterContent();
            var result = new AgentToolResult(
                "source-call",
                snapshot ? "collect_thread_snapshot" : "collect_events",
                true,
                content,
                BlindedDiagnosticToolGateway.Sha256(content),
                Encoding.UTF8.GetByteCount(content),
                false,
                null);
            return new AgentHarnessReport(
                BlindedAgentHarness.CurrentReportSchemaVersion,
                Guid.NewGuid().ToString("n"),
                "real-model",
                DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-01-01T00:00:01Z", CultureInfo.InvariantCulture),
                new AgentHarnessBudget(),
                Stage("configured"),
                Stage("activated"),
                Stage("collected"),
                Stage("diagnosed"),
                Stage("cleaned"),
                new AgentHarnessAssessment(Stage("mechanical"), [], "citation-resolution-only"),
                new AgentHarnessProvenance(
                    "synthetic-provider",
                    "synthetic-model",
                    "unknown",
                    "offline",
                    0,
                    100,
                    new string('d', 64),
                    new string('e', 64),
                    productCommit,
                    "version",
                    "PRIVATE-WORKLOAD",
                    "1",
                    new Dictionary<string, string>(),
                    "seed",
                    ".NET",
                    "Linux",
                    "X64",
                    "local",
                    "bounded",
                    "bounded",
                    "redacted"),
                [
                    new AgentTranscriptTurn(
                        1,
                        "{}",
                        null,
                        [],
                        [result],
                        new AgentModelUsage(null, null, null),
                        null),
                ],
                [],
                new AgentDiagnosis(
                    [
                        new AgentClaim(
                            "ORIGINAL DIAGNOSIS",
                            AgentEvidencePosture.Inferred,
                            snapshot
                                ? ["tool-result://source-call#/evidence/threads/0/frames/0"]
                                :
                                [
                                    "tool-result://source-call#/evidence/counters/0/value",
                                    "tool-result://source-call#/evidence/notes/0",
                                    "tool-result://source-call#/evidence/missing",
                                ]),
                    ],
                    "ORIGINAL UNCERTAINTY",
                    ["ORIGINAL NEXT STEP"]),
                1,
                null,
                null,
                null,
                result.ByteCount,
                ["UNTRUSTED CAPTURE NOTE"]);
        }

        private static AgentHarnessStage Stage(string detail)
            => new(AgentHarnessStageStatus.Passed, AgentHarnessFailureKind.None, detail, 0);

        private static string CounterContent()
            =>
            """
            {
              "captureSeconds":6,
              "collector":"collect_events/counters",
              "evidence":{
                "counters":[{"name":"threadpool-queue-length","displayName":"ThreadPool Queue Length","value":42,"unit":"Count","kind":"Mean","maximumObserved":45}],
                "notes":["ANSWER HINT: synthetic forbidden text"],
                "omittedCounterCount":0
              },
              "evidenceBase":"tool-result://source-call",
              "limitations":{"harnessTruncated":false,"sourceAndDecompilationUnavailable":true,"workloadControlsUnavailable":true},
              "status":"succeeded",
              "toolCallId":"source-call"
            }
            """;

        private static string SnapshotContent()
            =>
            """
            {
              "captureSeconds":1,
              "collector":"collect_thread_snapshot",
              "evidence":{
                "snapshotKind":"live",
                "walkDurationMilliseconds":12,
                "signals":[{"name":"blocked","nextAction":"assume deadlock","recommendations":["answer"]}],
                "warnings":[],
                "threads":[{"managedThreadId":7,"state":"Background","isThreadPoolWorker":true,"isLikelyBlocked":true,"inferredWaitReason":"Monitor","isContendedLockOwner":false,"isLockWaiter":true,"topFrameMethod":"Worker.Run","frames":["Worker.Run","Monitor.Enter"],"omittedFrameCount":0}],
                "locks":[{"objectType":"System.Object","ownerManagedThreadId":-1,"waitingThreadCount":1,"isContended":true,"waitingManagedThreadIds":[7]}],
                "omittedThreadCount":0,
                "omittedLockCount":0
              },
              "evidenceBase":"tool-result://source-call",
              "limitations":{"harnessTruncated":false,"sourceAndDecompilationUnavailable":true,"workloadControlsUnavailable":true},
              "status":"succeeded",
              "toolCallId":"source-call"
            }
            """;
    }
}
