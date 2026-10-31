using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class StatisticalEvidenceTests
{
    [Fact]
    public void PairedDelta_PositiveKnownShape_PreservesPairsAndNoVerdictSemantics()
    {
        var request = PairedRequest(
            [10, 11, 9],
            [14, 13, 15]);

        var result = ScenarioStatisticalEvidenceHarness.Evaluate(request);

        result.Availability.Should().Be(ScenarioStatisticalAvailability.Available);
        result.Delta.Should().Be(new ScenarioStatisticalDelta(10, 14, 4, 0.4));
        result.Counts.IndependenceUnits.Should().Be(3);
        result.Counts.Attempts.Should().Be(3);
        result.CalculationProvenance.OrderedPairs.Select(item => item.PairKey)
            .Should().Equal("pair-1", "pair-2", "pair-3");
        result.CalculationProvenance.OrderedPairs.Select(item => item.PairOrder)
            .Should().Equal(
                ScenarioStatisticalPairOrder.BaselineFirst,
                ScenarioStatisticalPairOrder.CandidateFirst,
                ScenarioStatisticalPairOrder.BaselineFirst);
        var json = ScenarioStatisticalEvidenceJson.SerializeResult(result);
        var normalizedJson = json.ToLowerInvariant();
        normalizedJson.Should().NotContain("\"verdict\"");
        normalizedJson.Should().NotContain("statistically significant");
        normalizedJson.Should().NotContain("regression");
        normalizedJson.Should().NotContain("improvement");
    }

    [Fact]
    public void PairedDelta_UnchangedControl_ReportsMeasuredZeroWithoutDecision()
    {
        var request = PairedRequest(
            [5, 6, 7],
            [5, 6, 7],
            candidateRole: ScenarioStatisticalArmRole.Control);
        request = request with
        {
            Arms =
            [
                request.Arms[0],
                request.Arms[1] with { Role = ScenarioStatisticalArmRole.Candidate },
            ],
        };

        var result = ScenarioStatisticalEvidenceHarness.Evaluate(request);

        result.Availability.Should().Be(ScenarioStatisticalAvailability.Available);
        result.Delta.Should().NotBeNull();
        result.Delta!.Absolute.Should().Be(0);
        result.Delta.Relative.Should().Be(0);
        result.Limitations.Should().Contain(item =>
            item.Contains("descriptive evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void AvailabilityPrecedence_IsDeterministic()
    {
        var candidateOnly = PairedRequest([1], [2]) with
        {
            Arms = [PairedRequest([1], [2]).Arms[1] with
            {
                Observations =
                [
                    PairedRequest([1], [2]).Arms[1].Observations[0] with
                    {
                        LostOrDropped = 1,
                        Representation = ScenarioStatisticalRepresentation.Truncated,
                    },
                ],
            }],
        };
        var notApplicable = ScenarioStatisticalEvidenceHarness.Evaluate(candidateOnly);
        notApplicable.Availability.Should().Be(ScenarioStatisticalAvailability.NotApplicable);
        notApplicable.UnavailableFields.Should().ContainSingle()
            .Which.Reason.Should().Be("baseline_arm_not_present");

        var incompatibleRequest = PairedRequest([1], [2]);
        var incompatibleIdentity = Identity("candidate", samplingPeriod: "10ms");
        incompatibleIdentity = incompatibleIdentity with
        {
            Dimensions = incompatibleIdentity.Dimensions.Select(dimension =>
                dimension.Name == "backend.id"
                    ? dimension with { Value = "ETW" }
                    : dimension).ToArray(),
        };
        incompatibleRequest = incompatibleRequest with
        {
            Arms =
            [
                incompatibleRequest.Arms[0],
                incompatibleRequest.Arms[1] with
                {
                    CompatibilityIdentity = incompatibleIdentity,
                    Observations =
                    [
                        incompatibleRequest.Arms[1].Observations[0] with { LostOrDropped = 3 },
                    ],
                },
            ],
        };
        var incompatible = ScenarioStatisticalEvidenceHarness.Evaluate(incompatibleRequest);
        incompatible.Availability.Should().Be(ScenarioStatisticalAvailability.Incompatible);
        incompatible.Compatibility!.Mismatches.Should().Contain(item =>
            item.Dimension == "capture.sampling-period");
        incompatible.Compatibility.Mismatches.Should().Contain(item =>
            item.Dimension == "backend.id");
        incompatible.Compatibility.Arms.Should().HaveCount(2);

        var degraded = SingleRequest(
            ScenarioStatisticalMethodIds.MedianAbsoluteDeviation,
            [1],
            minimum: 2,
            transform: observation => observation with
            {
                LostOrDropped = 2,
                Representation = ScenarioStatisticalRepresentation.ReservoirSampled,
            });
        degraded = degraded with
        {
            Arms =
            [
                degraded.Arms[0] with
                {
                    CompatibilityIdentity = Identity(
                        "observed",
                        unit: ScenarioStatisticalIndependenceUnit.BoundedTimeWindow,
                        representation: "reservoir_sampled"),
                },
            ],
        };
        ScenarioStatisticalEvidenceHarness.Evaluate(degraded).Availability
            .Should().Be(ScenarioStatisticalAvailability.LossDetected);

        var approximate = degraded with
        {
            Arms =
            [
                degraded.Arms[0] with
                {
                    Observations =
                    [
                        degraded.Arms[0].Observations[0] with { LostOrDropped = 0 },
                    ],
                },
            ],
        };
        ScenarioStatisticalEvidenceHarness.Evaluate(approximate).Availability
            .Should().Be(ScenarioStatisticalAvailability.UnsupportedApproximation);

        var insufficient = approximate with
        {
            Protocol = approximate.Protocol with { AdmitApproximation = true },
        };
        ScenarioStatisticalEvidenceHarness.Evaluate(insufficient).Availability
            .Should().Be(ScenarioStatisticalAvailability.InsufficientObservations);
    }

    [Fact]
    public void NonfiniteAndOverflowInputs_AreUnavailableWithExplicitReasons()
    {
        var nonfinite = SingleRequest(
            ScenarioStatisticalMethodIds.MedianAbsoluteDeviation,
            [1, double.NaN],
            minimum: 2);

        var nonfiniteResult = ScenarioStatisticalEvidenceHarness.Evaluate(nonfinite);

        nonfiniteResult.Availability.Should().Be(
            ScenarioStatisticalAvailability.InsufficientObservations);
        nonfiniteResult.Counts.ExcludedNonfinite.Should().Be(1);
        nonfiniteResult.UnavailableFields.Should().ContainSingle()
            .Which.Reason.Should().Be("minimum_independence_units_not_met");

        var overflow = PairedRequest([double.MaxValue], [-double.MaxValue]);
        var overflowResult = ScenarioStatisticalEvidenceHarness.Evaluate(overflow);

        overflowResult.Availability.Should().Be(
            ScenarioStatisticalAvailability.InsufficientObservations);
        overflowResult.UnavailableFields.Should().ContainSingle()
            .Which.Reason.Should().Be("nonfinite_calculation_result");
    }

    [Fact]
    public void RegistryMethods_UseV1Definitions()
    {
        var median = ScenarioStatisticalEvidenceHarness.Evaluate(
            SingleRequest(ScenarioStatisticalMethodIds.WindowMedian, [2, 4]));
        median.Estimate.Should().Be(3);

        var mad = ScenarioStatisticalEvidenceHarness.Evaluate(
            SingleRequest(ScenarioStatisticalMethodIds.MedianAbsoluteDeviation, [1, 2, 100]));
        mad.Dispersion.Should().Be(1);

        var wilson = ScenarioStatisticalEvidenceHarness.Evaluate(
            SingleRequest(
                ScenarioStatisticalMethodIds.WilsonScoreRate,
                [1, 1, 1, 1, 0],
                confidenceLevel: 0.95));
        wilson.Rate.Should().Be(new ScenarioStatisticalRate(4, 5, 0.8));
        wilson.Interval!.Lower.Should().BeApproximately(0.375534629, 1e-8);
        wilson.Interval.Upper.Should().BeApproximately(0.963775892, 1e-8);

        var slope = ScenarioStatisticalEvidenceHarness.Evaluate(
            SingleRequest(
                ScenarioStatisticalMethodIds.TheilSenSlope,
                [1, 3, 5],
                xValues: [0, 1, 2]));
        slope.Slope.Should().Be(2);
    }

    [Fact]
    public void Bootstrap_IsSeededBoundedAndOrderInvariant()
    {
        var firstRequest = SingleRequest(
            ScenarioStatisticalMethodIds.PercentileBootstrapInterval,
            [1, 2, 3, 4],
            bootstrapResamples: 512,
            confidenceLevel: 0.95,
            parentMethodId: ScenarioStatisticalMethodIds.WindowMedian);
        var reordered = firstRequest with
        {
            Arms =
            [
                firstRequest.Arms[0] with
                {
                    Attempts = firstRequest.Arms[0].Attempts.Reverse().ToArray(),
                    Observations = firstRequest.Arms[0].Observations.Reverse().ToArray(),
                },
            ],
        };

        var first = ScenarioStatisticalEvidenceHarness.Evaluate(firstRequest);
        var second = ScenarioStatisticalEvidenceHarness.Evaluate(firstRequest);
        var reorderedResult = ScenarioStatisticalEvidenceHarness.Evaluate(reordered);

        first.Availability.Should().Be(ScenarioStatisticalAvailability.Available);
        first.Interval.Should().NotBeNull();
        first.Interval!.ParentEstimate.Should().Be(2.5);
        first.CalculationProvenance.ResamplesRequested.Should().Be(512);
        first.CalculationProvenance.ResamplesCompleted.Should().Be(512);
        first.CalculationProvenance.FailedResamples.Should().Be(0);
        first.CalculationProvenance.SeedSha256.Should().HaveLength(64);
        ScenarioStatisticalEvidenceJson.SerializeResult(second)
            .Should().Be(ScenarioStatisticalEvidenceJson.SerializeResult(first));
        ScenarioStatisticalEvidenceJson.SerializeResult(reorderedResult)
            .Should().Be(ScenarioStatisticalEvidenceJson.SerializeResult(first));
    }

    [Fact]
    public void CorrelatedDuplicates_DoNotIncreaseIndependentCounts()
    {
        var request = SingleRequest(ScenarioStatisticalMethodIds.WindowMedian, [10, 20]);
        var duplicate = request.Arms[0].Observations[0] with
        {
            Id = "correlated-duplicate",
            SourceEventCount = 10_000,
        };
        request = request with
        {
            Arms =
            [
                request.Arms[0] with
                {
                    Observations = request.Arms[0].Observations.Concat([duplicate]).ToArray(),
                },
            ],
        };

        var result = ScenarioStatisticalEvidenceHarness.Evaluate(request);

        result.Availability.Should().Be(ScenarioStatisticalAvailability.Available);
        result.Estimate.Should().Be(15);
        result.Counts.IndependenceUnits.Should().Be(2);
        result.Counts.CollapsedCorrelatedDuplicates.Should().Be(1);
        result.Counts.SourceEvents.Should().Be(10_002);
    }

    [Fact]
    public void DeclaredUnitIdentity_PreventsNestedEvidenceFromInflatingCounts()
    {
        var capture = SingleRequest(ScenarioStatisticalMethodIds.WindowMedian, [10, 10]);
        capture = capture with
        {
            Protocol = capture.Protocol with
            {
                IndependenceUnit = ScenarioStatisticalIndependenceUnit.Capture,
            },
            Arms =
            [
                capture.Arms[0] with
                {
                    CompatibilityIdentity = Identity(
                        "observed",
                        unit: ScenarioStatisticalIndependenceUnit.Capture),
                    Attempts = [capture.Arms[0].Attempts[0]],
                    Observations =
                    [
                        capture.Arms[0].Observations[0] with
                        {
                            IndependenceUnitId = "caller-unit-a",
                        },
                        capture.Arms[0].Observations[1] with
                        {
                            RepetitionId = "rep-1",
                            AttemptId = "attempt-1",
                            CaptureId = "capture-1",
                            IndependenceUnitId = "caller-unit-b",
                        },
                    ],
                },
            ],
        };

        var captureResult = ScenarioStatisticalEvidenceHarness.Evaluate(capture);

        captureResult.Availability.Should().Be(ScenarioStatisticalAvailability.Available);
        captureResult.Counts.IndependenceUnits.Should().Be(1);
        captureResult.Counts.CollapsedCorrelatedDuplicates.Should().Be(1);

        var allocation = SingleRequest(ScenarioStatisticalMethodIds.WindowMedian, [7, 7]);
        allocation = allocation with
        {
            Protocol = allocation.Protocol with
            {
                IndependenceUnit = ScenarioStatisticalIndependenceUnit.EnvironmentAllocation,
            },
            Arms =
            [
                allocation.Arms[0] with
                {
                    CompatibilityIdentity = Identity(
                        "observed",
                        unit: ScenarioStatisticalIndependenceUnit.EnvironmentAllocation),
                    Observations = allocation.Arms[0].Observations
                        .Select((observation, index) => observation with
                        {
                            EnvironmentAllocationId = "allocation-shared",
                            IndependenceUnitId = $"caller-unit-{index}",
                        })
                        .ToArray(),
                },
            ],
        };

        var allocationResult = ScenarioStatisticalEvidenceHarness.Evaluate(allocation);

        allocationResult.Availability.Should().Be(ScenarioStatisticalAvailability.Available);
        allocationResult.Counts.IndependenceUnits.Should().Be(1);
        allocationResult.Counts.CollapsedCorrelatedDuplicates.Should().Be(1);
    }

    [Fact]
    public void FailedAndAbortedAttempts_RemainInProtocolDenominators()
    {
        var request = SingleRequest(ScenarioStatisticalMethodIds.WindowMedian, [12]);
        request = request with
        {
            Arms =
            [
                request.Arms[0] with
                {
                    Attempts = request.Arms[0].Attempts.Concat(
                    [
                        new ScenarioStatisticalAttempt(
                            "rep-2", "attempt-1", ScenarioStatisticalAttemptOutcome.Failed,
                            "Collection", "Collector failed."),
                        new ScenarioStatisticalAttempt(
                            "rep-3", "attempt-1", ScenarioStatisticalAttemptOutcome.Aborted,
                            "Environment", "Deadline expired."),
                    ]).ToArray(),
                },
            ],
        };

        var result = ScenarioStatisticalEvidenceHarness.Evaluate(request);

        result.Availability.Should().Be(ScenarioStatisticalAvailability.Available);
        result.Counts.Repetitions.Should().Be(3);
        result.Counts.Attempts.Should().Be(3);
        result.Counts.SuccessfulAttempts.Should().Be(1);
        result.Counts.FailedAttempts.Should().Be(1);
        result.Counts.AbortedAttempts.Should().Be(1);
        result.Counts.IndependenceUnits.Should().Be(1);
        result.Limitations.Should().Contain(item =>
            item.Contains("remain in the 3-attempt protocol denominator", StringComparison.Ordinal));
    }

    [Fact]
    public void PairedFailure_RemainsInDenominatorAndMakesPairIncomplete()
    {
        var request = PairedRequest([10], [12]);
        request = request with
        {
            Arms =
            [
                request.Arms[0],
                request.Arms[1] with
                {
                    Attempts =
                    [
                        request.Arms[1].Attempts[0] with
                        {
                            Outcome = ScenarioStatisticalAttemptOutcome.Failed,
                            FailureKind = "Collection",
                            Detail = "Candidate capture failed.",
                        },
                    ],
                    Observations = [],
                },
            ],
        };

        var result = ScenarioStatisticalEvidenceHarness.Evaluate(request);

        result.Availability.Should().Be(
            ScenarioStatisticalAvailability.InsufficientObservations);
        result.Counts.Attempts.Should().Be(1);
        result.Counts.FailedAttempts.Should().Be(1);
        result.Counts.IncompletePairs.Should().Be(1);
        result.UnavailableFields.Should().ContainSingle()
            .Which.Reason.Should().Be("minimum_complete_pairs_not_met");
    }

    [Fact]
    public void ApproximationCanBeAdmittedWithoutHidingItsLimitation()
    {
        var request = SingleRequest(
            ScenarioStatisticalMethodIds.WindowMedian,
            [1, 2, 3],
            admitApproximation: true,
            transform: observation => observation with
            {
                Representation = ScenarioStatisticalRepresentation.ReservoirSampled,
            });
        request = request with
        {
            Arms =
            [
                request.Arms[0] with
                {
                    CompatibilityIdentity = Identity(
                        "observed",
                        unit: ScenarioStatisticalIndependenceUnit.BoundedTimeWindow,
                        representation: "reservoir_sampled"),
                },
            ],
        };

        var result = ScenarioStatisticalEvidenceHarness.Evaluate(request);

        result.Availability.Should().Be(ScenarioStatisticalAvailability.Available);
        result.Limitations.Should().Contain(item =>
            item.Contains(nameof(ScenarioStatisticalRepresentation.ReservoirSampled), StringComparison.Ordinal));
    }

    [Fact]
    public void ControlArmAndTruncation_AreRepresentedExplicitly()
    {
        var control = SingleRequest(ScenarioStatisticalMethodIds.WindowMedian, [8, 8]);
        control = control with
        {
            Arms = [control.Arms[0] with { Role = ScenarioStatisticalArmRole.Control }],
        };

        var controlResult = ScenarioStatisticalEvidenceHarness.Evaluate(control);

        control.Arms[0].Role.Should().Be(ScenarioStatisticalArmRole.Control);
        controlResult.Availability.Should().Be(ScenarioStatisticalAvailability.Available);
        controlResult.Estimate.Should().Be(8);

        var truncated = SingleRequest(
            ScenarioStatisticalMethodIds.WindowMedian,
            [8, 9],
            transform: observation => observation with
            {
                Representation = ScenarioStatisticalRepresentation.Truncated,
            });
        truncated = truncated with
        {
            Arms =
            [
                truncated.Arms[0] with
                {
                    CompatibilityIdentity = Identity(
                        "observed",
                        unit: ScenarioStatisticalIndependenceUnit.BoundedTimeWindow,
                        representation: "truncated"),
                },
            ],
        };

        var truncatedResult = ScenarioStatisticalEvidenceHarness.Evaluate(truncated);

        truncatedResult.Availability.Should().Be(
            ScenarioStatisticalAvailability.UnsupportedApproximation);
        truncatedResult.UnavailableFields.Should().ContainSingle()
            .Which.Reason.Should().Be("approximate_representation_not_admitted");
    }

    [Fact]
    public void RelativeDelta_ZeroBaseline_HasFieldLevelUnavailableReason()
    {
        var result = ScenarioStatisticalEvidenceHarness.Evaluate(PairedRequest([0], [2]));

        result.Availability.Should().Be(ScenarioStatisticalAvailability.Available);
        result.Delta!.Absolute.Should().Be(2);
        result.Delta.Relative.Should().BeNull();
        result.UnavailableFields.Should().ContainSingle().Which.Should().Be(
            new ScenarioStatisticalUnavailableField("delta.relative", "baseline_estimate_zero"));
    }

    [Fact]
    public void LiveTrialProjection_PreservesFailedAttemptsAndBounds()
    {
        var manifest = ScenarioManifestLoader.LoadAll().Single(item => item.Id == "gc-storm");
        var evidence = ScenarioJson.ReadEvidence(
            ScenarioManifestLoader.ScenarioPath("Fixtures", "gc-storm.linux.evidence.json"));
        var passed = new ScenarioTrialArtifact(
            ScenarioJson.CurrentTrialArtifactSchemaVersion,
            manifest.Id,
            1,
            1,
            ScenarioTrialArtifactOutcome.Passed,
            ScenarioFailureKind.None,
            "passed",
            evidence,
            ScenarioEvaluator.CreateReport(manifest, evidence));
        var failed = new ScenarioTrialArtifact(
            ScenarioJson.CurrentTrialArtifactSchemaVersion,
            manifest.Id,
            2,
            1,
            ScenarioTrialArtifactOutcome.Failed,
            ScenarioFailureKind.Collection,
            "failed",
            null,
            null);
        var protocol = Protocol(ScenarioStatisticalMethodIds.WindowMedian);

        var request = ScenarioStatisticalEvidenceHarness.ProjectLiveTrialArtifacts(
            protocol,
            Identity("observed"),
            [passed, failed],
            "loh-size-max");
        var result = ScenarioStatisticalEvidenceHarness.Evaluate(request);

        result.Availability.Should().Be(ScenarioStatisticalAvailability.Available);
        result.Counts.Attempts.Should().Be(2);
        result.Counts.FailedAttempts.Should().Be(1);
        result.Counts.IndependenceUnits.Should().Be(1);
        result.Estimate.Should().BeGreaterThan(0);
    }

    [Fact]
    public void FixedBounds_RejectUnboundedObservationSets()
    {
        var values = Enumerable.Range(
            0,
            ScenarioStatisticalEvidenceBounds.MaximumObservations + 1)
            .Select(index => (double)index)
            .ToArray();
        var request = SingleRequest(ScenarioStatisticalMethodIds.WindowMedian, values);

        var action = () => ScenarioStatisticalEvidenceHarness.Evaluate(request);

        action.Should().Throw<InvalidDataException>().WithMessage("*fixed harness bound*");
    }

    [Fact]
    public void RegistryDefinitions_MatchDesignRegistryV1Exactly()
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(
                AppContext.BaseDirectory,
                "Design",
                "statistical-evidence-method-registry.v1.json")));
        var expected = document.RootElement.GetProperty("methods")
            .EnumerateArray()
            .Select(method => new
            {
                Id = method.GetProperty("id").GetString(),
                Version = method.GetProperty("version").GetInt32(),
                Minimum = method.GetProperty("minimumInputCount").GetInt32(),
                Units = method.GetProperty("allowedIndependenceUnits").EnumerateArray()
                    .Select(item => item.GetString()).ToArray(),
                RequiresPairing = method.GetProperty("requiresPairing").GetBoolean(),
                Produces = method.GetProperty("produces").EnumerateArray()
                    .Select(item => item.GetString()).ToArray(),
                EmitsVerdict = method.GetProperty("emitsVerdict").GetBoolean(),
            })
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var actual = ScenarioStatisticalEvidenceHarness.RegistryDefinitions
            .Select(method => new
            {
                method.Id,
                method.Version,
                Minimum = method.MinimumInputCount,
                Units = method.AllowedIndependenceUnits.Select(UnitName).ToArray(),
                method.RequiresPairing,
                Produces = method.Produces.ToArray(),
                method.EmitsVerdict,
            })
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

        actual.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    [Fact]
    public void InvalidMethodUnitPairingAndObservationShapes_FailExplicitly()
    {
        var unsupported = SingleRequest(ScenarioStatisticalMethodIds.WindowMedian, [1]) with
        {
            Protocol = Protocol(ScenarioStatisticalMethodIds.WindowMedian) with
            {
                Method = new ScenarioStatisticalMethod(
                    ScenarioStatisticalMethodIds.WindowMedian,
                    2),
            },
        };
        ScenarioStatisticalEvidenceHarness.Evaluate(unsupported).Availability
            .Should().Be(ScenarioStatisticalAvailability.Incompatible);

        var eventLevel = SingleRequest(ScenarioStatisticalMethodIds.WindowMedian, [1]);
        eventLevel = eventLevel with
        {
            Protocol = eventLevel.Protocol with
            {
                IndependenceUnit = ScenarioStatisticalIndependenceUnit.Event,
            },
            Arms =
            [
                eventLevel.Arms[0] with
                {
                    CompatibilityIdentity = Identity(
                        "observed",
                        unit: ScenarioStatisticalIndependenceUnit.Event),
                },
            ],
        };
        ScenarioStatisticalEvidenceHarness.Evaluate(eventLevel).UnavailableFields
            .Should().ContainSingle().Which.Reason.Should().Be("independence_unit_not_allowed");

        var pairOrder = PairedRequest([1], [2]);
        pairOrder = pairOrder with
        {
            Arms =
            [
                pairOrder.Arms[0],
                pairOrder.Arms[1] with
                {
                    Observations =
                    [
                        pairOrder.Arms[1].Observations[0] with
                        {
                            PairOrder = ScenarioStatisticalPairOrder.CandidateFirst,
                        },
                    ],
                },
            ],
        };
        ScenarioStatisticalEvidenceHarness.Evaluate(pairOrder).UnavailableFields
            .Should().ContainSingle().Which.Reason.Should().Be("pair_order_mismatch");

        var conflict = SingleRequest(ScenarioStatisticalMethodIds.WindowMedian, [1]);
        conflict = conflict with
        {
            Arms =
            [
                conflict.Arms[0] with
                {
                    Observations = conflict.Arms[0].Observations.Concat(
                    [
                        conflict.Arms[0].Observations[0] with
                        {
                            Id = "conflict",
                            IndependenceUnitId = "different-caller-id",
                            Value = 2,
                        },
                    ]).ToArray(),
                },
            ],
        };
        ScenarioStatisticalEvidenceHarness.Evaluate(conflict).UnavailableFields
            .Should().ContainSingle().Which.Reason.Should().Be(
                "conflicting_independence_unit_values");

        var failedObservation = SingleRequest(ScenarioStatisticalMethodIds.WindowMedian, [1]);
        failedObservation = failedObservation with
        {
            Arms =
            [
                failedObservation.Arms[0] with
                {
                    Attempts =
                    [
                        failedObservation.Arms[0].Attempts[0] with
                        {
                            Outcome = ScenarioStatisticalAttemptOutcome.Failed,
                        },
                    ],
                },
            ],
        };
        ScenarioStatisticalEvidenceHarness.Evaluate(failedObservation).UnavailableFields
            .Should().ContainSingle().Which.Reason.Should().Be(
                "observation_from_unsuccessful_attempt");

        var zeroWilson = ScenarioStatisticalEvidenceHarness.Evaluate(
            SingleRequest(
                ScenarioStatisticalMethodIds.WilsonScoreRate,
                [],
                confidenceLevel: 0.95));
        zeroWilson.Availability.Should().Be(
            ScenarioStatisticalAvailability.InsufficientObservations);
        zeroWilson.Rate.Should().BeNull();
    }

    [Fact]
    public void DuplicateOrBlankArmIdentities_FailBeforeCompatibilityAssessment()
    {
        var duplicate = PairedRequest([1], [2]);
        duplicate = duplicate with
        {
            Arms =
            [
                duplicate.Arms[0],
                duplicate.Arms[1] with
                {
                    Id = duplicate.Arms[0].Id,
                    CompatibilityIdentity = Identity("different-provider"),
                },
            ],
        };

        Action duplicateAction = () => ScenarioStatisticalEvidenceHarness.Evaluate(duplicate);
        duplicateAction.Should().Throw<InvalidDataException>()
            .WithMessage("*duplicate arm identities*");

        var blank = SingleRequest(ScenarioStatisticalMethodIds.WindowMedian, [1]);
        blank = blank with { Arms = [blank.Arms[0] with { Id = " " }] };

        Action blankAction = () => ScenarioStatisticalEvidenceHarness.Evaluate(blank);
        blankAction.Should().Throw<InvalidDataException>()
            .WithMessage("*arm.Id is required*");
    }

    [Theory]
    [InlineData("lostOrDropped")]
    [InlineData("representation")]
    [InlineData("outcome")]
    public void ReplayFixture_RejectsMissingRequiredEvidenceFields(string fieldName)
    {
        var sourcePath = ScenarioManifestLoader.ScenarioPath(
            "Fixtures", "StatisticalEvidence", "window-median-positive.v1.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(sourcePath))!.AsObject();
        var arm = root["request"]!["arms"]![0]!.AsObject();
        var removed = fieldName == "outcome"
            ? arm["attempts"]![0]!.AsObject().Remove(fieldName)
            : arm["observations"]![0]!.AsObject().Remove(fieldName);
        removed.Should().BeTrue();
        var path = Path.Combine(
            Path.GetTempPath(),
            $"dotnet-diagnostics-statistical-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, root.ToJsonString());

            var action = () => ScenarioStatisticalEvidenceJson.ReadFixture(path);

            action.Should().Throw<InvalidDataException>()
                .WithMessage($"*{fieldName}*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static TheoryData<string> StatisticalReplayFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(
                     ScenarioManifestLoader.ScenarioPath("Fixtures", "StatisticalEvidence"),
                     "*.json")
                     .Order(StringComparer.Ordinal))
        {
            data.Add(path);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StatisticalReplayFixtures))]
    public void CompactReplayFixture_ReproducesExpectedEvidence(string path)
    {
        var fixture = ScenarioStatisticalEvidenceJson.ReadFixture(path);

        var result = ScenarioStatisticalEvidenceHarness.Evaluate(fixture.Request);

        result.Availability.Should().Be(fixture.Expected.Availability);
        result.Counts.IndependenceUnits.Should().Be(fixture.Expected.IndependenceUnits);
        result.Counts.Attempts.Should().Be(fixture.Expected.Attempts);
        result.Counts.Windows.Should().Be(fixture.Expected.Windows);
        result.Counts.Captures.Should().Be(fixture.Expected.Captures);
        result.Counts.FailedAttempts.Should().Be(fixture.Expected.FailedAttempts);
        result.Counts.AbortedAttempts.Should().Be(fixture.Expected.AbortedAttempts);
        result.Estimate.Should().Be(fixture.Expected.Estimate);
        result.Delta?.Absolute.Should().Be(fixture.Expected.AbsoluteDelta);
        result.Interval?.Lower.Should().Be(fixture.Expected.IntervalLower);
        result.Interval?.Upper.Should().Be(fixture.Expected.IntervalUpper);
        result.CalculationProvenance.SeedSha256.Should().Be(fixture.Expected.SeedSha256);
        result.CalculationProvenance.ResamplesRequested.Should().Be(
            fixture.Expected.ResamplesRequested);
        (result.UnavailableFields.Count == 0 ? null : result.UnavailableFields[0].Reason).Should().Be(
            fixture.Expected.UnavailableReason);
        result.CalculationProvenance.OrderedInputSha256
            .Should().Be(fixture.Expected.OrderedInputSha256);
    }

    private static ScenarioStatisticalEvidenceRequest SingleRequest(
        string methodId,
        IReadOnlyList<double> values,
        int minimum = 1,
        bool admitApproximation = false,
        int? bootstrapResamples = null,
        double? confidenceLevel = null,
        string? parentMethodId = null,
        IReadOnlyList<double>? xValues = null,
        Func<ScenarioStatisticalObservation, ScenarioStatisticalObservation>? transform = null)
    {
        var attempts = values.Select((_, index) => new ScenarioStatisticalAttempt(
            $"rep-{index + 1}",
            "attempt-1",
            ScenarioStatisticalAttemptOutcome.Passed)).ToArray();
        var observations = values.Select((value, index) =>
        {
            var observation = new ScenarioStatisticalObservation(
                $"observation-{index + 1}",
                $"rep-{index + 1}",
                "attempt-1",
                $"allocation-{index + 1}",
                $"capture-{index + 1}",
                $"window-{index + 1}",
                $"unit-{index + 1}",
                value,
                xValues?[index],
                SourceEventCount: 1,
                LostOrDropped: 0,
                ScenarioStatisticalRepresentation.Exact);
            return transform?.Invoke(observation) ?? observation;
        }).ToArray();
        var unit = methodId == ScenarioStatisticalMethodIds.WindowMedian
                || methodId == ScenarioStatisticalMethodIds.MedianAbsoluteDeviation
                || methodId == ScenarioStatisticalMethodIds.TheilSenSlope
                || methodId == ScenarioStatisticalMethodIds.PercentileBootstrapInterval
            ? ScenarioStatisticalIndependenceUnit.BoundedTimeWindow
            : ScenarioStatisticalIndependenceUnit.ProcessRun;
        var protocol = Protocol(
            methodId,
            unit,
            minimum,
            admitApproximation,
            bootstrapResamples,
            confidenceLevel,
            parentMethodId);
        return new(
            ScenarioStatisticalEvidenceJson.CurrentSchemaVersion,
            protocol,
            [new ScenarioStatisticalArm(
                "observed",
                ScenarioStatisticalArmRole.Observed,
                Identity("observed", unit: unit),
                attempts,
                observations)],
            "observed");
    }

    private static ScenarioStatisticalEvidenceRequest PairedRequest(
        IReadOnlyList<double> baseline,
        IReadOnlyList<double> candidate,
        ScenarioStatisticalArmRole candidateRole = ScenarioStatisticalArmRole.Candidate)
    {
        baseline.Count.Should().Be(candidate.Count);
        var attempts = baseline.Select((_, index) => new ScenarioStatisticalAttempt(
            $"rep-{index + 1}",
            "attempt-1",
            ScenarioStatisticalAttemptOutcome.Passed)).ToArray();
        ScenarioStatisticalObservation[] Observations(
            IReadOnlyList<double> values,
            string armId)
            => values.Select((value, index) => new ScenarioStatisticalObservation(
                $"{armId}-observation-{index + 1}",
                $"rep-{index + 1}",
                "attempt-1",
                $"allocation-{index + 1}",
                $"{armId}-capture-{index + 1}",
                null,
                $"{armId}-unit-{index + 1}",
                value,
                null,
                SourceEventCount: 100,
                LostOrDropped: 0,
                ScenarioStatisticalRepresentation.Exact,
                $"pair-{index + 1}",
                index % 2 == 0
                    ? ScenarioStatisticalPairOrder.BaselineFirst
                    : ScenarioStatisticalPairOrder.CandidateFirst)).ToArray();

        return new(
            ScenarioStatisticalEvidenceJson.CurrentSchemaVersion,
            Protocol(
                ScenarioStatisticalMethodIds.PairedDelta,
                ScenarioStatisticalIndependenceUnit.ProcessRun),
            [
                new ScenarioStatisticalArm(
                    "baseline",
                    ScenarioStatisticalArmRole.Baseline,
                    Identity("baseline"),
                    attempts,
                    Observations(baseline, "baseline")),
                new ScenarioStatisticalArm(
                    "candidate",
                    candidateRole,
                    Identity("candidate"),
                    attempts,
                    Observations(candidate, "candidate")),
            ]);
    }

    private static ScenarioStatisticalProtocol Protocol(
        string methodId,
        ScenarioStatisticalIndependenceUnit unit = ScenarioStatisticalIndependenceUnit.ProcessRun,
        int minimum = 1,
        bool admitApproximation = false,
        int? bootstrapResamples = null,
        double? confidenceLevel = null,
        string? parentMethodId = null)
        => new(
            "test-protocol",
            "1",
            new ScenarioStatisticalMethod(methodId, 1),
            unit,
            minimum,
            AdmitLoss: false,
            admitApproximation,
            bootstrapResamples,
            confidenceLevel,
            parentMethodId);

    private static ScenarioStatisticalCompatibilityIdentity Identity(
        string axisValue,
        string samplingPeriod = "1ms",
        ScenarioStatisticalIndependenceUnit unit = ScenarioStatisticalIndependenceUnit.ProcessRun,
        string representation = "exact")
        => new(
        [
            new("target.runtime-flavor", "CoreCLR"),
            new("target.runtime-version-policy", "major-minor"),
            new("target.os", "test-os"),
            new("target.architecture", "x64"),
            new("target.process-run-id", $"run-{axisValue}"),
            new("backend.id", "EventPipe"),
            new("backend.version", "1"),
            new("quantity.id", "request-latency"),
            new("quantity.unit", "ms"),
            new("quantity.aggregation", "median"),
            new("quantity.normalization", "per-accepted-request"),
            new("quantity.filters", "endpoint=/work"),
            new("quantity.representation", representation),
            new("capture.duration", "10s"),
            new("capture.windowing", "non-overlapping-1s"),
            new("capture.providers", "System.Runtime"),
            new("capture.sampling-period", samplingPeriod),
            new("capture.retention", "bounded-v1"),
            new("capture.enrichment", "none"),
            new("workload.scenario", "known-shape"),
            new("workload.version", "1"),
            new("workload.parameters", "size=100"),
            new("workload.concurrency", "1"),
            new("workload.deployment", "local"),
            new("workload.variant", axisValue),
            new("independence.unit", UnitName(unit)),
            new("independence.nesting", "events->windows->captures->process-runs"),
            new("independence.pairing-key", "repetition"),
            new("comparison.axis", "workload.variant"),
        ]);

    private static string UnitName(ScenarioStatisticalIndependenceUnit unit)
        => unit switch
        {
            ScenarioStatisticalIndependenceUnit.Event => "event",
            ScenarioStatisticalIndependenceUnit.BoundedTimeWindow => "bounded_time_window",
            ScenarioStatisticalIndependenceUnit.Capture => "capture",
            ScenarioStatisticalIndependenceUnit.ProcessRun => "process_run",
            ScenarioStatisticalIndependenceUnit.EnvironmentAllocation => "environment_allocation",
            _ => throw new ArgumentOutOfRangeException(nameof(unit)),
        };
}
