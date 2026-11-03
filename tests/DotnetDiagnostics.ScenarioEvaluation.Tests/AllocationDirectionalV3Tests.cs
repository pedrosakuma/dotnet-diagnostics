using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class AllocationDirectionalV3Tests
{
    [Fact]
    public void DraftProtocol_DeclaresFreshCompleteInventoryAndReviewedContracts()
    {
        var root = AllocationDirectionalV3ProtocolDocument.Build(
            "draft",
            frozenAtUtc: null,
            includeFrozenHashes: false);

        root.GetProperty("protocolVersion").GetString().Should().Be("3.0.0");
        root.GetProperty("sourceBaseCommit").GetString().Should().Be(
            "9bf5701b879f7cdc85b93ec9efbf0e0cec76c548");
        root.GetProperty("prerequisites")
            .GetProperty("allocationErrorClassificationMerge").GetString()
            .Should().Be("9cb6966e249439925ce11cb5871b4f7d808dd9cc");
        root.GetProperty("modelVisibleMeasurementQualityFields").GetArrayLength()
            .Should().Be(0);
        root.GetProperty("requiredIndependentPairs").GetInt32().Should().Be(3);
        root.GetProperty("inventory").GetProperty("liveMcpPairs").GetInt32()
            .Should().Be(54);
        root.GetProperty("inventory").GetProperty("liveMcpSides").GetInt32()
            .Should().Be(108);
        root.GetProperty("inventory").GetProperty("totalPairs").GetInt32()
            .Should().Be(57);

        var capturePlan = root.GetProperty("capturePlan").EnumerateArray().ToArray();
        capturePlan.Should().HaveCount(18);
        capturePlan.Select(entry => entry.GetProperty("pairPrefix").GetString())
            .Should().OnlyHaveUniqueItems()
            .And.OnlyContain(prefix =>
                prefix != null && prefix.StartsWith("v3-", StringComparison.Ordinal));
        capturePlan.Single(entry =>
                entry.GetProperty("pairPrefix").GetString()
                    == "v3-throughput-effect")
            .GetProperty("baseline").GetProperty("shape").GetString()
            .Should().Be("primary-paced");
        capturePlan.Single(entry =>
                entry.GetProperty("pairPrefix").GetString()
                    == "v3-throughput-effect")
            .GetProperty("current").GetProperty("shape").GetString()
            .Should().Be("primary");
        capturePlan.Count(entry =>
                entry.GetProperty("scenario").GetString()
                    == nameof(AllocationDirectionalScenario.FixedStrideBias))
            .Should().Be(4);
        root.GetProperty("selectors").GetProperty("warmupPracticalFloor")
            .GetDecimal().Should().Be(0.10m);
        root.GetProperty("selectors").GetProperty("minimumInlierPairs")
            .GetInt32().Should().Be(2);
        root.GetProperty("failurePolicy").GetString().Should()
            .Contain("at most once")
            .And.Contain("No favorable retry");
        root.GetProperty("errorTaxonomy").GetProperty("permissionDenied")
            .GetString().Should().Contain("evidence-backed");
        root.GetProperty("frozenInputs").GetProperty("preservedArtifactsSha256")
            .EnumerateObject().Should().HaveCount(
                AllocationDirectionalV3Protocol.PreservedArtifactPaths.Count);
    }

    [Fact]
    public void SyntheticCompleteInventory_ReachesNumericGoOnly()
    {
        var report = AllocationDirectionalV3Harness.Evaluate(SyntheticFixture());

        report.NumericGatePassed.Should().BeTrue();
        report.BlindedEvaluationRequired.Should().BeTrue();
        report.BlindedEvaluationRan.Should().BeFalse();
        report.Decision.Should().Be(AllocationDirectionalDecision.Inconclusive);
        report.Scenarios.Should().OnlyContain(result =>
            result.Assessable
            && result.ExpectedBehaviorObserved
            && !result.UnsafeDirectionalEvidenceObserved);
        AllocationDirectionalJson.SerializeReport(report)
            .Should().Contain("No measurement-quality field is model-visible");
    }

    [Fact]
    public void ThroughputAndWarmupUseReviewedPairSummarySelectors()
    {
        var fixture = SyntheticFixture();
        var throughput = fixture.Pairs.Where(pair =>
            pair.Scenario
                == AllocationDirectionalScenario.ThroughputIncreaseStablePerOperation)
            .ToArray();
        var warmup = fixture.Pairs.Where(pair =>
            pair.Scenario == AllocationDirectionalScenario.WarmupTransientConverges)
            .ToArray();

        var throughputGate =
            AllocationDirectionalCalibrationSelector.EvaluateThroughput(throughput);
        var warmupGate =
            AllocationDirectionalCalibrationSelector.EvaluateWarmup(warmup);

        throughputGate.Passed.Should().BeTrue();
        throughputGate.ThroughputMultipliers.Median.Should().Be(2m);
        warmupGate.Passed.Should().BeTrue();
        warmupGate.EarlyDecisionFloor.Should().Be(0.10m);
        warmupGate.EarlyRelativeDeltas.Median.Should().Be(0.20m);
    }

    [Fact]
    public void V3ExecutionConstantsMatchTheFrozenV3Plan()
    {
        AllocationDirectionalCapturePlan.CaptureDurationSeconds.Should().Be(
            AllocationDirectionalV3CapturePlan.CaptureDurationSeconds);
        AllocationDirectionalCapturePlan.ArmDelayMilliseconds.Should().Be(
            AllocationDirectionalV3CapturePlan.ArmDelayMilliseconds);
        AllocationDirectionalCapturePlan.WorkloadMilliseconds.Should().Be(
            AllocationDirectionalV3CapturePlan.WorkloadMilliseconds);
        AllocationDirectionalCapturePlan.TopN.Should().Be(
            AllocationDirectionalV3CapturePlan.TopN);
        AllocationDirectionalCapturePlan.AttemptTimeoutSeconds.Should().Be(
            AllocationDirectionalV3CapturePlan.AttemptTimeoutSeconds);
        AllocationDirectionalCapturePlan.McpCompletionTimeoutSeconds.Should().Be(
            AllocationDirectionalV3CapturePlan.McpCompletionTimeoutSeconds);
        AllocationDirectionalCapturePlan.RuntimeEnvironment.Should().BeEquivalentTo(
            AllocationDirectionalV3CapturePlan.RuntimeEnvironment);
    }

    [Fact]
    public void V3DeclaredIdentitiesAreDisjointFromPreservedInventories()
    {
        var fixtureDirectory = RepositoryPath(
            "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional");
        var preserved = new[]
        {
            AllocationDirectionalJson.ReadFixture(Path.Combine(
                fixtureDirectory,
                "allocation-directional-baseline-current-v1.windows-x64.evidence.json")),
            AllocationDirectionalJson.ReadFixture(Path.Combine(
                fixtureDirectory,
                "allocation-directional-baseline-current-v2.windows-x64.evidence.json")),
        };
        var calibration = AllocationDirectionalCalibrationJson.ReadFixture(Path.Combine(
            fixtureDirectory,
            "allocation-directional-throughput-calibration-v1.windows-x64.json"));
        var priorPairs = preserved.SelectMany(fixture => fixture.Pairs)
            .Concat(calibration.Pairs)
            .ToArray();
        var priorPairIds = priorPairs.Select(pair => pair.PairId)
            .ToHashSet(StringComparer.Ordinal);
        var priorObservations = priorPairs.SelectMany(pair =>
            new[] { pair.Baseline, pair.Current }).ToArray();
        var priorAttemptIds = priorObservations.Select(value => value.AttemptId)
            .ToHashSet(StringComparer.Ordinal);
        var priorCaptureIds = priorObservations.Select(value => value.CaptureId)
            .Where(value => value is not null)
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);
        var priorWorkloads = priorObservations.Select(value => value.WorkloadIdentity)
            .ToHashSet(StringComparer.Ordinal);

        var v3PairIds = AllocationDirectionalV3CapturePlan.Entries
            .SelectMany(entry => Enumerable.Range(
                    1,
                    AllocationDirectionalBounds.RequiredPairs)
                .Select(repetition =>
                    $"{entry.PairPrefix}-{repetition:D2}"))
            .Concat(Enumerable.Range(
                    1,
                    AllocationDirectionalBounds.RequiredPairs)
                .Select(repetition => $"v3-partial-{repetition:D2}"))
            .ToArray();
        var v3AttemptIds = v3PairIds.SelectMany(pairId =>
            new[] { $"{pairId}-baseline", $"{pairId}-current" }).ToArray();
        var v3CaptureIds = v3AttemptIds.Select(attemptId =>
            attemptId.StartsWith("v3-partial-", StringComparison.Ordinal)
                ? $"projection-{attemptId}"
                : $"capture-{attemptId}").ToArray();
        var v3Workloads = AllocationDirectionalV3CapturePlan.Entries
            .SelectMany(entry => new[]
            {
                entry.Baseline.WorkloadIdentity,
                entry.Current.WorkloadIdentity,
            })
            .Append("partial-attribution-v3")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        v3PairIds.Should().NotIntersectWith(priorPairIds);
        v3AttemptIds.Should().NotIntersectWith(priorAttemptIds);
        v3CaptureIds.Should().NotIntersectWith(priorCaptureIds);
        v3Workloads.Should().NotIntersectWith(priorWorkloads);
    }

    [Fact]
    public void MissingDenominatorWithPositiveBackgroundWeightStillAbstainsSafely()
    {
        var fixture = SyntheticFixture();
        var target = fixture.Pairs.First(pair =>
            pair.Scenario == AllocationDirectionalScenario.MissingOrZeroWeight);
        var changed = fixture.Pairs.Select(pair =>
            pair.PairId == target.PairId
                ? pair with
                {
                    Current = pair.Current with
                    {
                        SampledEstimatedBytes = 107_896,
                        SampledAllocationTicks = 1,
                    },
                }
                : pair).ToArray();

        var report = AllocationDirectionalV3Harness.Evaluate(
            fixture with { Pairs = changed });
        var missing = report.Scenarios.Single(result =>
            result.Scenario == AllocationDirectionalScenario.MissingOrZeroWeight);

        missing.Assessable.Should().BeTrue();
        missing.ExpectedBehaviorObserved.Should().BeTrue();
        report.NumericGatePassed.Should().BeTrue();
    }

    [Fact]
    public void IncompletePairRemainsInDenominatorAndMakesGateInconclusive()
    {
        var fixture = SyntheticFixture();
        var target = fixture.Pairs.First(pair =>
            pair.Scenario == AllocationDirectionalScenario.TrueIncrease
            && pair.Current.Phase == "effect");
        var changed = fixture.Pairs.Select(pair =>
            pair.PairId == target.PairId
                ? pair with
                {
                    Current = pair.Current with
                    {
                        Outcome = AllocationDirectionalAttemptOutcome.Failed,
                        FailureCategory = "collection_failed",
                    },
                }
                : pair).ToArray();

        var report = AllocationDirectionalV3Harness.Evaluate(
            fixture with { Pairs = changed });
        var increase = report.Scenarios.Single(result =>
            result.Scenario == AllocationDirectionalScenario.TrueIncrease);

        report.Decision.Should().Be(AllocationDirectionalDecision.Inconclusive);
        increase.AttemptedPairs.Should().Be(6);
        increase.ValidPairs.Should().Be(5);
        increase.Assessable.Should().BeFalse();
    }

    [Fact]
    public void PostExecutionArtifacts_PreventAnyRerun()
    {
        var path = ScenarioManifestLoader.ScenarioPath(
            "Fixtures",
            "AllocationDirectional",
            AllocationDirectionalV3Protocol.FileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        root.GetProperty("status").GetString().Should().Be("frozen");
        root.GetProperty("frozenInputs").GetProperty("sourceFilesSha256")
            .EnumerateObject().Should().HaveCount(
                AllocationDirectionalV3Protocol.FrozenSourcePaths.Count);
        root.GetProperty("frozenInputs").GetProperty("runtimeArtifactsSha256")
            .EnumerateObject().Should().HaveCount(
                AllocationDirectionalV3Protocol.FrozenRuntimeArtifactPaths.Count);
        root.GetProperty("frozenInputs").GetProperty("preservedArtifactsSha256")
            .EnumerateObject().Should().HaveCount(
                AllocationDirectionalV3Protocol.PreservedArtifactPaths.Count);

        var validate = AllocationDirectionalV3Protocol.ValidateFrozen;

        validate.Should().Throw<InvalidDataException>()
            .WithMessage("*Frozen source hash mismatch*");
    }

    [EnvironmentRequiredFact(
        AllocationDirectionalEnvironmentVariables.V3ProtocolFreezePath,
        "Freezing the reviewed allocation directional v3 protocol requires an explicit new output path.")]
    public void ReviewedProtocolFreeze_WritesImmutableDocumentOnce()
    {
        var path = Environment.GetEnvironmentVariable(
            AllocationDirectionalEnvironmentVariables.V3ProtocolFreezePath)
            ?? throw new InvalidOperationException(
                "The allocation directional v3 freeze path was not configured.");
        File.Exists(path).Should().BeFalse(
            "a frozen protocol must never be overwritten");
        var document = AllocationDirectionalV3ProtocolDocument.Build(
            "frozen",
            DateTimeOffset.UtcNow.ToString("O"),
            includeFrozenHashes: true);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(document.GetRawText());
        writer.WriteLine();
    }

    private static AllocationDirectionalFixture SyntheticFixture()
    {
        var pairs = new List<AllocationDirectionalPair>();
        foreach (var entry in AllocationDirectionalV3CapturePlan.Entries)
        {
            for (var repetition = 1;
                 repetition <= AllocationDirectionalBounds.RequiredPairs;
                 repetition++)
            {
                var pairId = $"{entry.PairPrefix}-{repetition:D2}";
                pairs.Add(SyntheticPair(entry, pairId));
            }

        }

        var increases = pairs.Where(pair =>
            pair.Scenario == AllocationDirectionalScenario.TrueIncrease
            && pair.Current.Phase == "effect").ToArray();
        for (var index = 0; index < increases.Length; index++)
        {
            var source = increases[index];
            var pairId = $"v3-partial-{index + 1:D2}";
            pairs.Add(new(
                pairId,
                AllocationDirectionalScenario.PartialAttribution,
                "allocation-volume",
                Projection(
                    source.Baseline,
                    $"{pairId}-baseline",
                    removeSites: false),
                Projection(
                    source.Current,
                    $"{pairId}-current",
                    removeSites: true)));
        }

        return new(
            AllocationDirectionalJson.CurrentSchemaVersion,
            AllocationDirectionalV3Protocol.ProtocolId,
            AllocationDirectionalV3Protocol.ProtocolVersion,
            "synthetic-protocol",
            "synthetic-protocol",
            [],
            "2026-11-03T20:00:00Z",
            AllocationDirectionalV3Protocol.SourceBaseCommit,
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

    private static AllocationDirectionalPair SyntheticPair(
        AllocationDirectionalPlanEntry entry,
        string pairId)
    {
        var baselineOperations = 100L;
        var currentOperations = 100L;
        var baselineBytes = 10_000L;
        var currentBytes = 10_100L;
        IReadOnlyList<AllocationDirectionalRankedRow>? baselineTypes = null;
        IReadOnlyList<AllocationDirectionalRankedRow>? currentTypes = null;
        IReadOnlyList<AllocationDirectionalRankedRow>? baselineSites = null;
        IReadOnlyList<AllocationDirectionalRankedRow>? currentSites = null;

        switch (entry.Scenario)
        {
            case AllocationDirectionalScenario.TrueIncrease
                when entry.Current.Phase == "effect":
                currentBytes = 20_000;
                break;
            case AllocationDirectionalScenario.MixShiftStableTotal
                when entry.Current.Phase == "effect":
                baselineTypes =
                [
                    new("System.Byte[]", 8_000, 8),
                    new("System.Char[]", 2_000, 2),
                ];
                currentTypes =
                [
                    new("System.Int32[]", 8_080, 8),
                    new("System.Char[]", 2_020, 2),
                ];
                baselineSites =
                [
                    new("Primary", 8_000, 8),
                    new("Shared", 2_000, 2),
                ];
                currentSites =
                [
                    new("Changed", 8_080, 8),
                    new("Shared", 2_020, 2),
                ];
                break;
            case AllocationDirectionalScenario.ThroughputIncreaseStablePerOperation
                when entry.Current.Phase == "effect":
                currentOperations = 200;
                currentBytes = 20_200;
                break;
            case AllocationDirectionalScenario.LowerAllocationStableRetained
                when entry.Current.Phase == "effect":
                baselineBytes = 20_000;
                currentBytes = 10_000;
                break;
            case AllocationDirectionalScenario.LowerAllocationStableRetained:
                baselineBytes = 20_000;
                currentBytes = 20_200;
                break;
            case AllocationDirectionalScenario.WarmupTransientConverges
                when entry.Current.Phase == "early":
                currentBytes = 12_000;
                break;
            case AllocationDirectionalScenario.MissingOrZeroWeight:
                currentOperations = 0;
                currentBytes = 0;
                currentTypes = [];
                currentSites = [];
                break;
        }

        return new(
            pairId,
            entry.Scenario,
            entry.ComparisonAxis,
            Observation(
                $"{pairId}-baseline",
                entry.Baseline,
                baselineOperations,
                baselineBytes,
                baselineTypes,
                baselineSites),
            Observation(
                $"{pairId}-current",
                entry.Current,
                currentOperations,
                currentBytes,
                currentTypes,
                currentSites));
    }

    private static AllocationDirectionalObservation Observation(
        string id,
        AllocationDirectionalPlannedSide side,
        long operations,
        long bytes,
        IReadOnlyList<AllocationDirectionalRankedRow>? types,
        IReadOnlyList<AllocationDirectionalRankedRow>? sites)
    {
        var available = operations > 0 && bytes > 0;
        return new(
            id,
            AllocationDirectionalAttemptOutcome.Passed,
            AllocationDirectionalOrigin.LiveMcp,
            side.WorkloadIdentity,
            side.Shape,
            side.Phase,
            side.Workers,
            AllocationDirectionalV3CapturePlan.CaptureDurationSeconds,
            operations,
            side.ExpectedPayloadBytesPerOperation,
            side.RetainedBytes,
            bytes,
            Math.Max(0, bytes / 100),
            available ? "complete" : "unavailable",
            types ?? (available ? [new("System.Byte[]", bytes, 1)] : []),
            sites ?? (available ? [new("Primary", bytes, 1)] : []),
            null,
            [])
        {
            CaptureId = $"capture-{id}",
            ProcessIdentity = $"process-{id}",
            ComparisonRole = id.EndsWith("-baseline", StringComparison.Ordinal)
                ? "baseline"
                : "current",
            QuantityEvidence = new(
                "estimated sampled allocation bytes",
                "estimated sampled bytes",
                id.EndsWith("-baseline", StringComparison.Ordinal)
                    ? "baseline"
                    : "current",
                "completed workload operations",
                "operations",
                operations,
                "estimated sampled bytes divided by completed workload operations",
                LoadSensitive: false,
                available ? "available" : "unavailable",
                available
                    ? null
                    : "A positive operation denominator and positive sampled weight are required.",
                []),
            TypeAvailability = available ? "available" : "unavailable",
            SiteAvailability = available ? "available" : "unavailable",
        };
    }

    private static AllocationDirectionalObservation Projection(
        AllocationDirectionalObservation source,
        string id,
        bool removeSites)
        => source with
        {
            AttemptId = id,
            Origin = AllocationDirectionalOrigin.ControlledProjection,
            WorkloadIdentity = "partial-attribution-v3",
            AttributionState = removeSites ? "partial" : source.AttributionState,
            Sites = removeSites ? [] : source.Sites,
            CaptureId = $"projection-{id}",
            ComparisonRole = id.EndsWith("-baseline", StringComparison.Ordinal)
                ? "baseline"
                : "current",
            QuantityEvidence = source.QuantityEvidence! with
            {
                BaselineCurrentSemantics =
                    id.EndsWith("-baseline", StringComparison.Ordinal)
                        ? "baseline"
                        : "current",
            },
            TypeAvailability = "available",
            SiteAvailability = removeSites
                ? "unavailable-partial-attribution"
                : "available",
        };
}
