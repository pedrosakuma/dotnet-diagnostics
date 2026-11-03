using FluentAssertions;
using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class AllocationDirectionalTests
{
    private static readonly string EmpiricalFixturePath = ScenarioManifestLoader.ScenarioPath(
        "Fixtures",
        "AllocationDirectional",
        "allocation-directional-baseline-current-v1.windows-x64.evidence.json");
    private static readonly string EmpiricalArtifactDirectory = RepositoryPath(
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional");

    [Fact]
    public void FrozenProtocol_PredeclaresExecutionAndInternalOnlyGates()
    {
        var path = ScenarioManifestLoader.ScenarioPath(
            "Fixtures",
            "AllocationDirectional",
            "allocation-directional-baseline-current-v1.protocol.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        root.GetProperty("status").GetString().Should().Be("frozen");
        DateTimeOffset.TryParse(root.GetProperty("frozenAtUtc").GetString(), out _)
            .Should().BeTrue();
        root.GetProperty("prerequisiteCommit").GetString().Should().Be(
            AllocationDirectionalProtocol.SourceBaseCommit);
        root.GetProperty("sourceBaseCommit").GetString().Should().Be(
            AllocationDirectionalProtocol.SourceBaseCommit);
        var frozenInputs = root.GetProperty("frozenInputs");
        frozenInputs.GetProperty("textHashCanonicalization").GetString().Should().Be(
            AllocationDirectionalProtocol.TextHashCanonicalization);
        var sourceHashes = frozenInputs.GetProperty("sourceFilesSha256");
        sourceHashes.EnumerateObject().Should().HaveCount(
            AllocationDirectionalProtocol.FrozenSourcePaths.Count);
        foreach (var sourcePath in AllocationDirectionalProtocol.FrozenSourcePaths)
        {
            sourceHashes.GetProperty(sourcePath).GetString().Should()
                .MatchRegex("^[0-9a-f]{64}$");
        }
        root.GetProperty("modelVisibleMeasurementQualityFields").GetArrayLength()
            .Should().Be(0);
        root.GetProperty("requiredIndependentPairs").GetInt32().Should().Be(
            AllocationDirectionalBounds.RequiredPairs);
        var inventory = root.GetProperty("inventory");
        inventory.GetProperty("totalPairs").GetInt32().Should().Be(
            AllocationDirectionalCapturePlan.TotalPairs);
        inventory.GetProperty("liveMcpPairs").GetInt32().Should().Be(
            AllocationDirectionalCapturePlan.LiveMcpPairs);
        inventory.GetProperty("controlledProjectionPairs").GetInt32().Should().Be(
            AllocationDirectionalCapturePlan.ControlledProjectionPairs);
        inventory.GetProperty("liveMcpSides").GetInt32().Should().Be(
            AllocationDirectionalCapturePlan.LiveMcpSides);
        inventory.GetProperty("controlledProjectionSides").GetInt32().Should().Be(
            AllocationDirectionalCapturePlan.ControlledProjectionSides);
        root.GetProperty("capture").GetProperty("topN").GetInt32().Should().Be(
            AllocationDirectionalBounds.MaximumRankedRows);
        root.GetProperty("capture").GetProperty("durationSeconds").GetInt32()
            .Should().Be(AllocationDirectionalCapturePlan.CaptureDurationSeconds);
        root.GetProperty("capture").GetProperty("armDelayMilliseconds").GetInt32()
            .Should().Be(AllocationDirectionalCapturePlan.ArmDelayMilliseconds);
        root.GetProperty("capture").GetProperty("workloadMilliseconds").GetInt32()
            .Should().Be(AllocationDirectionalCapturePlan.WorkloadMilliseconds);
        root.GetProperty("capture").GetProperty("defaultWarmupMilliseconds").GetInt32()
            .Should().Be(AllocationDirectionalCapturePlan.DefaultWarmupMilliseconds);
        root.GetProperty("capture").GetProperty("attemptTimeoutSeconds").GetInt32()
            .Should().Be(AllocationDirectionalCapturePlan.AttemptTimeoutSeconds);
        root.GetProperty("capture").GetProperty("mcpCompletionTimeoutSeconds").GetInt32()
            .Should().Be(AllocationDirectionalCapturePlan.McpCompletionTimeoutSeconds);
        var targetEnvironment = root.GetProperty("capture")
            .GetProperty("targetRuntimeEnvironment");
        targetEnvironment.EnumerateObject().Should().HaveCount(
            AllocationDirectionalCapturePlan.RuntimeEnvironment.Count);
        foreach (var variable in AllocationDirectionalCapturePlan.RuntimeEnvironment)
        {
            targetEnvironment.GetProperty(variable.Key).GetString().Should()
                .Be(variable.Value);
        }
        root.GetProperty("internalOnlyGates")
            .GetProperty("maximumAllowedAaRelativeDelta").GetDecimal()
            .Should().Be(AllocationDirectionalBounds.MaximumAllowedAaRelativeDelta);
        root.GetProperty("internalOnlyGates")
            .GetProperty("maximumAllowedAaTypeShareMovement").GetDecimal()
            .Should().Be(AllocationDirectionalBounds.MaximumAllowedAaShareMovement);
        root.GetProperty("internalOnlyGates")
            .GetProperty("maximumAllowedAaSiteShareMovement").GetDecimal()
            .Should().Be(AllocationDirectionalBounds.MaximumAllowedAaShareMovement);
        root.GetProperty("internalOnlyGates")
            .GetProperty("minimumThroughputMultiplier").GetDecimal()
            .Should().Be(AllocationDirectionalCapturePlan.MinimumThroughputMultiplier);
        var capturePlan = root.GetProperty("capturePlan").EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("pairPrefix").GetString()!,
                item => item,
                StringComparer.Ordinal);
        capturePlan.Should().HaveCount(AllocationDirectionalCapturePlan.Entries.Count);
        foreach (var entry in AllocationDirectionalCapturePlan.Entries)
        {
            var item = capturePlan[entry.PairPrefix];
            item.GetProperty("scenario").GetString().Should().Be(
                entry.Scenario.ToString());
            item.GetProperty("comparisonAxis").GetString().Should().Be(
                entry.ComparisonAxis);
            AssertSide(item.GetProperty("baseline"), entry.Baseline);
            AssertSide(item.GetProperty("current"), entry.Current);
        }
        root.TryGetProperty("workloads", out _).Should().BeFalse(
            "capturePlan is the single authoritative workload declaration");
        root.GetProperty("failurePolicy").GetString().Should().Contain("at most once")
            .And.Contain("No favorable retry");
        File.ReadAllText(path).ToLowerInvariant()
            .Should().NotContain("median")
            .And.NotContain("jaccard")
            .And.NotContain("spearman");
    }

    [Fact]
    public void FrozenProtocol_CannotBeRerunAfterHarnessCorrection()
    {
        var validate = AllocationDirectionalProtocol.ValidateFrozen;

        validate.Should().Throw<InvalidDataException>()
            .WithMessage("*Frozen source hash mismatch*");
    }

    [Fact]
    public void KnownShapes_GoOnlyWhenDirectionalEvidencePassesEveryControl()
    {
        var report = AllocationDirectionalHarness.Evaluate(SyntheticFixture());

        report.Decision.Should().Be(AllocationDirectionalDecision.Inconclusive);
        report.NumericGatePassed.Should().BeTrue();
        report.BlindedEvaluationRequired.Should().BeTrue();
        report.Scenarios.Should().OnlyContain(result =>
            result.ExpectedBehaviorObserved
            && !result.UnsafeDirectionalEvidenceObserved);
        report.Limitations.Should().Contain(item =>
            item.Contains("not a model-visible field", StringComparison.Ordinal));
    }

    [Fact]
    public void FixedStrideBias_ForcesNoGoWithoutPromotingQualityMetadata()
    {
        var fixture = SyntheticFixture();
        var pairs = fixture.Pairs.Select(pair =>
            pair.Scenario == AllocationDirectionalScenario.FixedStrideBias
                ? pair with
                {
                    Current = pair.Current with
                    {
                        Types =
                        [
                            new("System.Byte[]", 1_000, 1),
                            new("System.Char[]", 9_000, 9),
                        ],
                    },
                }
                : pair).ToArray();

        var report = AllocationDirectionalHarness.Evaluate(fixture with { Pairs = pairs });

        report.Decision.Should().Be(AllocationDirectionalDecision.NoGo);
        report.Scenarios.Single(result =>
                result.Scenario == AllocationDirectionalScenario.FixedStrideBias)
            .UnsafeDirectionalEvidenceObserved.Should().BeTrue();
        AllocationDirectionalJson.SerializeReport(report)
            .Should().NotContain("median")
            .And.NotContain("jaccard")
            .And.NotContain("spearman");
    }

    [Fact]
    public void FailedPairs_RemainInDenominatorAndCanMakeResultInconclusive()
    {
        var fixture = SyntheticFixture();
        var first = fixture.Pairs.First(pair =>
            pair.Scenario == AllocationDirectionalScenario.TrueIncrease);
        var pairs = fixture.Pairs.Select(pair =>
            pair.PairId == first.PairId
                ? pair with
                {
                    Current = pair.Current with
                    {
                        Outcome = AllocationDirectionalAttemptOutcome.Failed,
                        FailureCategory = "collection_failed",
                    },
                }
                : pair).ToArray();

        var report = AllocationDirectionalHarness.Evaluate(fixture with { Pairs = pairs });
        var result = report.Scenarios.Single(value =>
            value.Scenario == AllocationDirectionalScenario.TrueIncrease);

        report.Decision.Should().Be(AllocationDirectionalDecision.Inconclusive);
        result.AttemptedPairs.Should().Be(AllocationDirectionalBounds.RequiredPairs);
        result.ValidPairs.Should().Be(AllocationDirectionalBounds.RequiredPairs - 1);
    }

    [Fact]
    public void MissingMixAttribution_IsInconclusiveRatherThanMovementOrNoGo()
    {
        var fixture = SyntheticFixture();
        var pairs = fixture.Pairs.Select(pair =>
            pair.Scenario == AllocationDirectionalScenario.MixShiftStableTotal
                ? pair with
                {
                    Current = pair.Current with { Sites = [] },
                }
                : pair).ToArray();

        var report = AllocationDirectionalHarness.Evaluate(fixture with { Pairs = pairs });
        var result = report.Scenarios.Single(value =>
            value.Scenario == AllocationDirectionalScenario.MixShiftStableTotal);

        report.Decision.Should().Be(AllocationDirectionalDecision.Inconclusive);
        result.ExpectedBehaviorObserved.Should().BeFalse();
        result.UnsafeDirectionalEvidenceObserved.Should().BeFalse();
    }

    [Theory]
    [InlineData(AllocationDirectionalScenario.ChangedWorkload)]
    [InlineData(AllocationDirectionalScenario.MissingOrZeroWeight)]
    public void FailedControlSides_NeverSatisfyExpectedBehavior(
        AllocationDirectionalScenario scenario)
    {
        var fixture = SyntheticFixture();
        var first = fixture.Pairs.First(pair => pair.Scenario == scenario);
        var pairs = fixture.Pairs.Select(pair =>
            pair.PairId == first.PairId
                ? pair with
                {
                    Current = pair.Current with
                    {
                        Outcome = AllocationDirectionalAttemptOutcome.Failed,
                        FailureCategory = "mcp_transport_failed",
                    },
                }
                : pair).ToArray();

        var report = AllocationDirectionalHarness.Evaluate(fixture with { Pairs = pairs });
        var result = report.Scenarios.Single(value => value.Scenario == scenario);

        report.Decision.Should().Be(AllocationDirectionalDecision.Inconclusive);
        result.ExpectedBehaviorObserved.Should().BeFalse();
    }

    [Fact]
    public void EmpiricalFixture_PreservesInconclusiveDecisionAndEveryDenominator()
    {
        var fixture = AllocationDirectionalJson.ReadFixture(EmpiricalFixturePath);
        var protocolPath = ScenarioManifestLoader.ScenarioPath(
            "Fixtures",
            "AllocationDirectional",
            "allocation-directional-baseline-current-v1.protocol.json");

        fixture.CanonicalProtocolSha256.Should().Be(
            AllocationDirectionalProtocol.Sha256(protocolPath));
        fixture.ProvenanceCorrections.Should().ContainSingle(item =>
            item.Contains("checkout-independent", StringComparison.Ordinal));
        fixture.Pairs.Should().HaveCount(AllocationDirectionalCapturePlan.TotalPairs);
        var observations = fixture.Pairs.SelectMany(pair =>
            new[] { pair.Baseline, pair.Current }).ToArray();
        observations.Count(observation =>
                observation.Origin == AllocationDirectionalOrigin.LiveMcp)
            .Should().Be(AllocationDirectionalCapturePlan.LiveMcpSides);
        observations.Count(observation =>
                observation.Origin == AllocationDirectionalOrigin.ControlledProjection)
            .Should().Be(AllocationDirectionalCapturePlan.ControlledProjectionSides);
        observations.Where(observation =>
                observation.Origin == AllocationDirectionalOrigin.LiveMcp)
            .Should().OnlyContain(observation =>
                observation.Outcome == AllocationDirectionalAttemptOutcome.Failed);

        var report = AllocationDirectionalHarness.Evaluate(fixture);

        report.Decision.Should().Be(AllocationDirectionalDecision.Inconclusive);
        report.NumericGatePassed.Should().BeFalse();
        report.BlindedEvaluationRequired.Should().BeFalse();
        report.Scenarios.Should().BeEmpty();
        report.Limitations.Should().ContainSingle(item =>
            item.Contains("Healthy A/A", StringComparison.Ordinal));
    }

    [Fact]
    public void EmpiricalArtifacts_HavePortableDocumentedFingerprints()
    {
        AssertFingerprint(
            "allocation-directional-baseline-current-v1.protocol.json",
            14_564,
            "ef12d0f6caa48cc2e4be51317f19142094e3d3e8c75af5b8ffd4a7bc2610713a");
        AssertFingerprint(
            "allocation-directional-baseline-current-v1.windows-x64.evidence.execution.json",
            65_282,
            "06b917e17524f9f4b2ffb4ebfa0dd504327a49a2fa59b2fb8efc37df92bc7dfc");
        AssertFingerprint(
            "allocation-directional-baseline-current-v1.windows-x64.evidence.json",
            65_727,
            "c251312571df99ea1a4d2e5c10b53c8adf4a2347ea2a24b0ef67dedbde061c50");
        AssertFingerprint(
            "allocation-directional-baseline-current-v1.windows-x64.evidence.ledger.jsonl",
            18_657,
            "087402d4a6813092a4c3f250b34713ba8c2e263eb269cec207036daa7dcaa690");
        AssertFingerprint(
            "allocation-directional-baseline-current-v1.windows-x64.evidence.report.json",
            483,
            "12ec9b0ecf6f551111af899e50af6ce9e635f3be2a6b7b505dea3773e058e7c4");
    }

    private static AllocationDirectionalFixture SyntheticFixture()
    {
        var pairs = new List<AllocationDirectionalPair>();
        for (var index = 1; index <= AllocationDirectionalBounds.RequiredPairs; index++)
        {
            pairs.Add(Pair(
                AllocationDirectionalScenario.HealthyAa,
                $"aa-{index}",
                Observation($"aa-b-{index}", 100, 10_000, "primary", "steady"),
                Observation($"aa-c-{index}", 100, 10_200, "primary", "steady")));
            pairs.Add(Pair(
                AllocationDirectionalScenario.TrueIncrease,
                $"increase-{index}",
                Observation($"inc-b-{index}", 100, 10_000, "volume", "steady"),
                Observation($"inc-c-{index}", 100, 20_000, "volume", "steady")));
            pairs.Add(Pair(
                AllocationDirectionalScenario.MixShiftStableTotal,
                $"mix-{index}",
                Observation(
                    $"mix-b-{index}",
                    100,
                    10_000,
                    "mix",
                    "steady",
                    payload: 1_000,
                    types: [new("Byte", 8_000, 8), new("Char", 2_000, 2)],
                    sites: [new("Primary", 8_000, 8), new("Shared", 2_000, 2)]),
                Observation(
                    $"mix-c-{index}",
                    100,
                    10_100,
                    "mix",
                    "steady",
                    payload: 1_000,
                    types: [new("Int", 8_080, 8), new("Char", 2_020, 2)],
                    sites: [new("Changed", 8_080, 8), new("Shared", 2_020, 2)])));
            pairs.Add(Pair(
                AllocationDirectionalScenario.ThroughputIncreaseStablePerOperation,
                $"throughput-aa-{index}",
                Observation($"tp-aa-b-{index}", 200, 20_000, "throughput", "steady", duration: 4),
                Observation($"tp-aa-c-{index}", 200, 20_200, "throughput", "aa", duration: 4)));
            pairs.Add(Pair(
                AllocationDirectionalScenario.ThroughputIncreaseStablePerOperation,
                $"throughput-intensity-{index}",
                Observation($"tp-b-{index}", 100, 10_000, "throughput", "steady", duration: 4),
                Observation($"tp-c-{index}", 200, 20_200, "throughput", "intensity", duration: 4)));
            pairs.Add(Pair(
                AllocationDirectionalScenario.LowerAllocationStableRetained,
                $"lower-{index}",
                Observation($"low-b-{index}", 100, 20_000, "retained", "steady", retained: 2_000),
                Observation($"low-c-{index}", 100, 10_000, "retained", "steady", retained: 2_000)));
            pairs.Add(Pair(
                AllocationDirectionalScenario.ChangedWorkload,
                $"changed-{index}",
                Observation($"changed-b-{index}", 100, 10_000, "workload-a", "steady"),
                Observation($"changed-c-{index}", 100, 20_000, "workload-b", "steady")));
            pairs.Add(Pair(
                AllocationDirectionalScenario.FixedStrideBias,
                $"stride-aa-{index}",
                Observation($"stride-b-{index}", 100, 10_000, "stride", "steady"),
                Observation($"stride-c-{index}", 100, 10_100, "stride", "aa")));
            pairs.Add(Pair(
                AllocationDirectionalScenario.FixedStrideBias,
                $"stride-reorder-{index}",
                Observation($"stride-rb-{index}", 100, 10_000, "stride", "steady"),
                Observation($"stride-rc-{index}", 100, 10_100, "stride", "reorder")));
            pairs.Add(Pair(
                AllocationDirectionalScenario.PartialAttribution,
                $"partial-{index}",
                Observation($"partial-b-{index}", 100, 10_000, "partial", "steady"),
                Observation(
                    $"partial-c-{index}",
                    100,
                    20_000,
                    "partial",
                    "steady",
                    attribution: "partial",
                    sites: [])));
            pairs.Add(Pair(
                AllocationDirectionalScenario.MissingOrZeroWeight,
                $"missing-{index}",
                Observation($"missing-b-{index}", 100, 10_000, "missing", "steady"),
                Observation(
                    $"missing-c-{index}",
                    0,
                    0,
                    "missing",
                    "steady",
                    workers: 0)));
            pairs.Add(Pair(
                AllocationDirectionalScenario.WarmupTransientConverges,
                $"warmup-early-{index}",
                Observation($"warm-b-e-{index}", 100, 10_000, "warmup", "steady"),
                Observation($"warm-c-e-{index}", 100, 20_000, "warmup", "early")));
            pairs.Add(Pair(
                AllocationDirectionalScenario.WarmupTransientConverges,
                $"warmup-late-{index}",
                Observation($"warm-b-l-{index}", 100, 10_000, "warmup", "steady"),
                Observation($"warm-c-l-{index}", 100, 10_100, "warmup", "converged")));
        }

        return new(
            AllocationDirectionalJson.CurrentSchemaVersion,
            AllocationDirectionalHarness.ProtocolId,
            AllocationDirectionalHarness.ProtocolVersion,
            "synthetic-protocol-sha256",
            "synthetic-canonical-protocol-sha256",
            [],
            "2026-11-03T15:48:04Z",
            AllocationDirectionalProtocol.SourceBaseCommit,
            "synthetic",
            pairs);
    }

    private static string RepositoryPath(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "DotnetDiagnostics.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName
                ?? throw new DirectoryNotFoundException(
                    "Could not locate the repository root."),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void AssertFingerprint(
        string fileName,
        int expectedBytes,
        string expectedSha256)
    {
        var path = Path.Combine(EmpiricalArtifactDirectory, fileName);
        AllocationDirectionalProtocol.CanonicalByteCount(path).Should().Be(expectedBytes);
        AllocationDirectionalProtocol.Sha256(path).Should().Be(expectedSha256);
    }

    private static AllocationDirectionalPair Pair(
        AllocationDirectionalScenario scenario,
        string id,
        AllocationDirectionalObservation baseline,
        AllocationDirectionalObservation current)
        => new(id, scenario, "frozen-axis", baseline, current);

    private static void AssertSide(
        JsonElement element,
        AllocationDirectionalPlannedSide expected)
    {
        element.GetProperty("workloadIdentity").GetString().Should()
            .Be(expected.WorkloadIdentity);
        element.GetProperty("shape").GetString().Should().Be(expected.Shape);
        element.GetProperty("workers").GetInt32().Should().Be(expected.Workers);
        element.GetProperty("phase").GetString().Should().Be(expected.Phase);
        element.GetProperty("expectedPayloadBytesPerOperation").GetInt64().Should()
            .Be(expected.ExpectedPayloadBytesPerOperation);
        element.GetProperty("warmupMilliseconds").GetInt32().Should()
            .Be(expected.WarmupMilliseconds);
        element.GetProperty("retainedBytes").GetInt32().Should()
            .Be(expected.RetainedBytes);
        element.GetProperty("driveWorkload").GetBoolean().Should()
            .Be(expected.DriveWorkload);
    }

    private static AllocationDirectionalObservation Observation(
        string id,
        long operations,
        long bytes,
        string workload,
        string phase,
        long payload = 1_000,
        long retained = 0,
        double duration = 4,
        string attribution = "complete",
        int workers = 1,
        IReadOnlyList<AllocationDirectionalRankedRow>? types = null,
        IReadOnlyList<AllocationDirectionalRankedRow>? sites = null)
        => new(
            id,
            AllocationDirectionalAttemptOutcome.Passed,
            AllocationDirectionalOrigin.ControlledProjection,
            workload,
            workload,
            phase,
            workers,
            duration,
            operations,
            payload,
            retained,
            bytes,
            Math.Max(1, bytes / 100),
            attribution,
            types ?? [new("Byte", bytes, Math.Max(1, bytes / 100))],
            sites ?? [new("Primary", bytes, Math.Max(1, bytes / 100))],
            null,
            []);
}
