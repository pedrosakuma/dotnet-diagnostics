using System.Reflection;
using System.Text.Json;
using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class AllocationDirectionalBoundedCalibrationTests
{
    [Fact]
    public void HealthyAa_PracticalSanityBoundaryIsInclusive()
    {
        var atBoundary =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateHealthyAa(
                PhasePairs("aa", [0.10m, 0.10m, 0.10m]));
        var aboveBoundary =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateHealthyAa(
                PhasePairs("aa", [0.101m, 0.101m, 0.101m]));

        atBoundary.Assessable.Should().BeTrue();
        atBoundary.WithinPracticalSanity.Should().BeTrue();
        aboveBoundary.Assessable.Should().BeTrue();
        aboveBoundary.WithinPracticalSanity.Should().BeFalse();
    }

    [Fact]
    public void Stability_UsesMaxOfAaEnvelopeAndPracticalFloor()
    {
        var below = StabilityPairs(
            [0.001m, 0.002m, 0.003m],
            [0.099m, 0.099m, 0.099m]);
        var at = StabilityPairs(
            [0.001m, 0.002m, 0.003m],
            [0.10m, 0.10m, 0.10m]);
        var above = StabilityPairs(
            [0.001m, 0.002m, 0.003m],
            [0.101m, 0.101m, 0.101m]);

        AllocationDirectionalPracticalEquivalenceSelector.EvaluateStability(below)
            .Equivalent.Should().BeTrue();
        var atResult =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateStability(at);
        atResult.EquivalenceMargin.Should().Be(0.10m);
        atResult.Equivalent.Should().BeTrue();
        AllocationDirectionalPracticalEquivalenceSelector.EvaluateStability(above)
            .Equivalent.Should().BeFalse();
    }

    [Fact]
    public void Stability_RetainsOneOutlierWithoutInflatingAaEnvelope()
    {
        var result =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateStability(
                StabilityPairs(
                    [0.01m, 0.01m, 0.50m],
                    [0.05m, 0.06m, 0.80m]));

        result.Assessable.Should().BeTrue();
        result.Equivalent.Should().BeTrue();
        result.AaEnvelope.Should().Be(0.01m);
        result.AaAbsoluteRelativeDeltas.OutlierIndexes.Should().Equal(2);
        result.EffectAbsoluteRelativeDeltas.OutlierIndexes.Should().Equal(2);
    }

    [Fact]
    public void Movement_RequiresPracticalMagnitudeAndSignAgreement()
    {
        var atFloor =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateMovement(
                MovementPairs([0.01m, 0.01m, 0.01m], [0.10m, 0.10m, 0.10m]),
                expectedIncrease: true);
        var aboveFloor =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateMovement(
                MovementPairs([0.01m, 0.01m, 0.01m], [0.11m, 0.12m, 0.13m]),
                expectedIncrease: true);
        var signDisagreement =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateMovement(
                MovementPairs([0.01m, 0.01m, 0.01m], [-0.01m, 0.11m, 0.23m]),
                expectedIncrease: true);

        atFloor.ExpectedMovementEstablished.Should().BeFalse();
        aboveFloor.ExpectedMovementEstablished.Should().BeTrue();
        signDisagreement.Assessable.Should().BeTrue();
        signDisagreement.ExpectedMovementEstablished.Should().BeFalse();
    }

    [Fact]
    public void LowerMovement_RequiresStrictMagnitudeAndNegativeSignAgreement()
    {
        var atFloor =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateMovement(
                MovementPairs(
                    [0.01m, 0.01m, 0.01m],
                    [-0.10m, -0.10m, -0.10m]),
                expectedIncrease: false);
        var belowFloor =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateMovement(
                MovementPairs(
                    [0.01m, 0.01m, 0.01m],
                    [-0.11m, -0.12m, -0.13m]),
                expectedIncrease: false);
        var signDisagreement =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateMovement(
                MovementPairs(
                    [0.01m, 0.01m, 0.01m],
                    [0.01m, -0.11m, -0.23m]),
                expectedIncrease: false);

        atFloor.ExpectedMovementEstablished.Should().BeFalse();
        belowFloor.ExpectedMovementEstablished.Should().BeTrue();
        signDisagreement.Assessable.Should().BeTrue();
        signDisagreement.ExpectedMovementEstablished.Should().BeFalse();
    }

    [Fact]
    public void Warmup_ConvergedPracticalBoundaryIsInclusive()
    {
        var atBoundary =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateWarmup(
                WarmupPairs(
                    [0.01m, 0.01m, 0.01m],
                    [0.20m, 0.21m, 0.22m],
                    [0.10m, 0.10m, 0.10m]));
        var aboveBoundary =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateWarmup(
                WarmupPairs(
                    [0.01m, 0.01m, 0.01m],
                    [0.20m, 0.21m, 0.22m],
                    [0.101m, 0.101m, 0.101m]));

        atBoundary.Passed.Should().BeTrue();
        atBoundary.ConvergenceEquivalenceMargin.Should().Be(0.10m);
        aboveBoundary.EarlyMovementEstablished.Should().BeTrue();
        aboveBoundary.Converged.Should().BeFalse();
    }

    [Fact]
    public void Warmup_EarlyPracticalBoundaryIsStrict()
    {
        var atBoundary =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateWarmup(
                WarmupPairs(
                    [0.01m, 0.01m, 0.01m],
                    [0.10m, 0.10m, 0.10m],
                    [0.01m, 0.01m, 0.01m]));
        var aboveBoundary =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateWarmup(
                WarmupPairs(
                    [0.01m, 0.01m, 0.01m],
                    [0.101m, 0.101m, 0.101m],
                    [0.01m, 0.01m, 0.01m]));

        atBoundary.EarlyMovementEstablished.Should().BeFalse();
        aboveBoundary.EarlyMovementEstablished.Should().BeTrue();
    }

    [Fact]
    public void Throughput_ImmaterialSubpercentNoiseRemainsEquivalent()
    {
        var pairs = new List<AllocationDirectionalPair>();
        AddPhasePairs(pairs, "aa", [0.002m, 0.003m, 0.004m]);
        for (var index = 0; index < 3; index++)
        {
            pairs.Add(Pair(
                $"effect-{index}",
                "effect",
                0.008m,
                operationMultiplier: 1.5m));
        }

        var result =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateThroughput(
                pairs);

        result.Passed.Should().BeTrue();
        result.EstimatedBytesPerOperationEquivalent.Should().BeTrue();
        result.EquivalenceMargin.Should().Be(0.10m);
    }

    [Fact]
    public void Throughput_ManipulationBoundaryIsStrict()
    {
        static AllocationDirectionalPracticalThroughputGate Evaluate(
            decimal multiplier)
        {
            var pairs = new List<AllocationDirectionalPair>();
            AddPhasePairs(pairs, "aa", [0.001m, 0.001m, 0.001m]);
            for (var index = 0; index < 3; index++)
            {
                pairs.Add(Pair(
                    $"effect-{index}",
                    "effect",
                    0.001m,
                    operationMultiplier: multiplier));
            }

            return AllocationDirectionalPracticalEquivalenceSelector
                .EvaluateThroughput(pairs);
        }

        Evaluate(1.25m).WorkloadManipulationEstablished.Should().BeFalse();
        Evaluate(1.251m).Passed.Should().BeTrue();
    }

    [Fact]
    public void Mix_BroadTotalEquivalenceCannotReplaceDirectionalEvidence()
    {
        var noMovement = MixPairs(effectMovesMix: false);
        var moved = MixPairs(effectMovesMix: true);

        var noMovementResult =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateMix(noMovement);
        var movedResult =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateMix(moved);

        noMovementResult.StableTotalEquivalent.Should().BeTrue();
        noMovementResult.Passed.Should().BeFalse();
        noMovementResult.AbstentionReason.Should().Contain(
            "cannot establish a mix shift");
        movedResult.Passed.Should().BeTrue();
        movedResult.TypeAbsoluteDecisionFloor.Should().Be(0.10m);
        movedResult.SiteAbsoluteDecisionFloor.Should().Be(0.10m);
    }

    [Fact]
    public void Mix_AbstainsWithInsufficientPairs()
    {
        var pairs = MixPairs(effectMovesMix: true).Take(4).ToArray();

        var result =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateMix(pairs);

        result.Assessable.Should().BeFalse();
        result.Passed.Should().BeFalse();
    }

    [Fact]
    public void PracticalSelectors_AbstainWithInsufficientPairs()
    {
        var result =
            AllocationDirectionalPracticalEquivalenceSelector.EvaluateStability(
                StabilityPairs(
                    [0.01m, 0.01m],
                    [0.02m, 0.02m]));

        result.Assessable.Should().BeFalse();
        result.Equivalent.Should().BeFalse();
    }

    [Fact]
    public void Projection_RetainsAllocationCapIdentifierAndStage()
    {
        const string raw =
            """
            {
              "error": {
                "kind": "ResourceLimitExceeded",
                "code": "allocation.capture.limit",
                "detail": "redacted"
              }
            }
            """;

        var result = AllocationDirectionalProjection.Project(
            "cap-attempt",
            "cap-workload",
            raw,
            isError: true);

        result.Succeeded.Should().BeFalse();
        result.Evidence.Stage.Should().Be(
            AllocationDirectionalAttemptStage.McpResponse);
        result.Evidence.FailureCategory.Should().Be(
            AllocationDirectionalFailureCategory.McpIsError);
        result.Evidence.ErrorKind.Should().Be("ResourceLimitExceeded");
        result.Evidence.ErrorCode.Should().Be("allocation.capture.limit");
    }

    [Fact]
    public void BoundedGate_AcceptsCompletePacedInventoryAndRetainedCapProbes()
    {
        var fixture = SyntheticBoundedFixture();

        var result = AllocationDirectionalBoundedCalibrationSelector.Evaluate(
            fixture);

        result.Passed.Should().BeTrue();
        result.Boundedness.ProbeAllocationCapFailures.Should().Be(3);
        result.Boundedness.RequiredPairs.Should().Be(24);
        result.Boundedness.RequiredPassedSides.Should().Be(48);
        result.Boundedness.RequiredFailedSides.Should().Be(0);
        result.Boundedness.CaptureDurationFractionOfProbe.Should().Be(0.25m);
        result.Boundedness.WorkloadDurationFractionOfProbe.Should()
            .Be(0.4666666666666666666666666667m);
        result.Boundedness.FrameCountAvailable.Should().BeFalse();
        result.Boundedness.ModuleCountAvailable.Should().BeFalse();
        result.Boundedness.FixedStrideShapesFeasible.Should().BeTrue();
        result.Boundedness.PartialAttributionSourcesFeasible.Should().BeTrue();
    }

    [Fact]
    public void BoundedGate_AbstainsWhenRequiredSideHitsCap()
    {
        var fixture = SyntheticBoundedFixture();
        var first = fixture.Pairs[0];
        var changed = fixture.Pairs.ToArray();
        changed[0] = first with
        {
            Current = CapFailure(first.Current.AttemptId, first.Current),
        };

        var result = AllocationDirectionalBoundedCalibrationSelector.Evaluate(
            fixture with { Pairs = changed });

        result.Passed.Should().BeFalse();
        result.Boundedness.RequiredFailedSides.Should().Be(1);
        result.Boundedness.AbstentionReason.Should().NotBeNull();
    }

    [Fact]
    public void BoundedGate_RejectsIncompleteProbeCapProvenance()
    {
        var fixture = SyntheticBoundedFixture();
        var probes = fixture.Probes.ToArray();
        probes[0] = probes[0] with
        {
            AttemptEvidence = probes[0].AttemptEvidence! with
            {
                ErrorCode = null,
            },
        };

        var result = AllocationDirectionalBoundedCalibrationSelector.Evaluate(
            fixture with { Probes = probes });

        result.Boundedness.ProbeAllocationCapFailures.Should().Be(2);
        result.Boundedness.Passed.Should().BeFalse();
    }

    [Theory]
    [InlineData(75_000, true)]
    [InlineData(75_001, false)]
    public void BoundedGate_EnforcesObservableEventMarginBoundary(
        long sampledAllocationTicks,
        bool expectedPassed)
    {
        var fixture = SyntheticBoundedFixture();
        var first = fixture.Pairs[0];
        var changed = fixture.Pairs.ToArray();
        changed[0] = first with
        {
            Baseline = first.Baseline with
            {
                SampledAllocationTicks = sampledAllocationTicks,
            },
        };

        var result = AllocationDirectionalBoundedCalibrationSelector.Evaluate(
            fixture with { Pairs = changed });

        result.Boundedness.MaximumObservedAllocationEvents.Should()
            .Be(sampledAllocationTicks);
        result.Boundedness.Passed.Should().Be(expectedPassed);
    }

    [Fact]
    public void BoundedGate_RejectsWiderOperatingGeometry()
    {
        var fixture = SyntheticBoundedFixture();
        var widerCapture = fixture with
        {
            OperatingSettings = fixture.OperatingSettings with
            {
                CaptureDurationSeconds = 2,
            },
        };
        var widerWorkload = fixture with
        {
            OperatingSettings = fixture.OperatingSettings with
            {
                WorkloadMilliseconds = 751,
            },
        };

        AllocationDirectionalBoundedCalibrationSelector.Evaluate(widerCapture)
            .Boundedness.Passed.Should().BeFalse();
        AllocationDirectionalBoundedCalibrationSelector.Evaluate(widerWorkload)
            .Boundedness.Passed.Should().BeFalse();
    }

    [Fact]
    public void BoundedGate_RejectsFixtureSettingsDifferentFromPlan()
    {
        var fixture = SyntheticBoundedFixture();
        var changed = fixture with
        {
            OperatingSettings = fixture.OperatingSettings with
            {
                ArmDelayMilliseconds = 201,
            },
        };

        AllocationDirectionalBoundedCalibrationSelector.Evaluate(changed)
            .Boundedness.Assessable.Should().BeFalse();
    }

    [Fact]
    public void BoundedGate_RejectsObservationDurationDifferentFromSettings()
    {
        var fixture = SyntheticBoundedFixture();
        var pairs = fixture.Pairs.ToArray();
        pairs[0] = pairs[0] with
        {
            Baseline = pairs[0].Baseline with
            {
                CaptureDurationSeconds = 4,
            },
        };

        AllocationDirectionalBoundedCalibrationSelector.Evaluate(
                fixture with { Pairs = pairs })
            .Boundedness.Assessable.Should().BeFalse();
    }

    [Fact]
    public void BoundedGate_RejectsProbeOrPairIdentityDifferentFromPlan()
    {
        var fixture = SyntheticBoundedFixture();
        var probes = fixture.Probes.ToArray();
        probes[0] = probes[0] with { WorkloadIdentity = "different-probe" };
        var pairs = fixture.Pairs.ToArray();
        pairs[0] = pairs[0] with
        {
            PairId = "different-pair",
            Baseline = pairs[0].Baseline with { Shape = "different-shape" },
        };

        AllocationDirectionalBoundedCalibrationSelector.Evaluate(
                fixture with { Probes = probes })
            .Boundedness.Assessable.Should().BeFalse();
        AllocationDirectionalBoundedCalibrationSelector.Evaluate(
                fixture with { Pairs = pairs })
            .Boundedness.Assessable.Should().BeFalse();
    }

    [Fact]
    public void CoreAllocationCaps_MatchCalibrationContract()
    {
        const BindingFlags flags =
            BindingFlags.Static | BindingFlags.NonPublic;
        var sampler = typeof(EventPipeAllocationSampler);

        sampler.GetField("MaxAllocationFrames", flags)!
            .GetRawConstantValue().Should()
            .Be(AllocationDirectionalBoundedCalibrationPlan.AllocationFrameCap);
        sampler.GetField("MaxAllocationEvents", flags)!
            .GetRawConstantValue().Should()
            .Be(AllocationDirectionalBoundedCalibrationPlan.AllocationEventCap);
    }

    [Fact]
    public void RetainedV6Fixture_ReevaluatesToFrozenPassingReport()
    {
        var fixture = JsonSerializer.Deserialize<
            AllocationDirectionalBoundedCalibrationFixture>(
            File.ReadAllText(FixturePath(
                "allocation-directional-bounded-calibration-v6.windows-x64.json")),
            AllocationDirectionalJson.Options);
        var report = JsonSerializer.Deserialize<
            AllocationDirectionalBoundedCalibrationReport>(
            File.ReadAllText(FixturePath(
                "allocation-directional-bounded-calibration-v6.windows-x64.report.json")),
            AllocationDirectionalJson.Options);

        fixture.Should().NotBeNull();
        report.Should().NotBeNull();
        AllocationDirectionalBoundedCalibrationSelector.Evaluate(fixture!)
            .Should().BeEquivalentTo(report);
        report!.Passed.Should().BeTrue();
    }

    [Fact]
    public void RetainedCalibrationArtifacts_HaveFrozenPortableFingerprints()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["allocation-directional-bounded-calibration-v1.windows-x64.json"] =
                "0ade798aab5dc1450a6869c63a4034df7bf568ad8d0de73ce8f35b158e9ceee3",
            ["allocation-directional-bounded-calibration-v1.windows-x64.report.json"] =
                "846fdac112edb916e5ed0ef0f2802d9d8124798f3bdeaa57bd243c313d6e016a",
            ["allocation-directional-bounded-calibration-v1.windows-x64.started.json"] =
                "cdeb16d63378b32abdeb6d86e59d62e3a2c0162b4af5dc7f0a6ef6afa4736023",
            ["allocation-directional-bounded-calibration-v2.windows-x64.json"] =
                "ae3b25b64da3e57316b129363e6ede44db4815457a4f260ac671c10bb25933ae",
            ["allocation-directional-bounded-calibration-v2.windows-x64.report.json"] =
                "991ccef0dc4784fcc6512486082d8391ad25c45f4f367ffe30851863bb5b3877",
            ["allocation-directional-bounded-calibration-v2.windows-x64.started.json"] =
                "f032d0333b02925d7c8acb41bdf0d8c7ef2ef7d8f4385d1f324a56d259779a88",
            ["allocation-directional-bounded-calibration-v3.windows-x64.started.json"] =
                "1bf71b2e2c62ae05dbfd2002e835adebbf65b82c88278e54fa5ddad7cf588885",
            ["allocation-directional-bounded-calibration-v4.windows-x64.json"] =
                "215eadc0e432fa318b5507961816b318947dabc0d0f88a2ba61ddee3e6bf972e",
            ["allocation-directional-bounded-calibration-v4.windows-x64.report.json"] =
                "7da0bd28bd58ad9e713f9fd3825ad20832f8c85c5c12c7f1236fc3094b508471",
            ["allocation-directional-bounded-calibration-v4.windows-x64.started.json"] =
                "456ee60c3ca1e0bf5cff3a083bbd80de72c2423bacf6b596d212f459dc18b934",
            ["allocation-directional-bounded-calibration-v5.windows-x64.json"] =
                "fdb29838d79227c1011a55ffa0f71f14ebd42f8ecc810d683dfc6d0ec66706cb",
            ["allocation-directional-bounded-calibration-v5.windows-x64.report.json"] =
                "c7420315db8f718564bfc7778d7ce7e1e9054c866d6e3026146da241cae43c04",
            ["allocation-directional-bounded-calibration-v5.windows-x64.started.json"] =
                "bc4102753e571480f3b03287438020eb0a82e29b7e3ec86cc00012181c1e0a0c",
            ["allocation-directional-bounded-calibration-v6.windows-x64.json"] =
                "aee5adc17052d1f6d4733f8a8bd4fe67fe9c5b4eccd1b57643a2ed5ce62f669e",
            ["allocation-directional-bounded-calibration-v6.windows-x64.report.json"] =
                "53570bc294843e33a9ba209a3d8ea3bd5fdf5df9c0725ae1d435c44cd8184178",
            ["allocation-directional-bounded-calibration-v6.windows-x64.started.json"] =
                "29bda9807d639dfa7fcb15040ac79b2d356481875bbfbe44dadeb97a10a512a2",
        };
        foreach (var (fileName, hash) in expected)
        {
            AllocationDirectionalProtocol.Sha256(
                    FixturePath(fileName))
                .Should().Be(hash, fileName);
        }

        File.Exists(FixturePath(
            "allocation-directional-bounded-calibration-v3.windows-x64.json"))
            .Should().BeFalse();
        File.Exists(FixturePath(
            "allocation-directional-bounded-calibration-v3.windows-x64.report.json"))
            .Should().BeFalse();
    }

    private static string FixturePath(string fileName)
        => Path.Combine(
            AllocationDirectionalProtocol.RepositoryRoot(),
            "tests",
            "DotnetDiagnostics.ScenarioEvaluation.Tests",
            "Fixtures",
            "AllocationDirectional",
            fileName);

    private static List<AllocationDirectionalPair> StabilityPairs(
        IReadOnlyList<decimal> aaDeltas,
        IReadOnlyList<decimal> effectDeltas)
    {
        var pairs = new List<AllocationDirectionalPair>();
        AddPhasePairs(pairs, "aa", aaDeltas);
        AddPhasePairs(pairs, "effect", effectDeltas);
        return pairs;
    }

    private static List<AllocationDirectionalPair> MovementPairs(
        IReadOnlyList<decimal> aaDeltas,
        IReadOnlyList<decimal> effectDeltas)
        => StabilityPairs(aaDeltas, effectDeltas);

    private static List<AllocationDirectionalPair> WarmupPairs(
        IReadOnlyList<decimal> aaDeltas,
        IReadOnlyList<decimal> earlyDeltas,
        IReadOnlyList<decimal> convergedDeltas)
    {
        var pairs = new List<AllocationDirectionalPair>();
        AddPhasePairs(pairs, "aa", aaDeltas);
        AddPhasePairs(pairs, "early", earlyDeltas);
        AddPhasePairs(pairs, "converged", convergedDeltas);
        return pairs;
    }

    private static List<AllocationDirectionalPair> PhasePairs(
        string phase,
        IReadOnlyList<decimal> deltas)
    {
        var pairs = new List<AllocationDirectionalPair>();
        AddPhasePairs(pairs, phase, deltas);
        return pairs;
    }

    private static void AddPhasePairs(
        List<AllocationDirectionalPair> pairs,
        string phase,
        IReadOnlyList<decimal> deltas)
    {
        for (var index = 0; index < deltas.Count; index++)
        {
            pairs.Add(Pair($"{phase}-{index}", phase, deltas[index]));
        }
    }

    private static AllocationDirectionalPair Pair(
        string id,
        string phase,
        decimal delta,
        decimal operationMultiplier = 1m)
    {
        const long baselineOperations = 1_000;
        const long baselineBytesPerOperation = 10_000;
        var currentOperations = decimal.ToInt64(
            baselineOperations * operationMultiplier);
        var currentBytesPerOperation = decimal.ToInt64(
            baselineBytesPerOperation * (1 + delta));
        return new(
            id,
            AllocationDirectionalScenario.HealthyAa,
            "synthetic",
            Observation(
                $"{id}-baseline",
                "synthetic",
                "steady",
                baselineOperations,
                baselineOperations * baselineBytesPerOperation),
            Observation(
                $"{id}-current",
                "synthetic",
                phase,
                currentOperations,
                currentOperations * currentBytesPerOperation));
    }

    private static List<AllocationDirectionalPair> MixPairs(
        bool effectMovesMix)
    {
        var pairs = new List<AllocationDirectionalPair>();
        for (var index = 0; index < 3; index++)
        {
            pairs.Add(MixPair($"mix-aa-{index}", "aa", moveMix: false));
            pairs.Add(MixPair(
                $"mix-effect-{index}",
                "effect",
                effectMovesMix));
        }

        return pairs;
    }

    private static AllocationDirectionalPair MixPair(
        string id,
        string phase,
        bool moveMix)
    {
        const long operations = 100;
        const long total = 1_000_000;
        var baselineTypes = Rows("TypeA", "TypeB", 800_000, 200_000);
        var baselineSites = Rows("SiteA", "SiteB", 800_000, 200_000);
        var currentTypes = moveMix
            ? Rows("TypeA", "TypeB", 200_000, 800_000)
            : baselineTypes;
        var currentSites = moveMix
            ? Rows("SiteA", "SiteB", 200_000, 800_000)
            : baselineSites;
        return new(
            id,
            AllocationDirectionalScenario.MixShiftStableTotal,
            "allocation-mix",
            Observation(
                $"{id}-baseline",
                "mix-workload",
                "steady",
                operations,
                total,
                baselineTypes,
                baselineSites),
            Observation(
                $"{id}-current",
                "mix-workload",
                phase,
                operations,
                total,
                currentTypes,
                currentSites));
    }

    private static IReadOnlyList<AllocationDirectionalRankedRow> Rows(
        string first,
        string second,
        long firstBytes,
        long secondBytes)
        =>
        [
            new(first, firstBytes, 1),
            new(second, secondBytes, 1),
        ];

    private static AllocationDirectionalBoundedCalibrationFixture
        SyntheticBoundedFixture()
    {
        var probes = AllocationDirectionalBoundedCalibrationPlan.Probes
            .Select((probe, index) =>
                CapFailure(
                    $"probe-{index}",
                    Observation(
                        $"probe-{index}",
                        probe.WorkloadIdentity,
                        probe.Phase,
                        0,
                        0) with
                    {
                        Shape = probe.Shape,
                        CaptureDurationSeconds =
                            AllocationDirectionalBoundedCalibrationPlan
                                .ProbeSettings.CaptureDurationSeconds,
                    }))
            .ToArray();
        var pairs = new List<AllocationDirectionalPair>();
        foreach (var entry in AllocationDirectionalBoundedCalibrationPlan.Entries)
        {
            for (var repetition = 1;
                 repetition <= AllocationDirectionalBounds.RequiredPairs;
                 repetition++)
            {
                pairs.Add(SyntheticPlanPair(entry, repetition));
            }
        }

        return new(
            1,
            AllocationDirectionalBoundedCalibrationPlan.CalibrationId,
            AllocationDirectionalBoundedCalibrationPlan.CalibrationVersion,
            "2026-11-03T22:00:00Z",
            "synthetic",
            $"{AllocationDirectionalBoundedCalibrationPlan.ProbeEvidenceFileName}; sha256={AllocationDirectionalBoundedCalibrationPlan.ProbeEvidenceSha256}",
            AllocationDirectionalBounds.RequiredPairs,
            AllocationDirectionalBoundedCalibrationPlan.ProbeSettings,
            AllocationDirectionalBoundedCalibrationPlan.OperatingSettings,
            probes,
            pairs);
    }

    private static AllocationDirectionalPair SyntheticPlanPair(
        AllocationDirectionalPlanEntry entry,
        int repetition)
    {
        var pairId = $"{entry.PairPrefix}-{repetition:D2}";
        const long operations = 100;
        var baselineBytes = entry.Baseline.ExpectedPayloadBytesPerOperation
            * operations;
        var currentBytes = entry.Current.ExpectedPayloadBytesPerOperation
            * operations;
        var baselineRows = entry.Baseline.Shape.StartsWith(
            "periodic-a",
            StringComparison.Ordinal)
                ? Rows("PeriodicBytes", "PeriodicChars", 12_000_000, 7_200_000)
                : entry.Baseline.Shape.StartsWith(
                    "periodic-b",
                    StringComparison.Ordinal)
                    ? Rows("PeriodicChars", "PeriodicBytes", 12_000_000, 7_200_000)
                    : Rows("Bytes", "Chars", baselineBytes * 8 / 10, baselineBytes * 2 / 10);
        var currentRows = entry.Current.Shape.StartsWith(
            "periodic-a",
            StringComparison.Ordinal)
                ? Rows("PeriodicBytes", "PeriodicChars", 12_000_000, 7_200_000)
                : entry.Current.Shape.StartsWith(
                    "periodic-b",
                    StringComparison.Ordinal)
                    ? Rows("PeriodicChars", "PeriodicBytes", 12_000_000, 7_200_000)
                    : Rows("Bytes", "Chars", currentBytes * 8 / 10, currentBytes * 2 / 10);
        return new(
            pairId,
            entry.Scenario,
            entry.ComparisonAxis,
            Observation(
                $"{pairId}-baseline",
                entry.Baseline.WorkloadIdentity,
                entry.Baseline.Phase,
                operations,
                baselineBytes,
                baselineRows,
                baselineRows,
                entry.Baseline.Shape,
                entry.Baseline.RetainedBytes),
            Observation(
                $"{pairId}-current",
                entry.Current.WorkloadIdentity,
                entry.Current.Phase,
                operations,
                currentBytes,
                currentRows,
                currentRows,
                entry.Current.Shape,
                entry.Current.RetainedBytes));
    }

    private static AllocationDirectionalObservation CapFailure(
        string id,
        AllocationDirectionalObservation source)
        => source with
        {
            AttemptId = id,
            Outcome = AllocationDirectionalAttemptOutcome.Failed,
            SampledEstimatedBytes = 0,
            SampledAllocationTicks = 0,
            Types = [],
            Sites = [],
            FailureCategory = AllocationDirectionalFailureCategory.McpIsError.ToString(),
            AttemptEvidence = new(
                id,
                source.WorkloadIdentity,
                AllocationDirectionalAttemptStage.McpResponse,
                "mcp.call-tool-result-is-error",
                AllocationDirectionalFailureCategory.McpIsError,
                true,
                null,
                "ResourceLimitExceeded",
                "present-redacted",
                8,
                null,
                null,
                AllocationDirectionalCleanupState.Succeeded)
            {
                ErrorCode = "allocation.capture.limit",
            },
        };

    private static AllocationDirectionalObservation Observation(
        string id,
        string workloadIdentity,
        string phase,
        long operations,
        long bytes,
        IReadOnlyList<AllocationDirectionalRankedRow>? types = null,
        IReadOnlyList<AllocationDirectionalRankedRow>? sites = null,
        string shape = "synthetic",
        int retainedBytes = 0)
        => new(
            id,
            AllocationDirectionalAttemptOutcome.Passed,
            AllocationDirectionalOrigin.LiveMcp,
            workloadIdentity,
            shape,
            phase,
            1,
            1,
            operations,
            operations > 0 ? bytes / operations : 0,
            retainedBytes,
            bytes,
            Math.Max(1, bytes / 100_000),
            "complete",
            types ?? Rows("Bytes", "Chars", bytes * 8 / 10, bytes * 2 / 10),
            sites ?? Rows("SiteA", "SiteB", bytes * 8 / 10, bytes * 2 / 10),
            null,
            [])
        {
            AttemptEvidence = new(
                id,
                workloadIdentity,
                AllocationDirectionalAttemptStage.Completed,
                "projection.data.allocation-complete",
                null,
                false,
                AllocationDirectionalProjection.SchemaIdentity,
                null,
                "absent",
                0,
                null,
                null,
                AllocationDirectionalCleanupState.Succeeded),
            ProcessIdentity = $"process-{id}",
            CaptureId = $"capture-{id}",
            TypeAvailability = "available",
            SiteAvailability = "available",
        };
}
