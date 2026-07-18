using FluentAssertions;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class ScenarioFailureClassifierTests
{
    [Fact]
    public void Classify_SeparatesTheFourFailureStages()
    {
        ScenarioFailureClassifier.Classify(
            new HttpRequestException("load failed"),
            ScenarioFailureKind.Workload).Should().Be(ScenarioFailureKind.Workload);
        ScenarioFailureClassifier.Classify(
            new DiagnosticsClientException("collection failed"),
            ScenarioFailureKind.Collection).Should().Be(ScenarioFailureKind.Collection);
        ScenarioFailureClassifier.Classify(
            new UnauthorizedAccessException("attach denied"),
            ScenarioFailureKind.Collection).Should().Be(ScenarioFailureKind.Environment);
        ScenarioFailureClassifier.Classify(
            new InvalidDataException("bad replay"),
            ScenarioFailureKind.Evaluation).Should().Be(ScenarioFailureKind.Evaluation);
    }
}
