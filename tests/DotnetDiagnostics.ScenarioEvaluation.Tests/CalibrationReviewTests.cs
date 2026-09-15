using System.Text;
using System.Text.Json;
using System.Globalization;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class CalibrationReviewTests
{
    private const string RubricFingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Packet_ResolvesEscapedPointers_ButLeavesSemanticSupportUnjudged()
    {
        using var files = new CalibrationTestFiles();
        var report = CreateReport(
            """
            {"evidence":{"a/b":{"~metric":{"name":"threadpool-queue-length","value":489}}}}
            """,
            ["tool-result://call-1#/evidence/a~1b/~0metric/value"]);
        var packet = CreatePacket(files, report);

        packet.Claims.Should().ContainSingle();
        packet.Claims[0].Citations.Should().ContainSingle(citation =>
            citation.Exists
            && citation.Value!.Value.GetInt32() == 489);
        var template = CalibrationPackets.CreateBlankReviewTemplate(packet);
        template.Claims[0].SemanticSupport.Should().BeNull(
            "a valid pointer or queue growth does not establish semantic support or confirmed starvation");
        template.Finalized.Should().BeFalse();
    }

    [Theory]
    [InlineData("tool-result://call-1#evidence")]
    [InlineData("tool-result://call-1#/evidence/~2bad")]
    [InlineData("tool-result://call-1#/evidence/missing")]
    public void Packet_MalformedOrMissingPointersRemainVisible(string location)
    {
        using var files = new CalibrationTestFiles();
        var packet = CreatePacket(files, CreateReport("""{"evidence":{"value":1}}""", [location]));

        packet.Claims[0].Citations.Should().ContainSingle(citation =>
            !citation.Exists && citation.Error != null);
        packet.Stages.Assessment.Status.Should().Be(AgentHarnessStageStatus.Passed);
    }

    [Fact]
    public void Harness_AmbiguousToolCallIdsFailCitationResolution()
    {
        var results = new[]
        {
            ToolResult("""{"value":1}"""),
            ToolResult("""{"value":2}"""),
        };

        var resolution = AgentEvidenceResolver.Resolve("tool-result://call-1#/value", results);

        resolution.Exists.Should().BeFalse();
        resolution.Error.Should().Contain("ambiguous");
    }

    [Fact]
    public void Summary_WithNoReviewsReportsMissingAndUnassessed()
    {
        using var files = new CalibrationTestFiles();
        var packet = CreatePacket(files, CreateReport("""{"metric":"wrong-one"}"""));

        var summary = CalibrationPackets.Summarize(packet, []);

        summary.ImportedReviews.Should().Be(0);
        summary.MissingReviews.Should().Contain("No human review has been imported.");
        summary.Dimensions.Single(value => value.Dimension == "claim-support")
            .Should().Match<CalibrationDimensionSummary>(value =>
                value.Eligible == 1 && value.Reviewed == 0 && value.Missing == 1);
        summary.Notes.Should().Contain(value => value.Contains("estimates no model-run variance", StringComparison.Ordinal));
    }

    [Fact]
    public void Summary_DraftTemplateDoesNotCountMissingLabelsAsPositive()
    {
        using var files = new CalibrationTestFiles();
        var packet = CreatePacket(files, CreateReport());
        var draft = CalibrationPackets.CreateBlankReviewTemplate(packet, "reviewer-a");

        var summary = CalibrationPackets.Summarize(packet, [draft]);

        summary.DistinctReviewerCount.Should().Be(0);
        summary.Dimensions.Single(value => value.Dimension == "claim-support").Reviewed.Should().Be(0);
        summary.MissingReviews.Should().Contain(value => value.Contains("No finalized", StringComparison.Ordinal));
    }

    [Fact]
    public void Packet_RejectsAlteredFingerprintAndForeignReview()
    {
        using var files = new CalibrationTestFiles();
        var packet = CreatePacket(files, CreateReport("""{"metric":"value"}"""));
        var packetPath = files.Path("packet.json");
        CalibrationPackets.WritePacket(packetPath, packet);
        var altered = File.ReadAllText(packetPath).Replace(
            packet.Claims[0].Text,
            "altered claim",
            StringComparison.Ordinal);
        File.WriteAllText(packetPath, altered);

        var readPacket = () => CalibrationPackets.ReadPacket(packetPath);
        readPacket.Should().Throw<InvalidDataException>().WithMessage("*fingerprint*");

        var foreign = FinalReview(packet, "review-1", "reviewer-a") with
        {
            PacketFingerprint = new string('b', 64),
        };
        var reviewPath = files.Path("review.json");
        CalibrationPackets.WriteReview(reviewPath, foreign);
        var readReview = () => CalibrationPackets.ReadReview(reviewPath, packet);
        readReview.Should().Throw<InvalidDataException>().WithMessage("*stale*");
    }

    [Fact]
    public void ReadersRejectUnknownFieldsDuplicatePropertiesAndInvalidEnums()
    {
        using var files = new CalibrationTestFiles();
        var packet = CreatePacket(files, CreateReport());
        var cleanPath = files.Path("clean.json");
        CalibrationPackets.WritePacket(cleanPath, packet);
        var json = File.ReadAllText(cleanPath);

        var unknownPath = files.Path("unknown.json");
        File.WriteAllText(unknownPath, json.Insert(json.IndexOf('{') + 1, "\"unknown\":1,"));
        var unknown = () => CalibrationPackets.ReadPacket(unknownPath);
        unknown.Should().Throw<JsonException>();

        var duplicatePath = files.Path("duplicate.json");
        File.WriteAllText(duplicatePath, json.Insert(json.IndexOf('{') + 1, "\"schemaVersion\":1,"));
        var duplicate = () => CalibrationPackets.ReadPacket(duplicatePath);
        duplicate.Should().Throw<InvalidDataException>().WithMessage("*Duplicate JSON property*");

        var invalidEnumPath = files.Path("invalid-enum.json");
        File.WriteAllText(invalidEnumPath, json.Replace(
            "\"partition\": \"development\"",
            "\"partition\": \"invented\"",
            StringComparison.Ordinal));
        var invalidEnum = () => CalibrationPackets.ReadPacket(invalidEnumPath);
        invalidEnum.Should().Throw<JsonException>();
    }

    [Fact]
    public void PacketExcludesEvaluatorPrivateProvenance()
    {
        using var files = new CalibrationTestFiles();
        var packet = CreatePacket(files, CreateReport());
        var path = files.Path("packet.json");

        CalibrationPackets.WritePacket(path, packet);
        var json = File.ReadAllText(path);

        json.Should().NotContain("PRIVATE-WORKLOAD-ID");
        json.Should().NotContain("PRIVATE_ROUTE");
        json.Should().NotContain("/secret");
        json.Should().NotContain("workloadConfiguration");
        json.Should().NotContain("rawModelResponse");
        json.Should().NotContain("argumentsJson");
    }

    [Fact]
    public void Summary_DifferentReviewersCanDisagreeWithoutAutomaticAdjudication()
    {
        using var files = new CalibrationTestFiles();
        var packet = CreatePacket(files, CreateReport("""{"metric":"value"}"""));
        var first = FinalReview(packet, "review-1", "reviewer-a", SemanticSupportRating.Supported);
        var second = FinalReview(packet, "review-2", "reviewer-b", SemanticSupportRating.Unsupported) with
        {
            Role = CalibrationReviewerRole.Independent,
        };

        var summary = CalibrationPackets.Summarize(packet, [first, second]);

        summary.HasIndependentReview.Should().BeTrue();
        summary.HasExplicitAdjudication.Should().BeFalse();
        summary.UnresolvedDisagreements.Should().ContainSingle(value => value.Subject == "claim-1");
        summary.MissingReviews.Should().Contain(value => value.Contains("adjudication", StringComparison.Ordinal));
    }

    [Fact]
    public void Summary_SameReviewerIdentityDoesNotCountAsIndependent()
    {
        using var files = new CalibrationTestFiles();
        var packet = CreatePacket(files, CreateReport("""{"metric":"value"}"""));
        var first = FinalReview(packet, "review-1", "reviewer-a");
        var second = FinalReview(packet, "review-2", "reviewer-a") with
        {
            Role = CalibrationReviewerRole.Independent,
            ReviewedAtUtc = first.ReviewedAtUtc.AddMinutes(1),
        };

        var summary = CalibrationPackets.Summarize(packet, [first, second]);

        summary.DistinctReviewerCount.Should().Be(1);
        summary.HasIndependentReview.Should().BeFalse();
    }

    [Fact]
    public void Summary_UsesLatestRevisionRegardlessOfInputOrder_AndRejectsTimestampTies()
    {
        using var files = new CalibrationTestFiles();
        var packet = CreatePacket(files, CreateReport());
        var original = FinalReview(packet, "a-original", "reviewer-a", SemanticSupportRating.Supported);
        var corrected = FinalReview(packet, "a-corrected", "reviewer-a", SemanticSupportRating.Unsupported) with
        {
            ReviewedAtUtc = original.ReviewedAtUtc.AddMinutes(1),
        };
        var independent = FinalReview(packet, "b-review", "reviewer-b", SemanticSupportRating.Supported) with
        {
            Role = CalibrationReviewerRole.Independent,
        };
        var first = CalibrationPackets.Summarize(packet, [corrected, independent, original]);
        var reordered = CalibrationPackets.Summarize(packet, [original, corrected, independent]);

        first.UnresolvedDisagreements.Should().BeEquivalentTo(reordered.UnresolvedDisagreements);
        first.UnresolvedDisagreements.Should().Contain(value =>
            value.Subject == "claim-1" && value.Labels["reviewer-a"] == "Unsupported");
        first.UnresolvedDisagreements.Should().Contain(value =>
            value.Subject == "claim-1/unsupported-certainty");

        var ambiguous = () => CalibrationPackets.Summarize(
            packet, [original, corrected with { ReviewedAtUtc = original.ReviewedAtUtc }]);
        ambiguous.Should().Throw<InvalidDataException>().WithMessage("*distinct timestamps*");
    }

    [Fact]
    public void Summary_StaleAdjudicationCannotResolveCorrectedReviews()
    {
        using var files = new CalibrationTestFiles();
        var packet = CreatePacket(files, CreateReport());
        var first = FinalReview(packet, "a-review", "reviewer-a", SemanticSupportRating.Supported);
        var second = FinalReview(packet, "b-review", "reviewer-b", SemanticSupportRating.Unsupported) with
        {
            Role = CalibrationReviewerRole.Independent,
        };
        var adjudication = FinalReview(packet, "adjudication", "reviewer-c") with
        {
            Role = CalibrationReviewerRole.Adjudicator,
            ReviewedAtUtc = first.ReviewedAtUtc.AddMinutes(1),
            AdjudicatesReviewIds = [first.ReviewId, second.ReviewId],
        };
        var completed = CalibrationPackets.Summarize(packet, [first, second, adjudication]);
        completed.HasExplicitAdjudication.Should().BeTrue();
        completed.UnresolvedDisagreements.Should().BeEmpty();

        var corrected = first with
        {
            ReviewId = "a-corrected",
            ReviewedAtUtc = first.ReviewedAtUtc.AddMinutes(2),
        };
        var stale = CalibrationPackets.Summarize(packet, [first, second, adjudication, corrected]);
        stale.HasExplicitAdjudication.Should().BeFalse();
        stale.UnresolvedDisagreements.Should().Contain(value => value.Subject == "claim-1");
    }

    [Fact]
    public void DamagedEvidenceCanReceiveHumanNotAssessableLabels()
    {
        using var files = new CalibrationTestFiles();
        var report = CreateReport("""{"notes":["loss"],"metric":1}""") with
        {
            Transcript =
            [
                new AgentTranscriptTurn(
                    1,
                    "{}",
                    null,
                    [],
                    [ToolResult("""{"notes":["loss"],"metric":1}""") with { Truncated = true }],
                    new AgentModelUsage(null, null, null),
                    null),
            ],
        };
        var packet = CreatePacket(files, report);
        var review = FinalReview(packet, "review-1", "reviewer-a", SemanticSupportRating.NotAssessable);

        var summary = CalibrationPackets.Summarize(packet, [review]);

        summary.Dimensions.Single(value => value.Dimension == "claim-support").Labels
            .Should().ContainSingle(value => value.EndsWith(
                $":{nameof(SemanticSupportRating.NotAssessable)}",
                StringComparison.Ordinal));
        packet.CaptureAndTruncationNotes.Should().Contain(value => value.Contains("truncated", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedRunProducesNotAssessablePacketWithStatusesAndInvalidCitation()
    {
        using var files = new CalibrationTestFiles();
        var report = CreateReport("""{"error":"denied"}""", ["tool-result://missing#/value"]) with
        {
            Collection = Stage(AgentHarnessStageStatus.Failed, AgentHarnessFailureKind.Collection, "collection failed"),
            AgentExecution = Stage(AgentHarnessStageStatus.Failed, AgentHarnessFailureKind.Model, "no final diagnosis"),
        };

        var packet = CreatePacket(files, report);

        packet.Reviewability.Should().Be(CalibrationReviewability.NotAssessable);
        packet.Stages.Collection.Status.Should().Be(AgentHarnessStageStatus.Failed);
        packet.Claims[0].Citations.Should().ContainSingle(citation => !citation.Exists);
    }

    [Fact]
    public void ProtocolValidatorRejectsDevelopmentHeldoutCaptureReuse()
    {
        using var files = new CalibrationTestFiles();
        var report = CreateReport("""{"metric":1}""");
        var development = CreatePacket(files, report, CalibrationPartition.Development);
        var heldout = CreatePacket(files, report, CalibrationPartition.Heldout);

        var action = () => CalibrationPackets.ValidateNoPartitionOverlap([development, heldout]);

        action.Should().Throw<InvalidDataException>().WithMessage("*cannot share*");
    }

    [EnvironmentRequiredFact(
        "DOTNET_DIAGNOSTICS_CALIBRATION_OPERATION",
        "Local packet export/import is opt-in and performs file-only calibration workflow operations.")]
    [Trait("Category", "ScenarioCalibrationWorkflow")]
    public void LocalWorkflow_ExportsPacketOrImportsReviews()
    {
        var operation = RequiredEnvironment("DOTNET_DIAGNOSTICS_CALIBRATION_OPERATION");
        var outputDirectory = RequiredEnvironment("DOTNET_DIAGNOSTICS_CALIBRATION_OUTPUT_DIRECTORY");
        Directory.CreateDirectory(outputDirectory);
        CalibrationPacket packet;
        if (operation == "export")
        {
            var protocolPath = Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_CALIBRATION_PROTOCOL");
            var protocol = string.IsNullOrWhiteSpace(protocolPath)
                ? null
                : CalibrationProtocols.Load(protocolPath);
            packet = CalibrationPackets.CreatePacket(
                RequiredEnvironment("DOTNET_DIAGNOSTICS_CALIBRATION_SOURCE_REPORT"),
                new CalibrationCaseDescriptor(
                    RequiredEnvironment("DOTNET_DIAGNOSTICS_CALIBRATION_PROTOCOL_ID"),
                    RequiredEnvironment("DOTNET_DIAGNOSTICS_CALIBRATION_RUBRIC_FINGERPRINT"),
                    RequiredEnvironment("DOTNET_DIAGNOSTICS_CALIBRATION_CASE_ID"),
                    Enum.Parse<CalibrationPartition>(
                        RequiredEnvironment("DOTNET_DIAGNOSTICS_CALIBRATION_PARTITION"),
                        ignoreCase: true),
                    Enum.Parse<CalibrationProvenanceKind>(
                        RequiredEnvironment("DOTNET_DIAGNOSTICS_CALIBRATION_PROVENANCE"),
                        ignoreCase: true),
                    RequiredEnvironment("DOTNET_DIAGNOSTICS_CALIBRATION_CAPTURE_ID"),
                    Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_CALIBRATION_CAPTURE_HASH"),
                    Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_CALIBRATION_NOTES") ?? string.Empty,
                    protocol?.ProtocolFingerprint),
                protocol);
            CalibrationPackets.WritePacket(Path.Combine(outputDirectory, "packet.json"), packet);
            CalibrationPackets.WriteReview(
                Path.Combine(outputDirectory, "review-template.json"),
                CalibrationPackets.CreateBlankReviewTemplate(packet));
            CalibrationPackets.WriteMarkdown(Path.Combine(outputDirectory, "packet.md"), packet);
        }
        else if (operation == "summarize")
        {
            packet = CalibrationPackets.ReadPacket(RequiredEnvironment("DOTNET_DIAGNOSTICS_CALIBRATION_PACKET"));
            var protocolPath = Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_CALIBRATION_PROTOCOL");
            if (!string.IsNullOrWhiteSpace(protocolPath))
            {
                CalibrationProtocols.ValidatePacket(CalibrationProtocols.Load(protocolPath), packet);
            }
        }
        else
        {
            throw new InvalidOperationException("Calibration operation must be 'export' or 'summarize'.");
        }

        var reviewPaths = (Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_CALIBRATION_REVIEWS") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var reviews = reviewPaths.Select(path => CalibrationPackets.ReadReview(path, packet)).ToArray();
        CalibrationPackets.WriteSummary(
            Path.Combine(outputDirectory, "summary.json"),
            CalibrationPackets.Summarize(packet, reviews));
    }

    private static CalibrationPacket CreatePacket(
        CalibrationTestFiles files,
        AgentHarnessReport report,
        CalibrationPartition partition = CalibrationPartition.Development)
    {
        var source = files.Path($"source-{Guid.NewGuid():n}.json");
        BlindedAgentHarness.WriteReport(source, report);
        return CalibrationPackets.CreatePacket(
            source,
            new CalibrationCaseDescriptor(
                "draft-protocol-v1",
                RubricFingerprint,
                "synthetic-test-case",
                partition,
                CalibrationProvenanceKind.Scripted,
                report.RunId,
                new string('c', 64),
                "SYNTHETIC TEST data; not an empirical human result."));
    }

    private static AgentHarnessReport CreateReport(
        string content = """{"metric":"wrong-metric","value":1}""",
        IReadOnlyList<string>? locations = null)
    {
        var result = ToolResult(content);
        return new AgentHarnessReport(
            BlindedAgentHarness.CurrentReportSchemaVersion,
            Guid.NewGuid().ToString("n"),
            "scripted",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-01-01T00:00:01Z", CultureInfo.InvariantCulture),
            new AgentHarnessBudget(),
            Stage(AgentHarnessStageStatus.Passed, AgentHarnessFailureKind.None, "configured"),
            Stage(AgentHarnessStageStatus.Passed, AgentHarnessFailureKind.None, "activated"),
            Stage(AgentHarnessStageStatus.Passed, AgentHarnessFailureKind.None, "collected"),
            Stage(AgentHarnessStageStatus.Passed, AgentHarnessFailureKind.None, "diagnosed"),
            Stage(AgentHarnessStageStatus.Passed, AgentHarnessFailureKind.None, "cleaned"),
            new AgentHarnessAssessment(
                Stage(AgentHarnessStageStatus.Passed, AgentHarnessFailureKind.None, "mechanical only"),
                [],
                "citation-resolution-only"),
            new AgentHarnessProvenance(
                "scripted",
                "fixture",
                "1",
                "offline",
                0,
                100,
                new string('d', 64),
                new string('e', 64),
                "commit",
                "version",
                "PRIVATE-WORKLOAD-ID",
                "1",
                new Dictionary<string, string> { ["PRIVATE_ROUTE"] = "/secret" },
                "seed",
                ".NET",
                "Linux",
                "X64",
                "local",
                "bounded",
                "private",
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
                [new AgentClaim(
                    "Queue growth alone does not prove ThreadPool starvation.",
                    AgentEvidencePosture.Inferred,
                    locations ?? ["tool-result://call-1#/metric"])],
                "The reason is unknown.",
                ["Collect a thread snapshot."]),
            1,
            null,
            null,
            null,
            result.ByteCount,
            ["Evidence may be incomplete."]);
    }

    private static AgentToolResult ToolResult(string content)
        => new(
            "call-1",
            "collect_events",
            true,
            content,
            BlindedDiagnosticToolGateway.Sha256(content),
            Encoding.UTF8.GetByteCount(content),
            false,
            null);

    private static AgentHarnessStage Stage(
        AgentHarnessStageStatus status,
        AgentHarnessFailureKind failureKind,
        string detail)
        => new(status, failureKind, detail, 0);

    private static CalibrationReview FinalReview(
        CalibrationPacket packet,
        string reviewId,
        string reviewerId,
        SemanticSupportRating support = SemanticSupportRating.PartiallySupported)
        => new(
            CalibrationPackets.CurrentReviewSchemaVersion,
            reviewId,
            reviewerId,
            CalibrationReviewerRole.Primary,
            DateTimeOffset.Parse("2026-01-02T00:00:00Z", CultureInfo.InvariantCulture),
            packet.Fingerprint,
            packet.Descriptor.ProtocolId,
            packet.Descriptor.RubricFingerprint,
            packet.CaseFingerprint,
            true,
            [],
            packet.Claims.Select(claim => new CalibrationClaimJudgment(
                claim.ClaimId,
                support,
                support == SemanticSupportRating.Unsupported
                    ? CertaintyRating.Overconfident
                    : CertaintyRating.Appropriate,
                "SYNTHETIC TEST judgment for deterministic mechanics only.")).ToArray(),
            new CalibrationResponseJudgment(
                support,
                UncertaintyAbstentionRating.AppropriateUncertainty,
                QualityRating.Appropriate,
                ApprovalComplianceRating.Compliant,
                null,
                QualityRating.NotAssessable,
                "SYNTHETIC TEST response judgment; no empirical human result."),
            packet.Descriptor.ProtocolFingerprint);

    private static string RequiredEnvironment(string name)
        => Environment.GetEnvironmentVariable(name)
           ?? throw new InvalidOperationException($"Environment variable {name} is required.");

    private sealed class CalibrationTestFiles : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "calibration-test-artifacts",
            Guid.NewGuid().ToString("n"));

        public string Path(string name)
        {
            Directory.CreateDirectory(directory);
            return System.IO.Path.Combine(directory, name);
        }

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
