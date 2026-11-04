using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class AllocationThroughputNormalizationArtifactTests
{
    private static readonly string ArtifactDirectory = RepositoryPath(
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional");

    [Fact]
    public void FrozenProtocol_PreservesPortableIdentity()
    {
        var protocolPath = Path.Combine(
            ArtifactDirectory,
            AllocationThroughputNormalizationProtocol.FileName);
        using var protocol = JsonDocument.Parse(File.ReadAllText(protocolPath));

        protocol.RootElement.GetProperty("status").GetString()
            .Should().Be("frozen");
        protocol.RootElement.GetProperty("protocolId").GetString()
            .Should().Be(
                AllocationThroughputNormalizationProtocol.ProtocolId);
        protocol.RootElement.GetProperty("protocolVersion").GetString()
            .Should().Be(
                AllocationThroughputNormalizationProtocol.ProtocolVersion);
        protocol.RootElement.GetProperty("sourceBaseCommit").GetString()
            .Should().Be(
            AllocationThroughputNormalizationProtocol.SourceBaseCommit);

        var frozenInputs = protocol.RootElement.GetProperty("frozenInputs");
        AssertCanonicalHashes(
            frozenInputs.GetProperty("sourceFilesSha256"),
            AllocationThroughputNormalizationProtocol.FrozenSourcePaths);
        AssertCanonicalHashes(
            frozenInputs.GetProperty("preservedArtifactsSha256"),
            AllocationThroughputNormalizationProtocol.PreservedArtifactPaths);
    }

    [Fact]
    public void FinalArtifacts_PreserveCompleteOneShotInconclusiveExecution()
    {
        var fixturePath = Path.Combine(
            ArtifactDirectory,
            "allocation-throughput-normalization-falsifier-v1.windows-x64.evidence.json");
        var ledgerPath = Path.Combine(
            ArtifactDirectory,
            "allocation-throughput-normalization-falsifier-v1.windows-x64.evidence.ledger.jsonl");
        var reportPath = Path.Combine(
            ArtifactDirectory,
            "allocation-throughput-normalization-falsifier-v1.windows-x64.evidence.report.json");
        var fixture = AllocationDirectionalJson.ReadFixture(fixturePath);
        var observations = fixture.Pairs.SelectMany(pair =>
            new[] { pair.Baseline, pair.Current }).ToArray();

        fixture.Pairs.Should().HaveCount(15);
        observations.Should().HaveCount(30);
        observations.Should().OnlyContain(observation =>
            observation.Outcome == AllocationDirectionalAttemptOutcome.Passed);
        observations.Max(observation => observation.SampledAllocationTicks)
            .Should().Be(53_694);
        observations.Select(observation => observation.AttemptId)
            .Should().OnlyHaveUniqueItems();
        observations.Select(observation => observation.ProcessIdentity)
            .Should().OnlyHaveUniqueItems();
        observations.Select(observation => observation.CaptureId)
            .Should().OnlyHaveUniqueItems();
        File.ReadLines(ledgerPath).Should().HaveCount(60);

        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        report.RootElement.GetProperty("decision").GetString()
            .Should().Be("inconclusive");
        report.RootElement.GetProperty("blindedEvaluationRan").GetBoolean()
            .Should().BeFalse();
        report.RootElement.GetProperty("healthyUnchangedRateControl")
            .GetProperty("withinPracticalSanity").GetBoolean()
            .Should().BeTrue();
        var cohorts = report.RootElement.GetProperty("cohorts")
            .EnumerateArray().ToArray();
        cohorts.Should().HaveCount(2);
        var primary = cohorts.Single(value =>
            value.GetProperty("cohortId").GetString()
                == "primary-every-operation");
        primary.GetProperty("assessable").GetBoolean().Should().BeFalse();
        primary.GetProperty("aaEnvelope").GetDecimal()
            .Should().BeGreaterThan(0.10m);
        primary.GetProperty("throughputMultipliers").GetProperty("median")
            .GetDecimal().Should().BeGreaterThan(1.25m);
        var low = cohorts.Single(value =>
            value.GetProperty("cohortId").GetString()
                == "low-every-eight-operations");
        low.GetProperty("assessable").GetBoolean().Should().BeTrue();
        low.GetProperty("workloadManipulationEstablished").GetBoolean()
            .Should().BeTrue();
        low.GetProperty("medianExceedsEquivalenceMargin").GetBoolean()
            .Should().BeTrue();
        low.GetProperty("classification").GetString()
            .Should().Be("normalization-failed");
    }

    [Fact]
    public void FinalArtifacts_HaveFrozenPortableFingerprints()
    {
        AssertFingerprint(
            AllocationThroughputNormalizationProtocol.FileName,
            33_151,
            "899c834399cd26b1068a867d2f48be569fdf586f21bb900eb75d8d6571630df5");
        AssertFingerprint(
            "allocation-throughput-normalization-falsifier-v1.windows-x64.evidence.json",
            129_880,
            "601d3c5513b5929000546d220d6d15d6677ea4c02a5984f72ba661bb37fc91cb");
        AssertFingerprint(
            "allocation-throughput-normalization-falsifier-v1.windows-x64.evidence.ledger.jsonl",
            20_576,
            "578f61cb20fce63634852a0ea6a1a48edc2640d1dfdfd5b4d641a6f587818164");
        AssertFingerprint(
            "allocation-throughput-normalization-falsifier-v1.windows-x64.evidence.report.json",
            5_301,
            "131f3ceaf101af5cebaf969c2c9a8cc0df998688b5c3d5ab8eb16ec57e52ae77");
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

    private static void AssertCanonicalHashes(
        JsonElement element,
        IReadOnlyList<string> paths)
    {
        var expectedHashes = element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.GetString(),
            StringComparer.Ordinal);
        expectedHashes.Keys.Should().BeEquivalentTo(paths);
        foreach (var path in paths)
        {
            expectedHashes[path].Should().Be(
                AllocationDirectionalProtocol.Sha256(RepositoryPath(path)));
        }
    }

    private static string RepositoryPath(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(
                   directory.FullName,
                   "DotnetDiagnostics.slnx")))
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
