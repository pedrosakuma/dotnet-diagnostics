using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class ScenarioLiveRunnerTests
{
    [Fact]
    public void StartupReadiness_PreservesFloorForShortScenariosAndScalesForLongCaptures()
    {
        var manifests = ScenarioManifestLoader.LoadAll();
        var lockStorm = manifests.Single(item => item.Id == "lock-storm");
        var cultureLookup = manifests.Single(item => item.Id == "culture-lookup");

        ScenarioLiveRunner.StartupStageTimeout(lockStorm)
            .Should().Be(TimeSpan.FromSeconds(10));
        ScenarioLiveRunner.StartupStageTimeout(cultureLookup)
            .Should().Be(TimeSpan.FromSeconds(37.5));
    }
}
