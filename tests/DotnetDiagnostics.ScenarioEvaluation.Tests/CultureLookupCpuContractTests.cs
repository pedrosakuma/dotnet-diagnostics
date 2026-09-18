using System.Net.Http.Json;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

[Collection(ScenarioEvaluationLiveGroup.Name)]
public sealed class CultureLookupCpuContractTests
{
    private static readonly ScenarioManifest Manifest =
        ScenarioManifestLoader.LoadAll().Single(manifest => manifest.Id == "culture-lookup");
    private static readonly ScenarioEvidence BaseEvidence =
        ScenarioJson.ReadEvidence(ScenarioManifestLoader.ScenarioPath("Fixtures", "culture-lookup.windows.evidence.json"));

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(1000, 1)]
    public void PairedOwnership_DoesNotRequireEitherComparerToStaySlower(int cultureSamples, int ordinalSamples)
    {
        var evidence = Pair(
            Artifact(CultureLookupCpuContract.CultureMethod, cultureSamples),
            Artifact(CultureLookupCpuContract.OrdinalMethod, ordinalSamples));
        var report = ScenarioEvaluator.CreateReport(Manifest, evidence);

        report.Evidence.Should().OnlyContain(item => item.Passed);
        evidence.Signals.Should().BeEmpty("ownership does not synthesize an exclusive concentration signal");
        evidence.Collection.DurationSeconds.Should().Be(16);
    }

    [Fact]
    public void InclusiveOwnership_DeduplicatesRecursiveOccurrencesAndDoesNotRequireExclusiveManagedCost()
    {
        var nested = Owner(CultureLookupCpuContract.CultureMethod, 60, Leaf(60));
        var owner = Owner(CultureLookupCpuContract.CultureMethod, 80, Leaf(20), nested);
        var artifact = Trace(100, owner, Leaf(20));

        CultureLookupCpuContract.InclusiveSamples(artifact, CultureLookupCpuContract.CultureMethod)
            .Should().Be(80, "the same stack must not be counted twice for a recursive owner");
        owner.ExclusiveSamples.Should().Be(0, "inclusive ownership must not be presented as exclusive leaf cost");
    }

    [Fact]
    public void NonemptyNativeOrRuntimeStacks_DoNotSatisfyOwnedWorkloadEvidence()
    {
        var evidence = Pair(Trace(100, Leaf(100)), Trace(100, Leaf(100)));
        var report = ScenarioEvaluator.CreateReport(Manifest, evidence);

        report.Evidence.Should().Contain(item => item.Id == "culture-owned-cpu" && !item.Passed);
        report.Evidence.Should().Contain(item => item.Id == "ordinal-owned-cpu" && !item.Passed);
    }

    [Fact]
    public void SwappedPhases_FailBothPositiveAndNegativeControls()
    {
        var evidence = Pair(
            Artifact(CultureLookupCpuContract.OrdinalMethod, 100),
            Artifact(CultureLookupCpuContract.CultureMethod, 100));
        var report = ScenarioEvaluator.CreateReport(Manifest, evidence);

        report.Evidence.Where(item => item.Id.EndsWith("owned-cpu", StringComparison.Ordinal)
            || item.Id.EndsWith("negative-control", StringComparison.Ordinal)).Should().OnlyContain(item => !item.Passed);
    }

    [Fact]
    public void UnexpectedOtherRoute_IsNotForgivenBecauseTheActiveRouteWasObserved()
    {
        var contaminated = Trace(100,
            Owner(CultureLookupCpuContract.CultureMethod, 80, Leaf(80)),
            Owner(CultureLookupCpuContract.OrdinalMethod, 20, Leaf(20)));
        var report = ScenarioEvaluator.CreateReport(Manifest,
            Pair(contaminated, Artifact(CultureLookupCpuContract.OrdinalMethod, 100)));

        report.Evidence.Single(item => item.Id == "culture-owned-cpu").Passed.Should().BeTrue();
        report.Evidence.Single(item => item.Id == "culture-negative-control").Passed.Should().BeFalse();
    }

    [Fact]
    public void StackFrequencyCannotBeRelabelledAsMeasuredOwnership()
    {
        var artifact = Artifact(CultureLookupCpuContract.CultureMethod, 100) with
        {
            Evidence = CpuSampleEvidence.EventPipeSampleProfiler,
        };
        var project = () => CultureLookupCpuContract.ProjectOwnership(
            artifact, CultureLookupCpuContract.CultureMethod, CultureLookupCpuContract.OrdinalMethod, 1);
        project.Should().Throw<InvalidOperationException>().WithMessage("*measured OS*");
    }

    [Fact]
    public void SameNamedMethodInAnotherAssembly_IsNotOwnedWorkloadEvidence()
    {
        var wrongModule = new CallTreeNode(
            new SampledFrame("AnotherAssembly", CultureLookupCpuContract.CultureMethod + ")"),
            100, 0, [Leaf(100)]);
        var query = () => CultureLookupCpuContract.InclusiveSamples(Trace(100, wrongModule), CultureLookupCpuContract.CultureMethod);
        query.Should().Throw<InvalidOperationException>().WithMessage("*unexpected module*");
    }

    [Fact]
    public void HistoricalPrivateCostEvidence_CannotBeReclassifiedAsAV2Pass()
    {
        var historical = ScenarioJson.ReadEvidence(ScenarioManifestLoader.ScenarioPath(
            "Fixtures", "Compatibility", "culture-lookup.v1.windows.evidence.json"));
        var evaluate = () => ScenarioEvaluator.CreateReport(Manifest, historical);
        evaluate.Should().Throw<InvalidDataException>().WithMessage("*does not match*");
        var combine = () => CultureLookupCpuContract.Combine(BaseEvidence, historical, 30);
        combine.Should().Throw<InvalidDataException>().WithMessage("*same scenario version*");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailedPhase_RemainsACollectionFailure(bool ordinalFailed)
    {
        var failed = BaseEvidence with
        {
            Collection = new(ScenarioStageStatus.Failed, ScenarioFailureKind.Collection, "symbols unavailable", 8),
        };
        var combined = CultureLookupCpuContract.Combine(
            ordinalFailed ? BaseEvidence : failed, ordinalFailed ? failed : BaseEvidence, 30);
        combined.Collection.Status.Should().Be(ScenarioStageStatus.Failed);
        combined.Collection.FailureKind.Should().Be(ScenarioFailureKind.Collection);
        combined.Collection.Detail.Should().Contain("symbols unavailable");
    }

    [Theory]
    [InlineData(0, 64, "InvariantCultureIgnoreCase")]
    [InlineData(64, 0, "InvariantCultureIgnoreCase")]
    [InlineData(64, 64, "OrdinalIgnoreCase")]
    public void IncorrectLookupResultsAreRejected(int loops, long hits, string comparer)
    {
        var validate = () => ScenarioLiveRunner.ValidateCultureResponse(
            new ScenarioLiveRunner.CultureLookupResponse(loops, hits, comparer), 64, "InvariantCultureIgnoreCase");
        validate.Should().Throw<InvalidDataException>();
    }

    [Fact(Timeout = 30_000)]
    public async Task ActualLookupRoutes_ReturnEquivalentVerifiedResults()
    {
        await using var sample = await LiveSampleProcess.StartPublishedAsync("BadCodeSample",
            new LiveSampleOptions { HarvestListeningUrl = true, WaitForHttpReady = true, ReadinessPath = "/" });
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl), Timeout = TimeSpan.FromSeconds(5) };
        var culture = await http.GetFromJsonAsync<ScenarioLiveRunner.CultureLookupResponse>("/culture-lookup?iterations=64");
        var ordinal = await http.GetFromJsonAsync<ScenarioLiveRunner.CultureLookupResponse>("/culture-lookup-fixed?iterations=64");

        ScenarioLiveRunner.ValidateCultureResponse(culture, 64, "InvariantCultureIgnoreCase");
        ScenarioLiveRunner.ValidateCultureResponse(ordinal, 64, "OrdinalIgnoreCase");
        culture!.Hits.Should().Be(ordinal!.Hits);
    }

    private static ScenarioEvidence Pair(CpuSampleTraceArtifact culture, CpuSampleTraceArtifact ordinal)
        => CultureLookupCpuContract.Combine(
            Phase(culture, CultureLookupCpuContract.CultureMethod, CultureLookupCpuContract.OrdinalMethod),
            Phase(ordinal, CultureLookupCpuContract.OrdinalMethod, CultureLookupCpuContract.CultureMethod), 30);

    private static ScenarioEvidence Phase(CpuSampleTraceArtifact artifact, string active, string inactive)
        => BaseEvidence with
        {
            Metrics = CultureLookupCpuContract.ProjectOwnership(artifact, active, inactive, verifiedResponses: 1),
            Collection = BaseEvidence.Collection with { DurationSeconds = 8 },
        };

    private static CpuSampleTraceArtifact Artifact(string owner, int samples)
        => Trace(samples, Owner(owner, samples, Leaf(samples)));

    private static CpuSampleTraceArtifact Trace(long total, params CallTreeNode[] children)
        => new(1, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(8), total,
            new CallTreeNode(new SampledFrame("", "<root>"), total, 0, children))
        {
            Evidence = CpuSampleEvidence.WindowsEtwOnCpu,
            SelfSamples = new SelfSampleBreakdown(total, 0),
            SymbolSource = NativeAotSymbolDemangler.SymbolSource.PdbResolved,
        };

    private static CallTreeNode Owner(string method, long inclusive, params CallTreeNode[] children)
        => new(new SampledFrame("BadCodeSample", method + ")"), inclusive, 0, children);

    private static CallTreeNode Leaf(long samples)
        => new(new SampledFrame("native", "uncontrolled_native_implementation"), samples, samples, [])
        {
            SelfSamples = new SelfSampleBreakdown(samples, 0),
        };
}
