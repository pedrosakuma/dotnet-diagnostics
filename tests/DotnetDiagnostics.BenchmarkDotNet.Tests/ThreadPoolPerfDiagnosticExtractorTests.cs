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
                  { "Count": 12 },
                  { "Count": 14 }
                ],
                "HillClimbing": [
                  { "Reason": "Initializing", "OldCount": 11, "NewCount": 12 },
                  { "Reason": "Starvation", "OldCount": 12, "NewCount": 14 }
                ],
                "TotalEnqueueEvents": 24
              }
            }
            """);

        evidence.HasCausalWait.Should().BeTrue();
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
                  { "Count": 12 },
                  { "Count": 19 }
                ],
                "HillClimbing": [
                  { "Reason": "CooperativeBlocking", "OldCount": 12, "NewCount": 19 }
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
                "TotalEnqueueEvents": 0
              }
            }
            """);

        evidence.HasCausalWait.Should().BeFalse();
        evidence.Signals.Should().Contain(signal =>
            signal.Name == "threadpool.starvationAdjustments" && signal.Value == 0);
    }

    [Fact]
    public void Extract_UnrelatedHillClimbing_DoesNotMatchWaitingAttribution()
    {
        var evidence = ThreadPoolPerfDiagnosticExtractor.Extract(
            """
            {
              "Data": {
                "WorkerThreadTimeline": [
                  { "Count": 4 },
                  { "Count": 5 }
                ],
                "HillClimbing": [
                  { "Reason": "ClimbingMove", "OldCount": 4, "NewCount": 5 }
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
    }
}
