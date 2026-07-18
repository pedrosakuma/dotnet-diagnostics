using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class ScenarioManifestTests
{
    [Fact]
    public void LoadAll_ValidatesTheThreeStartingScenarios()
    {
        var manifests = ScenarioManifestLoader.LoadAll();

        manifests.Select(manifest => manifest.Id).Should().Equal(
            "culture-lookup",
            "lock-storm",
            "sync-over-async");
        manifests.Should().OnlyContain(manifest => manifest.SchemaVersion == ScenarioManifestLoader.CurrentSchemaVersion);
    }

    [Fact]
    public void Schema_IsValidJsonSchemaDocument()
    {
        var path = ScenarioManifestLoader.ScenarioPath("Scenarios", "scenario-manifest.schema.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        document.RootElement.GetProperty("$schema").GetString().Should().Be("https://json-schema.org/draft/2020-12/schema");
        document.RootElement.GetProperty("title").GetString().Should().Be("Diagnostic scenario manifest");
    }

    [Fact]
    public void Validate_RejectsIncompleteInvariant()
    {
        var manifest = ScenarioManifestLoader.LoadAll()[0] with
        {
            ExpectedEvidence =
            [
                new EvidenceInvariant("broken", EvidenceInvariantKind.CounterComparison, Metric: "cpu-usage"),
            ],
        };

        var action = () => ScenarioManifestValidator.Validate(manifest);

        action.Should().Throw<InvalidDataException>().WithMessage("*incomplete*");
    }
}
