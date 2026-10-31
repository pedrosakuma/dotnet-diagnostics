using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using FluentAssertions;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class AllocationReproducibilityTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;
    private readonly ITestOutputHelper _output;

    public AllocationReproducibilityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void StableCaptures_ReportDescriptiveTotalsAndIndependentBoundedRanks()
    {
        var inputs = new[]
        {
            Input("a", Artifact(Start, 900, 9, 900, 9, Types(("A", 500, 5), ("B", 300, 3), ("C", 100, 1)), Stacks(("M1", 500, 5), ("M2", 300, 3), ("M3", 100, 1)))),
            Input("b", Artifact(Start.AddSeconds(10), 1000, 10, 1000, 10, Types(("A", 600, 6), ("B", 300, 3), ("C", 100, 1)), Stacks(("M1", 600, 6), ("M2", 300, 3), ("M3", 100, 1)))),
            Input("c", Artifact(Start.AddSeconds(20), 1100, 11, 1000, 10, Types(("A", 700, 7), ("B", 300, 3), ("C", 100, 1)), Stacks(("M1", 700, 7), ("M2", 300, 3), ("M3", 100, 1)))),
        };

        var result = AllocationReproducibility.Analyze(inputs).Data!;

        result.Schema.Should().Be(AllocationReproducibilityResult.SchemaV1);
        result.Method.Should().Be(AllocationReproducibilityResult.MethodV1);
        result.Availability.Should().Be(AllocationReproducibilityAvailability.Available);
        result.IndependenceUnit.Should().Be("capture");
        result.EstimatedBytes.Median.Should().Be(1000);
        result.EstimatedBytes.RawMedianAbsoluteDeviation.Should().Be(100);
        result.AllocationTicks.Median.Should().Be(10);
        result.AllocationTicks.RawMedianAbsoluteDeviation.Should().Be(1);
        result.AllocatedTypeStability.MedianMembershipJaccard.Should().Be(1);
        result.AllocatedTypeStability.MedianSharedKeySpearman.Should().Be(1);
        result.AllocationStackStability.MedianMembershipJaccard.Should().Be(1);
        result.AllocationStackStability.MedianSharedKeySpearman.Should().Be(1);
        result.Captures[2].AttributionState.Should().Be(AllocationAttributionState.Partial);
        result.Captures[2].AttributedByteShare.Should().BeApproximately(1000d / 1100d, 0.000001);
        result.Captures[2].UnattributedByteShare.Should().BeApproximately(100d / 1100d, 0.000001);

        var json = JsonSerializer.SerializeToElement(result);
        json.TryGetProperty("verdict", out _).Should().BeFalse();
        json.TryGetProperty("severity", out _).Should().BeFalse();
        json.TryGetProperty("regression", out _).Should().BeFalse();
        json.TryGetProperty("health", out _).Should().BeFalse();
    }

    [Fact]
    public void RankStability_UsesSharedKeyRerankingAndStaysBounded()
    {
        var left = Artifact(
            Start,
            100,
            3,
            100,
            3,
            Types(("A", 60, 1), ("B", 30, 1), ("C", 10, 1)),
            Stacks(("M1", 60, 1), ("M2", 30, 1), ("M3", 10, 1)));
        var reversed = Artifact(
            Start.AddSeconds(10),
            100,
            3,
            100,
            3,
            Types(("C", 60, 1), ("B", 30, 1), ("A", 10, 1)),
            Stacks(("M3", 60, 1), ("M2", 30, 1), ("M1", 10, 1)));

        var result = AllocationReproducibility.Analyze([Input("left", left), Input("right", reversed)]).Data!;

        result.AllocatedTypeStability.MedianMembershipJaccard.Should().Be(1);
        result.AllocatedTypeStability.MedianSharedKeySpearman.Should().Be(-1);
        result.AllocationStackStability.MedianSharedKeySpearman.Should().Be(-1);
        result.AllocatedTypeStability.MedianSharedKeySpearman.Should().BeInRange(-1, 1);
        result.AllocationStackStability.MedianSharedKeySpearman.Should().BeInRange(-1, 1);
    }

    [Fact]
    public void ChangedMixReplacesMembershipWithoutInventingRankCorrelation()
    {
        var before = Artifact(
            Start,
            100,
            2,
            100,
            2,
            Types(("A", 60, 1), ("B", 40, 1)),
            Stacks(("M1", 60, 1), ("M2", 40, 1)));
        var after = Artifact(
            Start.AddSeconds(10),
            100,
            2,
            100,
            2,
            Types(("C", 60, 1), ("D", 40, 1)),
            Stacks(("M3", 60, 1), ("M4", 40, 1)));

        var result = AllocationReproducibility.Analyze(
            [Input("before", before), Input("after", after)],
            topK: 2).Data!;

        result.AllocatedTypeStability.MedianMembershipJaccard.Should().Be(0);
        result.AllocationStackStability.MedianMembershipJaccard.Should().Be(0);
        result.AllocatedTypeStability.MedianSharedKeySpearman.Should().BeNull();
        result.AllocationStackStability.MedianSharedKeySpearman.Should().BeNull();
        result.AllocatedTypeStability.SharedRankUnavailableReason.Should()
            .Be("fewer-than-two-shared-ranked-keys");
    }

    [Fact]
    public void LowSampleCapturesRemainDescriptiveWithoutUniversalThresholdGate()
    {
        var one = Artifact(
            Start,
            16,
            1,
            16,
            1,
            Types(("A", 16, 1)),
            Stacks(("M", 16, 1)));
        var two = one with
        {
            Summary = one.Summary with { StartedAt = Start.AddSeconds(10) },
        };

        var result = AllocationReproducibility.Analyze([Input("one", one), Input("two", two)]).Data!;

        result.Availability.Should().Be(AllocationReproducibilityAvailability.Available);
        result.AllocationTicks.Median.Should().Be(1);
        result.AllocatedTypeStability.MedianMembershipJaccard.Should().Be(1);
        result.AllocatedTypeStability.MedianSharedKeySpearman.Should().BeNull();
        result.Limitations.Should().Contain(limitation =>
            limitation.Contains("Scenario validation thresholds are intentionally absent", StringComparison.Ordinal));
    }

    [Fact]
    public void AttributionMatrix_RemainsExplicit()
    {
        var cases = new[]
        {
            ("raw-empty", Artifact(Start, 0, 0, 0, 0, [], []), AllocationAttributionState.RawEmpty),
            ("no-stack", Artifact(Start, 100, 2, 0, 0, Types(("A", 100, 2)), []), AllocationAttributionState.NoStack),
            ("zero-weight", Artifact(Start, 0, 2, 0, 2, [], Stacks(("M", 0, 2))), AllocationAttributionState.ZeroWeight),
            ("partial", Artifact(Start, 100, 2, 50, 1, Types(("A", 100, 2)), Stacks(("M", 50, 1))), AllocationAttributionState.Partial),
            ("complete", Artifact(Start, 100, 2, 100, 2, Types(("A", 100, 2)), Stacks(("M", 100, 2))), AllocationAttributionState.Complete),
            ("unavailable", Artifact(Start, 100, 2, null, null, Types(("A", 100, 2)), []), AllocationAttributionState.Unavailable),
        };

        foreach (var (handle, artifact, expected) in cases)
        {
            var result = AllocationReproducibility.Analyze(
                [Input(handle, artifact), Input(handle + "-second", artifact with
                {
                    Summary = artifact.Summary with { StartedAt = Start.AddSeconds(10) },
                })]).Data!;
            result.Captures.Should().OnlyContain(capture => capture.AttributionState == expected);
        }
    }

    [Fact]
    public void UnavailableHandlesRemainInDenominatorWithStableReasons()
    {
        var available = Input("available", Artifact(
            Start,
            100,
            2,
            100,
            2,
            Types(("A", 100, 2)),
            Stacks(("M", 100, 2))));
        var expired = new AllocationReproducibilityInput(
            "expired",
            DiagnosticHandleLookupStatus.Expired,
            null,
            null);

        var result = AllocationReproducibility.Analyze([available, expired]).Data!;

        result.Availability.Should().Be(AllocationReproducibilityAvailability.InsufficientCaptureCount);
        result.RequestedCaptureCount.Should().Be(2);
        result.AvailableCaptureCount.Should().Be(1);
        result.Captures.Single(capture => capture.Handle == "expired").Should().Match<AllocationReproducibilityCapture>(
            capture => capture.HandleStatus == "expired"
                && capture.AttributionState == AllocationAttributionState.UnavailableHandle
                && capture.UnavailableReason == "handle-expired");
        result.EstimatedBytes.Median.Should().BeNull();
    }

    [Theory]
    [InlineData(DiagnosticHandleLookupStatus.Unknown, "unknown", "handle-unknown")]
    [InlineData(DiagnosticHandleLookupStatus.CapacityEvicted, "capacity-evicted", "handle-capacity-evicted")]
    public void InvalidHandlesRetainLookupState(
        DiagnosticHandleLookupStatus status,
        string expectedStatus,
        string expectedReason)
    {
        var result = AllocationReproducibility.Analyze(
            [new AllocationReproducibilityInput("missing", status, null, null)]).Data!;

        result.Availability.Should().Be(AllocationReproducibilityAvailability.InsufficientCaptureCount);
        result.Captures.Should().ContainSingle().Which.Should().Match<AllocationReproducibilityCapture>(
            capture => capture.HandleStatus == expectedStatus
                && capture.UnavailableReason == expectedReason
                && capture.AttributionState == AllocationAttributionState.UnavailableHandle);
    }

    [Fact]
    public void MissingOrMismatchedIdentityIsIncompatibleAndNeverBackfilled()
    {
        var current = Artifact(
            Start,
            100,
            2,
            100,
            2,
            Types(("A", 100, 2)),
            Stacks(("M", 100, 2)));
        var legacy = current with
        {
            Summary = current.Summary with { StartedAt = Start.AddSeconds(10) },
            Reproducibility = null,
        };

        var missing = AllocationReproducibility.Analyze([Input("current", current), Input("legacy", legacy)]).Data!;
        missing.Availability.Should().Be(AllocationReproducibilityAvailability.Incompatible);
        missing.Compatibility.Reasons.Should().ContainSingle(reason => reason.Code == "CaptureIdentityUnavailable");
        missing.EstimatedBytes.Median.Should().BeNull();

        var differentDuration = current with
        {
            Summary = current.Summary with { StartedAt = Start.AddSeconds(20) },
            Reproducibility = current.Reproducibility! with { RequestedDurationSeconds = 5 },
        };
        var mismatched = AllocationReproducibility.Analyze(
            [Input("current", current), Input("different", differentDuration)]).Data!;
        mismatched.Availability.Should().Be(AllocationReproducibilityAvailability.Incompatible);
        mismatched.Compatibility.Reasons.Should().Contain(reason =>
            reason.Code == "CaptureIdentityMismatch"
            && reason.Field == "requestedDurationSeconds");
    }

    [Fact]
    public void EquivalentInputOrderingProducesIdenticalBoundedPayload()
    {
        var inputs = Enumerable.Range(0, AllocationReproducibilityBounds.MaximumCaptures)
            .Select(index => Input(
                $"capture-{index}",
                Artifact(
                    Start.AddSeconds(index * 10),
                    1000 + index,
                    10 + index,
                    1000 + index,
                    10 + index,
                    Types(Enumerable.Range(0, AllocationReproducibilityBounds.MaximumTopK)
                        .Select(row => ($"Type{row:D2}", 1000L - row, 20L - row))
                        .ToArray()),
                    Stacks(Enumerable.Range(0, AllocationReproducibilityBounds.MaximumTopK)
                        .Select(row => ($"Method{row:D2}", 1000L - row, 20L - row))
                        .ToArray()))))
            .ToArray();

        var forward = AllocationReproducibility.Analyze(
            inputs,
            AllocationReproducibilityBounds.MaximumTopK).Data!;
        var reversed = AllocationReproducibility.Analyze(
            inputs.Reverse().ToArray(),
            AllocationReproducibilityBounds.MaximumTopK).Data!;

        JsonSerializer.Serialize(reversed).Should().Be(JsonSerializer.Serialize(forward));
        forward.Captures.Should().HaveCount(AllocationReproducibilityBounds.MaximumCaptures);
        forward.Captures.Should().OnlyContain(capture =>
            capture.AllocatedTypes.Count <= AllocationReproducibilityBounds.MaximumTopK
            && capture.AllocationStacks.Count <= AllocationReproducibilityBounds.MaximumTopK);
        var payloadBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(forward));
        _output.WriteLine(
            $"maximum bounded allocation reproducibility payload: {payloadBytes:N0} bytes");
        payloadBytes.Should().Be(37_670,
            "the documented bounded 8-capture × top-16 payload measurement must be updated deliberately when the additive schema changes");
    }

    [Fact]
    public void BoundsAndDuplicateHandlesFailExplicitly()
    {
        var artifact = Artifact(Start, 1, 1, 1, 1, Types(("A", 1, 1)), Stacks(("M", 1, 1)));
        AllocationReproducibility.Analyze(
                Enumerable.Range(0, AllocationReproducibilityBounds.MaximumCaptures + 1)
                    .Select(index => Input(index.ToString(), artifact))
                    .ToArray())
            .IsError.Should().BeTrue();
        AllocationReproducibility.Analyze([Input("same", artifact), Input("same", artifact)])
            .IsError.Should().BeTrue();
        AllocationReproducibility.Analyze([Input("one", artifact)], 0)
            .IsError.Should().BeTrue();
    }

    [Fact]
    public void AvailableWrongKindHandleIsRejectedInsteadOfBeingLabeledHistorical()
    {
        var metadata = new DiagnosticHandle(
            "cpu",
            Start.AddHours(1),
            42,
            "cpu-sample");
        var wrongKind = new AllocationReproducibilityInput(
            "cpu",
            DiagnosticHandleLookupStatus.Found,
            metadata,
            null);

        var result = AllocationReproducibility.Analyze([wrongKind]);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Message.Should().Contain("cpu-sample");
        result.Error.Message.Should().NotContain("predate");
    }

    [Fact]
    public void HistoricalArtifactWithoutIdentityBlockStillDecodesWithoutBackfill()
    {
        var artifact = Artifact(Start, 100, 2, 100, 2, Types(("A", 100, 2)), Stacks(("M", 100, 2)));
        var encoded = CaptureArtifactCodec.Encode("allocation-sample", artifact, 1024 * 1024);
        var payload = JsonNode.Parse(encoded)!;
        payload["snapshot"]!.AsObject().Remove("Reproducibility").Should().BeTrue();

        var restored = (AllocationSampleArtifact)CaptureArtifactCodec.Decode(
            "allocation-sample",
            CaptureArtifactCodec.FormatVersion,
            Encoding.UTF8.GetBytes(payload.ToJsonString()),
            1024 * 1024);

        restored.Reproducibility.Should().BeNull();
        restored.Summary.Should().BeEquivalentTo(artifact.Summary);
        restored.EstimatedBytesTrace.Should().BeEquivalentTo(artifact.EstimatedBytesTrace);
    }

    private static AllocationReproducibilityInput Input(string handle, AllocationSampleArtifact artifact)
    {
        var metadata = new DiagnosticHandle(
            handle,
            Start.AddHours(1),
            artifact.Summary.ProcessId,
            "allocation-sample")
        {
            Origin = HandleOrigin.Live,
            ProducingTool = "collect_sample",
        };
        return new(handle, DiagnosticHandleLookupStatus.Found, metadata, artifact);
    }

    private static AllocationSampleArtifact Artifact(
        DateTimeOffset startedAt,
        long estimatedBytes,
        long ticks,
        long? attributedBytes,
        long? attributedTicks,
        IReadOnlyList<AllocatedType> types,
        IReadOnlyList<AllocationSite> stacks)
    {
        var sample = new AllocationSample(
            42,
            startedAt,
            TimeSpan.FromSeconds(3),
            ticks,
            estimatedBytes,
            types,
            types.OrderByDescending(static row => row.EventCount).ToArray())
        {
            TopBySite = stacks,
        };
        return new AllocationSampleArtifact(sample, Trace(ticks, attributedTicks ?? 0))
        {
            EstimatedBytesTrace = attributedBytes is null
                ? null
                : Trace(estimatedBytes, attributedBytes.Value),
            Reproducibility = Metadata(),
        };
    }

    private static AllocationReproducibilityCaptureMetadata Metadata()
        => new(
            AllocationReproducibilityCaptureMetadata.SchemaV1,
            "CoreClr",
            "10.0.0",
            "windows",
            "x64",
            AllocationReproducibilityCaptureMetadata.EventPipeBackend,
            "gc-allocation-tick-estimated-bytes",
            "estimated-bytes",
            "capture-total",
            "none",
            3,
            AllocationReproducibilityBounds.MaximumTopK,
            Start.AddMinutes(-1));

    private static AllocatedType[] Types(params (string Name, long Bytes, long Ticks)[] rows)
        => rows.Select(row => new AllocatedType(
            row.Name,
            row.Bytes,
            row.Ticks,
            HeapKind.Small)).ToArray();

    private static AllocationSite[] Stacks(params (string Method, long Bytes, long Ticks)[] rows)
        => rows.Select(row => new AllocationSite(
            new SampledFrame("App.dll", row.Method),
            row.Bytes,
            row.Ticks,
            HeapKind.Small)).ToArray();

    private static CpuSampleTraceArtifact Trace(long total, long attributed)
        => new(
            42,
            Start,
            TimeSpan.FromSeconds(3),
            total,
            new CallTreeNode(
                new SampledFrame(string.Empty, "<root>"),
                attributed,
                0,
                []));
}
