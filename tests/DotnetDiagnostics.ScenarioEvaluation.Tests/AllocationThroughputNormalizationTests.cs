using System.Reflection;
using System.Text.Json;
using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class AllocationThroughputNormalizationTests
{
    [Fact]
    public void DraftProtocol_DeclaresFocusedFreshOneShotInventory()
    {
        var root = AllocationThroughputNormalizationProtocolDocument.Build(
            "draft",
            frozenAtUtc: null,
            includeFrozenHashes: false);

        root.GetProperty("protocolVersion").GetString().Should().Be("1.0.0");
        root.GetProperty("sourceBaseCommit").GetString().Should().Be(
            "56c2ef4a9eb296beb1f64d33b8f47ccf693250a3");
        root.GetProperty("prerequisites")
            .GetProperty("preservedV3Merge").GetString().Should().Be(
                "6777e0a05ca68a5a55e1d6191860ff36012db450");
        root.GetProperty("prerequisites")
            .GetProperty("ledgerSchemaVersion").GetInt32().Should().Be(2);
        root.GetProperty("prerequisites")
            .GetProperty("projectorSchema").GetString().Should().Be(
                AllocationDirectionalProjection.SchemaIdentity);
        root.GetProperty("inventory").GetProperty("cohortCount").GetInt32()
            .Should().Be(2);
        root.GetProperty("inventory").GetProperty("totalPairs").GetInt32()
            .Should().Be(15);
        root.GetProperty("inventory").GetProperty("liveMcpSides").GetInt32()
            .Should().Be(30);
        root.GetProperty("cohorts").GetArrayLength().Should().Be(2);
        root.GetProperty("boundaries").GetProperty("blindedEvaluation")
            .GetBoolean().Should().BeFalse();
        root.GetProperty("boundaries").GetProperty("productImplementation")
            .GetBoolean().Should().BeFalse();
        root.GetProperty("failurePolicy").GetString().Should()
            .Contain("at most once")
            .And.Contain("No retry");
    }

    [Fact]
    public void Plan_HasIndependentPacingAndEqualAllocationWithinEveryPair()
    {
        FluentActions
            .Invoking(
                AllocationThroughputNormalizationCapturePlan
                    .ValidateConstruction)
            .Should().NotThrow();

        AllocationThroughputNormalizationCapturePlan.Cohorts.Should()
            .HaveCount(2)
            .And.OnlyHaveUniqueItems(cohort => cohort.CohortId);
        AllocationThroughputNormalizationCapturePlan.Cohorts
            .Select(cohort => (
                cohort.BaselinePacing.PauseMilliseconds,
                cohort.BaselinePacing.PauseEveryOperations))
            .Should().OnlyHaveUniqueItems();
        foreach (var cohort in
                 AllocationThroughputNormalizationCapturePlan.Cohorts)
        {
            cohort.BaselinePacing.OperationIdentity.Should().Be(
                cohort.CurrentPacing.OperationIdentity);
            cohort.AaEntry.Baseline.ExpectedPayloadBytesPerOperation.Should()
                .Be(cohort.AaEntry.Current.ExpectedPayloadBytesPerOperation);
            cohort.EffectEntry.Baseline.ExpectedPayloadBytesPerOperation.Should()
                .Be(cohort.EffectEntry.Current.ExpectedPayloadBytesPerOperation);
            cohort.EffectEntry.Baseline.Workers.Should()
                .Be(cohort.EffectEntry.Current.Workers);
        }
    }

    [Fact]
    public void PacingDefinitions_AreSourceBackedBySameAllocationOperation()
    {
        var source = File.ReadAllText(RepositoryPath(
            "samples/BadCodeSample/AllocationShapeWorkload.cs"));

        source.Should().Contain(
            "RunPrimaryPaced(TimeSpan duration)")
            .And.Contain("PrimaryOperation")
            .And.Contain("\"primary-paced\"")
            .And.Contain("RunLowBoundedV4(TimeSpan duration)")
            .And.Contain("LowOperation")
            .And.Contain("\"low-bounded-v4\"")
            .And.Contain("pauseEveryOperations: 8");
    }

    [Fact]
    public void SyntheticReproducibleFailuresAcrossBothCohorts_AreNoGo()
    {
        var report = AllocationThroughputNormalizationHarness.Evaluate(
            SyntheticFixture(
                cohortEffectDeltas:
                [
                    [0.50m, 0.52m, 0.54m],
                    [0.30m, 0.32m, 0.34m],
                ]));

        report.Decision.Should().Be(
            AllocationThroughputNormalizationDecision.NoGo);
        report.Cohorts.Should().OnlyContain(cohort =>
            cohort.Assessable
            && cohort.WorkloadManipulationEstablished
            && cohort.MedianExceedsEquivalenceMargin
            && !cohort.NormalizedQuantityEquivalent
            && cohort.EffectAbsoluteRelativeDeltas.Inliers.Count >= 2);
        report.BlindedEvaluationRan.Should().BeFalse();
    }

    [Fact]
    public void SyntheticEquivalentCohorts_AllowNormalizationToSurvive()
    {
        var report = AllocationThroughputNormalizationHarness.Evaluate(
            SyntheticFixture(
                cohortEffectDeltas:
                [
                    [0.08m, 0.09m, 0.10m],
                    [0.02m, 0.03m, 0.04m],
                ]));

        report.Decision.Should().Be(
            AllocationThroughputNormalizationDecision.NormalizationSurvives);
        report.Cohorts.Should().OnlyContain(cohort =>
            cohort.NormalizedQuantityEquivalent
            && !cohort.MedianExceedsEquivalenceMargin
            && cohort.EquivalenceMargin == 0.10m);
    }

    [Fact]
    public void ConflictingCohorts_AreInconclusive()
    {
        var report = AllocationThroughputNormalizationHarness.Evaluate(
            SyntheticFixture(
                cohortEffectDeltas:
                [
                    [0.50m, 0.52m, 0.54m],
                    [0.02m, 0.03m, 0.04m],
                ]));

        report.Decision.Should().Be(
            AllocationThroughputNormalizationDecision.Inconclusive);
        report.Cohorts.Select(cohort => cohort.Classification).Should()
            .Contain("normalization-failed")
            .And.Contain("within-equivalence-margin");
    }

    [Fact]
    public void PairFirstOutlierRetention_UsesMedianAndTwoInliers()
    {
        var report = AllocationThroughputNormalizationHarness.Evaluate(
            SyntheticFixture(
                cohortEffectDeltas:
                [
                    [0.50m, 0.52m, 0.01m],
                    [0.30m, 0.32m, 0.34m],
                ]));

        var first = report.Cohorts[0];
        first.EffectAbsoluteRelativeDeltas.OutlierIndexes.Should().Equal(2);
        first.EffectAbsoluteRelativeDeltas.Inliers.Should().HaveCount(2);
        first.EffectAbsoluteRelativeDeltas.Median.Should().Be(0.50m);
        first.InlierMedianAbsoluteRelativeDelta.Should().Be(0.51m);
        first.MedianExceedsEquivalenceMargin.Should().BeTrue();
        report.Decision.Should().Be(
            AllocationThroughputNormalizationDecision.NoGo);
    }

    [Fact]
    public void NoGoUsesInlierMedianRatherThanOutlierInclusiveMedian()
    {
        var report = AllocationThroughputNormalizationHarness.Evaluate(
            SyntheticFixture(
                cohortEffectDeltas:
                [
                    [0.09m, 0.11m, 0.90m],
                    [0.30m, 0.32m, 0.34m],
                ]));

        var first = report.Cohorts[0];
        first.EffectAbsoluteRelativeDeltas.Median.Should().Be(0.11m);
        first.EffectAbsoluteRelativeDeltas.OutlierIndexes.Should().Equal(2);
        first.InlierMedianAbsoluteRelativeDelta.Should().Be(0.10m);
        first.MedianExceedsEquivalenceMargin.Should().BeFalse();
        report.Decision.Should().Be(
            AllocationThroughputNormalizationDecision.Inconclusive);
    }

    [Fact]
    public void EquivalenceBoundaryIsInclusiveAndThroughputBoundaryIsStrict()
    {
        var atEquivalence = AllocationThroughputNormalizationHarness.Evaluate(
            SyntheticFixture(
                cohortEffectDeltas:
                [
                    [0.10m, 0.10m, 0.10m],
                    [0.10m, 0.10m, 0.10m],
                ]));
        var atThroughput = AllocationThroughputNormalizationHarness.Evaluate(
            SyntheticFixture(
                cohortEffectDeltas:
                [
                    [0.02m, 0.02m, 0.02m],
                    [0.02m, 0.02m, 0.02m],
                ],
                throughputMultiplier: 1.25m));

        atEquivalence.Decision.Should().Be(
            AllocationThroughputNormalizationDecision.NormalizationSurvives);
        atThroughput.Decision.Should().Be(
            AllocationThroughputNormalizationDecision.Inconclusive);
        atThroughput.Cohorts.Should().OnlyContain(cohort =>
            !cohort.WorkloadManipulationEstablished);
    }

    [Fact]
    public void FailedSideRemainsInDenominatorAndIsInconclusive()
    {
        var fixture = SyntheticFixture(
            cohortEffectDeltas:
            [
                [0.50m, 0.52m, 0.54m],
                [0.30m, 0.32m, 0.34m],
            ]);
        var target = fixture.Pairs.First(pair =>
            pair.Current.Phase == "effect");
        var failed = fixture with
        {
            Pairs = fixture.Pairs.Select(pair =>
                pair.PairId == target.PairId
                    ? pair with
                    {
                        Current = Failure(pair.Current),
                    }
                    : pair).ToArray(),
        };

        var report =
            AllocationThroughputNormalizationHarness.Evaluate(failed);

        report.AttemptedPairs.Should().Be(15);
        report.AttemptedSides.Should().Be(30);
        report.FailedOrAbortedSides.Should().Be(1);
        report.Decision.Should().Be(
            AllocationThroughputNormalizationDecision.Inconclusive);
    }

    [Fact]
    public void FailedBaselineRemainsInDenominatorAndIsInconclusive()
    {
        var fixture = SyntheticFixture(
            cohortEffectDeltas:
            [
                [0.50m, 0.52m, 0.54m],
                [0.30m, 0.32m, 0.34m],
            ]);
        var target = fixture.Pairs.First(pair =>
            pair.Current.Phase == "effect");
        var failed = fixture with
        {
            Pairs = fixture.Pairs.Select(pair =>
                pair.PairId == target.PairId
                    ? pair with
                    {
                        Baseline = Failure(pair.Baseline),
                    }
                    : pair).ToArray(),
        };

        var report =
            AllocationThroughputNormalizationHarness.Evaluate(failed);

        report.FailedOrAbortedSides.Should().Be(1);
        report.Decision.Should().Be(
            AllocationThroughputNormalizationDecision.Inconclusive);
    }

    [Fact]
    public void OperatingLimitBreachIsHarnessInconclusiveNotNoGo()
    {
        var fixture = SyntheticFixture(
            cohortEffectDeltas:
            [
                [0.50m, 0.52m, 0.54m],
                [0.30m, 0.32m, 0.34m],
            ]);
        var first = fixture.Pairs[0];
        var changed = fixture with
        {
            Pairs = fixture.Pairs.Select(pair =>
                pair.PairId == first.PairId
                    ? pair with
                    {
                        Baseline = pair.Baseline with
                        {
                            SampledAllocationTicks = 75_001,
                        },
                    }
                    : pair).ToArray(),
        };

        var report =
            AllocationThroughputNormalizationHarness.Evaluate(changed);

        report.MaximumObservedAllocationEvents.Should().Be(75_001);
        report.Decision.Should().Be(
            AllocationThroughputNormalizationDecision.Inconclusive);
    }

    [Fact]
    public void ProjectorLedgerTaxonomyAndBoundedGeometryRemainRequired()
    {
        AllocationDirectionalLedger.CurrentSchemaVersion.Should().Be(2);
        AllocationDirectionalProjection.SchemaIdentity.Should().Be(
            "collect_sample:data.kind=allocation:data.allocation/v1");
        AllocationThroughputNormalizationCapturePlan.CaptureDurationSeconds
            .Should().Be(
                AllocationDirectionalBoundedCalibrationPlan.OperatingSettings
                    .CaptureDurationSeconds);
        AllocationThroughputNormalizationCapturePlan.ArmDelayMilliseconds
            .Should().Be(
                AllocationDirectionalBoundedCalibrationPlan.OperatingSettings
                    .ArmDelayMilliseconds);
        AllocationThroughputNormalizationCapturePlan.WorkloadMilliseconds
            .Should().Be(
                AllocationDirectionalBoundedCalibrationPlan.OperatingSettings
                    .WorkloadMilliseconds);
        AllocationThroughputNormalizationCapturePlan
            .AllocationEventOperatingLimit.Should().Be(75_000);

        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        typeof(EventPipeAllocationSampler)
            .GetField("MaxAllocationFrames", flags)!
            .GetRawConstantValue().Should().Be(16_384);
        typeof(EventPipeAllocationSampler)
            .GetField("MaxAllocationEvents", flags)!
            .GetRawConstantValue().Should().Be(100_000);

        const string cap =
            """{"error":{"kind":"ResourceLimitExceeded","code":"allocation.capture.limit","detail":"redacted"}}""";
        var projected = AllocationDirectionalProjection.Project(
            "throughput-projection",
            "throughput-workload",
            cap,
            isError: true);
        projected.Evidence.Stage.Should().Be(
            AllocationDirectionalAttemptStage.McpResponse);
        projected.Evidence.FailureCategory.Should().Be(
            AllocationDirectionalFailureCategory.McpIsError);
        projected.Evidence.ErrorKind.Should().Be("ResourceLimitExceeded");
        projected.Evidence.ErrorCode.Should().Be("allocation.capture.limit");
    }

    [Fact]
    public void LedgerRejectsAnythingOtherThanOneStartAndTerminalPerSide()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"throughput-ledger-{Guid.NewGuid():n}.jsonl");
        try
        {
            AllocationDirectionalLedger.AppendStarted(
                path,
                "attempt-1",
                DateTimeOffset.UtcNow);
            AllocationDirectionalLedger.AppendCompleted(
                path,
                Observation(
                    "attempt-1",
                    AllocationThroughputNormalizationCapturePlan
                        .HealthyControl.Baseline,
                    operations: 100,
                    bytesPerOperation: 100m),
                DateTimeOffset.UtcNow);

            FluentActions.Invoking(() =>
                    AllocationDirectionalLedger.ValidateExactTerminalAccounting(
                        path,
                        ["attempt-1"]))
                .Should().NotThrow();
            File.AppendAllText(
                path,
                File.ReadLines(path).Last() + Environment.NewLine);
            FluentActions.Invoking(() =>
                    AllocationDirectionalLedger.ValidateExactTerminalAccounting(
                        path,
                        ["attempt-1"]))
                .Should().Throw<InvalidDataException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FreshIdentitiesDoNotIntersectPreservedArtifacts()
    {
        var fixtureDirectory = RepositoryPath(
            "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional");
        var priorValues = Directory.EnumerateFiles(
                fixtureDirectory,
                "*",
                SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).StartsWith(
                "allocation-throughput-normalization-falsifier-v1",
                StringComparison.Ordinal))
            .SelectMany(ReadIdentityValues)
            .ToHashSet(StringComparer.Ordinal);
        var pairIds =
            AllocationThroughputNormalizationCapturePlan.Entries.SelectMany(
                entry => Enumerable.Range(
                        1,
                        AllocationDirectionalBounds.RequiredPairs)
                    .Select(repetition =>
                        $"{entry.PairPrefix}-{repetition:D2}"))
                .ToArray();
        var attemptIds = pairIds.SelectMany(pairId =>
            new[] { $"{pairId}-baseline", $"{pairId}-current" }).ToArray();
        var workloads =
            AllocationThroughputNormalizationCapturePlan.Entries.SelectMany(
                    entry => new[]
                    {
                        entry.Baseline.WorkloadIdentity,
                        entry.Current.WorkloadIdentity,
                    })
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        pairIds.Should().NotIntersectWith(priorValues);
        attemptIds.Should().NotIntersectWith(priorValues);
        workloads.Should().NotIntersectWith(priorValues);
    }

    [Fact]
    public void PreservedProtocolsAndResultsRemainHashGuarded()
    {
        AllocationThroughputNormalizationProtocol.PreservedArtifactPaths.Should()
            .OnlyHaveUniqueItems();
        foreach (var relativePath in
                 AllocationThroughputNormalizationProtocol
                     .PreservedArtifactPaths)
        {
            var path = RepositoryPath(relativePath);
            File.Exists(path).Should().BeTrue(relativePath);
            AllocationDirectionalProtocol.CanonicalByteCount(path).Should()
                .BeGreaterThan(0);
            AllocationDirectionalProtocol.Sha256(path).Should()
                .MatchRegex("^[0-9a-f]{64}$");
        }
    }

    private static AllocationDirectionalFixture SyntheticFixture(
        IReadOnlyList<IReadOnlyList<decimal>> cohortEffectDeltas,
        decimal throughputMultiplier = 2m)
    {
        cohortEffectDeltas.Should().HaveCount(
            AllocationThroughputNormalizationCapturePlan.Cohorts.Count);
        var pairs = new List<AllocationDirectionalPair>();
        foreach (var entry in
                 AllocationThroughputNormalizationCapturePlan.Entries)
        {
            var cohortIndex =
                AllocationThroughputNormalizationCapturePlan.Cohorts
                    .Select((cohort, index) => (cohort, index))
                    .Where(value =>
                        ReferenceEquals(value.cohort.AaEntry, entry)
                        || ReferenceEquals(value.cohort.EffectEntry, entry))
                    .Select(value => (int?)value.index)
                    .SingleOrDefault();
            for (var repetition = 1;
                 repetition <= AllocationDirectionalBounds.RequiredPairs;
                 repetition++)
            {
                var isEffect = entry.Current.Phase == "effect";
                pairs.Add(SyntheticPair(
                    entry,
                    repetition,
                    isEffect && cohortIndex is not null
                        ? cohortEffectDeltas[cohortIndex.Value][repetition - 1]
                        : 0.01m,
                    isEffect ? throughputMultiplier : 1m));
            }
        }

        return new(
            AllocationDirectionalJson.CurrentSchemaVersion,
            AllocationThroughputNormalizationProtocol.ProtocolId,
            AllocationThroughputNormalizationProtocol.ProtocolVersion,
            "synthetic-protocol",
            "synthetic-protocol",
            [],
            "2026-11-04T15:30:00Z",
            AllocationThroughputNormalizationProtocol.SourceBaseCommit,
            "synthetic",
            pairs);
    }

    private static AllocationDirectionalPair SyntheticPair(
        AllocationDirectionalPlanEntry entry,
        int repetition,
        decimal delta,
        decimal throughputMultiplier)
    {
        var pairId = $"{entry.PairPrefix}-{repetition:D2}";
        const long baselineOperations = 100;
        const decimal baselineBytesPerOperation = 100m;
        var currentOperations = decimal.ToInt64(
            baselineOperations * throughputMultiplier);
        var currentBytesPerOperation =
            baselineBytesPerOperation * (1m + delta);
        return new(
            pairId,
            entry.Scenario,
            entry.ComparisonAxis,
            Observation(
                $"{pairId}-baseline",
                entry.Baseline,
                baselineOperations,
                baselineBytesPerOperation),
            Observation(
                $"{pairId}-current",
                entry.Current,
                currentOperations,
                currentBytesPerOperation));
    }

    private static AllocationDirectionalObservation Observation(
        string id,
        AllocationDirectionalPlannedSide side,
        long operations,
        decimal bytesPerOperation)
    {
        var bytes = decimal.ToInt64(operations * bytesPerOperation);
        return new(
            id,
            AllocationDirectionalAttemptOutcome.Passed,
            AllocationDirectionalOrigin.LiveMcp,
            side.WorkloadIdentity,
            side.Shape,
            side.Phase,
            side.Workers,
            AllocationThroughputNormalizationCapturePlan
                .CaptureDurationSeconds,
            operations,
            side.ExpectedPayloadBytesPerOperation,
            side.RetainedBytes,
            bytes,
            Math.Max(1, bytes / 100),
            "complete",
            [new("System.Byte[]", bytes, 1)],
            [new("Primary", bytes, 1)],
            null,
            [])
        {
            AttemptEvidence = new(
                id,
                side.WorkloadIdentity,
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
            CaptureId = $"capture-{id}",
            ProcessIdentity = $"process-{id}",
            ComparisonRole = id.EndsWith(
                "-baseline",
                StringComparison.Ordinal)
                ? "baseline"
                : "current",
            QuantityEvidence = new(
                "estimated sampled allocation bytes",
                "estimated sampled bytes",
                id.EndsWith("-baseline", StringComparison.Ordinal)
                    ? "baseline"
                    : "current",
                "completed logical operations",
                "operations",
                operations,
                "estimated sampled bytes divided by completed logical operations",
                LoadSensitive: false,
                "available",
                null,
                []),
            TypeAvailability = "available",
            SiteAvailability = "available",
        };
    }

    private static AllocationDirectionalObservation Failure(
        AllocationDirectionalObservation source)
        => source with
        {
            Outcome = AllocationDirectionalAttemptOutcome.Failed,
            Operations = 0,
            RequestedPayloadBytesPerOperation = 0,
            RetainedBytes = 0,
            SampledEstimatedBytes = 0,
            SampledAllocationTicks = 0,
            AttributionState = "unavailable",
            Types = [],
            Sites = [],
            FailureCategory = "collection_failed",
            QuantityEvidence = source.QuantityEvidence! with
            {
                DenominatorValue = 0,
                Availability = "unavailable",
                AbstentionReason =
                    "The live collection failed before a quantity could be established.",
            },
            TypeAvailability = "unavailable",
            SiteAvailability = "unavailable",
            AttemptEvidence = source.AttemptEvidence! with
            {
                Stage = AllocationDirectionalAttemptStage.McpResponse,
                OperationId = "mcp.response-json",
                FailureCategory =
                    AllocationDirectionalFailureCategory.McpIsError,
                IsError = true,
                ErrorKind = "ResourceLimitExceeded",
                ErrorCode = "allocation.capture.limit",
            },
        };

    private static IEnumerable<string> ReadIdentityValues(string path)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            foreach (var value in ReadIdentityValues(document.RootElement))
            {
                yield return value;
            }
        }
    }

    private static IEnumerable<string> ReadIdentityValues(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && property.Name is "pairId"
                        or "attemptId"
                        or "captureId"
                        or "workloadIdentity")
                {
                    yield return property.Value.GetString()!;
                }

                foreach (var value in ReadIdentityValues(property.Value))
                {
                    yield return value;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var value in ReadIdentityValues(item))
                {
                    yield return value;
                }
            }
        }
    }

    private static string RepositoryPath(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(
                   directory.FullName,
                   "DotnetDiagnostics.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName
                ?? throw new DirectoryNotFoundException(
                    "Could not locate the repository root."),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
