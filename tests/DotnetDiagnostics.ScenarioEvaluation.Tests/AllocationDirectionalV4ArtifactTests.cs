using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class AllocationDirectionalV4ArtifactTests
{
    private static readonly string ArtifactDirectory = RepositoryPath(
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional");

    [Fact]
    public void FinalArtifacts_PreserveCompleteOneShotInconclusiveExecution()
    {
        var fixturePath = Path.Combine(
            ArtifactDirectory,
            "allocation-directional-baseline-current-v4.windows-x64.evidence.json");
        var ledgerPath = Path.Combine(
            ArtifactDirectory,
            "allocation-directional-baseline-current-v4.windows-x64.evidence.ledger.jsonl");
        var reportPath = Path.Combine(
            ArtifactDirectory,
            "allocation-directional-baseline-current-v4.windows-x64.evidence.report.json");
        var fixture = AllocationDirectionalJson.ReadFixture(fixturePath);
        var observations = fixture.Pairs.SelectMany(pair =>
            new[] { pair.Baseline, pair.Current }).ToArray();
        var live = observations.Where(observation =>
            observation.Origin == AllocationDirectionalOrigin.LiveMcp).ToArray();

        fixture.Pairs.Should().HaveCount(57);
        live.Should().HaveCount(108);
        live.Should().OnlyContain(observation =>
            observation.Outcome == AllocationDirectionalAttemptOutcome.Passed);
        live.Max(observation => observation.SampledAllocationTicks)
            .Should().Be(5_539);
        live.Select(observation => observation.AttemptId)
            .Should().OnlyHaveUniqueItems();
        live.Select(observation => observation.ProcessIdentity)
            .Should().OnlyHaveUniqueItems();
        live.Select(observation => observation.CaptureId)
            .Should().OnlyHaveUniqueItems();
        File.ReadLines(ledgerPath).Should().HaveCount(216);

        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        report.RootElement.GetProperty("decision").GetString()
            .Should().Be("inconclusive");
        report.RootElement.GetProperty("numericGatePassed").GetBoolean()
            .Should().BeFalse();
        report.RootElement.GetProperty("blindedEvaluationRequired").GetBoolean()
            .Should().BeFalse();
        report.RootElement.GetProperty("blindedEvaluationRan").GetBoolean()
            .Should().BeFalse();
        var scenarios = report.RootElement.GetProperty("scenarios")
            .EnumerateArray().ToArray();
        scenarios.Single(value =>
                value.GetProperty("scenario").GetString()
                    == "throughputIncreaseStablePerOperation")
            .GetProperty("unsafeDirectionalEvidenceObserved").GetBoolean()
            .Should().BeTrue();
        scenarios.Single(value =>
                value.GetProperty("scenario").GetString()
                    == "lowerAllocationStableRetained")
            .GetProperty("assessable").GetBoolean().Should().BeFalse();
        scenarios.Single(value =>
                value.GetProperty("scenario").GetString()
                    == "fixedStrideBias")
            .GetProperty("assessable").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void FinalArtifacts_HavePortableFingerprints()
    {
        AssertFingerprint(
            AllocationDirectionalV4Protocol.FileName,
            45_377,
            "9147ed54248aa479330d0f4669f64b4777dd711b53cfdaccd4bcfa394e094e24");
        AssertFingerprint(
            "allocation-directional-baseline-current-v4.windows-x64.evidence.json",
            496_905,
            "29d7e7b50fe2ba4a4d00475e24366fe73465d6502458e81ac78a512c4840a7da");
        AssertFingerprint(
            "allocation-directional-baseline-current-v4.windows-x64.evidence.ledger.jsonl",
            73_277,
            "57d8016c5fd845f02793b1a1fb5f21a20f20415a72a07724c639bde136034668");
        AssertFingerprint(
            "allocation-directional-baseline-current-v4.windows-x64.evidence.report.json",
            5_520,
            "98b6c9d22fb6843708456247261a95406fbf31900097278434759e3c8db53faa");
    }

    private static void AssertFingerprint(
        string fileName,
        int expectedBytes,
        string expectedSha256)
    {
        var path = Path.Combine(ArtifactDirectory, fileName);
        AllocationDirectionalProtocol.CanonicalByteCount(path).Should()
            .Be(expectedBytes);
        AllocationDirectionalProtocol.Sha256(path).Should().Be(expectedSha256);
    }

    private static string RepositoryPath(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "DotnetDiagnostics.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName
                ?? throw new DirectoryNotFoundException(
                    "Could not locate the repository root."),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
