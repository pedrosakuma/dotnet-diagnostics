using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class AgentResponseInterpreterTests
{
    private readonly AgentResponseInterpreter interpreter = new();

    [Fact]
    public void AgentResponse_CultureLookup_MapsGlobalizationHashEvidence()
    {
        var result = interpreter.Interpret(
            "culture-lookup",
            "This likely points to a culture-aware hash hotspot: CompareInfo.IcuGetHashCodeOfString owns about 50.9% self time, so InvariantCultureIgnoreCase is the expensive path.");

        result.Interpretation.EvidenceIds.Should().Contain(["cpu-self-time-signal", "globalization-hash-leaf"]);
        result.EvidenceCitations.Should().Contain(citation =>
            citation.EvidencePath.Contains("cpu.self-time.concentration", StringComparison.Ordinal)
            && citation.SupportedEvidenceIds.Contains("globalization-hash-leaf", StringComparer.Ordinal));
        result.Uncertainty.Disposition.Should().Be(AgentResponseUncertaintyDisposition.Hedged);
    }

    [Fact]
    public void AgentResponse_SyncOverAsync_MapsQueueAndBlockingEvidence()
    {
        var result = interpreter.Interpret(
            "sync-over-async",
            "This is sync-over-async: CPU remains low, the ThreadPool queue keeps growing, and many blocked workers sit in SpinThenBlockingWait / GetAwaiter().GetResult.");

        result.Interpretation.EvidenceIds.Should().Contain(["cpu-remains-low", "threadpool-backlog", "blocking-wait-frames"]);
        result.EvidenceCitations.Should().Contain(citation =>
            citation.EvidencePath == "metrics[name=threadpool-queue-length]"
            && citation.SupportedEvidenceIds.Contains("threadpool-backlog", StringComparer.Ordinal));
        result.EvidenceCitations.Should().Contain(citation =>
            citation.EvidencePath.StartsWith("frames[displayName=System.Threading.Tasks.Task.SpinThenBlockingWait", StringComparison.Ordinal));
    }

    [Fact]
    public void AgentResponse_SyncOverAsync_DoesNotMapNegatedCpuBoundToForbiddenHypothesis()
    {
        var result = interpreter.Interpret(
            "sync-over-async",
            "This likely is sync-over-async: the service is not CPU bound, the ThreadPool queue keeps growing, and GetAwaiter().GetResult is blocking workers.");

        result.Interpretation.HypothesisIds.Should().Contain("threadpool-starvation-from-sync-over-async");
        result.Interpretation.HypothesisIds.Should().NotContain("cpu-compute-demand");
        result.Interpretation.ConclusionIds.Should().NotContain("cpu-compute-demand");
    }

    [Fact]
    public void AgentResponse_LockStorm_MapsContendedMonitorAndSleepingOwnerEvidence()
    {
        var result = interpreter.Interpret(
            "lock-storm",
            "The owner thread is sleeping while holding the monitor. You can see contended monitor waiters piling up behind the same owner, with Thread.Sleep on the owner side.");

        result.Interpretation.EvidenceIds.Should().Contain(["monitor-wait-concentration", "owner-overlap-signal", "sleeping-owner-with-waiters"]);
        result.EvidenceCitations.Should().Contain(citation =>
            citation.EvidencePath == "relations[relation=thread-owner-overlap,ownerWaitReason=Thread.Sleep]"
            && citation.SupportedEvidenceIds.Contains("sleeping-owner-with-waiters", StringComparer.Ordinal));
    }

    [Fact]
    public void AgentResponse_GcStorm_MapsGen2AndLohEvidence()
    {
        var result = interpreter.Interpret(
            "gc-storm",
            "Tail latency appears to come from LOH churn: the large object heap is elevated, gen2 collections are frequent, and the gen2 share suggests sustained GC pause pressure.");

        result.Interpretation.EvidenceIds.Should().Contain(["gen2-counter-elevated", "loh-size-elevated", "gen2-share-signal"]);
        result.EvidenceCitations.Should().Contain(citation =>
            citation.EvidencePath == "metrics[name=loh-size]"
            && citation.SupportedEvidenceIds.Contains("loh-size-elevated", StringComparer.Ordinal));
        result.Uncertainty.Disposition.Should().Be(AgentResponseUncertaintyDisposition.Hedged);
    }

    [Theory]
    [InlineData("culture-lookup", "This likely points to CompareInfo.", AgentResponseUncertaintyDisposition.Hedged)]
    [InlineData("sync-over-async", "This is sync-over-async because GetAwaiter().GetResult blocks ThreadPool workers.", AgentResponseUncertaintyDisposition.Assertive)]
    [InlineData("lock-storm", "This likely is lock contention, but the owner thread is clearly sleeping while holding the monitor.", AgentResponseUncertaintyDisposition.Mixed)]
    public void AgentResponse_UncertaintyClassification_IsDetected(
        string scenarioId,
        string response,
        AgentResponseUncertaintyDisposition expected)
    {
        var result = interpreter.Interpret(scenarioId, response);

        result.Uncertainty.Disposition.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AgentResponse_EmptyOrWhitespaceResponse_DegradesGracefully(string response)
    {
        var result = interpreter.Interpret("sync-over-async", response);

        result.EvidenceCitations.Should().BeEmpty();
        result.Interpretation.EvidenceIds.Should().BeEmpty();
        result.Interpretation.HypothesisIds.Should().BeEmpty();
        result.Interpretation.AttributionIds.Should().BeEmpty();
        result.Interpretation.NextActionIds.Should().BeEmpty();
        result.Interpretation.CausalityPosture.Should().Be("unmapped");
        result.Uncertainty.Disposition.Should().Be(AgentResponseUncertaintyDisposition.NoneDetected);
    }
}
