using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed partial class AdvisoryLlmAssessmentTests
{
    private static readonly JsonSerializerOptions ContinuationTestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void JsonFramingNormalizer_AcceptsOnlyOneExactOuterFence(string newline)
    {
        using var files = new AssessmentTestFiles();
        var protocol = files.CreateProtocol();
        var projection = AdvisoryLlmAssessment.Project(protocol, protocol.Slots[0]);
        var payload = PhaseAJson();
        var fenced = "```json" + newline + payload + newline + "```";

        var normalized = AdvisoryLlmAssessment.NormalizeJsonResponse(
            fenced,
            protocol.Limits.MaximumResponseBytes);

        normalized.Payload.Should().Be(payload);
        normalized.Transform.Should().Be("outer-json-fence-removed");
        normalized.Version.Should().Be("advisory-json-framing-v1");
        normalized.PayloadSha256.Should().Be(Sha256(Encoding.UTF8.GetBytes(payload)));
        AdvisoryLlmAssessment.ParsePhaseA(fenced, projection, protocol.Limits)
            .Hypotheses.Should().ContainSingle();
        AdvisoryLlmAssessment.NormalizeJsonResponse(
                payload,
                protocol.Limits.MaximumResponseBytes)
            .Should().Be(new AdvisoryNormalizedJson(
                payload,
                Sha256(Encoding.UTF8.GetBytes(payload)),
                "bare-json",
                "advisory-json-framing-v1"));
    }

    [Theory]
    [InlineData("prefix```json\n{0}\n```")]
    [InlineData("```json\n{0}\n```suffix")]
    [InlineData("```json\n{0}\n```\n```json\n{0}\n```")]
    [InlineData("```json\n{0}")]
    public void JsonFramingNormalizer_RejectsProseMultipleOrIncompleteFences(string format)
    {
        using var files = new AssessmentTestFiles();
        var protocol = files.CreateProtocol();
        var projection = AdvisoryLlmAssessment.Project(protocol, protocol.Slots[0]);
        var response = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            format,
            PhaseAJson());

        FluentActions.Invoking(() =>
                AdvisoryLlmAssessment.ParsePhaseA(response, projection, protocol.Limits))
            .Should().Throw<Exception>();
    }

    [Fact]
    public void JsonFramingNormalizer_DoesNotRepairMalformedSchemaOrCitations()
    {
        using var files = new AssessmentTestFiles();
        var protocol = files.CreateProtocol();
        var projection = AdvisoryLlmAssessment.Project(protocol, protocol.Slots[0]);
        var malformed = "```json\n{\"observations\":\n```";
        var invalidCitation = "```json\n"
            + PhaseAJson().Replace(
                "tool-result://evidence-01#/evidence/counters/0/value",
                "tool-result://evidence-01#/evidence/missing",
                StringComparison.Ordinal)
            + "\n```";

        FluentActions.Invoking(() =>
                AdvisoryLlmAssessment.ParsePhaseA(malformed, projection, protocol.Limits))
            .Should().Throw<JsonException>();
        FluentActions.Invoking(() =>
                AdvisoryLlmAssessment.ParsePhaseA(invalidCitation, projection, protocol.Limits))
            .Should().Throw<InvalidDataException>().WithMessage("*does not exist*");
    }

    [Fact]
    public async Task NormalRunner_RecordsRawAndNormalizedHashesForFencedAAndB()
    {
        using var files = new AssessmentTestFiles();
        var protocol = files.CreateProtocol();
        var fencedA = "```json\n" + PhaseAJson() + "\n```";
        var fencedB = "```json\r\n" + PhaseBJson() + "\r\n```";
        var transport = new RecordingTransport([fencedA, fencedB]);
        var previous = Environment.GetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable);
        Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable, "1");
        try
        {
            var summary = await AdvisoryLlmAssessment.RunAsync(
                protocol,
                files.Path("fenced-normal-run"),
                transport,
                CancellationToken.None);

            var first = summary.Cases[0];
            first.PhaseA.Status.Should().Be(AdvisoryCallStatus.Succeeded);
            first.PhaseA.RawResponse.Should().Be(fencedA);
            first.PhaseA.ResponseSha256.Should().Be(Sha256(Encoding.UTF8.GetBytes(fencedA)));
            first.PhaseA.NormalizedResponseSha256.Should().Be(
                Sha256(Encoding.UTF8.GetBytes(PhaseAJson())));
            first.PhaseA.ResponseFramingTransform.Should().Be("outer-json-fence-removed");
            first.PhaseB.Status.Should().Be(AdvisoryCallStatus.Succeeded);
            first.PhaseB.RawResponse.Should().Be(fencedB);
            first.PhaseB.NormalizedResponseSha256.Should().Be(
                Sha256(Encoding.UTF8.GetBytes(PhaseBJson())));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AdvisoryLlmAssessment.RunAuthorizationVariable, previous);
        }
    }

    [Fact]
    public async Task Continuation_RecoversSixFencedResponsesWithoutAnyNewPhaseA()
    {
        using var files = new AssessmentTestFiles();
        var fixture = await CreateContinuationFixtureAsync(files);
        var sourceHashes = HashTree(fixture.SourceRoot);
        var frozenPath = files.Path("continuation-plan.json");
        var plan = AdvisoryLlmAssessment.FreezeContinuationPlan(
            fixture.Protocol,
            fixture.PlanDraftPath,
            frozenPath);
        HashTree(fixture.SourceRoot).Should().BeEquivalentTo(sourceHashes);
        FluentActions.Invoking(() => AdvisoryLlmAssessment.FreezeContinuationPlan(
                fixture.Protocol,
                fixture.PlanDraftPath,
                frozenPath))
            .Should().Throw<IOException>();
        var output = files.Path("continuation-output");
        var transport = new PhaseBOnlyTransport(output, fixture.Protocol.PhaseBModel.Model);
        var previous = Environment.GetEnvironmentVariable(
            AdvisoryLlmAssessment.ContinuationAuthorizationVariable);
        Environment.SetEnvironmentVariable(
            AdvisoryLlmAssessment.ContinuationAuthorizationVariable,
            "1");
        try
        {
            var summary = await AdvisoryLlmAssessment.ContinueAsync(
                fixture.Protocol,
                plan,
                output,
                transport,
                CancellationToken.None);

            summary.ActualNewModelCalls.Should().Be(6);
            summary.MaximumNewPhaseBCalls.Should().Be(6);
            summary.TechnicallyComplete.Should().BeFalse();
            transport.Calls.Should().Be(6);
            transport.Models.Should().OnlyContain(value =>
                value == fixture.Protocol.PhaseBModel.Model);
            transport.Prompts.Should().OnlyContain(prompt =>
                prompt.Contains("opaque candidate interpretations", StringComparison.Ordinal)
                && !prompt.Contains(plan.PlanId, StringComparison.Ordinal)
                && !prompt.Contains(plan.SourceRunSummarySha256, StringComparison.Ordinal));
            summary.Cases.Count(value =>
                value.PhaseAOrigin == AdvisoryPhaseOrigin.SourceFailurePreserved
                && value.PhaseBOrigin == AdvisoryPhaseOrigin.NewlyInvoked).Should().Be(6);
            summary.Cases.Should().ContainSingle(value =>
                value.PhaseAOrigin == AdvisoryPhaseOrigin.SourceCompletedReused
                && value.PhaseBOrigin == AdvisoryPhaseOrigin.SourceCompletedReused);
            summary.Cases.Should().ContainSingle(value =>
                value.Disposition == AdvisoryContinuationDisposition.Unavailable
                && value.Result.PhaseA.RawResponse == null
                && value.Result.PhaseB.Status == AdvisoryCallStatus.Skipped);
            summary.Cases.Where(value =>
                    value.Disposition == AdvisoryContinuationDisposition.RecoverRetainedPhaseA)
                .Should().OnlyContain(value =>
                    value.SourcePhaseAStatus == AdvisoryCallStatus.InvalidResponse
                    && value.Result.PhaseA.Status == AdvisoryCallStatus.InvalidResponse
                    && value.SourcePhaseARawResponseSha256 == value.Result.PhaseA.ResponseSha256
                    && value.NormalizedPhaseAPayloadSha256 != null
                    && value.PhaseAFramingTransform == "outer-json-fence-removed"
                    && value.Result.PhaseB.Status == AdvisoryCallStatus.Succeeded
                    && value.Result.PhaseBResponse != null
                    && File.Exists(value.Result.SealedPhaseAPath));
            HashTree(fixture.SourceRoot).Should().BeEquivalentTo(sourceHashes);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                AdvisoryLlmAssessment.ContinuationAuthorizationVariable,
                previous);
        }
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("packet")]
    [InlineData("prompt")]
    [InlineData("response")]
    [InlineData("seal")]
    [InlineData("model")]
    public async Task ContinuationPreparation_RejectsChangedBoundSource(string defect)
    {
        using var files = new AssessmentTestFiles();
        var fixture = await CreateContinuationFixtureAsync(files, defect);
        var output = files.Path("rejected-plan.json");

        FluentActions.Invoking(() => AdvisoryLlmAssessment.FreezeContinuationPlan(
                fixture.Protocol,
                fixture.PlanDraftPath,
                output))
            .Should().Throw<InvalidDataException>();
        File.Exists(output).Should().BeFalse();
    }

    [Theory]
    [InlineData("nonemptyFingerprint")]
    [InlineData("duplicateProperty")]
    [InlineData("unknownProperty")]
    [InlineData("missingExplicitSealBinding")]
    public async Task ContinuationPreparation_StrictlyRejectsMalformedDraft(string defect)
    {
        using var files = new AssessmentTestFiles();
        var fixture = await CreateContinuationFixtureAsync(files);
        var json = File.ReadAllText(fixture.PlanDraftPath);
        json = defect switch
        {
            "nonemptyFingerprint" => json.Replace(
                "\"planFingerprint\": \"\"",
                $"\"planFingerprint\": \"{new string('a', 64)}\"",
                StringComparison.Ordinal),
            "duplicateProperty" => json.Insert(1, "\"schemaVersion\":1,"),
            "unknownProperty" => json.Insert(1, "\"unexpected\":true,"),
            _ => json.Replace(
                ",\n      \"sourcePhaseASealSha256\": null",
                string.Empty,
                StringComparison.Ordinal),
        };
        var draft = files.Path("malformed-continuation-plan.json");
        File.WriteAllText(draft, json);
        var output = files.Path("should-not-freeze.json");

        FluentActions.Invoking(() => AdvisoryLlmAssessment.FreezeContinuationPlan(
                fixture.Protocol,
                draft,
                output))
            .Should().Throw<Exception>();
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public async Task Continuation_RequiresExplicitAuthorizationAndCreateNewOutput()
    {
        using var files = new AssessmentTestFiles();
        var fixture = await CreateContinuationFixtureAsync(files);
        var planPath = files.Path("authorization-plan.json");
        var plan = AdvisoryLlmAssessment.FreezeContinuationPlan(
            fixture.Protocol,
            fixture.PlanDraftPath,
            planPath);
        var output = files.Path("existing-output");
        Directory.CreateDirectory(output);
        var previous = Environment.GetEnvironmentVariable(
            AdvisoryLlmAssessment.ContinuationAuthorizationVariable);
        Environment.SetEnvironmentVariable(
            AdvisoryLlmAssessment.ContinuationAuthorizationVariable,
            null);
        try
        {
            await FluentActions.Awaiting(() => AdvisoryLlmAssessment.ContinueAsync(
                    fixture.Protocol,
                    plan,
                    output,
                    new PhaseBOnlyTransport(output, fixture.Protocol.PhaseBModel.Model),
                    CancellationToken.None))
                .Should().ThrowAsync<InvalidOperationException>();

            Environment.SetEnvironmentVariable(
                AdvisoryLlmAssessment.ContinuationAuthorizationVariable,
                "1");
            await FluentActions.Awaiting(() => AdvisoryLlmAssessment.ContinueAsync(
                    fixture.Protocol,
                    plan,
                    output,
                    new PhaseBOnlyTransport(output, fixture.Protocol.PhaseBModel.Model),
                    CancellationToken.None))
                .Should().ThrowAsync<IOException>();
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                AdvisoryLlmAssessment.ContinuationAuthorizationVariable,
                previous);
        }
    }

    [Fact]
    public async Task Continuation_AbortsRemainingNewCallsAfterGlobalPrerequisiteFailure()
    {
        using var files = new AssessmentTestFiles();
        var fixture = await CreateContinuationFixtureAsync(files);
        var plan = AdvisoryLlmAssessment.FreezeContinuationPlan(
            fixture.Protocol,
            fixture.PlanDraftPath,
            files.Path("abort-plan.json"));
        var transport = new AuthenticationFailureTransport();
        var output = files.Path("abort-continuation");
        var previous = Environment.GetEnvironmentVariable(
            AdvisoryLlmAssessment.ContinuationAuthorizationVariable);
        Environment.SetEnvironmentVariable(
            AdvisoryLlmAssessment.ContinuationAuthorizationVariable,
            "1");
        try
        {
            var summary = await AdvisoryLlmAssessment.ContinueAsync(
                fixture.Protocol,
                plan,
                output,
                transport,
                CancellationToken.None);

            summary.AbortedForGlobalPrerequisite.Should().BeTrue();
            summary.ActualNewModelCalls.Should().Be(1);
            transport.Calls.Should().Be(1);
            summary.Cases.Count(value =>
                value.Disposition == AdvisoryContinuationDisposition.RecoverRetainedPhaseA
                && value.PhaseBOrigin == AdvisoryPhaseOrigin.NotRun).Should().Be(5);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                AdvisoryLlmAssessment.ContinuationAuthorizationVariable,
                previous);
        }
    }

    [Fact]
    public async Task Continuation_LeavesInvalidNormalizedPayloadUnassessedWithoutRetry()
    {
        using var files = new AssessmentTestFiles();
        var fixture = await CreateContinuationFixtureAsync(files, "invalidInner");
        var plan = AdvisoryLlmAssessment.FreezeContinuationPlan(
            fixture.Protocol,
            fixture.PlanDraftPath,
            files.Path("partial-plan.json"));
        var output = files.Path("partial-continuation");
        var transport = new PhaseBOnlyTransport(output, fixture.Protocol.PhaseBModel.Model);
        var previous = Environment.GetEnvironmentVariable(
            AdvisoryLlmAssessment.ContinuationAuthorizationVariable);
        Environment.SetEnvironmentVariable(
            AdvisoryLlmAssessment.ContinuationAuthorizationVariable,
            "1");
        try
        {
            var summary = await AdvisoryLlmAssessment.ContinueAsync(
                fixture.Protocol,
                plan,
                output,
                transport,
                CancellationToken.None);

            summary.ActualNewModelCalls.Should().Be(5);
            summary.Cases.Should().ContainSingle(value =>
                value.Disposition == AdvisoryContinuationDisposition.RecoverRetainedPhaseA
                && value.PhaseBOrigin == AdvisoryPhaseOrigin.NotRun
                && value.Detail.Contains("remained invalid", StringComparison.Ordinal)
                && value.Result.PhaseB.Status == AdvisoryCallStatus.Skipped);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                AdvisoryLlmAssessment.ContinuationAuthorizationVariable,
                previous);
        }
    }

    private static async Task<ContinuationFixture> CreateContinuationFixtureAsync(
        AssessmentTestFiles files,
        string? defect = null)
    {
        var protocol = files.CreateProtocol("continuation");
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

        var recover = new HashSet<int> { 0, 1, 2, 3, 5, 6 };
        var cases = source.Cases.ToArray();
        for (var index = 0; index < cases.Length; index++)
        {
            var sourceCase = cases[index];
            if (recover.Contains(index))
            {
                var raw = "```json\n" + sourceCase.PhaseA.RawResponse + "\n```";
                sourceCase = sourceCase with
                {
                    PhaseA = sourceCase.PhaseA with
                    {
                        Status = AdvisoryCallStatus.InvalidResponse,
                        Detail = "Source parser rejected the retained outer JSON fence.",
                        RawResponse = raw,
                        ResponseSha256 = Sha256(Encoding.UTF8.GetBytes(raw)),
                        ResponseFramingTransform = "unparsed",
                        NormalizedResponseSha256 = null,
                    },
                    SealedPhaseAPath = null,
                    SealedPhaseASha256 = null,
                    PhaseB = SourceSkippedCall(protocol.PhaseBModel),
                    CandidateMapping = [],
                    PhaseAResponse = null,
                    PhaseBResponse = null,
                };
                DeleteIfExists(Path.Combine(sourceRoot, sourceCase.SlotId, "phase-a.sealed.json"));
            }
            else if (index == 4)
            {
                sourceCase = sourceCase with
                {
                    PhaseA = sourceCase.PhaseA with
                    {
                        Status = AdvisoryCallStatus.TransportFailed,
                        Detail = "Copilot CLI output exceeded the configured response-byte budget.",
                        RawResponse = null,
                        ResponseSha256 = null,
                        ResponseFramingTransform = "unparsed",
                        NormalizedResponseSha256 = null,
                    },
                    SealedPhaseAPath = null,
                    SealedPhaseASha256 = null,
                    PhaseB = SourceSkippedCall(protocol.PhaseBModel),
                    CandidateMapping = [],
                    PhaseAResponse = null,
                    PhaseBResponse = null,
                };
                DeleteIfExists(Path.Combine(sourceRoot, sourceCase.SlotId, "phase-a.sealed.json"));
            }
            cases[index] = sourceCase;
        }

        source = source with { Cases = cases };
        if (defect is "prompt" or "response" or "model" or "invalidInner")
        {
            var changed = cases[0];
            changed = defect switch
            {
                "prompt" => changed with
                {
                    PhaseA = changed.PhaseA with { PromptSha256 = new string('a', 64) },
                },
                "response" => changed with
                {
                    PhaseA = changed.PhaseA with { RawResponse = changed.PhaseA.RawResponse + "changed" },
                },
                "invalidInner" => changed with
                {
                    PhaseA = changed.PhaseA with
                    {
                        RawResponse = "```json\n{\"observations\":\n```",
                        ResponseSha256 = Sha256(Encoding.UTF8.GetBytes(
                            "```json\n{\"observations\":\n```")),
                    },
                },
                _ => changed with
                {
                    PhaseA = changed.PhaseA with { Model = "wrong-model" },
                },
            };
            cases[0] = changed;
            source = source with { Cases = cases };
        }
        else if (defect == "summary")
        {
            source = source with
            {
                Cases = cases.Select((value, index) => index == 0
                    ? value with { PhaseA = value.PhaseA with { Detail = value.PhaseA.Detail + " changed" } }
                    : value).ToArray(),
            };
        }

        for (var index = 0; index < cases.Length; index++)
        {
            WriteJson(
                Path.Combine(sourceRoot, cases[index].SlotId, "case-result.json"),
                cases[index]);
        }
        WriteJson(Path.Combine(sourceRoot, "run-summary.json"), source);
        if (defect == "packet")
        {
            File.AppendAllText(protocol.Slots[0].PacketPath, " ");
        }
        if (defect == "seal")
        {
            File.AppendAllText(Path.Combine(sourceRoot, protocol.Slots[7].SlotId, "phase-a.sealed.json"), " ");
        }

        var plan = BuildContinuationPlan(protocol, sourceRoot);
        var draftPath = files.Path("continuation-plan.draft.json");
        WriteJson(draftPath, plan);
        return new ContinuationFixture(protocol, sourceRoot, draftPath);
    }

    private static AdvisoryContinuationPlan BuildContinuationPlan(
        AdvisoryLlmProtocol protocol,
        string sourceRoot)
    {
        var bindings = protocol.Slots.Select((slot, index) => new AdvisoryContinuationCaseBinding(
            slot.SlotId,
            Sha256(File.ReadAllBytes(Path.Combine(sourceRoot, slot.SlotId, "case-result.json"))),
            index == 4
                ? AdvisoryContinuationDisposition.Unavailable
                : index == 7
                    ? AdvisoryContinuationDisposition.ReuseCompleted
                    : AdvisoryContinuationDisposition.RecoverRetainedPhaseA,
            index == 7
                ? Sha256(File.ReadAllBytes(Path.Combine(sourceRoot, slot.SlotId, "phase-a.sealed.json")))
                : null)).ToArray();
        return new AdvisoryContinuationPlan(
            1,
            "synthetic-continuation",
            string.Empty,
            protocol.ProtocolFingerprint,
            Path.GetFullPath(sourceRoot),
            Sha256(File.ReadAllBytes(Path.Combine(sourceRoot, "run-summary.json"))),
            6,
            bindings);
    }

    private static AdvisoryCallRecord SourceSkippedCall(AdvisoryLlmModel model)
        => new(
            AdvisoryCallStatus.Skipped,
            "Source Phase B skipped because Phase A did not seal successfully.",
            model.Model,
            model.ModelVersion,
            string.Empty,
            string.Empty,
            null,
            null,
            null,
            null,
            null,
            0);

    private static void WriteJson<T>(string path, T value)
        => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, ContinuationTestJsonOptions));

    private static Dictionary<string, string> HashTree(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => Sha256(File.ReadAllBytes(path)),
                StringComparer.Ordinal);

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record ContinuationFixture(
        AdvisoryLlmProtocol Protocol,
        string SourceRoot,
        string PlanDraftPath);

    private sealed class PhaseBOnlyTransport(string outputRoot, string expectedModel)
        : IAdvisoryStructuredTransport
    {
        public int Calls { get; private set; }

        public List<string> Models { get; } = [];

        public List<string> Prompts { get; } = [];

        public Task<AdvisoryStructuredInvocation> CompleteAsync(
            AdvisoryLlmModel model,
            string prompt,
            int maximumResponseBytes,
            CancellationToken cancellationToken)
        {
            model.Model.Should().Be(expectedModel, "continuation must never invoke Phase A");
            Calls++;
            Models.Add(model.Model);
            Prompts.Add(prompt);
            Directory.EnumerateFiles(
                    outputRoot,
                    "phase-a.recovered.sealed.json",
                    SearchOption.AllDirectories)
                .Should().HaveCountGreaterThanOrEqualTo(
                    Calls,
                    "each recovered Phase A must be sealed before its B call");
            var response = Calls == 1
                ? "```json\r\n" + PhaseBJson() + "\r\n```"
                : PhaseBJson();
            return Task.FromResult(new AdvisoryStructuredInvocation(response));
        }
    }
}
