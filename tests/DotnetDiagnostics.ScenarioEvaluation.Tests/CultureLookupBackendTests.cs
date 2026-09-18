using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;
using Microsoft.Diagnostics.Tracing.Session;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class CultureLookupBackendTests
{
    [WindowsOnlyFact("The ETW availability control requires native Windows.")]
    public async Task NativeWindowsEtw_AvailabilityMatchesElevationAndRejectsMissingPermission()
    {
        var sampler = new EtwNativeAotCpuSampler();
        sampler.IsAvailable().Should().Be(TraceEventSession.IsElevated() == true);
        if (!sampler.IsAvailable())
        {
            var capture = () => sampler.SampleAsync(Environment.ProcessId, TimeSpan.FromSeconds(1));
            await capture.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not available*");
        }
    }

    [Fact]
    public void Capture_RequiresExplicitOsBackendAndKeepsWindowsGuard()
    {
        ScenarioLiveRunner.CultureLookupSamplingMode.Should().Be(CpuSamplingMode.Os);
        var manifest = ScenarioManifestLoader.LoadAll().Single(item => item.Id == "culture-lookup");
        manifest.SupportedLivePlatforms.Should().Equal(ScenarioPlatform.Windows);
        manifest.ExpectedEvidence.Single(item => item.Id == "globalization-hash-leaf").Threshold.Should().Be(20);
    }

    [Fact]
    public void EventPipeCannotSatisfyMeasuredCpuContract()
    {
        var result = Result(CpuSampleEvidence.EventPipeSampleProfiler, 100);
        var validate = () => ScenarioLiveRunner.ValidateCultureCpuEvidence(result);
        validate.Should().Throw<InvalidOperationException>().WithMessage("*no EventPipe fallback*");
    }

    [Fact]
    public void MissingSamplesAndSymbolsAreCollectionFailuresNotLowMagnitude()
    {
        var empty = () => ScenarioLiveRunner.ValidateCultureCpuEvidence(Result(CpuSampleEvidence.WindowsEtwOnCpu, 0));
        empty.Should().Throw<InvalidOperationException>().WithMessage("*no samples*");
        var stripped = Result(CpuSampleEvidence.WindowsEtwOnCpu, 100);
        stripped = stripped with { Summary = stripped.Summary with { SymbolSource = NativeAotSymbolDemangler.SymbolSource.Stripped } };
        var validate = () => ScenarioLiveRunner.ValidateCultureCpuEvidence(stripped);
        validate.Should().Throw<InvalidOperationException>().WithMessage("*no usable symbols*");
        var resolved = () => ScenarioLiveRunner.ValidateCultureCpuEvidence(Result(CpuSampleEvidence.WindowsEtwOnCpu, 100));
        resolved.Should().NotThrow();
    }

    [Theory]
    [InlineData("PermissionDenied")]
    [InlineData("UnsupportedPrerequisite")]
    [InlineData("UnsupportedPlatform")]
    public void UnavailableBackendIsEnvironmentFailure(string errorKind)
    {
        ScenarioFailureClassifier.Classify(
            new CpuSamplingUnavailableException(errorKind, "OS backend unavailable; no fallback"),
            ScenarioFailureKind.Collection).Should().Be(ScenarioFailureKind.Environment);
        ScenarioFailureClassifier.Classify(new InvalidOperationException("backend processing failed"),
            ScenarioFailureKind.Collection).Should().Be(ScenarioFailureKind.Collection);
    }

    private static CpuSampleResult Result(CpuSampleEvidence evidence, long count)
    {
        var started = DateTimeOffset.UnixEpoch;
        var duration = TimeSpan.FromSeconds(8);
        return new(
            new CpuSample(1, started, duration, count, [])
            {
                Evidence = evidence,
                SymbolSource = NativeAotSymbolDemangler.SymbolSource.PdbResolved,
            },
            new CpuSampleTraceArtifact(1, started, duration, count,
                new CallTreeNode(new SampledFrame("", "root"), count, 0, [])) { Evidence = evidence });
    }
}
