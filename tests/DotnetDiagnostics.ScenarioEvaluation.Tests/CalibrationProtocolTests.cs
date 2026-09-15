using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    public void FrozenProtocol_LoadsAndMatchesThePublishedMatrix()
    {
        var path = CalibrationProtocols.ProtocolPath(
            "Calibration",
            "advisory-calibration-v1.protocol.json");

        var protocol = CalibrationProtocols.Load(path);

        protocol.ProtocolId.Should().Be("advisory-calibration-v1");
        protocol.Model.TransportVersion.Should().Be("GitHub Copilot CLI 1.0.83.");
        protocol.Slots.Should().HaveCount(15);
        protocol.Slots.Where(slot => slot.Partition == CalibrationPartition.Heldout)
            .Should().OnlyContain(slot =>
                slot.DefinitionVisibility == CalibrationDefinitionVisibility.PrivateCommitted
                && slot.WorkloadFamily == null
                && slot.PublicWorkloadParameters == null
                && slot.EvidenceQualityMarkers.Count == 0);
        var rubricPath = Path.GetFullPath(
            "../../../../../docs/advisory-agent-calibration.md",
            AppContext.BaseDirectory);
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(rubricPath)))
            .Should().Be(protocol.RubricFingerprint);
        var manifest = CalibrationProtocols.ResolveWorkload(
            protocol,
            "dev-live-healthy-01");

        manifest.Id.Should().Be("healthy-sync-over-async");
        manifest.Workload.Parameters.Should().Equal(
            protocol.Slots.Single(slot => slot.Id == "dev-live-healthy-01")
                .PublicWorkloadParameters!);
    }

    [Fact]
    public void Validate_RejectsStaleProtocolFingerprint()
    {
        var protocol = CreateProtocol() with { RubricId = "changed-after-freeze" };

        var action = () => CalibrationProtocols.Validate(protocol);

        action.Should().Throw<InvalidDataException>().WithMessage("*fingerprint*");
    }

    [Fact]
    public void ComputeCanonicalJsonFingerprint_NormalizesPlatformLineEndings()
    {
        var lf = Encoding.UTF8.GetBytes("{\n  \"value\": \"line endings\"\n}");
        var crlf = Encoding.UTF8.GetBytes("{\r\n  \"value\": \"line endings\"\r\n}");

        CalibrationProtocols.ComputeCanonicalJsonFingerprint(crlf)
            .Should().Be(CalibrationProtocols.ComputeCanonicalJsonFingerprint(lf));
    }

    [EnvironmentRequiredFact(
        "DOTNET_DIAGNOSTICS_CALIBRATION_PRIVATE_DEFINITION",
        "The evaluator-private heldout definition is intentionally unavailable in CI.")]
    [Trait("Category", "ScenarioCalibrationPrivate")]
    public void ResolveWorkload_VerifiesPrivateDefinitionCommitment()
    {
        var protocol = CalibrationProtocols.Load(CalibrationProtocols.ProtocolPath(
            "Calibration",
            "advisory-calibration-v1.protocol.json"));

        var manifest = CalibrationProtocols.ResolveWorkload(
            protocol,
            "h-01",
            Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_CALIBRATION_PRIVATE_DEFINITION"));

        manifest.Workload.Parameters.Should().NotBeEmpty();
        manifest.Version.Should().Contain("heldout");
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
            EvidenceQualityMarkers = [CalibrationEvidenceQualityMarker.CompetingExplanation],
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

        var wrongTransport = packet with
        {
            Generation = packet.Generation with { TransportVersion = "different" },
        };
        action = () => CalibrationProtocols.ValidatePacket(protocol, wrongTransport);
        action.Should().Throw<InvalidDataException>().WithMessage("*baseline*");

        var relabeled = packet with
        {
            Generation = packet.Generation with { EvidenceKind = "scripted" },
        };
        action = () => CalibrationProtocols.ValidatePacket(protocol, relabeled);
        action.Should().Throw<InvalidDataException>().WithMessage("*evidence kind*");
    }

    [Fact]
    public void ValidateSourceReport_AllowsProtocolBoundAuthoredReplay()
    {
        var protocol = CreateProtocol();
        var slot = protocol.Slots.First(value =>
            value.Kind == CalibrationProtocolSlotKind.AuthoredEditedReplay);
        var descriptor = new CalibrationCaseDescriptor(
            protocol.ProtocolId,
            protocol.RubricFingerprint,
            slot.Id,
            slot.Partition,
            slot.ProvenanceKind,
            "edited-replay-1",
            new string('d', 64),
            "SYNTHETIC TEST replay.",
            protocol.ProtocolFingerprint);
        var report = new AgentHarnessReport(
            BlindedAgentHarness.CurrentReportSchemaVersion,
            "edited-replay-run",
            "authored-edited-replay",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            new AgentHarnessBudget(),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            [],
            [],
            null,
            0,
            null,
            null,
            null,
            0,
            []);

        var action = () => CalibrationProtocols.ValidateSourceReport(protocol, descriptor, report);

        action.Should().NotThrow();

        action = () => CalibrationProtocols.ValidateSourceReport(
            protocol,
            descriptor,
            report with { EvidenceKind = "scripted" });
        action.Should().Throw<InvalidDataException>().WithMessage("*evidence kind*");
    }

    [Fact]
    public void ValidateCompletePacketSet_RequiresOneFreshCapturePerSlot()
    {
        var protocol = CreateProtocol();
        var packets = protocol.Slots.Select((slot, index) =>
            Packet(protocol, slot, index)).ToArray();

        CalibrationProtocols.ValidateCompletePacketSet(protocol, packets);

        packets[1] = packets[1] with
        {
            Descriptor = packets[1].Descriptor with
            {
                CaptureId = packets[0].Descriptor.CaptureId,
            },
        };
        var action = () => CalibrationProtocols.ValidateCompletePacketSet(protocol, packets);
        action.Should().Throw<InvalidDataException>().WithMessage("*capture id*");
    }

    [Fact]
    public void ValidateCompletePacketSet_RejectsMissingDenominator()
    {
        var protocol = CreateProtocol();
        var packets = protocol.Slots.Skip(1).Select((slot, index) =>
            Packet(protocol, slot, index)).ToArray();

        var action = () => CalibrationProtocols.ValidateCompletePacketSet(protocol, packets);

        action.Should().Throw<InvalidDataException>().WithMessage("*exactly one packet*");
    }

    [Fact]
    public void ValidatePacket_RequiresCaptureHashForEveryFrozenSlot()
    {
        var protocol = CreateProtocol();
        var packet = Packet(protocol, protocol.Slots[0]) with
        {
            Descriptor = Packet(protocol, protocol.Slots[0]).Descriptor with { CaptureHash = null },
        };

        var action = () => CalibrationProtocols.ValidatePacket(protocol, packet);

        action.Should().Throw<InvalidDataException>().WithMessage("*capture hash*");
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

    internal static CalibrationProtocol CreateProtocolForPacketTests()
        => CreateProtocol();

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
            Heldout("h-03", 1, budget),
            Heldout("h-04", 1, budget),
            Heldout("h-05", 1, budget),
            Heldout("h-06", 1, budget),
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
                "github-copilot-cli",
                "GitHub Copilot CLI 1.0.83"),
            new CalibrationProductBaseline(
                "dotnet-diagnostics-mcp",
                "0.25.0",
                new string('b', 40)),
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
            [],
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
        CalibrationProtocolSlot slot,
        int index = 0)
        => new(
            CalibrationPackets.CurrentPacketSchemaVersion,
            new string('1', 64),
            new string('2', 64),
            $"run-{index}",
            new string('3', 64),
            new CalibrationCaseDescriptor(
                protocol.ProtocolId,
                protocol.RubricFingerprint,
                slot.Id,
                slot.Partition,
                slot.ProvenanceKind,
                $"capture-{index}",
                Convert.ToHexStringLower(SHA256.HashData(
                    Encoding.UTF8.GetBytes(index.ToString(CultureInfo.InvariantCulture)))),
                "SYNTHETIC TEST packet.",
                protocol.ProtocolFingerprint),
            new CalibrationGenerationProvenance(
                slot.ProvenanceKind,
                slot.Kind == CalibrationProtocolSlotKind.Live
                    ? "real-model"
                    : "authored-edited-replay",
                protocol.Model.Provider,
                protocol.Model.Model,
                protocol.Model.ModelVersion,
                protocol.Product.Commit,
                protocol.Model.Transport,
                protocol.Model.TransportVersion),
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
