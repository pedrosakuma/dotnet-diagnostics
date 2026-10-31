using System.Text.Json;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Memory;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class CpuHotspotComparisonTests
{
    [Fact]
    public void AaMatrix_ReturnsCaptureLevelDistributionsWithoutVerdict()
    {
        var baseline = CaptureSet("baseline", [40, 42, 38, 40]);
        var candidate = CaptureSet("candidate", [41, 39, 40, 40]);

        var result = CpuHotspotComparison.Compare(baseline, candidate);

        result.Availability.Should().Be(CpuHotspotEvidenceAvailability.Available);
        result.PairCounts.ValidIndependentComplete.Should().Be(4);
        result.BaselineSelectedShare.Median.Should().BeApproximately(0.4, 0.000001);
        result.BaselineSelectedShare.InterquartileRange.Should().BeApproximately(0.01, 0.000001);
        result.BaselineSelectedShare.Minimum.Should().BeApproximately(0.38, 0.000001);
        result.BaselineSelectedShare.Maximum.Should().BeApproximately(0.42, 0.000001);
        result.PairedShareDeltas.Should().HaveCount(4);
        result.RankStability.Should().NotBeNull();
        result.RankStability!.TopK.Should().Be(2);
        result.GetType().GetProperty("Verdict").Should().BeNull();
        result.Limitations.Should().Contain(line =>
            line.Contains("not elapsed CPU time", StringComparison.Ordinal));
    }

    [Fact]
    public void DeterministicShift_ReturnsPairedShareAndRankEvidenceWithoutInterpretation()
    {
        var baseline = CaptureSet("baseline", [30, 31, 29, 30], otherSamples: 60);
        var candidate = CaptureSet("candidate", [60, 61, 59, 60], otherSamples: 30);

        var result = CpuHotspotComparison.Compare(baseline, candidate);

        result.Availability.Should().Be(CpuHotspotEvidenceAvailability.Available);
        result.MedianPairedShareDelta.Should().BeApproximately(0.3, 0.000001);
        result.PairedShareDeltas.Select(static pair => pair.CandidateMinusBaseline)
            .Should().Equal(0.3, 0.3, 0.3, 0.3);
        result.RankStability!.SameSelectedRankFraction.Should().Be(0);
        result.RankStability.MedianAbsoluteSelectedRankDelta.Should().Be(1);
        result.GetType().GetProperty("Verdict").Should().BeNull();
        result.Method.CaptureEstimate.Should().NotContain("verdict");
        result.Method.PairedEstimate.Should().NotContain("verdict");
    }

    [Fact]
    public void ChangedOfferedLoad_IsIncompatibleAndWithholdsPooledMovement()
    {
        var baseline = CaptureSet("baseline", [40, 40, 40, 40]);
        var candidate = CaptureSet("candidate", [60, 60, 60, 60]) with
        {
            Identity = Identity("candidate", offeredLoad: 101),
        };

        var result = CpuHotspotComparison.Compare(baseline, candidate);

        result.Availability.Should().Be(CpuHotspotEvidenceAvailability.Incompatible);
        result.UnavailableReason.Should().Be("compatibility_identity_mismatch");
        result.IdentityMismatches.Should().ContainSingle(mismatch =>
            mismatch.Field == "workload.offeredLoad");
        result.BaselineCaptures.Should().NotContain(capture =>
            capture.SelectedMethodInclusiveSamples == null);
        result.PairedShareDeltas.Should().BeEmpty();
        result.MedianPairedShareDelta.Should().BeNull();
        result.RankStability.Should().BeNull();
    }

    [Fact]
    public void ExcessUnresolvedShare_IsExplicitlyDegraded()
    {
        var baseline = CaptureSet("baseline", [40, 40, 40, 40]);
        var candidate = CaptureSet("candidate", [40, 40, 40, 40], unresolvedSamples: 40);

        var result = CpuHotspotComparison.Compare(baseline, candidate);

        result.Availability.Should().Be(CpuHotspotEvidenceAvailability.UnsupportedApproximation);
        result.UnavailableReason.Should().Be("unresolved_symbol_share_exceeds_protocol");
        result.CandidateUnresolvedShare.Median.Should().BeApproximately(0.4, 0.000001);
        result.PairedShareDeltas.Should().BeEmpty();
    }

    [Fact]
    public void LowSampleCount_IsInsufficientRatherThanUnresolvedDegradation()
    {
        var baseline = CaptureSet("baseline", [20, 20, 20, 20], totalSamples: 50);
        var candidate = CaptureSet("candidate", [20, 20, 20, 20], totalSamples: 50);

        var result = CpuHotspotComparison.Compare(baseline, candidate);

        result.Availability.Should().Be(CpuHotspotEvidenceAvailability.InsufficientObservations);
        result.UnavailableReason.Should().Be("minimum_capture_sample_count_not_met");
        result.BaselineCounts.BelowMinimumSamples.Should().Be(4);
    }

    [Fact]
    public void MissingSelectedMethod_IsInsufficientAndNotTreatedAsZero()
    {
        var baseline = CaptureSet("baseline", [40, 40, 40, 40]);
        var candidate = CaptureSet("candidate", [40, 40, 40, 40]);
        candidate = candidate with
        {
            Captures = candidate.Captures.Select((capture, index) =>
                index == 2
                    ? capture with
                    {
                        Capture = capture.Capture with
                        {
                            TotalMethods = 2,
                            Methods =
                            [
                                new("other", "Other", 60),
                                new("third", "Third", 40),
                            ],
                        },
                    }
                    : capture).ToArray(),
        };

        var result = CpuHotspotComparison.Compare(baseline, candidate);

        result.Availability.Should().Be(CpuHotspotEvidenceAvailability.InsufficientObservations);
        result.UnavailableReason.Should().Be("selected_method_not_observed_in_every_complete_pair");
        result.CandidateCaptures.Single(capture => capture.PairKey == "pair-3")
            .SelectedMethodInclusiveSamples.Should().BeNull();
    }

    [Fact]
    public void PairingContractFailures_AreExplicitlyIncompatible()
    {
        var baseline = CaptureSet("baseline", [40, 40, 40, 40]);
        var candidate = CaptureSet("candidate", [40, 40, 40, 40]);
        candidate = candidate with
        {
            Captures = candidate.Captures.Select((capture, index) =>
                index == 0
                    ? capture with
                    {
                        PairOrder = capture.PairOrder == CpuHotspotPairOrder.BaselineFirst
                            ? CpuHotspotPairOrder.CandidateFirst
                            : CpuHotspotPairOrder.BaselineFirst,
                    }
                    : index == 1
                        ? capture with { ProcessRunId = "different-run" }
                        : capture).ToArray(),
        };

        var result = CpuHotspotComparison.Compare(baseline, candidate);

        result.Availability.Should().Be(CpuHotspotEvidenceAvailability.Incompatible);
        result.UnavailableReason.Should().Be("pair_order_mismatch");
        result.PairCounts.PairOrderMismatches.Should().Be(1);
        result.PairCounts.ProcessRunMismatches.Should().Be(1);
        result.PairCounts.ValidIndependentComplete.Should().Be(2);
    }

    [Fact]
    public void IncompletePairs_AreInsufficient()
    {
        var baseline = CaptureSet("baseline", [40, 40, 40, 40]);
        var candidate = CaptureSet("candidate", [40, 40, 40, 40]) with
        {
            Captures = CaptureSet("candidate", [40, 40, 40, 40]).Captures.Take(3).ToArray(),
        };

        var result = CpuHotspotComparison.Compare(baseline, candidate);

        result.Availability.Should().Be(CpuHotspotEvidenceAvailability.InsufficientObservations);
        result.UnavailableReason.Should().Be("incomplete_capture_pairs");
        result.PairCounts.Unpaired.Should().Be(1);
    }

    [Fact]
    public void ReusedOrOverlappingCaptures_AreIncompatibleIndependenceUnits()
    {
        var baseline = CaptureSet("baseline", [40, 40, 40, 40]);
        var candidate = CaptureSet("candidate", [40, 40, 40, 40]);
        var firstBaseline = baseline.Captures[0];
        candidate = candidate with
        {
            Captures = candidate.Captures.Select((capture, index) =>
                index == 0
                    ? capture with
                    {
                        Capture = capture.Capture with
                        {
                            ProcessId = firstBaseline.Capture.ProcessId,
                            StartedAt = firstBaseline.Capture.StartedAt,
                        },
                    }
                    : capture).ToArray(),
        };

        var result = CpuHotspotComparison.Compare(baseline, candidate);

        result.Availability.Should().Be(CpuHotspotEvidenceAvailability.Incompatible);
        result.UnavailableReason.Should().Be("overlapping_capture_intervals");
        result.PairCounts.OverlappingCaptureIntervals.Should().BeGreaterThan(0);
        result.PairCounts.ValidIndependentComplete.Should().BeLessThan(
            CpuHotspotComparison.MinimumCompletePairs);
        result.PairedShareDeltas.Should().BeEmpty();
    }

    [Fact]
    public void PairProcessIdentityMismatch_IsIncompatible()
    {
        var baseline = CaptureSet("baseline", [40, 40, 40, 40]);
        var candidate = CaptureSet("candidate", [40, 40, 40, 40]);
        candidate = candidate with
        {
            Captures = candidate.Captures.Select((capture, index) =>
                index == 0
                    ? capture with
                    {
                        Capture = capture.Capture with { ProcessId = 9999 },
                    }
                    : capture).ToArray(),
        };

        var result = CpuHotspotComparison.Compare(baseline, candidate);

        result.Availability.Should().Be(CpuHotspotEvidenceAvailability.Incompatible);
        result.UnavailableReason.Should().Be("process_identity_pairing_mismatch");
        result.PairCounts.ProcessIdMismatches.Should().Be(1);
    }

    [Fact]
    public void InputOrdering_DoesNotChangeEvidenceOrProvenance()
    {
        var baseline = CaptureSet("baseline", [40, 42, 38, 40]);
        var candidate = CaptureSet("candidate", [41, 39, 40, 40]);

        var original = CpuHotspotComparison.Compare(baseline, candidate);
        var reordered = CpuHotspotComparison.Compare(
            baseline with { Captures = baseline.Captures.Reverse().ToArray() },
            candidate with { Captures = candidate.Captures.Reverse().ToArray() });

        reordered.PairedShareDeltas.Should().Equal(original.PairedShareDeltas);
        reordered.Provenance.OrderedInputSha256.Should().Be(original.Provenance.OrderedInputSha256);
        reordered.Provenance.OrderedPairKeys.Should().Equal(
            "pair-1", "pair-2", "pair-3", "pair-4");
    }

    [Fact]
    public void Validation_EnforcesBoundsAndV1Quantity()
    {
        var baseline = CaptureSet("baseline", [40, 40, 40, 40]);
        var tooMany = baseline with
        {
            Captures = Enumerable.Range(1, CpuHotspotComparison.MaximumCapturesPerSide + 1)
                .Select(index => Observation(index, 40))
                .ToArray(),
        };
        var invalidQuantity = baseline with
        {
            Identity = baseline.Identity with
            {
                Quantity = baseline.Identity.Quantity with { Unit = "milliseconds" },
            },
        };

        var compareTooMany = () => CpuHotspotComparison.Compare(tooMany, baseline);
        var compareInvalidQuantity = () => CpuHotspotComparison.Compare(invalidQuantity, baseline);

        compareTooMany.Should().Throw<ArgumentException>()
            .WithMessage($"*1..{CpuHotspotComparison.MaximumCapturesPerSide}*");
        compareInvalidQuantity.Should().Throw<ArgumentException>()
            .WithMessage("*inclusive sample-share quantity*");
    }

    [Fact]
    public void CaptureProjection_IsBoundedAndPreservesCountsAndUnresolvedShareInput()
    {
        var children = Enumerable.Range(1, 18)
            .Select(index => new CallTreeNode(
                new SampledFrame("Demo", index == 18 ? "[unknown]" : $"Method{index:D2}"),
                20 - index,
                index == 18 ? 7 : 1,
                []))
            .ToArray();
        var artifact = new CpuSampleTraceArtifact(
            42,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(5),
            100,
            new CallTreeNode(new SampledFrame("Demo", "Root"), 100, 0, children),
            new Dictionary<SymbolRef, SourceLocation>(),
            new Dictionary<SymbolRef, MethodIdentity>())
        {
            Evidence = CpuSampleEvidence.EventPipeSampleProfiler,
        };

        var profile = CpuHotspotCaptureProjection.Project(artifact);

        profile.TotalSamples.Should().Be(100);
        profile.UnresolvedExclusiveSamples.Should().Be(7);
        profile.Methods.Should().HaveCount(CpuHotspotCaptureProjection.MaximumMethodsPerCapture);
        profile.TotalMethods.Should().BeGreaterThan(profile.Methods.Count);
        profile.OmittedMethods.Should().Be(profile.TotalMethods - profile.Methods.Count);
        profile.Methods.Select(static method => method.InclusiveSamples)
            .Should().BeInDescendingOrder();
        profile.Methods.Should().OnlyContain(method => !string.IsNullOrWhiteSpace(method.MethodId));
    }

    private static CpuHotspotCaptureSet CaptureSet(
        string axisValue,
        int[] selectedSamples,
        int totalSamples = 100,
        int otherSamples = 50,
        int unresolvedSamples = 5)
        => new(
            CpuHotspotCaptureSet.SchemaV1,
            axisValue,
            Identity(axisValue),
            selectedSamples.Select((samples, index) =>
                Observation(
                    index + 1,
                    samples,
                    totalSamples,
                    otherSamples,
                    unresolvedSamples,
                    candidate: string.Equals(axisValue, "candidate", StringComparison.Ordinal)))
                .ToArray());

    private static CpuHotspotCompatibilityIdentity Identity(string axisValue, int offeredLoad = 100)
        => new(
            new("CoreCLR", "same-major-minor", "linux", "x64"),
            new(
                CpuSampleBackend.EventPipeSampleProfiler.ToString(),
                "1",
                CpuSampleEvidenceKind.StackFrequencyWithHeuristicWaits),
            new(
                "cpu-hotspot-inclusive-share",
                "sample-share",
                "inclusive-stack-membership",
                "retained-total-samples",
                "none",
                "selected",
                CpuHotspotRepresentation.Exact),
            new(5, "1ms", "top-16", "none"),
            new("steady-load", "1", "route=/work", offeredLoad, "same-node"),
            new("capture", "samples-within-capture", "pairKey"),
            new("build", axisValue));

    private static CpuHotspotCaptureObservation Observation(
        int index,
        int selectedSamples,
        int totalSamples = 100,
        int otherSamples = 50,
        int unresolvedSamples = 5,
        bool candidate = false)
    {
        var pairOrder = index % 2 == 0
            ? CpuHotspotPairOrder.CandidateFirst
            : CpuHotspotPairOrder.BaselineFirst;
        var startedAt = DateTimeOffset.UnixEpoch.AddMinutes(index * 2);
        if ((candidate && pairOrder == CpuHotspotPairOrder.BaselineFirst)
            || (!candidate && pairOrder == CpuHotspotPairOrder.CandidateFirst))
        {
            startedAt = startedAt.AddSeconds(6);
        }
        var methods = new[]
        {
            new CpuHotspotMethodObservation("selected", "Selected", selectedSamples),
            new CpuHotspotMethodObservation("other", "Other", otherSamples),
            new CpuHotspotMethodObservation("third", "Third", 10),
        };
        return new(
            $"capture-{index}",
            $"pair-{index}",
            pairOrder,
            $"run-{index}",
            new(
                CpuHotspotCaptureProfile.SchemaV1,
                1000 + index,
                startedAt,
                5,
                totalSamples,
                unresolvedSamples,
                CpuSampleBackend.EventPipeSampleProfiler,
                CpuSampleEvidenceKind.StackFrequencyWithHeuristicWaits,
                CpuHotspotRepresentation.Exact,
                methods.Length,
                0,
                methods));
    }
}
