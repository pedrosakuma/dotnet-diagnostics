using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetDiagnostics.Core.Comparison;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class RequestLatencyComparisonTests
{
    [Fact]
    public void Compare_CompatibleCompleteWindows_ProducesDeterministicQuantilesAndBootstrap()
    {
        var baseline = WindowSet("baseline", "build-a", offset: 10);
        var candidate = WindowSet("candidate", "build-b", offset: 30);

        var result = RequestLatencyComparison.Compare(baseline, candidate);

        result.Availability.Should().Be(RequestLatencyEvidenceAvailability.Available);
        result.UnavailableReason.Should().BeNull();
        result.BaselineCounts.SourceCompleted.Should().Be(64);
        result.BaselineCounts.RetainedCompleted.Should().Be(64);
        result.PairCounts.ValidIndependentComplete.Should().Be(8);
        result.Quantiles.Should().HaveCount(3);
        foreach (var quantile in result.Quantiles)
        {
            quantile.AbsoluteDeltaMilliseconds.Should().BeApproximately(20, 1e-10);
            quantile.RelativeDelta.Should().BePositive();
            quantile.PairedWindowMedianDeltaMilliseconds.Should().BeApproximately(20, 1e-10);
            quantile.PairedWindowBootstrapInterval.Should().NotBeNull();
            quantile.PairedWindowBootstrapInterval!.LowerMilliseconds.Should().BeApproximately(20, 1e-10);
            quantile.PairedWindowBootstrapInterval.UpperMilliseconds.Should().BeApproximately(20, 1e-10);
            quantile.UnavailableFields.Should().BeEmpty();
        }
        result.Limitations.Should().Contain(limitation =>
            limitation.Contains("No regression", StringComparison.Ordinal));
        result.Limitations.Should().Contain(limitation =>
            limitation.Contains("rank-biserial", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_ReorderedEquivalentWindowsAndSamples_IsDeterministic()
    {
        var baseline = WindowSet("baseline", "build-a", offset: 10);
        var candidate = WindowSet("candidate", "build-b", offset: 30);
        var reorderedBaseline = baseline with
        {
            Windows = baseline.Windows.Reverse()
                .Select(window => window with
                {
                    RetainedLatenciesMilliseconds =
                        window.RetainedLatenciesMilliseconds.Reverse().ToArray(),
                })
                .ToArray(),
        };
        var reorderedCandidate = candidate with
        {
            Windows = candidate.Windows.Reverse()
                .Select(window => window with
                {
                    RetainedLatenciesMilliseconds =
                        window.RetainedLatenciesMilliseconds.Reverse().ToArray(),
                })
                .ToArray(),
        };

        var first = RequestLatencyComparison.Compare(baseline, candidate);
        var second = RequestLatencyComparison.Compare(reorderedBaseline, reorderedCandidate);

        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public void Compare_IncompatibleIdentity_PreservesDescriptiveCountsButWithholdsDeltas()
    {
        var baseline = WindowSet("baseline", "build-a", offset: 10);
        var candidate = WindowSet("candidate", "build-b", offset: 30) with
        {
            Identity = WindowSet("candidate", "build-b", offset: 30).Identity with
            {
                Workload = WindowSet("candidate", "build-b", offset: 30).Identity.Workload with
                {
                    Parameters = "endpoint=/different",
                },
            },
        };

        var result = RequestLatencyComparison.Compare(baseline, candidate);

        result.Availability.Should().Be(RequestLatencyEvidenceAvailability.Incompatible);
        result.IdentityMismatches.Should().ContainSingle(mismatch =>
            mismatch.Field == "workload.parameters");
        result.Quantiles.Should().OnlyContain(quantile =>
            quantile.Baseline.ValueMilliseconds != null
            && quantile.Candidate.ValueMilliseconds != null
            && quantile.AbsoluteDeltaMilliseconds == null
            && quantile.RelativeDelta == null
            && quantile.PairedWindowMedianDeltaMilliseconds == null
            && quantile.PairedWindowBootstrapInterval == null);
    }

    [Fact]
    public void Compare_ApproximationAndLoss_HaveExplicitPrecedenceAndCounts()
    {
        var baseline = WindowSet(
            "baseline",
            "build-a",
            offset: 10,
            representation: RequestLatencyRepresentation.ReservoirSampled,
            retainedPerWindow: 4,
            sourcePerWindow: 12);
        var candidate = WindowSet(
            "candidate",
            "build-b",
            offset: 30,
            representation: RequestLatencyRepresentation.ReservoirSampled,
            retainedPerWindow: 4,
            sourcePerWindow: 12);

        var approximate = RequestLatencyComparison.Compare(baseline, candidate);

        approximate.Availability.Should().Be(
            RequestLatencyEvidenceAvailability.UnsupportedApproximation);
        approximate.BaselineCounts.SourceCompleted.Should().Be(96);
        approximate.BaselineCounts.RetainedCompleted.Should().Be(32);
        approximate.Quantiles.Should().OnlyContain(quantile =>
            quantile.AbsoluteDeltaMilliseconds != null
            && quantile.PairedWindowBootstrapInterval == null);

        var lossyCandidate = candidate with
        {
            Windows = candidate.Windows
                .Select((window, index) => index == 0
                    ? window with { LostOrDropped = 3 }
                    : window)
                .ToArray(),
        };
        var lossy = RequestLatencyComparison.Compare(baseline, lossyCandidate);

        lossy.Availability.Should().Be(RequestLatencyEvidenceAvailability.LossDetected);
        lossy.CandidateCounts.LostOrDropped.Should().Be(3);
    }

    [Fact]
    public void Compare_IncompleteFailedAndAbortedWindows_RemainInDenominators()
    {
        var baseline = WindowSet("baseline", "build-a", offset: 10);
        var candidate = WindowSet("candidate", "build-b", offset: 30);
        candidate = candidate with
        {
            Windows = candidate.Windows
                .Select((window, index) => index switch
                {
                    0 => window with { ErrorCount = 1, MissingCompletionCount = 1 },
                    1 => window with
                    {
                        Outcome = RequestLatencyWindowOutcome.Failed,
                        SourceCompletedCount = 0,
                        RetainedLatenciesMilliseconds = Array.Empty<double>(),
                        ErrorCount = 1,
                    },
                    2 => window with
                    {
                        Outcome = RequestLatencyWindowOutcome.Aborted,
                        SourceCompletedCount = 0,
                        RetainedLatenciesMilliseconds = Array.Empty<double>(),
                        CensoredCount = 1,
                    },
                    _ => window,
                })
                .ToArray(),
        };

        var result = RequestLatencyComparison.Compare(baseline, candidate);

        result.Availability.Should().Be(RequestLatencyEvidenceAvailability.InsufficientObservations);
        result.CandidateCounts.Windows.Should().Be(8);
        result.CandidateCounts.FailedWindows.Should().Be(1);
        result.CandidateCounts.AbortedWindows.Should().Be(1);
        result.CandidateCounts.Errors.Should().Be(2);
        result.CandidateCounts.Censored.Should().Be(1);
        result.CandidateCounts.MissingCompletions.Should().Be(1);
        result.PairCounts.ValidIndependentComplete.Should().Be(5);
        result.Quantiles.Should().OnlyContain(quantile =>
            quantile.PairedWindowMedianDeltaMilliseconds == null
            && quantile.PairedWindowBootstrapInterval == null);
    }

    [Fact]
    public void Compare_ZeroBaseline_MakesOnlyRelativeDeltaUnavailable()
    {
        var baseline = WindowSet("baseline", "build-a", offset: 0, constant: true);
        var candidate = WindowSet("candidate", "build-b", offset: 5, constant: true);

        var result = RequestLatencyComparison.Compare(baseline, candidate);

        result.Availability.Should().Be(RequestLatencyEvidenceAvailability.Available);
        result.Quantiles.Should().OnlyContain(quantile =>
            quantile.AbsoluteDeltaMilliseconds == 5
            && quantile.RelativeDelta == null
            && quantile.UnavailableFields.Contains(
                new RequestLatencyUnavailableField("relativeDelta", "baseline_estimate_is_zero")));
    }

    [Fact]
    public void Compare_RejectsExactWindowThatDoesNotRetainEveryCompletedRequest()
    {
        var baseline = WindowSet("baseline", "build-a", offset: 10) with
        {
            Windows =
            [
                WindowSet("baseline", "build-a", offset: 10).Windows[0] with
                {
                    SourceCompletedCount = 9,
                },
            ],
        };
        var candidate = WindowSet("candidate", "build-b", offset: 30);

        var action = () => RequestLatencyComparison.Compare(baseline, candidate);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*Exact complete windows must retain every completed-request latency value*");
    }

    [Fact]
    public void Compare_MissingNestedIdentity_ReturnsContractErrorInsteadOfNullReference()
    {
        var baseline = WindowSet("baseline", "build-a", offset: 10) with
        {
            Identity = WindowSet("baseline", "build-a", offset: 10).Identity with
            {
                Target = null!,
            },
        };
        var candidate = WindowSet("candidate", "build-b", offset: 30);

        var action = () => RequestLatencyComparison.Compare(baseline, candidate);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*compatibility identity section is required*");
    }

    [Fact]
    public void Compare_AggregateCountOverflow_ReturnsContractError()
    {
        var baseline = WindowSet(
            "baseline",
            "build-a",
            offset: 10,
            representation: RequestLatencyRepresentation.Projected,
            retainedPerWindow: 1,
            sourcePerWindow: int.MaxValue);
        var candidate = WindowSet(
            "candidate",
            "build-b",
            offset: 30,
            representation: RequestLatencyRepresentation.Projected,
            retainedPerWindow: 1,
            sourcePerWindow: int.MaxValue);

        var action = () => RequestLatencyComparison.Compare(baseline, candidate);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*Aggregate request counts must fit a signed 32-bit integer*");
    }

    [Fact]
    public void Compare_UnknownPairOrder_ReturnsContractError()
    {
        var baseline = WindowSet("baseline", "build-a", offset: 10);
        baseline = baseline with
        {
            Windows =
            [
                baseline.Windows[0] with { PairOrder = (RequestLatencyPairOrder)99 },
                .. baseline.Windows.Skip(1),
            ],
        };
        var candidate = WindowSet("candidate", "build-b", offset: 30);

        var action = () => RequestLatencyComparison.Compare(baseline, candidate);

        action.Should().Throw<ArgumentException>()
            .WithMessage("*must be recognized v1 values*");
    }

    [Fact]
    public void Compare_PairOrderMismatch_IsIncompatibleAndWithholdsDeltas()
    {
        var baseline = WindowSet("baseline", "build-a", offset: 10);
        var candidate = WindowSet("candidate", "build-b", offset: 30);
        candidate = candidate with
        {
            Windows =
            [
                candidate.Windows[0] with
                {
                    PairOrder = candidate.Windows[0].PairOrder
                        == RequestLatencyPairOrder.BaselineFirst
                        ? RequestLatencyPairOrder.CandidateFirst
                        : RequestLatencyPairOrder.BaselineFirst,
                },
                .. candidate.Windows.Skip(1),
            ],
        };

        var result = RequestLatencyComparison.Compare(baseline, candidate);

        result.Availability.Should().Be(RequestLatencyEvidenceAvailability.Incompatible);
        result.UnavailableReason.Should().Be("pair_order_mismatch");
        result.PairCounts.PairOrderMismatches.Should().Be(1);
        result.Quantiles.Should().OnlyContain(quantile =>
            quantile.AbsoluteDeltaMilliseconds == null
            && quantile.RelativeDelta == null
            && quantile.PairedWindowMedianDeltaMilliseconds == null
            && quantile.PairedWindowBootstrapInterval == null);
    }

    [Fact]
    public void Compare_ExtremeFiniteLatencies_DoNotProduceNonfiniteOutput()
    {
        var baseline = WindowSet("baseline", "build-a", offset: double.Epsilon, constant: true);
        var candidate = WindowSet("candidate", "build-b", offset: double.MaxValue, constant: true);

        var result = RequestLatencyComparison.Compare(baseline, candidate);

        result.Availability.Should().Be(RequestLatencyEvidenceAvailability.Available);
        result.Quantiles.Should().OnlyContain(quantile =>
            quantile.AbsoluteDeltaMilliseconds == double.MaxValue
            && quantile.RelativeDelta == null
            && quantile.PairedWindowMedianDeltaMilliseconds == double.MaxValue
            && quantile.PairedWindowBootstrapInterval != null
            && double.IsFinite(quantile.PairedWindowBootstrapInterval.LowerMilliseconds)
            && double.IsFinite(quantile.PairedWindowBootstrapInterval.UpperMilliseconds)
            && quantile.UnavailableFields.Contains(
                new RequestLatencyUnavailableField("relativeDelta", "relative_delta_nonfinite")));
    }

    [Fact]
    public void Deserialize_MissingLossCount_IsRejectedInsteadOfDefaultingToZero()
    {
        var json = JsonSerializer.SerializeToNode(
            WindowSet("baseline", "build-a", offset: 10),
            RequestLatencyComparisonJsonContext.Default.RequestLatencyWindowSet)!;
        json["windows"]![0]!.AsObject().Remove("lostOrDropped");

        var action = () => json.Deserialize(
            RequestLatencyComparisonJsonContext.Default.RequestLatencyWindowSet);

        action.Should().Throw<JsonException>()
            .WithMessage("*lostOrDropped*");
    }

    internal static RequestLatencyWindowSet WindowSet(
        string id,
        string axisValue,
        double offset,
        RequestLatencyRepresentation representation = RequestLatencyRepresentation.Exact,
        int retainedPerWindow = RequestLatencyComparison.MinimumCompletedRequestsPerWindow,
        int? sourcePerWindow = null,
        bool constant = false)
    {
        var source = sourcePerWindow ?? retainedPerWindow;
        var windows = Enumerable.Range(0, RequestLatencyComparison.MinimumCompleteWindowPairs)
            .Select(window => new RequestLatencyWindow(
                $"window-{window + 1}",
                $"pair-{window + 1}",
                window % 2 == 0
                    ? RequestLatencyPairOrder.BaselineFirst
                    : RequestLatencyPairOrder.CandidateFirst,
                RequestLatencyWindowOutcome.Complete,
                source,
                ErrorCount: 0,
                CensoredCount: 0,
                MissingCompletionCount: 0,
                LostOrDropped: 0,
                representation,
                Enumerable.Range(0, retainedPerWindow)
                    .Select(sample => offset + (constant ? 0 : window * 0.1 + sample * 0.01))
                    .ToArray()))
            .ToArray();
        return new(
            RequestLatencyWindowSet.SchemaV1,
            id,
            new(
                new("CoreCLR", "major-compatible", "windows", "x64", $"{id}-run"),
                new("eventpipe", "1"),
                new(
                    "request-latency",
                    "ms",
                    "completed-request-duration",
                    "per-completed-request",
                    "endpoint=/checkout;method=GET",
                    representation),
                new(
                    "12-requests",
                    "non-overlapping-sequential-windows",
                    "Microsoft.AspNetCore.Hosting",
                    "activity-stop",
                    $"source={source};retained={retainedPerWindow}",
                    "none"),
                new(
                    "checkout",
                    "1",
                    "endpoint=/checkout;method=GET",
                    "1",
                    "single-process"),
                new(
                    "bounded_time_window",
                    "request-event-within-window-pair",
                    "pairKey"),
                new("build", axisValue)),
            windows);
    }
}
