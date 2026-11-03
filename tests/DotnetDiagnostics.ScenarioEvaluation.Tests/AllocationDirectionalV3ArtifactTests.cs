using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class AllocationDirectionalV3ArtifactTests
{
    private static readonly string ArtifactDirectory = RepositoryPath(
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional");

    [Fact]
    public void FinalArtifacts_PreserveCompleteInconclusiveExecution()
    {
        var fixturePath = Path.Combine(
            ArtifactDirectory,
            "allocation-directional-baseline-current-v3.windows-x64.evidence.json");
        var ledgerPath = Path.Combine(
            ArtifactDirectory,
            "allocation-directional-baseline-current-v3.windows-x64.evidence.ledger.jsonl");
        var reportPath = Path.Combine(
            ArtifactDirectory,
            "allocation-directional-baseline-current-v3.windows-x64.evidence.report.json");
        var fixture = AllocationDirectionalJson.ReadFixture(fixturePath);
        var observations = fixture.Pairs.SelectMany(pair =>
            new[] { pair.Baseline, pair.Current }).ToArray();
        var live = observations.Where(observation =>
            observation.Origin == AllocationDirectionalOrigin.LiveMcp).ToArray();

        fixture.Pairs.Should().HaveCount(57);
        live.Should().HaveCount(108);
        live.Count(observation =>
                observation.Outcome == AllocationDirectionalAttemptOutcome.Passed)
            .Should().Be(72);
        live.Count(observation =>
                observation.Outcome == AllocationDirectionalAttemptOutcome.Failed)
            .Should().Be(36);
        live.Should().NotContain(observation =>
            observation.Outcome == AllocationDirectionalAttemptOutcome.Aborted);
        live.Where(observation =>
                observation.Outcome == AllocationDirectionalAttemptOutcome.Failed)
            .Should().OnlyContain(observation =>
                observation.AttemptEvidence != null
                && observation.AttemptEvidence.FailureCategory
                    == AllocationDirectionalFailureCategory.McpIsError
                && observation.AttemptEvidence.ErrorKind
                    == "ResourceLimitExceeded");
        live.Select(observation => observation.AttemptId)
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
    }

    [Fact]
    public void FinalArtifacts_HaveDocumentedPortableFingerprints()
    {
        AssertFingerprint(
            AllocationDirectionalV3Protocol.FileName,
            35_702,
            "5650195a237c3066832b2bec5f2dcb833b1dd8448e774a3c9c75a81f033a0070");
        AssertFingerprint(
            "allocation-directional-baseline-current-v3.windows-x64.evidence.json",
            464_964,
            "051cfc304bb4f7837505d8bc9bb106955fa75c2741bbbd8fabf82fb443577f81");
        AssertFingerprint(
            "allocation-directional-baseline-current-v3.windows-x64.evidence.ledger.jsonl",
            72_116,
            "5d8ff0be8bd0d902d041ec981b122ce5449152f1084dcb1cd7b32f499bb0d70b");
        AssertFingerprint(
            "allocation-directional-baseline-current-v3.windows-x64.evidence.report.json",
            5_113,
            "228d515322bbc7c9bd8a4f94d6223d92cc3bacbbcc70eb61c72c2ae748305ce1");
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
