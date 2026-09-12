using DotnetDiagnostics.BenchmarkDotNet.Regression;
using FluentAssertions;

namespace DotnetDiagnostics.BenchmarkDotNet.Tests;

public sealed class ThreadPoolPerfDiagnosticExtractorTests
{
    [Fact]
    public void Extract_StarvationAdjustment_IsCausalWaitEvidence()
    {
        var evidence = ThreadPoolPerfDiagnosticExtractor.Extract(
            """
            {
              "Summary": "generic summary text is intentionally ignored",
              "Data": {
                "WorkerThreadTimeline": [
                  { "Count": 12, "CountProvenance": "runtime-observed" },
                  { "Count": 14, "CountProvenance": "runtime-observed" }
                ],
                "HillClimbing": [
                  { "Reason": "Initializing", "ReasonProvenance": "runtime-observed", "OldCount": 11, "NewCount": 12 },
                  {
                    "Reason": "Starvation",
                    "ReasonProvenance": "runtime-observed",
                    "OldCount": 12,
                    "OldCountProvenance": "runtime-observed",
                    "NewCount": 14,
                    "NewCountProvenance": "runtime-observed"
                  }
                ],
                "TotalEnqueueEvents": 24
              }
            }
            """);

        evidence.HasCausalWait.Should().BeTrue();
        evidence.HasConclusiveCausalAssessment.Should().BeTrue();
        evidence.Signals.Should().Contain(signal =>
            signal.Name == "threadpool.starvationAdjustments" && signal.Value == 1);
        evidence.Signals.Should().Contain(signal =>
            signal.Name == "threadpool.starvationWorkerIncrease" && signal.Value == 2);
    }

    [Fact]
    public void Extract_CooperativeBlockingAdjustment_IsCausalWaitEvidence()
    {
        var evidence = ThreadPoolPerfDiagnosticExtractor.Extract(
            """
            {
              "Data": {
                "WorkerThreadTimeline": [
                  { "Count": 12, "CountProvenance": "runtime-observed" },
                  { "Count": 19, "CountProvenance": "carried-forward" }
                ],
                "HillClimbing": [
                  {
                    "Reason": "CooperativeBlocking",
                    "ReasonProvenance": "runtime-observed",
                    "OldCount": 12,
                    "OldCountProvenance": "runtime-observed",
                    "NewCount": 19,
                    "NewCountProvenance": "runtime-observed"
                  }
                ],
                "TotalEnqueueEvents": 0
              }
            }
            """);

        evidence.HasCausalWait.Should().BeTrue();
        evidence.Signals.Should().Contain(signal =>
            signal.Name == "threadpool.cooperativeBlockingAdjustments" && signal.Value == 1);
        evidence.Signals.Should().Contain(signal =>
            signal.Name == "threadpool.cooperativeBlockingWorkerIncrease" && signal.Value == 7);
    }

    [Fact]
    public void Extract_AbsentThreadPoolEvents_IsExplicitlyUnmatched()
    {
        var evidence = ThreadPoolPerfDiagnosticExtractor.Extract(
            """
            {
              "Summary": "starvation appeared only in generic text",
              "Data": {
                "WorkerThreadTimeline": [],
                "HillClimbing": [],
                "Evidence": {
                  "HillClimbingEvents": 0,
                  "ConfirmedStarvationAdjustments": 0,
                  "ConfirmedCooperativeBlockingAdjustments": 0,
                  "HasCompleteRuntimeReasonEvidence": true
                },
                "TotalEnqueueEvents": 0
              }
            }
            """);

        evidence.HasCausalWait.Should().BeFalse();
        evidence.HasConclusiveCausalAssessment.Should().BeFalse();
        evidence.Signals.Should().Contain(signal =>
            signal.Name == "threadpool.starvationAdjustments" && signal.Value == 0);
        evidence.Signals.Should().NotContain(signal =>
            signal.Name == "threadpool.starvationWorkerIncrease"
                || signal.Name == "threadpool.cooperativeBlockingWorkerIncrease");
    }

    [Fact]
    public void Extract_UnrelatedHillClimbing_DoesNotMatchWaitingAttribution()
    {
        var evidence = ThreadPoolPerfDiagnosticExtractor.Extract(
            """
            {
              "Data": {
                "WorkerThreadTimeline": [
                  { "Count": 4, "CountProvenance": "runtime-observed" },
                  { "Count": 5, "CountProvenance": "runtime-observed" }
                ],
                "HillClimbing": [
                  { "Reason": "ClimbingMove", "ReasonProvenance": "runtime-observed", "OldCount": 4, "NewCount": 5 }
                ],
                "TotalEnqueueEvents": 8
              }
            }
            """);

        evidence.HasCausalWait.Should().BeFalse();
        evidence.Signals.Should().Contain(signal =>
            signal.Name == "threadpool.hillClimbingEvents" && signal.Value == 1);
        evidence.Signals.Should().Contain(signal =>
            signal.Name == "threadpool.starvationAdjustments" && signal.Value == 0);
        evidence.Signals.Should().Contain(signal =>
            signal.Name == "threadpool.workerGrowth" && signal.BetterDirection == PerfSignalDirection.Neutral);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("runtime-unrecognized")]
    [InlineData("missing")]
    public void Extract_LegacyOrUnknownStarvationReason_IsInconclusive(string? provenance)
    {
        var provenanceProperty = provenance is null
            ? string.Empty
            : $""", "ReasonProvenance": "{provenance}" """;
        var evidence = ThreadPoolPerfDiagnosticExtractor.Extract(
            $$"""
            {
              "Data": {
                "WorkerThreadTimeline": [
                  { "Count": 4 }
                ],
                "HillClimbing": [
                  { "Reason": "Starvation" {{provenanceProperty}}, "OldCount": 4, "NewCount": 5 }
                ]
              }
            }
            """);

        evidence.HasCausalWait.Should().BeFalse();
        evidence.HasConclusiveCausalAssessment.Should().BeFalse();
        evidence.Signals.Should().NotContain(signal => signal.Name == "threadpool.starvationAdjustments");
        evidence.Signals.Should().NotContain(signal => signal.Name == "threadpool.workerPeak");
    }

    [Fact]
    public void Extract_PartiallyTrimmedSummary_DoesNotReportPartialWorkerIncreaseAsComplete()
    {
        var evidence = ThreadPoolPerfDiagnosticExtractor.Extract(
            """
            {
              "Data": {
                "HillClimbing": [{
                  "Reason": "Starvation",
                  "ReasonProvenance": "runtime-observed",
                  "OldCount": 4,
                  "OldCountProvenance": "runtime-observed",
                  "NewCount": 6,
                  "NewCountProvenance": "runtime-observed"
                }],
                "Evidence": {
                  "HillClimbingEvents": 3,
                  "ConfirmedStarvationAdjustments": 2,
                  "ConfirmedCooperativeBlockingAdjustments": 0,
                  "HasCompleteRuntimeReasonEvidence": true
                }
              }
            }
            """);

        evidence.HasCausalWait.Should().BeTrue();
        evidence.HasConclusiveCausalAssessment.Should().BeTrue();
        evidence.Signals.Should().Contain(signal =>
            signal.Name == "threadpool.starvationAdjustments" && signal.Value == 2);
        evidence.Signals.Should().NotContain(signal => signal.Name == "threadpool.starvationWorkerIncrease");
    }

    [Fact]
    public void Extract_SummaryTrimmedArtifact_UsesPersistedEvidenceWithoutInventingWorkerCounts()
    {
        var evidence = ThreadPoolPerfDiagnosticExtractor.Extract(
            """
            {
              "Data": {
                "WorkerThreadTimeline": [],
                "HillClimbing": [],
                "Evidence": {
                  "HillClimbingEvents": 3,
                  "ConfirmedStarvationAdjustments": 1,
                  "ConfirmedCooperativeBlockingAdjustments": 0,
                  "HasCompleteRuntimeReasonEvidence": true
                }
              }
            }
            """);

        evidence.HasCausalWait.Should().BeTrue();
        evidence.HasConclusiveCausalAssessment.Should().BeTrue();
        evidence.Signals.Should().Contain(signal =>
            signal.Name == "threadpool.starvationAdjustments" && signal.Value == 1);
        evidence.Signals.Should().NotContain(signal => signal.Name == "threadpool.starvationWorkerIncrease");
        evidence.Signals.Should().NotContain(signal => signal.Name == "threadpool.workerPeak");
    }

    [Theory]
    [InlineData(""" "NewCount": 6, "NewCountProvenance": "runtime-observed" """)]
    [InlineData(""" "OldCount": 4, "OldCountProvenance": "runtime-observed" """)]
    public void Extract_MissingWorkerCountOperand_DoesNotEmitMeasuredIncrease(string countProperties)
    {
        var evidence = ThreadPoolPerfDiagnosticExtractor.Extract(
            $$"""
            {
              "Data": {
                "HillClimbing": [{
                  "Reason": "Starvation",
                  "ReasonProvenance": "runtime-observed",
                  {{countProperties}}
                }]
              }
            }
            """);

        evidence.HasCausalWait.Should().BeTrue();
        evidence.Signals.Should().Contain(signal =>
            signal.Name == "threadpool.starvationAdjustments" && signal.Value == 1);
        evidence.Signals.Should().NotContain(signal => signal.Name == "threadpool.starvationWorkerIncrease");
    }

    [Theory]
    [InlineData("runtime-unrecognized")]
    [InlineData("carried-forward")]
    [InlineData("inferred-from-delta")]
    [InlineData("inferred-from-neighbor")]
    [InlineData("synthetic")]
    [InlineData("future-provenance")]
    public void Extract_NonRuntimeAdjustmentCountProvenance_DoesNotEmitMeasuredIncrease(string provenance)
    {
        var evidence = ThreadPoolPerfDiagnosticExtractor.Extract(
            $$"""
            {
              "Data": {
                "HillClimbing": [{
                  "Reason": "Starvation",
                  "ReasonProvenance": "runtime-observed",
                  "OldCount": 4,
                  "OldCountProvenance": "runtime-observed",
                  "NewCount": 6,
                  "NewCountProvenance": "{{provenance}}"
                }]
              }
            }
            """);

        evidence.HasCausalWait.Should().BeTrue();
        evidence.Signals.Should().NotContain(signal => signal.Name == "threadpool.starvationWorkerIncrease");
    }

    [Theory]
    [InlineData("runtime-unrecognized")]
    [InlineData("carried-forward")]
    [InlineData("inferred-from-delta")]
    [InlineData("inferred-from-neighbor")]
    [InlineData("synthetic")]
    [InlineData("future-provenance")]
    public void Extract_NonRuntimeTimelineCountProvenance_DoesNotEmitWorkerMeasurements(string provenance)
    {
        var evidence = ThreadPoolPerfDiagnosticExtractor.Extract(
            $$"""
            {
              "Data": {
                "WorkerThreadTimeline": [
                  { "Count": 4, "CountProvenance": "{{provenance}}" }
                ],
                "HillClimbing": []
              }
            }
            """);

        evidence.Signals.Should().NotContain(signal =>
            signal.Name == "threadpool.workerPeak" || signal.Name == "threadpool.workerGrowth");
    }

    [Fact]
    public void Extract_ExplicitStarvationAlongsideUnknownReason_PreservesCausalEvidenceButNotHealthyControl()
    {
        var evidence = ThreadPoolPerfDiagnosticExtractor.Extract(
            """
            {
              "Data": {
                "HillClimbing": [
                  { "Reason": "Starvation", "ReasonProvenance": "runtime-observed", "OldCount": 4, "NewCount": 5 },
                  { "Reason": "99", "ReasonProvenance": "runtime-unrecognized", "OldCount": 5, "NewCount": 6 }
                ]
              }
            }
            """);

        evidence.HasCausalWait.Should().BeTrue();
        evidence.HasConclusiveCausalAssessment.Should().BeFalse();
        evidence.Signals.Should().Contain(signal =>
            signal.Name == "threadpool.starvationAdjustments" && signal.Value == 1);
    }
}
