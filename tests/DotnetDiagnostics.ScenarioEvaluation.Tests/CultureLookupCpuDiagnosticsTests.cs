using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Signals;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class CultureLookupCpuDiagnosticsTests
{
    private static readonly ScenarioManifest Manifest =
        ScenarioManifestLoader.LoadAll().Single(manifest => manifest.Id == "culture-lookup");
    private static readonly ScenarioEvidence BaseEvidence =
        ScenarioJson.ReadEvidence(ScenarioManifestLoader.ScenarioPath("Fixtures", "culture-lookup.windows.evidence.json"));

    [Fact]
    public void DiffuseRawLeaves_RetainCandidatesAndGateMagnitudeWithoutInventingHashAttribution()
    {
        var result = DiffuseResult();
        CpuSampleSignals.Detect(result.Artifact, "replay")
            .Should().NotContain(signal => signal.Signal == "cpu.self-time.concentration");

        var evidence = ScenarioLiveRunner.CompleteCultureCpuEvidence(
            BaseEvidence with { Signals = [] }, result, maximumEvidenceItems: 3,
            CultureLookupCpuContract.CultureMethod, CultureLookupCpuContract.OrdinalMethod, verifiedResponses: 1);

        evidence.Collection.Status.Should().Be(ScenarioStageStatus.Passed);
        evidence.Signals.Should().BeEmpty();
        evidence.Frames.Should().HaveCount(3).And.OnlyContain(frame =>
            frame.DisplayName.StartsWith("icu.dll!0x", StringComparison.Ordinal) && frame.MatchCount == 2);
        evidence.Frames.Should().NotContain(frame => frame.DisplayName.Contains("GetHashCodeOfString", StringComparison.Ordinal),
            "the managed hashing ancestor has no exclusive observations");
        evidence.Metrics.Single(metric => metric.Name == "cpu-top1-running-self-share").Value.Should().Be(2);
        evidence.Metrics.Single(metric => metric.Name == "cpu-concentration-min-top1-share").Value.Should().Be(15);
        evidence.Metrics.Single(metric => metric.Name == "cpu-retained-exclusive-samples").Value.Should().Be(6);
        evidence.Notes.Should().Contain(note => note.Contains("Exclusive module: icu.dll; samples=100; share=100%", StringComparison.Ordinal));
        evidence.Notes.Should().Contain(note => note.Contains("artifactSymbolSource=PdbResolved; summarySymbolSource=missing", StringComparison.Ordinal));
        evidence.Notes.Count.Should().BeLessThanOrEqualTo(20);

        var report = ScenarioEvaluator.CreateReport(
            Manifest, CultureLookupCpuContract.Combine(evidence, evidence, maximumEvidenceItems: 3));
        report.Evidence.Should().Contain(item => item.Id == "culture-owned-cpu" && !item.Passed);
    }

    [Theory]
    [InlineData(NativeAotSymbolDemangler.SymbolSource.Stripped)]
    [InlineData(NativeAotSymbolDemangler.SymbolSource.Unknown)]
    public void SymbolFailure_RetainsTheEvidenceNeededToDiagnoseIt(
        NativeAotSymbolDemangler.SymbolSource symbolSource)
    {
        var result = DiffuseResult();
        result = result with { Artifact = result.Artifact with { SymbolSource = symbolSource } };

        var evidence = ScenarioLiveRunner.CompleteCultureCpuEvidence(BaseEvidence, result, maximumEvidenceItems: 5);

        evidence.Collection.Status.Should().Be(ScenarioStageStatus.Failed);
        evidence.Collection.FailureKind.Should().Be(ScenarioFailureKind.Collection);
        evidence.Collection.Detail.Should().Contain("no usable symbols");
        evidence.Frames.Should().HaveCount(5);
        evidence.Signals.Should().BeEmpty("an invalid capture must not retain even preexisting signals");
        evidence.Metrics.Should().Contain(metric => metric.Name == "cpu-top1-running-self-share" && metric.Value == 2);
    }

    private static CpuSampleResult DiffuseResult()
    {
        var started = DateTimeOffset.UnixEpoch;
        var duration = TimeSpan.FromSeconds(8);
        var leaves = Enumerable.Range(0, 50)
            .Select(index => new CallTreeNode(new SampledFrame("icu.dll", $"0x{index:X}"), 2, 2, [])
            {
                SelfSamples = new SelfSampleBreakdown(2, 0),
            }).ToArray();
        var ancestor = new CallTreeNode(
            new SampledFrame("System.Private.CoreLib", "System.Globalization.CompareInfo.GetHashCodeOfString"),
            100, 0, leaves);
        var artifact = new CpuSampleTraceArtifact(
            1, started, duration, 100, new CallTreeNode(new SampledFrame("", "<root>"), 100, 0, [ancestor]))
        {
            Evidence = CpuSampleEvidence.WindowsEtwOnCpu,
            SelfSamples = new SelfSampleBreakdown(100, 0),
            SymbolSource = NativeAotSymbolDemangler.SymbolSource.PdbResolved,
        };
        return new(
            new CpuSample(1, started, duration, 100, [])
            {
                Evidence = CpuSampleEvidence.WindowsEtwOnCpu,
                SelfSamples = artifact.SelfSamples,
                SymbolSource = null,
            },
            artifact);
    }
}
