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

    [Fact]
    public void IcuCoverage_ProjectsCapturedIdentityAndExactDenominators()
    {
        var result = DiffuseResult();
        var signature = Guid.Parse("94451369-D782-EA5D-26A5-A3501C131722");
        var coverage = new NativeLeafCoverage(
            RetainedPcLimit: 4096,
            RetainedModuleLimit: 512,
            TotalLeafSamples: 12,
            VerifiedRangeSamples: 9,
            UnretainedSampleWeight: 2,
            UnretainedVerifiedRangeSampleWeight: 1,
            UnretainedModuleSampleWeight: 0,
            RetainedDistinctPcs: 3,
            RetainedVerifiedDistinctPcs: 2,
            RetainedModules: 1,
            UnretainedModules: 0,
            Modules:
            [
                new NativeModuleLeafCoverage(
                    "icu.dll",
                    @"C:\Windows\System32\icu.dll",
                    0x1800_0000,
                    2_769_976,
                    "icu.pdb",
                    signature,
                    1,
                    "Ready",
                    TotalLeafSamples: 12,
                    VerifiedRangeSamples: 9,
                    UnretainedSampleWeight: 2,
                    RetainedDistinctPcs: 3,
                    RetainedVerifiedDistinctPcs: 2,
                    RetainedPcs:
                    [
                        new(0x1800_0100, 0x100, 8, NativeLeafResolutionStatus.VerifiedContainingRange, "icu_function"),
                        new(0x1800_0200, 0x200, 1, NativeLeafResolutionStatus.VerifiedContainingRange, "icu_other"),
                        new(0x1800_0300, 0x300, 1, NativeLeafResolutionStatus.OutsideRange, null),
                    ])
            ]);
        result = result with
        {
            Artifact = result.Artifact with { NativeLeafCoverage = coverage },
            Summary = result.Summary with { NativeLeafCoverage = coverage },
        };

        var evidence = ScenarioLiveRunner.CompleteCultureCpuEvidence(
            BaseEvidence, result, maximumEvidenceItems: 5);

        evidence.Metrics.Should().Contain(metric =>
            metric.Name == "icu-native-leaf-sample-weighted-coverage" && metric.Value == 75);
        evidence.Metrics.Should().Contain(metric =>
            metric.Name == "icu-native-leaf-retained-distinct-coverage"
            && Math.Abs(metric.Value - 66.66666666666667) < 0.0001);
        evidence.Metrics.Should().Contain(metric =>
            metric.Name == "icu-native-leaf-unretained-sample-weight" && metric.Value == 2);
        evidence.Notes.Should().Contain(note =>
            note.Contains("guid=94451369-d782-ea5d-26a5-a3501c131722", StringComparison.OrdinalIgnoreCase)
            && note.Contains("resolver=Ready", StringComparison.Ordinal));
        evidence.Notes.Should().Contain(note =>
            note.Contains("does not identify, rebase or recover the historical hosted capture", StringComparison.Ordinal));
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
