using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Evidence;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class RetainedGrowthEvidenceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TypeIdentity Target = new("GcDumpPressureNode")
    {
        ModuleName = "CoreClrSample.dll",
    };

    [Fact]
    public void RetainedGrowth_ReportsExactPairsAndBoundedRepeatedEvidence()
    {
        var result = RetainedGrowthEvidence.Build(
            Target,
            Attempts(
                bytesDelta: 640_000,
                instancesDelta: 20_000,
                allocationBytes: [11_700_000, 11_724_552, 11_800_000]));

        result.Pairs.Should().OnlyContain(pair =>
            pair.Availability == RetainedGrowthEvidenceAvailability.Available
            && pair.BaselineRetainedBytes == 128_000
            && pair.CurrentRetainedBytes == 768_000
            && pair.RetainedBytesDelta == 640_000
            && pair.BaselineRetainedInstances == 4_000
            && pair.CurrentRetainedInstances == 24_000
            && pair.RetainedInstancesDelta == 20_000);
        result.Repeated.Availability.Should().Be(RetainedGrowthEvidenceAvailability.Available);
        result.Repeated.EstimateMethod.Id.Should().Be(RetainedGrowthEvidenceMethodIds.WindowMedian);
        result.Repeated.DispersionMethod.Id.Should()
            .Be(RetainedGrowthEvidenceMethodIds.MedianAbsoluteDeviation);
        result.Repeated.IntervalMethod.Id.Should()
            .Be(RetainedGrowthEvidenceMethodIds.PercentileBootstrapInterval);
        result.Repeated.IndependenceUnit.Should().Be("process_run");
        result.Repeated.AcceptedPairs.Should().Be(3);
        result.Repeated.PositivePairedGrowthCount.Should().Be(3);
        result.Repeated.MedianRetainedBytesDelta.Should().Be(640_000);
        result.Repeated.MedianRetainedInstancesDelta.Should().Be(20_000);
        result.Repeated.RetainedBytesDeltaMad.Should().Be(0);
        result.Repeated.RetainedInstancesDeltaMad.Should().Be(0);
        result.Repeated.RetainedBytesDeltaInterval.Should().Be(
            new RetainedGrowthInterval(640_000, 640_000, 0.95, 640_000));
        result.Repeated.OrderedInputSha256.Should().HaveLength(64);
        result.Repeated.RetainedBytesBootstrapSeedSha256.Should().HaveLength(64);
        result.Repeated.RetainedInstancesBootstrapSeedSha256.Should().HaveLength(64);
        result.Repeated.RetainedBytesBootstrapSeedSha256.Should()
            .NotBe(result.Repeated.RetainedInstancesBootstrapSeedSha256);
        result.Repeated.BootstrapResamples.Should().Be(RetainedGrowthEvidenceBounds.BootstrapResamples);
        result.Counts.Should().Be(new RetainedGrowthEvidenceCounts(
            Attempts: 3,
            SuccessfulAttempts: 3,
            FailedAttempts: 0,
            AbortedAttempts: 0,
            Captures: 6,
            CompletePairs: 3,
            AcceptedPairs: 3,
            RejectedPairs: 0,
            ProcessRuns: 3,
            AllocationObservations: 3));
        result.AllocationEvidence.Availability.Should().Be(RetainedGrowthEvidenceAvailability.Available);
        result.AllocationEvidence.MedianSampledBytes.Should().Be(11_724_552);
        result.RootPathEvidence.Availability.Should()
            .Be(RetainedGrowthRootPathAvailability.UnavailableByMechanism);
        result.ObserverEffects.ObservedGen2Collections.Should().Be(9);
    }

    [Fact]
    public void AllocationChurn_KeepsAllocationTrafficSeparateFromZeroRetainedDelta()
    {
        var result = RetainedGrowthEvidence.Build(
            Target,
            Attempts(
                bytesDelta: 0,
                instancesDelta: 0,
                allocationBytes: [11_400_000, 11_495_728, 11_600_000]));

        result.Pairs.Should().OnlyContain(pair =>
            pair.RetainedBytesDelta == 0 && pair.RetainedInstancesDelta == 0);
        result.Repeated.MedianRetainedBytesDelta.Should().Be(0);
        result.Repeated.MedianRetainedInstancesDelta.Should().Be(0);
        result.Repeated.PositivePairedGrowthCount.Should().Be(0);
        result.AllocationEvidence.MedianSampledBytes.Should().Be(11_495_728);
        result.Limitations.Should().Contain(item =>
            item.Contains("never contributes", StringComparison.Ordinal));
    }

    [Fact]
    public void StableHeap_ReportsZeroWithoutManufacturingAllocationEvidenceOrHealthVerdict()
    {
        var result = RetainedGrowthEvidence.Build(
            Target,
            Attempts(bytesDelta: 0, instancesDelta: 0, allocationBytes: null));

        result.Repeated.Availability.Should().Be(RetainedGrowthEvidenceAvailability.Available);
        result.Repeated.MedianRetainedBytesDelta.Should().Be(0);
        result.AllocationEvidence.Availability.Should()
            .Be(RetainedGrowthEvidenceAvailability.NotApplicable);

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        });
        json.Should().NotContain("\"verdict\"");
        json.Should().NotContain("leak_suspected");
        json.Should().NotContain("no_leak");
        json.Should().NotContain("\"healthy\"");
    }

    [Fact]
    public void OneCompatiblePair_ReportsExactPairButRepeatedEvidenceIsInsufficient()
    {
        var result = RetainedGrowthEvidence.Build(
            Target,
            [Attempt(1, 640_000, 20_000)]);

        result.Pairs.Should().ContainSingle().Which.Availability.Should()
            .Be(RetainedGrowthEvidenceAvailability.Available);
        result.Pairs[0].RetainedBytesDelta.Should().Be(640_000);
        result.Repeated.Availability.Should()
            .Be(RetainedGrowthEvidenceAvailability.InsufficientObservations);
        result.Repeated.MedianRetainedBytesDelta.Should().BeNull();
        result.Repeated.UnavailableFields.Should().Contain(field =>
            field.Field == "medianRetainedBytesDelta"
            && field.Reason == "minimum_three_compatible_process_run_pairs_not_met");
    }

    [Fact]
    public void IncompatiblePair_DoesNotPoolRuntimeMismatch()
    {
        var attempt = Attempt(1, 640_000, 20_000);
        attempt = attempt with
        {
            Current = attempt.Current! with
            {
                Runtime = attempt.Current.Runtime with { Version = "9.0.0" },
            },
        };

        var result = RetainedGrowthEvidence.Build(Target, [attempt]);

        result.Pairs.Should().ContainSingle().Which.Availability.Should()
            .Be(RetainedGrowthEvidenceAvailability.Incompatible);
        result.Pairs[0].RetainedBytesDelta.Should().BeNull();
        result.Pairs[0].CompatibilityMismatches.Should().Contain(mismatch =>
            mismatch.Dimension == "runtime.version");
        result.Repeated.Availability.Should().Be(RetainedGrowthEvidenceAvailability.Incompatible);
    }

    [Fact]
    public void DegradedLegacyGcdump_DoesNotConvertObservedRowsIntoAvailableGrowth()
    {
        var attempt = Attempt(1, 640_000, 20_000);
        attempt = attempt with
        {
            Baseline = attempt.Baseline! with { Quality = EvidenceQuality.LegacyUnknown },
        };

        var result = RetainedGrowthEvidence.Build(Target, [attempt]);

        result.Pairs.Should().ContainSingle().Which.Availability.Should()
            .Be(RetainedGrowthEvidenceAvailability.UnsupportedApproximation);
        result.Pairs[0].RetainedBytesDelta.Should().BeNull();
        result.Pairs[0].UnavailableFields.Should().Contain(field =>
            field.Reason == "retained_positive_evidence_not_supported");
        result.RootPathEvidence.Availability.Should()
            .Be(RetainedGrowthRootPathAvailability.UnavailableByMechanism);
    }

    [Fact]
    public void DetectedLoss_TakesPrecedenceOverSuccessShapedGrowth()
    {
        var lossQuality = new EvidenceQuality(
            EvidenceQuality.SchemaV1,
            [
                new EvidenceLimitation(
                    EvidenceLimitationCategory.DetectedTransportLoss,
                    "eventpipe",
                    7,
                    "Seven events were reported lost."),
            ],
            new EvidenceConclusionPolicy(
                EvidenceConclusionSupport.Supported,
                EvidenceConclusionSupport.Inconclusive,
                EvidenceConclusionSupport.Inconclusive));
        var attempt = Attempt(1, 640_000, 20_000);
        attempt = attempt with
        {
            Current = attempt.Current! with { Quality = lossQuality },
        };

        var result = RetainedGrowthEvidence.Build(Target, [attempt]);

        result.Pairs.Should().ContainSingle().Which.Availability.Should()
            .Be(RetainedGrowthEvidenceAvailability.LossDetected);
        result.Pairs[0].RetainedBytesDelta.Should().BeNull();
        result.Repeated.Availability.Should().Be(RetainedGrowthEvidenceAvailability.LossDetected);
    }

    [Fact]
    public void MissingTypeInBoundedClrMdRanking_DoesNotFabricateExactZero()
    {
        var attempt = Attempt(1, 640_000, 20_000);
        var other = new TypeStat("Other.Type", null, 10, 1_000, 100);
        attempt = attempt with
        {
            Current = attempt.Current! with
            {
                Origin = HeapSnapshotOrigin.Live,
                TopTypesByBytes = [other],
                TopTypesByInstances = [other],
                Quality = null,
                GcDumpStatus = null,
            },
            Baseline = attempt.Baseline! with
            {
                Origin = HeapSnapshotOrigin.Live,
                Quality = null,
                GcDumpStatus = null,
            },
        };

        var result = RetainedGrowthEvidence.Build(Target, [attempt]);

        result.Pairs.Should().ContainSingle().Which.Availability.Should()
            .Be(RetainedGrowthEvidenceAvailability.UnsupportedApproximation);
        result.Pairs[0].BaselineRetainedBytes.Should().Be(128_000);
        result.Pairs[0].CurrentRetainedBytes.Should().BeNull();
        result.Pairs[0].RetainedBytesDelta.Should().BeNull();
        result.Pairs[0].UnavailableFields.Should().Contain(field =>
            field.Reason == "selected_type_not_observed_and_absence_not_supported");
    }

    [Fact]
    public void ReusedProcessId_DoesNotCountAsIndependentRepeatedEvidence()
    {
        var attempts = Attempts(640_000, 20_000, allocationBytes: null).ToArray();
        attempts[2] = attempts[2] with
        {
            Baseline = attempts[2].Baseline! with { ProcessId = attempts[0].Baseline!.ProcessId },
            Current = attempts[2].Current! with { ProcessId = attempts[0].Current!.ProcessId },
        };

        var result = RetainedGrowthEvidence.Build(Target, attempts);

        result.Counts.AcceptedPairs.Should().Be(2);
        result.Counts.RejectedPairs.Should().Be(1);
        result.Counts.ProcessRuns.Should().Be(2);
        result.Repeated.Availability.Should().Be(RetainedGrowthEvidenceAvailability.Incompatible);
        result.Pairs.Should().ContainSingle(pair =>
            pair.CompatibilityMismatches.Any(mismatch =>
                mismatch.Reason == "cross_attempt_process_id_reused"));
    }

    [Fact]
    public void FailedAndAbortedAttempts_RemainInCountsWithoutRetryReplacement()
    {
        var attempts = Attempts(640_000, 20_000, allocationBytes: null).ToList();
        attempts.Add(new(
            "run-4",
            RetainedGrowthAttemptOutcome.Failed,
            null,
            null,
            null,
            null)
        {
            FailureReason = "capture failed",
        });
        attempts.Add(new(
            "run-5",
            RetainedGrowthAttemptOutcome.Aborted,
            null,
            null,
            null,
            null));

        var result = RetainedGrowthEvidence.Build(Target, attempts);

        result.Counts.Attempts.Should().Be(5);
        result.Counts.SuccessfulAttempts.Should().Be(3);
        result.Counts.FailedAttempts.Should().Be(1);
        result.Counts.AbortedAttempts.Should().Be(1);
        result.Repeated.Availability.Should().Be(RetainedGrowthEvidenceAvailability.Available);
        result.Repeated.AcceptedPairs.Should().Be(3);
        result.Pairs.Where(pair => pair.Outcome != RetainedGrowthAttemptOutcome.Completed)
            .Should().OnlyContain(pair => pair.CompatibilityIdentity == null);
        result.Limitations.Should().Contain(item =>
            item.Contains("hidden retries", StringComparison.Ordinal));
    }

    private static IReadOnlyList<RetainedGrowthEvidenceAttempt> Attempts(
        long bytesDelta,
        long instancesDelta,
        IReadOnlyList<long>? allocationBytes)
        => Enumerable.Range(1, 3)
            .Select(index =>
            {
                var attempt = Attempt(index, bytesDelta, instancesDelta);
                return allocationBytes is null
                    ? attempt
                    : attempt with
                    {
                        Allocation = new(
                            allocationBytes[index - 1],
                            SampleEvents: 100 + index,
                            LostOrDropped: 0),
                    };
            })
            .ToArray();

    private static RetainedGrowthEvidenceAttempt Attempt(
        int run,
        long bytesDelta,
        long instancesDelta)
    {
        const long baselineBytes = 128_000;
        const long baselineInstances = 4_000;
        var processId = 1000 + run;
        return new(
            $"run-{run}",
            RetainedGrowthAttemptOutcome.Completed,
            $"baseline-{run}",
            Snapshot(processId, T0.AddMinutes(run), baselineBytes, baselineInstances),
            $"current-{run}",
            Snapshot(
                processId,
                T0.AddMinutes(run).AddSeconds(10),
                baselineBytes + bytesDelta,
                baselineInstances + instancesDelta))
        {
            ObservedGen2Collections = 3,
        };
    }

    private static HeapSnapshotArtifact Snapshot(
        int processId,
        DateTimeOffset capturedAt,
        long bytes,
        long instances)
    {
        var row = new TypeStat(
            Target.TypeFullName,
            Target.ModuleName,
            instances,
            bytes,
            100,
            Target);
        return new(
            HeapSnapshotOrigin.GcDump,
            processId,
            capturedAt,
            TimeSpan.FromMilliseconds(50),
            new DumpRuntimeInfo("CoreCLR", "10.0.0", "x64", IsServerGC: false, HeapCount: 1),
            new DumpHeapSummary(bytes, 0, 0, bytes, 0, 0, bytes),
            [row],
            [row])
        {
            Quality = CompleteGcDumpQuality,
            GcDumpStatus = new(
                GcStopObserved: true,
                EventStreamCompleted: true,
                TimedOut: false,
                ReaderFailed: false,
                TraceExportRequested: false,
                TraceExportCompleted: false),
        };
    }

    private static EvidenceQuality CompleteGcDumpQuality { get; } = new(
        EvidenceQuality.SchemaV1,
        [
            new EvidenceLimitation(
                EvidenceLimitationCategory.MechanismUnobservable,
                "eventpipe-loss",
                null,
                "EventPipe loss is not observable for gcdump."),
            new EvidenceLimitation(
                EvidenceLimitationCategory.MechanismUnavailable,
                "object-graph",
                null,
                "Gcdump has no root/path graph."),
        ],
        new EvidenceConclusionPolicy(
            EvidenceConclusionSupport.Supported,
            EvidenceConclusionSupport.Inconclusive,
            EvidenceConclusionSupport.Inconclusive));
}
