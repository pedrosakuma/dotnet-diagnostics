using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class AllocationThroughputNormalizationV2ArtifactTests
{
    private static readonly string ArtifactDirectory = RepositoryPath(
        "tests/DotnetDiagnostics.ScenarioEvaluation.Tests/Fixtures/AllocationDirectional");

    [Fact]
    public void FrozenProtocol_PreservesPortableIdentity()
    {
        var protocolPath = Path.Combine(
            ArtifactDirectory,
            AllocationThroughputNormalizationV2Protocol.FileName);
        using var protocol = JsonDocument.Parse(File.ReadAllText(protocolPath));

        protocol.RootElement.GetProperty("status").GetString()
            .Should().Be("frozen");
        protocol.RootElement.GetProperty("protocolId").GetString()
            .Should().Be(
                AllocationThroughputNormalizationV2Protocol.ProtocolId);
        protocol.RootElement.GetProperty("protocolVersion").GetString()
            .Should().Be(
                AllocationThroughputNormalizationV2Protocol.ProtocolVersion);
        protocol.RootElement.GetProperty("sourceBaseCommit").GetString()
            .Should().Be(
            AllocationThroughputNormalizationV2Protocol.SourceBaseCommit);

        var frozenInputs = protocol.RootElement.GetProperty("frozenInputs");
        AssertCanonicalHashes(
            frozenInputs.GetProperty("sourceFilesSha256"),
            AllocationThroughputNormalizationV2Protocol.FrozenSourcePaths);
        AssertCanonicalHashes(
            frozenInputs.GetProperty("preservedArtifactsSha256"),
            AllocationThroughputNormalizationV2Protocol.PreservedArtifactPaths);
        FluentActions.Invoking(
                AllocationThroughputNormalizationV2Protocol.ValidateFrozen)
            .Should().NotThrow();
    }

    [Fact]
    public void FinalArtifacts_PreserveCompleteOneShotNoGoExecution()
    {
        var fixturePath = Path.Combine(
            ArtifactDirectory,
            "allocation-throughput-normalization-falsifier-v2.windows-x64.evidence.json");
        var ledgerPath = Path.Combine(
            ArtifactDirectory,
            "allocation-throughput-normalization-falsifier-v2.windows-x64.evidence.ledger.jsonl");
        var reportPath = Path.Combine(
            ArtifactDirectory,
            "allocation-throughput-normalization-falsifier-v2.windows-x64.evidence.report.json");
        var fixture = AllocationDirectionalJson.ReadFixture(fixturePath);
        var observations = fixture.Pairs.SelectMany(pair =>
            new[] { pair.Baseline, pair.Current }).ToArray();

        fixture.Pairs.Should().HaveCount(25);
        observations.Should().HaveCount(50);
        observations.Should().OnlyContain(observation =>
            observation.Outcome == AllocationDirectionalAttemptOutcome.Passed);
        observations.Max(observation => observation.SampledAllocationTicks)
            .Should().Be(53_109);
        observations.Select(observation => observation.AttemptId)
            .Should().OnlyHaveUniqueItems();
        observations.Select(observation => observation.ProcessIdentity)
            .Should().OnlyHaveUniqueItems();
        observations.Select(observation => observation.CaptureId)
            .Should().OnlyHaveUniqueItems();
        File.ReadLines(ledgerPath).Should().HaveCount(100);
        AllocationDirectionalLedger.ValidateExactTerminalAccounting(
            ledgerPath,
            observations.Select(observation => observation.AttemptId).ToArray());

        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        report.RootElement.GetProperty("decision").GetString()
            .Should().Be("noGo");
        report.RootElement.GetProperty("blindedEvaluationRan").GetBoolean()
            .Should().BeFalse();
        report.RootElement.GetProperty("healthyUnchangedRateControl")
            .GetProperty("assessable").GetBoolean()
            .Should().BeTrue();
        report.RootElement.GetProperty("healthyUnchangedRateControl")
            .GetProperty("withinPracticalSanity").GetBoolean()
            .Should().BeTrue();
        report.RootElement.GetProperty("healthyUnchangedRateControl")
            .GetProperty("absoluteRelativeDeltas")
            .GetProperty("inliers").GetArrayLength().Should().Be(5);
        var cohorts = report.RootElement.GetProperty("cohorts")
            .EnumerateArray().ToArray();
        cohorts.Should().HaveCount(2);
        var primary = cohorts.Single(value =>
            value.GetProperty("cohortId").GetString()
                == "primary-every-operation");
        primary.GetProperty("assessable").GetBoolean().Should().BeTrue();
        primary.GetProperty("aaEnvelope").GetDecimal()
            .Should().BeLessThan(0.10m);
        primary.GetProperty("throughputMultipliers").GetProperty("median")
            .GetDecimal().Should().Be(63m);
        primary.GetProperty("effectAbsoluteRelativeDeltas")
            .GetProperty("inliers").GetArrayLength().Should().Be(4);
        primary.GetProperty("inlierMedianAbsoluteRelativeDelta")
            .GetDecimal().Should().BeGreaterThan(0.32m);
        primary.GetProperty("medianExceedsEquivalenceMargin")
            .GetBoolean().Should().BeTrue();
        primary.GetProperty("classification").GetString()
            .Should().Be("normalization-failed");
        var low = cohorts.Single(value =>
            value.GetProperty("cohortId").GetString()
                == "low-every-eight-operations");
        low.GetProperty("assessable").GetBoolean().Should().BeTrue();
        low.GetProperty("workloadManipulationEstablished").GetBoolean()
            .Should().BeTrue();
        low.GetProperty("medianExceedsEquivalenceMargin").GetBoolean()
            .Should().BeTrue();
        low.GetProperty("throughputMultipliers").GetProperty("median")
            .GetDecimal().Should().BeGreaterThan(293m);
        low.GetProperty("effectAbsoluteRelativeDeltas")
            .GetProperty("inliers").GetArrayLength().Should().Be(5);
        low.GetProperty("inlierMedianAbsoluteRelativeDelta")
            .GetDecimal().Should().BeGreaterThan(0.13m);
        low.GetProperty("classification").GetString()
            .Should().Be("normalization-failed");

        var evaluated =
            AllocationThroughputNormalizationV2Harness.Evaluate(fixture);
        evaluated.Decision.Should().Be(
            AllocationThroughputNormalizationV2Decision.NoGo);
        evaluated.AttemptedPairs.Should().Be(25);
        evaluated.AttemptedSides.Should().Be(50);
        evaluated.FailedOrAbortedSides.Should().Be(0);
        evaluated.Cohorts.Should().OnlyContain(cohort =>
            cohort.Assessable
            && cohort.WorkloadManipulationEstablished
            && cohort.MedianExceedsEquivalenceMargin
            && !cohort.NormalizedQuantityEquivalent
            && cohort.EffectAbsoluteRelativeDeltas.Inliers.Count >= 4);
    }

    [Fact]
    public void FinalArtifacts_HaveFrozenPortableFingerprints()
    {
        AssertFingerprint(
            AllocationThroughputNormalizationV2Protocol.FileName,
            35_238,
            "9e6a5be34c474d7085ebe4a885adb737385274d7e6a3a662c20abc634c1f2804");
        AssertFingerprint(
            "allocation-throughput-normalization-falsifier-v2.windows-x64.evidence.json",
            217_734,
            "d4101ed984a4a5c1c495a33e624d62603e4b2a994e04f7849a55261d711bfbd9");
        AssertFingerprint(
            "allocation-throughput-normalization-falsifier-v2.windows-x64.evidence.ledger.jsonl",
            34_293,
            "82fdbfdd38b1306d873682b6372bb26196c531c23108e8a05060517ba5033801");
        AssertFingerprint(
            "allocation-throughput-normalization-falsifier-v2.windows-x64.evidence.report.json",
            5_746,
            "4cded16413ab923a6c5c81e6ba88d68b86c2869b09db1684c8d33d51bcabecc9");
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
