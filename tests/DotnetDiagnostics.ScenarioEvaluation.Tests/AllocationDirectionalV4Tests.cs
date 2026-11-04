using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class AllocationDirectionalV4Tests
{
    [Fact]
    public void DraftProtocol_DeclaresFreshBoundedInventoryAndReviewedContracts()
    {
        var root = AllocationDirectionalV4ProtocolDocument.Build(
            "draft",
            frozenAtUtc: null,
            includeFrozenHashes: false);

        root.GetProperty("protocolVersion").GetString().Should().Be("4.0.0");
        root.GetProperty("sourceBaseCommit").GetString().Should().Be(
            "9a46d91e160126253e962432ca37f77ccae3f646");
        root.GetProperty("prerequisites")
            .GetProperty("boundedCalibrationVersion").GetString()
            .Should().Be("6.0.0");
        root.GetProperty("prerequisites")
            .GetProperty("ledgerSchemaVersion").GetInt32().Should().Be(2);
        root.GetProperty("modelVisibleMeasurementQualityFields").GetArrayLength()
            .Should().Be(0);
        root.GetProperty("requiredIndependentPairs").GetInt32().Should().Be(3);
        root.GetProperty("inventory").GetProperty("liveMcpPairs").GetInt32()
            .Should().Be(54);
        root.GetProperty("inventory").GetProperty("liveMcpSides").GetInt32()
            .Should().Be(108);
        root.GetProperty("inventory").GetProperty("totalPairs").GetInt32()
            .Should().Be(57);

        var capture = root.GetProperty("capture");
        capture.GetProperty("durationSeconds").GetInt32().Should().Be(1);
        capture.GetProperty("armDelayMilliseconds").GetInt32().Should().Be(200);
        capture.GetProperty("workloadMilliseconds").GetInt32().Should().Be(700);
        capture.GetProperty("burstPauseMilliseconds").GetInt32().Should().Be(1);
        capture.GetProperty("burstPauseEveryOperations").GetInt32().Should().Be(8);
        capture.GetProperty("allocationEventOperatingLimit").GetInt64()
            .Should().Be(75_000);
        capture.GetProperty("maxAllocationEvents").GetInt32()
            .Should().Be(100_000);
        capture.GetProperty("maxAllocationFrames").GetInt32()
            .Should().Be(16_384);

        var selectors = root.GetProperty("selectors");
        selectors.GetProperty("pairFirst").GetBoolean().Should().BeTrue();
        selectors.GetProperty("minimumValidPairs").GetInt32().Should().Be(3);
        selectors.GetProperty("minimumInlierPairs").GetInt32().Should().Be(2);
        selectors.GetProperty("practicalRelativeFloor").GetDecimal()
            .Should().Be(0.10m);
        selectors.GetProperty("minimumThroughputMultiplier").GetDecimal()
            .Should().Be(1.25m);
        selectors.GetProperty("equivalenceMargin").GetString().Should()
            .Contain("robust A/A").And.Contain("10%");

        root.GetProperty("scenarioContracts").GetArrayLength().Should().Be(10);
        var capturePlan = root.GetProperty("capturePlan").EnumerateArray().ToArray();
        capturePlan.Should().HaveCount(18);
        capturePlan.Select(entry => entry.GetProperty("pairPrefix").GetString())
            .Should().OnlyHaveUniqueItems()
            .And.OnlyContain(prefix =>
                prefix != null
                && prefix.StartsWith("final-v4-", StringComparison.Ordinal));
        root.GetProperty("failurePolicy").GetString().Should()
            .Contain("at most once")
            .And.Contain("No retry");
        root.GetProperty("errorTaxonomy").GetProperty("permissionDenied")
            .GetString().Should().Contain("evidence-backed");
        root.GetProperty("errorTaxonomy").GetProperty("successFallback")
            .GetString().Should().Be("forbidden");
        root.GetProperty("frozenInputs").GetProperty("preservedArtifactsSha256")
            .EnumerateObject().Should().HaveCount(
                AllocationDirectionalV4Protocol.PreservedArtifactPaths.Count);
    }

    [Fact]
    public void SyntheticCompleteInventory_ReachesNumericGoOnly()
    {
        var report = AllocationDirectionalV4Harness.Evaluate(SyntheticFixture());

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
    public void EventOperatingLimit_IsInclusiveAndExcessIsNoGo()
    {
        var fixture = SyntheticFixture();
        var target = fixture.Pairs.First(pair =>
            pair.Baseline.Origin == AllocationDirectionalOrigin.LiveMcp);
        var atLimit = fixture with
        {
            Pairs = fixture.Pairs.Select(pair =>
                pair.PairId == target.PairId
                    ? pair with
                    {
                        Baseline = pair.Baseline with
                        {
                            SampledAllocationTicks = 75_000,
                        },
                    }
                    : pair).ToArray(),
        };
        var overLimit = atLimit with
        {
            Pairs = atLimit.Pairs.Select(pair =>
                pair.PairId == target.PairId
                    ? pair with
                    {
                        Baseline = pair.Baseline with
                        {
                            SampledAllocationTicks = 75_001,
                        },
                    }
                    : pair).ToArray(),
        };

        AllocationDirectionalV4Harness.Evaluate(atLimit).NumericGatePassed
            .Should().BeTrue();
        var report = AllocationDirectionalV4Harness.Evaluate(overLimit);
        report.Decision.Should().Be(AllocationDirectionalDecision.NoGo);
        report.NumericGatePassed.Should().BeFalse();
        report.Limitations.Should().ContainSingle()
            .Which.Should().Contain("75,000-event operating limit");
    }

    [Fact]
    public void IncompletePairRemainsInDenominatorAndMakesGateInconclusive()
    {
        var fixture = SyntheticFixture();
        var target = fixture.Pairs.First(pair =>
            pair.Scenario == AllocationDirectionalScenario.TrueIncrease
            && pair.Current.Phase == "effect");
        var changed = fixture with
        {
            Pairs = fixture.Pairs.Select(pair =>
                pair.PairId == target.PairId
                    ? pair with
                    {
                        Current = pair.Current with
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
                            QuantityEvidence = pair.Current.QuantityEvidence! with
                            {
                                DenominatorValue = 0,
                                Availability = "unavailable",
                                AbstentionReason =
                                    "The live collection failed before a quantity could be established.",
                            },
                            TypeAvailability = "unavailable",
                            SiteAvailability = "unavailable",
                            AttemptEvidence = pair.Current.AttemptEvidence! with
                            {
                                Stage =
                                    AllocationDirectionalAttemptStage.McpResponse,
                                OperationId = "mcp.response-json",
                                FailureCategory =
                                    AllocationDirectionalFailureCategory.McpIsError,
                                IsError = true,
                                ErrorKind = "ResourceLimitExceeded",
                                ErrorCode = "allocation.capture.limit",
                            },
                        },
                    }
                    : pair).ToArray(),
        };

        var report = AllocationDirectionalV4Harness.Evaluate(changed);
        var increase = report.Scenarios.Single(result =>
            result.Scenario == AllocationDirectionalScenario.TrueIncrease);

        report.Decision.Should().Be(AllocationDirectionalDecision.Inconclusive);
        report.NumericGatePassed.Should().BeFalse();
        increase.AttemptedPairs.Should().Be(6);
        increase.Assessable.Should().BeFalse();
    }

    [Fact]
    public void DeclaredIdentitiesAreDisjointFromEveryPreservedInventory()
    {
        var fixtureDirectory = RepositoryPath(
            "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional");
        var priorValues = Directory.EnumerateFiles(fixtureDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
                !Path.GetFileName(path).StartsWith(
                    "allocation-directional-baseline-current-v4",
                    StringComparison.Ordinal))
            .SelectMany(ReadIdentityValues)
            .ToHashSet(StringComparer.Ordinal);
        var pairIds = AllocationDirectionalV4CapturePlan.Entries
            .SelectMany(entry => Enumerable.Range(
                    1,
                    AllocationDirectionalBounds.RequiredPairs)
                .Select(repetition =>
                    $"{entry.PairPrefix}-{repetition:D2}"))
            .Concat(Enumerable.Range(
                    1,
                    AllocationDirectionalBounds.RequiredPairs)
                .Select(repetition => $"final-v4-partial-{repetition:D2}"))
            .ToArray();
        var attemptIds = pairIds.SelectMany(pairId =>
            new[] { $"{pairId}-baseline", $"{pairId}-current" }).ToArray();
        var captureIds = attemptIds.Select(attemptId =>
            attemptId.StartsWith("final-v4-partial-", StringComparison.Ordinal)
                ? $"projection-{attemptId}"
                : $"capture-{attemptId}").ToArray();
        var workloads = AllocationDirectionalV4CapturePlan.Entries
            .SelectMany(entry => new[]
            {
                entry.Baseline.WorkloadIdentity,
                entry.Current.WorkloadIdentity,
            })
            .Append("final-partial-attribution-v4")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        pairIds.Should().NotIntersectWith(priorValues);
        attemptIds.Should().NotIntersectWith(priorValues);
        captureIds.Should().NotIntersectWith(priorValues);
        workloads.Should().NotIntersectWith(priorValues);
    }

    [Fact]
    public void PriorProtocolsAndCalibrationArtifactsRemainHashGuarded()
    {
        AllocationDirectionalV4Protocol.PreservedArtifactPaths.Should()
            .OnlyHaveUniqueItems();
        foreach (var relativePath in
                 AllocationDirectionalV4Protocol.PreservedArtifactPaths)
        {
            var path = RepositoryPath(relativePath);
            File.Exists(path).Should().BeTrue(relativePath);
            AllocationDirectionalProtocol.CanonicalByteCount(path)
                .Should().BeGreaterThan(0);
            AllocationDirectionalProtocol.Sha256(path)
                .Should().MatchRegex("^[0-9a-f]{64}$");
        }
    }

    [Fact]
    public void V4ExecutionSettingsMatchBoundedCalibrationV6()
    {
        AllocationDirectionalV4CapturePlan.CaptureDurationSeconds.Should().Be(
            AllocationDirectionalBoundedCalibrationPlan.OperatingSettings
                .CaptureDurationSeconds);
        AllocationDirectionalV4CapturePlan.ArmDelayMilliseconds.Should().Be(
            AllocationDirectionalBoundedCalibrationPlan.OperatingSettings
                .ArmDelayMilliseconds);
        AllocationDirectionalV4CapturePlan.WorkloadMilliseconds.Should().Be(
            AllocationDirectionalBoundedCalibrationPlan.OperatingSettings
                .WorkloadMilliseconds);
        AllocationDirectionalV4CapturePlan.AllocationEventOperatingLimit.Should()
            .Be(AllocationDirectionalBoundedCalibrationPlan
                .AllocationEventOperatingLimit);
        AllocationDirectionalV4CapturePlan.Entries.Where(entry =>
                entry.Scenario is AllocationDirectionalScenario.TrueIncrease
                    or AllocationDirectionalScenario.LowerAllocationStableRetained
                    or AllocationDirectionalScenario.FixedStrideBias)
            .SelectMany(entry => new[]
            {
                entry.Baseline.Shape,
                entry.Current.Shape,
            })
            .Should().OnlyContain(shape =>
                shape.EndsWith("-bounded-v4", StringComparison.Ordinal));
    }

    [EnvironmentRequiredFact(
        AllocationDirectionalEnvironmentVariables.V4ProtocolFreezePath,
        "Freezing the reviewed allocation directional v4 protocol requires an explicit new output path.")]
    public void ReviewedProtocolFreeze_WritesImmutableDocumentOnce()
    {
        var path = Environment.GetEnvironmentVariable(
            AllocationDirectionalEnvironmentVariables.V4ProtocolFreezePath)
            ?? throw new InvalidOperationException(
                "The allocation directional v4 freeze path was not configured.");
        File.Exists(path).Should().BeFalse(
            "a frozen protocol must never be overwritten");
        var document = AllocationDirectionalV4ProtocolDocument.Build(
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
        foreach (var entry in AllocationDirectionalV4CapturePlan.Entries)
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
            var pairId = $"final-v4-partial-{index + 1:D2}";
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
            AllocationDirectionalV4Protocol.ProtocolId,
            AllocationDirectionalV4Protocol.ProtocolVersion,
            "synthetic-protocol",
            "synthetic-protocol",
            [],
            "2026-11-03T23:59:00Z",
            AllocationDirectionalV4Protocol.SourceBaseCommit,
            "synthetic",
            pairs);
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
            AllocationDirectionalV4CapturePlan.CaptureDurationSeconds,
            operations,
            side.ExpectedPayloadBytesPerOperation,
            side.RetainedBytes,
            bytes,
            available ? Math.Max(1, bytes / 100) : 0,
            available ? "complete" : "unavailable",
            types ?? (available ? [new("System.Byte[]", bytes, 1)] : []),
            sites ?? (available ? [new("Primary", bytes, 1)] : []),
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
            WorkloadIdentity = "final-partial-attribution-v4",
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
}
