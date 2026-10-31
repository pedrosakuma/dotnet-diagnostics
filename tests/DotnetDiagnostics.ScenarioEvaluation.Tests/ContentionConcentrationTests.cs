using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class ContentionConcentrationTests
{
    [Fact]
    public void KnownShapes_ChangeFocusWithoutCreatingCausalOrZeroWaitClaims()
    {
        var report = ContentionConcentrationHarness.Evaluate(SyntheticCaptures(), "synthetic");

        report.Verdict.Should().Be("GO");
        report.Scenarios.Single(item => item.Scenario == ContentionConcentrationScenario.DominantLock)
            .DrilldownFocus.Should().Be(ContentionDrilldownFocus.FocusDominantLockAndOwner);
        report.Scenarios.Single(item => item.Scenario == ContentionConcentrationScenario.DiffuseLocks)
            .DrilldownFocus.Should().Be(ContentionDrilldownFocus.KeepContentionInvestigationBroad);
        report.Scenarios.Single(item => item.Scenario == ContentionConcentrationScenario.OwnerUnavailable)
            .DrilldownFocus.Should().Be(ContentionDrilldownFocus.FocusDominantLockOwnerUnavailable);
        report.Scenarios.Single(item => item.Scenario == ContentionConcentrationScenario.ShortBurst)
            .DrilldownFocus.Should().Be(ContentionDrilldownFocus.TimeAlignAndRepeatCapture);
        report.Scenarios.Single(item => item.Scenario == ContentionConcentrationScenario.TruncatedInventory)
            .DrilldownFocus.Should().Be(ContentionDrilldownFocus.InconclusiveDueToTruncatedInventory);
        var healthy = report.Scenarios.Single(item =>
            item.Scenario == ContentionConcentrationScenario.HealthyControl);
        healthy.DrilldownFocus.Should().Be(ContentionDrilldownFocus.BoundedCaptureObservedNoContention);
        healthy.Top1Median.Availability.Should().Be(
            ScenarioStatisticalAvailability.InsufficientObservations);
        healthy.Top1Median.Estimate.Should().BeNull();

        var json = Serialize(report).ToLowerInvariant();
        json.Should().Contain("does not establish why");
        json.Should().Contain("does not establish zero waiting");
        json.Should().NotContain("\"rootcause\"");
        json.Should().NotContain("\"causedby\"");
    }

    [Fact]
    public void OwnerOverlapWilson_UsesIndependentCapturesNotCorrelatedEvents()
    {
        var report = ContentionConcentrationHarness.Evaluate(SyntheticCaptures(), "synthetic");
        var dominant = report.Scenarios.Single(item =>
            item.Scenario == ContentionConcentrationScenario.DominantLock);

        dominant.OwnerOverlapRate.Should().NotBeNull();
        dominant.OwnerOverlapRate!.IndependenceUnit.Should().Be(
            ScenarioStatisticalIndependenceUnit.Capture);
        dominant.OwnerOverlapRate.Counts.IndependenceUnits.Should().Be(4);
        dominant.OwnerOverlapRate.Counts.SourceEvents.Should().Be(4);
        dominant.OwnerOverlapRate.Rate.Should().Be(new ScenarioStatisticalRate(3, 4, 0.75));
        dominant.OwnerOverlapRate.Interval.Should().NotBeNull();
        dominant.OwnerOverlapRate.Interval!.Lower.Should().BeApproximately(0.300641842, 1e-8);
        dominant.OwnerOverlapRate.Interval.Upper.Should().BeApproximately(0.954412739, 1e-8);

        var ownerUnavailable = report.Scenarios.Single(item =>
            item.Scenario == ContentionConcentrationScenario.OwnerUnavailable);
        ownerUnavailable.OwnerOverlapRate.Should().BeNull();
    }

    [Fact]
    public void DominantFocus_DoesNotClaimOwnerWithoutAvailablePositiveWilsonEvidence()
    {
        var insufficient = SyntheticCaptures();
        var lastDominant = insufficient.FindIndex(capture =>
            capture.Scenario == ContentionConcentrationScenario.DominantLock
            && capture.CaptureId.EndsWith("-04", StringComparison.Ordinal));
        insufficient[lastDominant] = insufficient[lastDominant] with
        {
            OwnerMetadataAvailable = false,
            OwnerOverlapObserved = null,
        };

        var insufficientReport = ContentionConcentrationHarness.Evaluate(insufficient, "synthetic");
        var insufficientDominant = insufficientReport.Scenarios.Single(item =>
            item.Scenario == ContentionConcentrationScenario.DominantLock);

        insufficientDominant.OwnerOverlapRate.Should().NotBeNull();
        insufficientDominant.OwnerOverlapRate!.Availability.Should().Be(
            ScenarioStatisticalAvailability.InsufficientObservations);
        insufficientDominant.DrilldownFocus.Should().Be(
            ContentionDrilldownFocus.FocusDominantLockOwnerUnavailable);
        insufficientDominant.ExpectedBehaviorObserved.Should().BeFalse();

        var zeroOverlap = SyntheticCaptures();
        for (var index = 0; index < zeroOverlap.Count; index++)
        {
            if (zeroOverlap[index].Scenario == ContentionConcentrationScenario.DominantLock)
            {
                zeroOverlap[index] = zeroOverlap[index] with { OwnerOverlapObserved = false };
            }
        }

        var zeroReport = ContentionConcentrationHarness.Evaluate(zeroOverlap, "synthetic");
        var zeroDominant = zeroReport.Scenarios.Single(item =>
            item.Scenario == ContentionConcentrationScenario.DominantLock);
        zeroDominant.OwnerOverlapRate!.Availability.Should().Be(ScenarioStatisticalAvailability.Available);
        zeroDominant.OwnerOverlapRate.Rate!.PositiveCount.Should().Be(0);
        zeroDominant.DrilldownFocus.Should().Be(
            ContentionDrilldownFocus.FocusDominantLockOwnerUnavailable);
        zeroDominant.ExpectedBehaviorObserved.Should().BeFalse();
    }

    [Fact]
    public void ReorderedCaptures_ProduceEquivalentScenarioStatistics()
    {
        var captures = SyntheticCaptures();

        var first = ContentionConcentrationHarness.Evaluate(captures, "synthetic");
        var reordered = ContentionConcentrationHarness.Evaluate(
            captures.AsEnumerable().Reverse().ToArray(),
            "synthetic");

        Serialize(reordered).Should().Be(Serialize(first));
    }

    [Fact]
    public void LiveReplayFixture_PreservesGoEvidenceAndBounds()
    {
        var path = ScenarioManifestLoader.ScenarioPath(
            "Fixtures",
            "Contention",
            "contention-concentration.windows.v1.json");
        var report = ContentionConcentrationHarness.Read(path);

        report.Verdict.Should().Be("GO");
        report.CapturesPerScenario.Should().BeInRange(4, ContentionConcentrationBounds.MaximumCapturesPerScenario);
        report.Captures.Should().OnlyContain(capture =>
            capture.TopLocks.Count <= ContentionConcentrationBounds.MaximumReportedLocks
            && capture.Windows.Count <= ContentionConcentrationBounds.MaximumWindowsPerCapture);
        report.Scenarios.Should().OnlyContain(scenario => scenario.ExpectedBehaviorObserved);
        report.Scenarios.Single(item =>
                item.Scenario == ContentionConcentrationScenario.TruncatedInventory)
            .Limitations.Should().Contain(item =>
                item.Contains("bounded absence is not zero", StringComparison.Ordinal));
        report.Scenarios.Single(item =>
                item.Scenario == ContentionConcentrationScenario.HealthyControl)
            .Top1Median.Estimate.Should().BeNull();
    }

    [Fact]
    public void Bounds_RejectUnequalOrExcessiveCaptureSets()
    {
        var captures = SyntheticCaptures().ToList();
        captures.RemoveAt(0);

        Action unequal = () => ContentionConcentrationHarness.Evaluate(captures, "synthetic");

        unequal.Should().Throw<InvalidDataException>()
            .WithMessage("*same bounded capture count*");
    }

    private static List<ContentionCaptureEvidence> SyntheticCaptures()
    {
        var captures = new List<ContentionCaptureEvidence>();
        for (var index = 1; index <= 4; index++)
        {
            captures.Add(Capture(
                ContentionConcentrationScenario.DominantLock,
                index,
                top1: 0.82 + index * 0.01,
                top3: 0.96,
                distinctLocks: 4,
                ownerAvailable: true,
                ownerOverlap: index != 4,
                activeWindows: 3));
            captures.Add(Capture(
                ContentionConcentrationScenario.DiffuseLocks,
                index,
                top1: 0.18 + index * 0.01,
                top3: 0.48,
                distinctLocks: 8,
                ownerAvailable: true,
                ownerOverlap: false,
                activeWindows: 3));
            captures.Add(Capture(
                ContentionConcentrationScenario.OwnerUnavailable,
                index,
                top1: 0.84,
                top3: 0.97,
                distinctLocks: 3,
                ownerAvailable: false,
                ownerOverlap: null,
                activeWindows: 3));
            captures.Add(Capture(
                ContentionConcentrationScenario.ShortBurst,
                index,
                top1: 0.88,
                top3: 0.98,
                distinctLocks: 2,
                ownerAvailable: true,
                ownerOverlap: true,
                activeWindows: 1));
            captures.Add(Capture(
                ContentionConcentrationScenario.TruncatedInventory,
                index,
                top1: 0.18,
                top3: 0.45,
                distinctLocks: 12,
                ownerAvailable: true,
                ownerOverlap: false,
                activeWindows: 3,
                inventoryTruncated: true));
            captures.Add(Capture(
                ContentionConcentrationScenario.HealthyControl,
                index,
                top1: null,
                top3: null,
                distinctLocks: 0,
                ownerAvailable: false,
                ownerOverlap: null,
                activeWindows: 0,
                totalEvents: 0));
        }

        return captures;
    }

    private static ContentionCaptureEvidence Capture(
        ContentionConcentrationScenario scenario,
        int index,
        double? top1,
        double? top3,
        int distinctLocks,
        bool ownerAvailable,
        bool? ownerOverlap,
        int activeWindows,
        bool inventoryTruncated = false,
        int totalEvents = 120)
    {
        var totalWait = totalEvents == 0 ? 0 : 1200;
        ContentionLockWaitEvidence[] topLocks = top1 is null
            ? Array.Empty<ContentionLockWaitEvidence>()
            :
            [
                new ContentionLockWaitEvidence(
                    "lock-1",
                    Math.Max(1, totalEvents / Math.Max(1, distinctLocks)),
                    totalWait * top1.Value,
                    top1.Value),
            ];
        var windows = Enumerable.Range(0, activeWindows)
            .Select(window => new ContentionWindowEvidence(
                window,
                Math.Max(1, totalEvents / Math.Max(1, activeWindows)),
                totalWait / Math.Max(1, activeWindows),
                top1 ?? 0,
                top3 ?? 0))
            .ToArray();
        var limitations = inventoryTruncated
            ?
            [
                $"The reported top-lock inventory is bounded to {ContentionConcentrationBounds.MaximumReportedLocks}; omitted locks remain possible and bounded absence is not zero.",
            ]
            : Array.Empty<string>();
        return new(
            $"{scenario}-{index:D2}",
            scenario,
            1000 + index,
            "10.0",
            "synthetic",
            4,
            totalEvents,
            distinctLocks,
            totalWait,
            totalEvents == 0 ? 0 : 2,
            totalEvents == 0 ? 0 : 20,
            totalEvents == 0 ? 0 : 100,
            top1,
            top3,
            topLocks.Length,
            inventoryTruncated,
            AggregationOverflow: false,
            ownerAvailable,
            ownerOverlap,
            activeWindows,
            TotalWindowCount: 4,
            topLocks,
            windows,
            limitations);
    }

    private static string Serialize(ContentionConcentrationReport report)
    {
        using var stream = new MemoryStream();
        System.Text.Json.JsonSerializer.Serialize(
            stream,
            report,
            ContentionConcentrationJsonContext.Default.ContentionConcentrationReport);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}

[Collection(ScenarioEvaluationLiveGroup.Name)]
public sealed class ContentionConcentrationLiveTests
{
    [WindowsOnlyFact(
        "Live contention concentration characterization requires Windows EventPipe contention events and ClrMD owner snapshots.",
        Timeout = 600_000)]
    [Trait("Category", "ScenarioEvaluationLive")]
    public async Task LiveCapture_ContentionShapes_AreBoundedAndReplayable()
    {
        var reportPath = Environment.GetEnvironmentVariable(
            ContentionCharacterizationEnvironmentVariables.ReportPath);
        var repetitions = Repetitions(reportPath);
        var captures = await ContentionConcentrationLive.CaptureAllAsync(
            repetitions,
            CancellationToken.None);
        ContentionConcentrationReport? report = null;
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            report = ContentionConcentrationHarness.Evaluate(
                captures,
                $"{Environment.OSVersion}; {Environment.Version}");
            ContentionConcentrationHarness.Write(reportPath, report);
        }

        captures.Should().HaveCount(repetitions * Enum.GetValues<ContentionConcentrationScenario>().Length);
        captures.Should().OnlyContain(capture =>
            capture.TopLocks.Count <= ContentionConcentrationBounds.MaximumReportedLocks
            && capture.Windows.Count <= ContentionConcentrationBounds.MaximumWindowsPerCapture);
        captures.Where(capture => capture.Scenario == ContentionConcentrationScenario.DominantLock)
            .Should().OnlyContain(capture => capture.TotalEvents > 0 && capture.Top1WaitDurationShare > 0.5);
        captures.Where(capture => capture.Scenario == ContentionConcentrationScenario.DiffuseLocks)
            .Should().OnlyContain(capture => capture.TotalEvents > 0 && capture.DistinctLocks >= 4);
        captures.Where(capture => capture.Scenario == ContentionConcentrationScenario.OwnerUnavailable)
            .Should().OnlyContain(capture =>
                !capture.OwnerMetadataAvailable && capture.OwnerOverlapObserved == null);
        captures.Where(capture => capture.Scenario == ContentionConcentrationScenario.ShortBurst)
            .Should().OnlyContain(capture => capture.ActiveWindowCount <= 2);
        captures.Where(capture => capture.Scenario == ContentionConcentrationScenario.TruncatedInventory)
            .Should().OnlyContain(capture => capture.InventoryTruncated);
        captures.Where(capture => capture.Scenario == ContentionConcentrationScenario.HealthyControl)
            .Should().OnlyContain(capture =>
                capture.TotalEvents == 0 && capture.Top1WaitDurationShare == null);

        if (report is not null)
        {
            report.Verdict.Should().Be(
                "GO",
                string.Join(
                    Environment.NewLine,
                    report.Scenarios.Select(scenario =>
                        $"{scenario.Scenario}: focus={scenario.DrilldownFocus}; expected={scenario.ExpectedBehaviorObserved}; top1={scenario.Top1Median.Estimate}; top3={scenario.Top3Median.Estimate}; mad={scenario.Top1CaptureMad.Dispersion}; windows={scenario.MedianActiveWindowFraction}")));
        }
    }

    private static int Repetitions(string? reportPath)
    {
        var raw = Environment.GetEnvironmentVariable(
            ContentionCharacterizationEnvironmentVariables.Repetitions);
        if (int.TryParse(raw, out var repetitions))
        {
            return Math.Clamp(repetitions, 1, ContentionConcentrationBounds.MaximumCapturesPerScenario);
        }

        return string.IsNullOrWhiteSpace(reportPath) ? 1 : 6;
    }
}
