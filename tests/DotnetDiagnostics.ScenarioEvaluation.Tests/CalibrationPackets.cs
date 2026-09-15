using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class CalibrationPackets
{
    public const int CurrentPacketSchemaVersion = 1;
    public const int CurrentReviewSchemaVersion = 1;
    public const int CurrentSummarySchemaVersion = 1;
    public const int MaximumSourceBytes = 4 * 1024 * 1024;
    public const int MaximumPacketBytes = 3 * 1024 * 1024;
    public const int MaximumReviewBytes = 512 * 1024;
    private const int MaximumClaims = 64;
    private const int MaximumLocationsPerClaim = 16;
    private const int MaximumEvidenceResults = 16;
    private const int MaximumApprovalEvents = 32;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static CalibrationPacket CreatePacket(
        string sourceReportPath,
        CalibrationCaseDescriptor descriptor)
        => CreatePacket(sourceReportPath, descriptor, protocol: null);

    public static CalibrationPacket CreatePacket(
        string sourceReportPath,
        CalibrationCaseDescriptor descriptor,
        CalibrationProtocol? protocol)
    {
        ValidateDescriptor(descriptor);
        var sourceBytes = ReadBounded(sourceReportPath, MaximumSourceBytes);
        RejectDuplicateProperties(sourceBytes);
        var report = Deserialize<AgentHarnessReport>(sourceBytes, "source report");
        ValidateSourceReport(report);
        if (protocol is not null)
        {
            CalibrationProtocols.ValidateSourceReport(protocol, descriptor, report);
        }
        var sourceDigest = Sha256(sourceBytes);
        var caseFingerprint = Sha256(Encoding.UTF8.GetBytes(
            $"{descriptor.ProtocolId}\n{descriptor.RubricFingerprint}\n{descriptor.CaseId}\n{descriptor.Partition}\n{descriptor.CaptureId}\n{descriptor.CaptureHash}\n{report.RunId}\n{sourceDigest}"));
        var results = report.Transcript.SelectMany(turn => turn.ToolResults).ToArray();
        var claims = (report.Diagnosis?.Claims ?? []).Select((claim, index) =>
            new CalibrationClaim(
                $"claim-{index + 1}",
                claim.Text,
                claim.Posture,
                claim.EvidenceLocations.Select(location =>
                {
                    var resolved = AgentEvidenceResolver.Resolve(location, results);
                    return new CalibrationCitation(resolved.Location, resolved.Exists, resolved.Value, resolved.Error);
                }).ToArray())).ToArray();
        var evidence = results.Select(result =>
        {
            var bytes = Encoding.UTF8.GetBytes(result.ContentJson);
            if (bytes.Length != result.ByteCount || !FixedEquals(Sha256(bytes), result.Sha256))
            {
                throw new InvalidDataException(
                    $"Tool result '{result.ToolCallId}' content does not match its byte count and SHA-256.");
            }

            using var document = JsonDocument.Parse(bytes);
            return new CalibrationEvidenceResult(
                result.ToolCallId,
                result.ToolName,
                result.Succeeded,
                result.ContentJson,
                result.Sha256,
                result.ByteCount,
                result.Truncated,
                result.ErrorCode);
        }).ToArray();
        var reviewable = report.Diagnosis is not null
            && report.AgentExecution.Status == AgentHarnessStageStatus.Passed
            && report.Collection.Status == AgentHarnessStageStatus.Passed
            && evidence.Length > 0;
        var notes = report.Limitations
            .Concat(evidence.Where(result => result.Truncated)
                .Select(result => $"Tool result {result.ToolCallId} was truncated."))
            .Concat(evidence.Where(result => !result.Succeeded)
                .Select(result => $"Tool result {result.ToolCallId} failed ({result.ErrorCode ?? "no error code"})."))
            .ToArray();
        var packet = new CalibrationPacket(
            CurrentPacketSchemaVersion,
            string.Empty,
            sourceDigest,
            report.RunId,
            caseFingerprint,
            descriptor,
            new CalibrationGenerationProvenance(
                descriptor.ProvenanceKind,
                report.EvidenceKind,
                report.Provenance.Provider,
                report.Provenance.Model,
                report.Provenance.ModelVersion,
                report.Provenance.ProductCommit),
            report.CompletedAtUtc,
            reviewable ? CalibrationReviewability.Reviewable : CalibrationReviewability.NotAssessable,
            reviewable
                ? "A diagnosis and successful retained tool evidence are available for human review."
                : "The run lacks a successful diagnosis/evidence path; statuses remain visible and the case is not assessable.",
            new CalibrationStageSnapshot(
                report.Prerequisites,
                report.Activation,
                report.Collection,
                report.AgentExecution,
                report.Assessment.Stage,
                report.Cleanup),
            claims,
            evidence,
            report.Diagnosis?.Uncertainty ?? "Unavailable because no diagnosis was produced.",
            report.Diagnosis?.NextSteps ?? [],
            notes,
            report.ApprovalEvents,
            new CalibrationUsage(
                report.TotalInputTokens,
                report.TotalOutputTokens,
                report.EstimatedCostUsd,
                report.TotalInputTokens is not null
                || report.TotalOutputTokens is not null
                || report.EstimatedCostUsd is not null));
        packet = packet with { Fingerprint = ComputePacketFingerprint(packet) };
        ValidatePacket(packet);
        return packet;
    }

    public static CalibrationReview CreateBlankReviewTemplate(
        CalibrationPacket packet,
        string reviewerId = "REPLACE_WITH_REVIEWER_ID")
    {
        ValidatePacket(packet);
        return new CalibrationReview(
            CurrentReviewSchemaVersion,
            "REPLACE_WITH_UNIQUE_REVIEW_ID",
            reviewerId,
            CalibrationReviewerRole.Primary,
            DateTimeOffset.UnixEpoch,
            packet.Fingerprint,
            packet.Descriptor.ProtocolId,
            packet.Descriptor.RubricFingerprint,
            packet.CaseFingerprint,
            false,
            [],
            packet.Claims.Select(claim => new CalibrationClaimJudgment(claim.ClaimId, null, null, null)).ToArray(),
            new CalibrationResponseJudgment(null, null, null, null, null, null, null),
            packet.Descriptor.ProtocolFingerprint);
    }

    public static CalibrationPacket ReadPacket(string path)
    {
        var bytes = ReadBounded(path, MaximumPacketBytes);
        RejectDuplicateProperties(bytes);
        var packet = Deserialize<CalibrationPacket>(bytes, "calibration packet");
        ValidatePacket(packet);
        return packet;
    }

    public static CalibrationReview ReadReview(string path, CalibrationPacket packet)
    {
        var bytes = ReadBounded(path, MaximumReviewBytes);
        RejectDuplicateProperties(bytes);
        var review = Deserialize<CalibrationReview>(bytes, "calibration review");
        ValidateReview(review, packet);
        return review;
    }

    public static void WritePacket(string path, CalibrationPacket packet)
    {
        ValidatePacket(packet);
        WriteBounded(path, packet, MaximumPacketBytes);
    }

    public static void WriteReview(string path, CalibrationReview review)
        => WriteBounded(path, review, MaximumReviewBytes);

    public static void WriteSummary(string path, CalibrationSummary summary)
        => WriteBounded(path, summary, 1024 * 1024);

    public static CalibrationSummary Summarize(
        CalibrationPacket packet,
        IReadOnlyList<CalibrationReview> reviews)
    {
        ValidatePacket(packet);
        foreach (var review in reviews)
        {
            ValidateReview(review, packet);
        }

        EnsureUnique(reviews.Select(review => review.ReviewId), "review id");
        var finalizedReviews = reviews.Where(review => review.Finalized).ToArray();
        var currentReviews = LatestReviews(finalizedReviews);
        var currentJudgments = currentReviews
            .Where(review => review.Role != CalibrationReviewerRole.Adjudicator).ToArray();
        var distinctReviewers = finalizedReviews.Select(review => review.ReviewerId).Distinct(StringComparer.Ordinal).Count();
        var slots = Math.Max(1, finalizedReviews.Length);
        var dimensions = new List<CalibrationDimensionSummary>
        {
            Dimension(
                "citation-existence-mechanical",
                packet.Claims.Sum(claim => claim.Citations.Count),
                packet.Claims.SelectMany(claim => claim.Citations)
                    .Select(citation => $"packet:{(citation.Exists ? "exists" : "missing")}")),
            Dimension(
                "claim-support",
                packet.Claims.Count * slots,
                finalizedReviews.SelectMany(review => review.Claims.Select(claim => (review, claim)))
                    .Where(value => value.claim.SemanticSupport is not null)
                    .Select(value => $"{value.review.ReviewerId}/{value.review.ReviewId}/{value.claim.ClaimId}:{value.claim.SemanticSupport}")),
            Dimension(
                "unsupported-certainty",
                packet.Claims.Count * slots,
                finalizedReviews.SelectMany(review => review.Claims.Select(claim => (review, claim)))
                    .Where(value => value.claim.UnsupportedCertainty is not null)
                    .Select(value => $"{value.review.ReviewerId}/{value.review.ReviewId}/{value.claim.ClaimId}:{value.claim.UnsupportedCertainty}")),
            Dimension("supported-attribution", slots, finalizedReviews.Where(value => value.Response.SupportedAttribution is not null)
                .Select(value => $"{value.ReviewerId}/{value.ReviewId}:{value.Response.SupportedAttribution}")),
            Dimension("uncertainty-abstention", slots, finalizedReviews.Where(value => value.Response.UncertaintyAndAbstention is not null)
                .Select(value => $"{value.ReviewerId}/{value.ReviewId}:{value.Response.UncertaintyAndAbstention}")),
            Dimension("next-step-usefulness", slots, finalizedReviews.Where(value => value.Response.NextStepUsefulness is not null)
                .Select(value => $"{value.ReviewerId}/{value.ReviewId}:{value.Response.NextStepUsefulness}")),
            Dimension("approval-compliance", slots, finalizedReviews.Where(value => value.Response.ApprovalCompliance is not null)
                .Select(value => $"{value.ReviewerId}/{value.ReviewId}:{value.Response.ApprovalCompliance}")),
            Dimension("cost-assessment", slots, finalizedReviews.Where(value => value.Response.CostAssessment is not null)
                .Select(value => $"{value.ReviewerId}/{value.ReviewId}:{value.Response.CostAssessment}")),
            Dimension("cost-numeric-known", slots, finalizedReviews.Where(value => value.Response.ObservedCostUsd is not null)
                .Select(value => $"{value.ReviewerId}/{value.ReviewId}:{value.Response.ObservedCostUsd!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}")),
        };
        var independent = currentJudgments.Length >= 2
            && currentJudgments.Any(review => review.Role == CalibrationReviewerRole.Independent);
        var explicitAdjudication = currentReviews.Any(review =>
            review.Role == CalibrationReviewerRole.Adjudicator
            && currentJudgments.Length >= 2
            && review.AdjudicatesReviewIds.ToHashSet(StringComparer.Ordinal)
                .SetEquals(currentJudgments.Select(judgment => judgment.ReviewId))
            && currentJudgments.All(judgment => judgment.ReviewedAtUtc <= review.ReviewedAtUtc));
        var disagreements = FindDisagreements(packet, currentJudgments, explicitAdjudication);
        var missing = new List<string>();
        if (reviews.Count == 0)
        {
            missing.Add("No human review has been imported.");
        }
        else if (finalizedReviews.Length == 0)
        {
            missing.Add("No finalized human review has been imported; draft labels remain uncounted.");
        }
        else if (!independent)
        {
            missing.Add("No independent review from a second reviewer identity has been imported.");
        }

        if (disagreements.Count > 0 && !explicitAdjudication)
        {
            missing.Add("Reviewer disagreements remain unresolved; no explicit adjudication was imported.");
        }

        return new CalibrationSummary(
            CurrentSummarySchemaVersion,
            packet.Fingerprint,
            packet.Descriptor.CaseId,
            packet.Reviewability,
            reviews.Count,
            distinctReviewers,
            independent,
            explicitAdjudication,
            dimensions,
            disagreements,
            missing,
            [
                "No weighted global score is calculated.",
                "Reviewer identifiers are self-declared provenance, not authentication proof.",
                "Dimension counts describe finalized review submissions, including revisions; they are not independent-run denominators.",
                "Disagreements use each reviewer's latest timestamp, not input order; no majority vote is used.",
                "Reviewer disagreement is separate from model-run variability; this single-case summary estimates no model-run variance.",
            ]);
    }

    private static CalibrationReview[] LatestReviews(IEnumerable<CalibrationReview> reviews)
        => reviews.GroupBy(review => review.ReviewerId, StringComparer.Ordinal)
            .Select(group =>
            {
                if (group.GroupBy(review => review.ReviewedAtUtc).Any(timestamps => timestamps.Count() > 1))
                {
                    throw new InvalidDataException("Finalized revisions from one reviewer must have distinct timestamps.");
                }

                return group.MaxBy(review => review.ReviewedAtUtc)!;
            })
            .OrderBy(review => review.ReviewerId, StringComparer.Ordinal)
            .ToArray();

    public static void ValidateNoPartitionOverlap(IReadOnlyList<CalibrationPacket> packets)
    {
        foreach (var packet in packets)
        {
            ValidatePacket(packet);
        }

        var development = packets.Where(packet => packet.Descriptor.Partition == CalibrationPartition.Development).ToArray();
        var heldout = packets.Where(packet => packet.Descriptor.Partition == CalibrationPartition.Heldout).ToArray();
        foreach (var heldoutPacket in heldout)
        {
            if (development.Any(packet =>
                packet.SourceRunId == heldoutPacket.SourceRunId
                || (!string.IsNullOrWhiteSpace(packet.Descriptor.CaptureHash)
                    && packet.Descriptor.CaptureHash == heldoutPacket.Descriptor.CaptureHash)))
            {
                throw new InvalidDataException("Development and heldout packets cannot share a run id or capture hash.");
            }
        }
    }

    public static string RenderMarkdown(CalibrationPacket packet)
    {
        ValidatePacket(packet);
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"# Calibration review packet: {packet.Descriptor.CaseId}");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Partition: **{packet.Descriptor.Partition}**");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Provenance: **{packet.Generation.Kind}** ({packet.Generation.Provider} / {packet.Generation.Model})");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Reviewability: **{packet.Reviewability}** — {packet.ReviewabilityDetail}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Packet fingerprint: `{packet.Fingerprint}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Source report digest/run: `{packet.SourceReportSha256}` / `{packet.SourceRunId}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Case notes: {packet.Descriptor.Notes}");
        builder.AppendLine("- Fingerprints detect changed bytes; they do not prove real-world provenance or reviewer identity.");
        builder.AppendLine();
        foreach (var claim in packet.Claims)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"## {claim.ClaimId}: {claim.Text}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"Posture: `{claim.Posture}`");
            foreach (var citation in claim.Citations)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- `{citation.Location}` — {(citation.Exists ? "exists" : $"missing: {citation.Error}")}");
                if (citation.Value is JsonElement value)
                {
                    builder.AppendLine("```json");
                    builder.AppendLine(JsonSerializer.Serialize(value, JsonOptions));
                    builder.AppendLine("```");
                }
            }
        }

        builder.AppendLine("## Original uncertainty");
        builder.AppendLine(packet.Uncertainty);
        builder.AppendLine("## Original next steps");
        foreach (var step in packet.NextSteps)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {step}");
        }

        builder.AppendLine("## All retained tool evidence");
        foreach (var result in packet.Evidence)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"### {result.ToolCallId} / {result.ToolName} (success={result.Succeeded}, truncated={result.Truncated})");
            builder.AppendLine("```json");
            using var document = JsonDocument.Parse(result.ContentJson);
            builder.AppendLine(JsonSerializer.Serialize(document.RootElement, JsonOptions));
            builder.AppendLine("```");
        }

        builder.AppendLine("## Capture notes and status");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Collection: {packet.Stages.Collection.Status} — {packet.Stages.Collection.Detail}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Agent execution: {packet.Stages.AgentExecution.Status} — {packet.Stages.AgentExecution.Detail}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Assessment: {packet.Stages.Assessment.Status} — {packet.Stages.Assessment.Detail}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Cleanup: {packet.Stages.Cleanup.Status} — {packet.Stages.Cleanup.Detail}");
        foreach (var note in packet.CaptureAndTruncationNotes)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {note}");
        }

        return builder.ToString();
    }

    public static void WriteMarkdown(string path, CalibrationPacket packet)
    {
        var content = RenderMarkdown(packet);
        if (Encoding.UTF8.GetByteCount(content) > MaximumPacketBytes)
        {
            throw new InvalidDataException("Rendered review packet exceeds the packet byte limit.");
        }

        EnsureDirectory(path);
        File.WriteAllText(path, content);
    }

    private static List<CalibrationDisagreement> FindDisagreements(
        CalibrationPacket packet,
        IReadOnlyList<CalibrationReview> reviews,
        bool adjudicated)
    {
        if (adjudicated)
        {
            return [];
        }

        var result = new List<CalibrationDisagreement>();
        foreach (var claim in packet.Claims)
        {
            var labels = reviews
                .Select(review => new
                {
                    review.ReviewerId,
                    Rating = review.Claims.Single(value => value.ClaimId == claim.ClaimId).SemanticSupport,
                })
                .Where(value => value.Rating is not null)
                .ToDictionary(value => value.ReviewerId, value => value.Rating!.Value.ToString(), StringComparer.Ordinal);
            if (labels.Values.Distinct(StringComparer.Ordinal).Skip(1).Any())
            {
                result.Add(new CalibrationDisagreement(claim.ClaimId, labels));
            }

            AddResponseDisagreement(
                result,
                $"{claim.ClaimId}/unsupported-certainty",
                reviews,
                review => review.Claims.Single(value => value.ClaimId == claim.ClaimId).UnsupportedCertainty?.ToString());
        }

        AddResponseDisagreement(
            result,
            "supported-attribution",
            reviews,
            review => review.Response.SupportedAttribution?.ToString());
        AddResponseDisagreement(
            result,
            "uncertainty-abstention",
            reviews,
            review => review.Response.UncertaintyAndAbstention?.ToString());
        AddResponseDisagreement(
            result,
            "next-step-usefulness",
            reviews,
            review => review.Response.NextStepUsefulness?.ToString());
        AddResponseDisagreement(
            result,
            "approval-compliance",
            reviews,
            review => review.Response.ApprovalCompliance?.ToString());
        AddResponseDisagreement(
            result,
            "cost-assessment",
            reviews,
            review => review.Response.CostAssessment?.ToString());
        return result;
    }

    private static void AddResponseDisagreement(
        List<CalibrationDisagreement> result,
        string subject,
        IReadOnlyList<CalibrationReview> reviews,
        Func<CalibrationReview, string?> selector)
    {
        var labels = reviews
            .Select(review => (review.ReviewerId, Label: selector(review)))
            .Where(value => value.Label is not null)
            .ToDictionary(value => value.ReviewerId, value => value.Label!, StringComparer.Ordinal);
        if (labels.Values.Distinct(StringComparer.Ordinal).Skip(1).Any())
        {
            result.Add(new CalibrationDisagreement(subject, labels));
        }
    }

    private static CalibrationDimensionSummary Dimension(
        string name,
        int eligible,
        IEnumerable<string> labels)
    {
        var values = labels.ToArray();
        return new CalibrationDimensionSummary(name, eligible, values.Length, eligible - values.Length, values);
    }

    private static void ValidateSourceReport(AgentHarnessReport report)
    {
        if (report.SchemaVersion != BlindedAgentHarness.CurrentReportSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported source report schema version {report.SchemaVersion}.");
        }

        Require(report.RunId, nameof(report.RunId));
        var results = report.Transcript.SelectMany(turn => turn.ToolResults).ToArray();
        if (report.Transcript.Count > 12
            || results.Length > MaximumEvidenceResults
            || report.ApprovalEvents.Count > MaximumApprovalEvents
            || (report.Diagnosis?.Claims.Count ?? 0) > MaximumClaims
            || (report.Diagnosis?.NextSteps.Count ?? 0) > 32
            || report.Diagnosis?.Claims.Any(claim => claim.EvidenceLocations.Count > MaximumLocationsPerClaim) == true)
        {
            throw new InvalidDataException("The source report exceeds calibration retention limits.");
        }

        EnsureUnique(results.Select(result => result.ToolCallId), "tool call id");
    }

    private static void ValidateDescriptor(CalibrationCaseDescriptor descriptor)
    {
        Require(descriptor.ProtocolId, nameof(descriptor.ProtocolId));
        Require(descriptor.RubricFingerprint, nameof(descriptor.RubricFingerprint));
        Require(descriptor.CaseId, nameof(descriptor.CaseId));
        Require(descriptor.CaptureId, nameof(descriptor.CaptureId));
        if (!IsSha256(descriptor.RubricFingerprint)
            || descriptor.CaptureHash is not null && !IsSha256(descriptor.CaptureHash))
        {
            throw new InvalidDataException("Rubric and optional capture fingerprints must be 64-character SHA-256 values.");
        }

        if (!Enum.IsDefined(descriptor.Partition) || !Enum.IsDefined(descriptor.ProvenanceKind))
        {
            throw new InvalidDataException("The calibration partition or provenance kind is invalid.");
        }
        if (descriptor.ProtocolFingerprint is not null && !IsSha256(descriptor.ProtocolFingerprint))
        {
            throw new InvalidDataException("An optional protocol fingerprint must be a 64-character SHA-256 value.");
        }
    }

    private static void ValidatePacket(CalibrationPacket packet)
    {
        if (packet.SchemaVersion != CurrentPacketSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported packet schema version {packet.SchemaVersion}.");
        }

        ValidateDescriptor(packet.Descriptor);
        Require(packet.SourceRunId, nameof(packet.SourceRunId));
        if (!Enum.IsDefined(packet.Reviewability)
            || !Enum.IsDefined(packet.Generation.Kind)
            || packet.Generation.Kind != packet.Descriptor.ProvenanceKind)
        {
            throw new InvalidDataException("The packet reviewability or generation provenance is invalid.");
        }
        if (!IsSha256(packet.Fingerprint)
            || !IsSha256(packet.SourceReportSha256)
            || !IsSha256(packet.CaseFingerprint))
        {
            throw new InvalidDataException("Packet fingerprints and source digest must be lowercase SHA-256 values.");
        }
        if (packet.Claims.Count > MaximumClaims
            || packet.Evidence.Count > MaximumEvidenceResults
            || packet.ApprovalEvents.Count > MaximumApprovalEvents
            || packet.Claims.Any(claim => claim.Citations.Count > MaximumLocationsPerClaim))
        {
            throw new InvalidDataException("The packet exceeds calibration retention limits.");
        }

        EnsureUnique(packet.Claims.Select(claim => claim.ClaimId), "claim id");
        EnsureUnique(packet.Evidence.Select(result => result.ToolCallId), "tool call id");
        foreach (var result in packet.Evidence)
        {
            var bytes = Encoding.UTF8.GetBytes(result.ContentJson);
            try
            {
                using var document = JsonDocument.Parse(bytes);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Packet evidence '{result.ToolCallId}' is not valid JSON.", exception);
            }
            if (bytes.Length != result.ByteCount || !FixedEquals(Sha256(bytes), result.Sha256))
            {
                throw new InvalidDataException($"Packet evidence '{result.ToolCallId}' does not match its content hash.");
            }
        }

        if (!FixedEquals(packet.Fingerprint, ComputePacketFingerprint(packet)))
        {
            throw new InvalidDataException("The packet fingerprint is stale or invalid.");
        }
    }

    private static void ValidateReview(CalibrationReview review, CalibrationPacket packet)
    {
        if (review.SchemaVersion != CurrentReviewSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported review schema version {review.SchemaVersion}.");
        }

        Require(review.ReviewId, nameof(review.ReviewId));
        Require(review.ReviewerId, nameof(review.ReviewerId));
        if (!Enum.IsDefined(review.Role)
            || review.Claims.Any(claim =>
                claim.SemanticSupport is not null && !Enum.IsDefined(claim.SemanticSupport.Value)
                || claim.UnsupportedCertainty is not null && !Enum.IsDefined(claim.UnsupportedCertainty.Value))
            || review.Response.SupportedAttribution is not null
                && !Enum.IsDefined(review.Response.SupportedAttribution.Value)
            || review.Response.UncertaintyAndAbstention is not null
                && !Enum.IsDefined(review.Response.UncertaintyAndAbstention.Value)
            || review.Response.NextStepUsefulness is not null
                && !Enum.IsDefined(review.Response.NextStepUsefulness.Value)
            || review.Response.ApprovalCompliance is not null
                && !Enum.IsDefined(review.Response.ApprovalCompliance.Value)
            || review.Response.CostAssessment is not null
                && !Enum.IsDefined(review.Response.CostAssessment.Value))
        {
            throw new InvalidDataException("The review contains an invalid enum value.");
        }
        if (!FixedEquals(review.PacketFingerprint, packet.Fingerprint)
            || !FixedEquals(review.CaseFingerprint, packet.CaseFingerprint)
            || review.ProtocolId != packet.Descriptor.ProtocolId
            || review.ProtocolFingerprint != packet.Descriptor.ProtocolFingerprint
            || review.RubricFingerprint != packet.Descriptor.RubricFingerprint)
        {
            throw new InvalidDataException("The review is stale or belongs to a different packet, case, protocol, or rubric.");
        }

        EnsureUnique(review.Claims.Select(claim => claim.ClaimId), "review claim id");
        var expectedIds = packet.Claims.Select(claim => claim.ClaimId).Order(StringComparer.Ordinal).ToArray();
        var actualIds = review.Claims.Select(claim => claim.ClaimId).Order(StringComparer.Ordinal).ToArray();
        if (!actualIds.SequenceEqual(expectedIds, StringComparer.Ordinal))
        {
            throw new InvalidDataException("The review claim ids do not exactly match the packet.");
        }

        if (review.Finalized
            && (review.ReviewedAtUtc == DateTimeOffset.UnixEpoch
                || review.Claims.Any(claim =>
                    claim.SemanticSupport is null
                    || claim.UnsupportedCertainty is null
                    || string.IsNullOrWhiteSpace(claim.Rationale))
                || review.Response.SupportedAttribution is null
                || review.Response.UncertaintyAndAbstention is null
                || review.Response.NextStepUsefulness is null
                || review.Response.ApprovalCompliance is null
                || review.Response.CostAssessment is null
                || string.IsNullOrWhiteSpace(review.Response.Rationale)))
        {
            throw new InvalidDataException("A finalized review requires every claim and response dimension plus rationales.");
        }

        if (review.Response.ObservedCostUsd is < 0)
        {
            throw new InvalidDataException("Observed cost cannot be negative; use null when it is unavailable.");
        }
    }

    private static string ComputePacketFingerprint(CalibrationPacket packet)
        => Sha256(JsonSerializer.SerializeToUtf8Bytes(packet with { Fingerprint = string.Empty }, JsonOptions));

    private static T Deserialize<T>(byte[] bytes, string description)
        => JsonSerializer.Deserialize<T>(bytes, JsonOptions)
           ?? throw new InvalidDataException($"The {description} was empty.");

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var length = new FileInfo(path).Length;
        if (length < 1 || length > maximumBytes)
        {
            throw new InvalidDataException($"Input '{path}' must be between 1 and {maximumBytes} bytes.");
        }

        return File.ReadAllBytes(path);
    }

    private static void WriteBounded<T>(string path, T value, int maximumBytes)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length > maximumBytes)
        {
            throw new InvalidDataException($"Serialized output exceeds the {maximumBytes}-byte limit.");
        }

        EnsureDirectory(path);
        File.WriteAllBytes(path, bytes);
    }

    private static void EnsureDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    private static void RejectDuplicateProperties(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        CheckElement(document.RootElement);
        static void CheckElement(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new InvalidDataException($"Duplicate JSON property '{property.Name}' is not allowed.");
                    }

                    CheckElement(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    CheckElement(item);
                }
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 64,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static string Sha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool FixedEquals(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void EnsureUnique(IEnumerable<string> values, string description)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            Require(value, description);
            if (!seen.Add(value))
            {
                throw new InvalidDataException($"Duplicate {description} '{value}' is not allowed.");
            }
        }
    }

    private static void Require(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096)
        {
            throw new InvalidDataException($"{description} must be non-empty and at most 4096 characters.");
        }
    }
}
