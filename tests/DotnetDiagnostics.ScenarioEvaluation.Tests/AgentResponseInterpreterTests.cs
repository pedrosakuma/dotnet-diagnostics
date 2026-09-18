using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class AgentResponseInterpreterTests
{
    private readonly AgentResponseInterpreter interpreter = new();

    [Fact]
    public void AgentResponse_CultureLookup_MapsInclusiveWorkloadOwnershipEvidence()
    {
        var result = interpreter.Interpret(
            "culture-lookup",
            "This likely shows inclusive workload ownership: RunCultureSensitive has culture ownership, while RunOrdinal has ordinal ownership in its separate phase. Inspect caller callee paths.");

        result.Interpretation.EvidenceIds.Should().Contain(["culture-owned-cpu", "ordinal-owned-cpu"]);
        result.EvidenceCitations.Should().Contain(citation =>
            citation.EvidencePath == "metrics[name=culture.owned-inclusive-samples]"
            && citation.SupportedEvidenceIds.Contains("culture-owned-cpu", StringComparer.Ordinal));
        result.Interpretation.AttributionIds.Should().Contain("BadCodeSample.CultureLookupWorkload.RunCultureSensitive");
        result.Uncertainty.Disposition.Should().Be(AgentResponseUncertaintyDisposition.Hedged);
    }

    [Fact]
    public void AgentResponse_CulturePrivateNames_DoNotEstablishTheOwnedWorkloadContract()
    {
        var result = interpreter.Interpret("culture-lookup",
            "CompareInfo.IcuGetHashCodeOfString has 50.9% self time; the native hash leaf is expensive.");

        result.Interpretation.EvidenceIds.Should().NotContain("culture-owned-cpu")
            .And.NotContain("ordinal-owned-cpu");
        result.Interpretation.AttributionIds.Should().BeEmpty();
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
            citation.EvidencePath == "metrics[name=loh-size-max]"
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
