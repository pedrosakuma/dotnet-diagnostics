using System.Collections.Concurrent;
using System.Text.Json;
using DotnetDiagnostics.Core.Evidence;
using DotnetDiagnostics.Core.ThreadPool;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class EventPipeThreadPoolCollectorTests
{
    [Theory]
    [InlineData(50, "ThreadPoolWorkerThreadStart")]
    [InlineData(55, "ThreadPoolWorkerThreadAdjustmentAdjustment")]
    [InlineData(59, "ThreadPoolMinMaxThreadsChanged")]
    [InlineData(60, "ThreadPoolWorkingThreadCount")]
    public void GetCanonicalEventName_PrefersRuntimeEventId_WhenWindowsReportsTaskGuid(
        int eventId,
        string expected)
    {
        var name = EventPipeThreadPoolCollector.GetCanonicalEventName(
            eventId,
            "Task(8a9a44ab-f681-4271-8810-830dab9f5621)");

        name.Should().Be(expected);
    }

    [Theory]
    [InlineData("0", "Warmup")]
    [InlineData("6", "Starvation")]
    [InlineData("8", "CooperativeBlocking")]
    [InlineData("Starvation", "Starvation")]
    [InlineData("99", "99")]
    public void NormalizeAdjustmentReason_MapsNumericRuntimeEnumValues(
        string reason,
        string expected)
    {
        EventPipeThreadPoolCollector.NormalizeAdjustmentReason(reason)
            .Should().Be(expected);
    }

    [Fact]
    public void NormalizeHillClimbing_PreservesUnknownReason_AndAttributesNeighborCounts()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var samples = new List<ThreadPoolHillClimbingSample>
        {
            new(
                timestamp,
                "99",
                null,
                null,
                null,
                ThreadPoolEvidence.RuntimeUnrecognized,
                ThreadPoolEvidence.Missing,
                ThreadPoolEvidence.Missing),
        };
        var workers = new List<EventPipeThreadPoolCollector.CountSample>
        {
            new(timestamp.AddMilliseconds(-10), 4, ThreadPoolEvidence.RuntimeObserved),
            new(timestamp.AddMilliseconds(10), 5, ThreadPoolEvidence.RuntimeObserved),
        };
        var notes = new ConcurrentDictionary<string, byte>();

        var normalized = EventPipeThreadPoolCollector.NormalizeHillClimbing(samples, workers, notes);

        normalized.Should().ContainSingle();
        normalized[0].Reason.Should().Be("99");
        normalized[0].ReasonProvenance.Should().Be(ThreadPoolEvidence.RuntimeUnrecognized);
        normalized[0].OldCount.Should().Be(4);
        normalized[0].NewCount.Should().Be(5);
        normalized[0].OldCountProvenance.Should().Be(ThreadPoolEvidence.InferredFromNeighbor);
        normalized[0].NewCountProvenance.Should().Be(ThreadPoolEvidence.InferredFromNeighbor);
        ThreadPoolEvidence.IsConfirmedReason(normalized[0], "Starvation").Should().BeFalse();
    }

    [Fact]
    public void EvidenceSummary_ConfirmsOnlyRuntimeObservedCausalReasons()
    {
        var timestamp = DateTimeOffset.UtcNow;
        ThreadPoolHillClimbingSample[] samples =
        [
            new(timestamp, "Warmup", 1, 2, null, ThreadPoolEvidence.RuntimeObserved),
            new(timestamp, "Starvation", 2, 3, null, ThreadPoolEvidence.RuntimeObserved),
            new(timestamp, "Starvation", 3, 4, null, ThreadPoolEvidence.RuntimeUnrecognized),
            new(timestamp, "CooperativeBlocking", 4, 5, null, null),
        ];

        var summary = ThreadPoolEvidence.Summarize(samples);

        summary.ConfirmedStarvationAdjustments.Should().Be(1);
        summary.ConfirmedCooperativeBlockingAdjustments.Should().Be(0);
        summary.HasCompleteRuntimeReasonEvidence.Should().BeFalse();
    }

    [Fact]
    public void EvidenceSummary_EmptyWindow_IsInconclusive()
    {
        var summary = ThreadPoolEvidence.Summarize([]);

        summary.HillClimbingEvents.Should().Be(0);
        summary.ConfirmedStarvationAdjustments.Should().Be(0);
        summary.ConfirmedCooperativeBlockingAdjustments.Should().Be(0);
        summary.HasCompleteRuntimeReasonEvidence.Should().BeFalse();
    }

    [Fact]
    public void LegacySnapshotJson_DeserializesWithoutInventingProvenance()
    {
        var snapshot = JsonSerializer.Deserialize<ThreadPoolEventSnapshot>(
            """
            {
              "ProcessId": 42,
              "StartedAt": "2026-01-01T00:00:00Z",
              "Duration": "00:00:05",
              "WorkerThreadTimeline": [{ "Timestamp": "2026-01-01T00:00:00Z", "Count": 4 }],
              "IocpThreadTimeline": [],
              "HillClimbing": [{
                "Timestamp": "2026-01-01T00:00:01Z",
                "Reason": "Starvation",
                "OldCount": 4,
                "NewCount": 5,
                "Throughput": 10
              }],
              "WorkItemOrigins": [],
              "EffectiveSettings": null,
              "TotalEnqueueEvents": 1,
              "TotalDequeueEvents": 0,
              "Notes": []
            }
            """);

        snapshot.Should().NotBeNull();
        snapshot!.Evidence.Should().BeNull();
        snapshot.Quality.Should().BeNull();
        snapshot.WorkerThreadTimeline[0].CountProvenance.Should().BeNull();
        snapshot.HillClimbing[0].ReasonProvenance.Should().BeNull();
        ThreadPoolEvidence.GetSummary(snapshot).Should().BeNull();
        ThreadPoolEvidence.GetQuality(snapshot).Limitations.Should().ContainSingle(
            limitation => limitation.Category == EvidenceLimitationCategory.LegacyUnknown);
    }

    [Fact]
    public void BuildQuality_DistinguishesLossEvictionInferenceAndUnavailableMechanisms()
    {
        var quality = ThreadPoolEvidence.BuildQuality(
            new ThreadPoolEvidenceSummary(5, 1, 0, true),
            detectedTransportLossEvents: 7,
            processingFailures: 1,
            workerTimelineEvictions: 2,
            iocpTimelineEvictions: 3,
            hillClimbingEvictions: 4,
            inferredValues: 6,
            effectiveSettingsAvailable: false,
            workItemOriginsObservable: false);

        quality.Limitations.Should().Contain(l => l.Category == EvidenceLimitationCategory.DetectedTransportLoss && l.AffectedCount == 7);
        quality.Limitations.Should().Contain(l => l.Category == EvidenceLimitationCategory.ProcessingFailure && l.AffectedCount == 1);
        quality.Limitations.Should().Contain(l => l.Category == EvidenceLimitationCategory.CollectorEviction && l.Scope == "hill-climbing" && l.AffectedCount == 4);
        quality.Limitations.Should().Contain(l => l.Category == EvidenceLimitationCategory.Inference && l.AffectedCount == 6);
        quality.Limitations.Should().Contain(l => l.Category == EvidenceLimitationCategory.MechanismUnavailable);
        quality.Limitations.Should().Contain(l => l.Category == EvidenceLimitationCategory.MechanismUnobservable);
        quality.Conclusions.RetainedExplicitPositiveEvidence.Should().Be(EvidenceConclusionSupport.Supported);
        quality.Conclusions.AbsenceOrExhaustiveCounts.Should().Be(EvidenceConclusionSupport.Inconclusive);
        quality.Conclusions.RegressionOrHealthyControl.Should().Be(EvidenceConclusionSupport.Inconclusive);
    }

    [Fact]
    public void BuildQuality_NoEventWindow_DoesNotEstablishHealthyControl()
    {
        var quality = ThreadPoolEvidence.BuildQuality(
            new ThreadPoolEvidenceSummary(0, 0, 0, false),
            detectedTransportLossEvents: 0,
            processingFailures: 0,
            workerTimelineEvictions: 0,
            iocpTimelineEvictions: 0,
            hillClimbingEvictions: 0,
            inferredValues: 0,
            effectiveSettingsAvailable: true,
            workItemOriginsObservable: true);

        quality.Limitations.Should().Contain(l => l.Category == EvidenceLimitationCategory.CaptureWindow);
        quality.Limitations.Should().NotContain(l => l.Category == EvidenceLimitationCategory.Startup);
        quality.Conclusions.RetainedExplicitPositiveEvidence.Should().Be(EvidenceConclusionSupport.NotEstablished);
        quality.Conclusions.AbsenceOrExhaustiveCounts.Should().Be(EvidenceConclusionSupport.Inconclusive);
    }

    [Fact]
    public void BuildQuality_UnavailableLossCount_PreservesPositiveEvidenceButNotCompleteness()
    {
        var quality = ThreadPoolEvidence.BuildQuality(
            new ThreadPoolEvidenceSummary(5, 1, 0, true),
            detectedTransportLossEvents: null,
            processingFailures: 0,
            workerTimelineEvictions: 0,
            iocpTimelineEvictions: 0,
            hillClimbingEvictions: 0,
            inferredValues: 0,
            effectiveSettingsAvailable: true,
            workItemOriginsObservable: true);

        quality.Limitations.Should().Contain(l =>
            l.Category == EvidenceLimitationCategory.MechanismUnobservable
            && l.Scope == "eventpipe-loss"
            && l.AffectedCount == null);
        quality.Limitations.Should().NotContain(l => l.Category == EvidenceLimitationCategory.DetectedTransportLoss);
        quality.Conclusions.RetainedExplicitPositiveEvidence.Should().Be(EvidenceConclusionSupport.Supported);
        quality.Conclusions.AbsenceOrExhaustiveCounts.Should().Be(EvidenceConclusionSupport.Inconclusive);
        quality.Conclusions.RegressionOrHealthyControl.Should().Be(EvidenceConclusionSupport.Inconclusive);
    }

    [Fact]
    public void WithProjection_AddsBoundedProjectionWithoutChangingCollectorConclusions()
    {
        var source = ThreadPoolEvidence.BuildQuality(
            new ThreadPoolEvidenceSummary(2, 1, 0, true),
            0, 0, 0, 0, 0, 0, true, true);

        var projected = ThreadPoolEvidence.WithProjection(source, "query:hill-climbing", 9, "top-N");
        projected = ThreadPoolEvidence.WithProjection(projected, "query:hill-climbing", 4, "replacement");

        projected.Limitations.Should().ContainSingle(l =>
            l.Category == EvidenceLimitationCategory.OutputProjection
            && l.Scope == "query:hill-climbing"
            && l.AffectedCount == 4);
        projected.Conclusions.Should().Be(source.Conclusions);
    }
}
