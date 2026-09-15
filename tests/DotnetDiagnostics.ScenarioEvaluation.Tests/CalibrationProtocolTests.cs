using System.Globalization;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class CalibrationProtocolTests
{
    [Fact]
    public void Validate_AcceptsBoundedFifteenSlotProtocol()
    {
        var protocol = CreateProtocol();

        var action = () => CalibrationProtocols.Validate(protocol);

        action.Should().NotThrow();
        protocol.Slots.Should().HaveCount(15);
        protocol.Slots.Count(slot => slot.Kind == CalibrationProtocolSlotKind.Live).Should().Be(12);
        protocol.Slots.Count(slot =>
            slot.Kind == CalibrationProtocolSlotKind.AuthoredEditedReplay).Should().Be(3);
        protocol.Slots.Where(slot => slot.Kind == CalibrationProtocolSlotKind.Live)
            .Sum(slot => slot.Budget!.MaximumModelTurns).Should().Be(48);
    }

    [Fact]
    public void Validate_RejectsStaleProtocolFingerprint()
    {
        var protocol = CreateProtocol() with { RubricId = "changed-after-freeze" };

        var action = () => CalibrationProtocols.Validate(protocol);

        action.Should().Throw<InvalidDataException>().WithMessage("*fingerprint*");
    }

    [Fact]
    public void Validate_RejectsPublicHeldoutDefinition()
    {
        var protocol = CreateProtocol();
        var slots = protocol.Slots.ToArray();
        slots[6] = slots[6] with
        {
            DefinitionVisibility = CalibrationDefinitionVisibility.Public,
            WorkloadFamily = "leaked-family",
            PublicWorkloadParameters = new Dictionary<string, string> { ["endpoint"] = "/leaked" },
        };
        protocol = Rehash(protocol with { Slots = slots });

        var action = () => CalibrationProtocols.Validate(protocol);

        action.Should().Throw<InvalidDataException>().WithMessage("*Heldout slots must be opaque*");
    }

    [Fact]
    public void Validate_RejectsMissingEvidenceQualityCoverage()
    {
        var protocol = CreateProtocol();
        var slots = protocol.Slots
            .Select(slot => slot with
            {
                EvidenceQualityMarkers = slot.EvidenceQualityMarkers
                    .Select(marker => marker == CalibrationEvidenceQualityMarker.CaptureWindowLimited
                        ? CalibrationEvidenceQualityMarker.CompetingExplanation
                        : marker)
                    .ToArray(),
            })
            .ToArray();
        protocol = Rehash(protocol with { Slots = slots });

        var action = () => CalibrationProtocols.Validate(protocol);

        action.Should().Throw<InvalidDataException>().WithMessage("*evidence-quality marker*");
    }

    [Fact]
    public void ValidatePacket_RequiresExactProtocolBinding()
    {
        var protocol = CreateProtocol();
        var slot = protocol.Slots[0];
        var packet = Packet(protocol, slot);

        CalibrationProtocols.ValidatePacket(protocol, packet);

        var stale = packet with
        {
            Descriptor = packet.Descriptor with { ProtocolFingerprint = new string('f', 64) },
        };
        var action = () => CalibrationProtocols.ValidatePacket(protocol, stale);
        action.Should().Throw<InvalidDataException>().WithMessage("*fingerprints*");
    }

    [Fact]
    public void Validate_RejectsIndependentReviewSubsetWithoutHeldoutCase()
    {
        var protocol = CreateProtocol();
        var slots = protocol.Slots.Select(slot => slot with
        {
            RequiresIndependentReview = slot.Id is "dev-live-sync-01" or "dev-replay-threadpool-unknown",
        }).ToArray();
        protocol = Rehash(protocol with
        {
            Slots = slots,
            Review = protocol.Review with
            {
                IndependentSlotIds = ["dev-live-sync-01", "dev-replay-threadpool-unknown"],
            },
        });

        var action = () => CalibrationProtocols.Validate(protocol);

        action.Should().Throw<InvalidDataException>().WithMessage("*Independent-review slots*");
    }

    private static CalibrationProtocol CreateProtocol()
    {
        var budget = new AgentHarnessBudget(
            MaximumWallTimeSeconds: 45,
            MaximumToolCalls: 3,
            MaximumModelTurns: 4,
            MaximumCaptureSeconds: 10,
            MaximumInputTokens: 10_000,
            MaximumOutputTokens: 1_200,
            MaximumEstimatedCostUsd: 0.25m,
            MaximumResponseBytes: 131_072,
            MaximumArtifactBytes: 393_216);
        var slots = new List<CalibrationProtocolSlot>
        {
            PublicLive("dev-live-culture-01", "culture-lookup", 1, budget),
            PublicLive("dev-live-gc-01", "gc-storm", 1, budget),
            PublicLive("dev-live-lock-01", "lock-storm", 1, budget,
                CalibrationEvidenceQualityMarker.CompetingExplanation),
            PublicLive("dev-live-sync-01", "sync-over-async", 1, budget,
                CalibrationEvidenceQualityMarker.UnknownThreadPoolReason, independent: true),
            PublicLive("dev-live-sync-02", "sync-over-async", 2, budget,
                CalibrationEvidenceQualityMarker.CaptureWindowLimited),
            PublicLive("dev-live-healthy-01", "healthy-sync-over-async", 1, budget,
                CalibrationEvidenceQualityMarker.HealthyControl),
            Heldout("h-01", 1, budget, independent: true),
            Heldout("h-02", 1, budget),
            Heldout("h-03", 1, budget, CalibrationEvidenceQualityMarker.CompetingExplanation),
            Heldout("h-04", 1, budget, CalibrationEvidenceQualityMarker.UnknownThreadPoolReason),
            Heldout("h-05", 1, budget, CalibrationEvidenceQualityMarker.HealthyControl),
            Heldout("h-06", 2, budget, CalibrationEvidenceQualityMarker.CaptureWindowLimited),
            Replay(
                "dev-replay-threadpool-unknown",
                "unknown ThreadPool reason",
                CalibrationEvidenceQualityMarker.UnknownThreadPoolReason,
                independent: true),
            Replay(
                "dev-replay-loss",
                "loss and truncation",
                CalibrationEvidenceQualityMarker.LossOrTruncation),
            Replay(
                "dev-replay-contradictory",
                "contradictory and insufficient evidence",
                CalibrationEvidenceQualityMarker.ContradictoryOrInsufficientEvidence),
        };
        return Rehash(new CalibrationProtocol(
            CalibrationProtocols.CurrentSchemaVersion,
            "advisory-calibration-v1",
            string.Empty,
            "advisory-calibration-rubric-v1",
            new string('a', 64),
            DateTimeOffset.Parse("2026-09-15T00:00:00Z", CultureInfo.InvariantCulture),
            new CalibrationModelBaseline(
                "github-copilot-cli",
                "gpt-5.4-mini",
                "unavailable",
                "The provider does not expose an immutable model build identifier.",
                "GitHub Copilot CLI",
                "1.0.83"),
            new CalibrationProductBaseline(
                "dotnet-diagnostics-mcp",
                "0.25.0",
                new string('b', 64)),
            new CalibrationProtocolLimits(12, 3, 60),
            new CalibrationReviewPlan(
                2,
                ["dev-live-sync-01", "h-01", "dev-replay-threadpool-unknown"],
                true),
            new CalibrationHoldoutPlan(new string('c', 64), true, true, true),
            slots));
    }

    private static CalibrationProtocolSlot PublicLive(
        string id,
        string family,
        int repetition,
        AgentHarnessBudget budget,
        CalibrationEvidenceQualityMarker marker = CalibrationEvidenceQualityMarker.CompetingExplanation,
        bool independent = false)
        => new(
            id,
            CalibrationPartition.Development,
            CalibrationProtocolSlotKind.Live,
            CalibrationProvenanceKind.LiveModel,
            CalibrationDefinitionVisibility.Public,
            family,
            $"public://{family}/{repetition}",
            new Dictionary<string, string> { ["variant"] = repetition.ToString(CultureInfo.InvariantCulture) },
            repetition,
            budget,
            [marker],
            independent);

    private static CalibrationProtocolSlot Heldout(
        string id,
        int repetition,
        AgentHarnessBudget budget,
        CalibrationEvidenceQualityMarker marker = CalibrationEvidenceQualityMarker.CompetingExplanation,
        bool independent = false)
        => new(
            id,
            CalibrationPartition.Heldout,
            CalibrationProtocolSlotKind.Live,
            CalibrationProvenanceKind.LiveModel,
            CalibrationDefinitionVisibility.PrivateCommitted,
            null,
            $"private://heldout/{id}",
            null,
            repetition,
            budget,
            [marker],
            independent);

    private static CalibrationProtocolSlot Replay(
        string id,
        string perturbation,
        CalibrationEvidenceQualityMarker marker,
        bool independent = false)
        => new(
            id,
            CalibrationPartition.Development,
            CalibrationProtocolSlotKind.AuthoredEditedReplay,
            CalibrationProvenanceKind.AuthoredEditedReplay,
            CalibrationDefinitionVisibility.Public,
            "edited-replay",
            $"public://edited-replay/{id}",
            new Dictionary<string, string> { ["perturbation"] = perturbation },
            1,
            null,
            [marker],
            independent);

    private static CalibrationProtocol Rehash(CalibrationProtocol protocol)
        => protocol with { ProtocolFingerprint = CalibrationProtocols.ComputeFingerprint(protocol) };

    private static CalibrationPacket Packet(
        CalibrationProtocol protocol,
        CalibrationProtocolSlot slot)
        => new(
            CalibrationPackets.CurrentPacketSchemaVersion,
            new string('1', 64),
            new string('2', 64),
            "run-1",
            new string('3', 64),
            new CalibrationCaseDescriptor(
                protocol.ProtocolId,
                protocol.RubricFingerprint,
                slot.Id,
                slot.Partition,
                slot.ProvenanceKind,
                "capture-1",
                new string('4', 64),
                "SYNTHETIC TEST packet.",
                protocol.ProtocolFingerprint),
            new CalibrationGenerationProvenance(
                slot.ProvenanceKind,
                "real-model",
                protocol.Model.Provider,
                protocol.Model.Model,
                protocol.Model.ModelVersion,
                protocol.Product.Commit),
            DateTimeOffset.Parse("2026-09-15T00:00:00Z", CultureInfo.InvariantCulture),
            CalibrationReviewability.Reviewable,
            "SYNTHETIC TEST packet.",
            null!,
            [],
            [],
            "SYNTHETIC TEST uncertainty.",
            [],
            [],
            [],
            new CalibrationUsage(null, null, null, false));
}
