using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Triage;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliTriageOutputTests
{
    [Fact]
    public void SignalSeparation_LatencyOnly_DistinguishesRemovedStarvationSignal()
    {
        var triage = CreateTriage(queueLength: 0, includeQueueSignal: false);

        var note = CliCommands.BuildCliTriageSignalSeparationNote(triage);

        note.Should().Contain("starvation signal is absent");
        note.Should().Contain("expected only if it matches the workload or SLO");
    }

    [Fact]
    public void SignalSeparation_BacklogStillElevated_DoesNotClaimStarvationSignalWasRemoved()
    {
        var triage = CreateTriage(queueLength: 100, includeQueueSignal: true);

        CliCommands.BuildCliTriageSignalSeparationNote(triage).Should().BeNull();
    }

    private static TriageResult CreateTriage(double queueLength, bool includeQueueSignal)
    {
        var signals = new List<TriageObservedSignal>
        {
            new(
                "http.request-duration-p95",
                "critical",
                "HTTP request duration p95 was 3000 ms.",
                [new TriageEvidenceItem("request-duration-p95", 3000, "ms", ">=", 2000, "threshold")]),
        };
        if (includeQueueSignal)
        {
            signals.Add(new TriageObservedSignal(
                "threadpool.queue",
                "high",
                "The ThreadPool queue contained 100 work items.",
                [new TriageEvidenceItem("threadpool-queue-length", queueLength, "items", ">=", 50, "threshold")]));
        }

        return new TriageResult(
            TriageClassifier.Inconclusive,
            TriageSeverity.Degraded,
            new TriageEvidence(
                CpuUsage: 3,
                TimeInGc: 0,
                ThreadPoolQueueLength: queueLength,
                MonitorLockContentionCount: 0,
                AllocRate: 0,
                Gen2GcCount: 0,
                GcHeapSize: 10_000,
                ExceptionCount: 0,
                RequestDurationP95: 3),
            TopIndicators: [])
        {
            ModelVersion = 2,
            Assessment = TriageClassifier.InconclusiveAssessment,
            ObservedSignals = signals,
            Hypotheses = [],
        };
    }
}
