using System.Text.Json;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class ScenarioManifestLoader
{
    public const int CurrentSchemaVersion = 1;

    public static IReadOnlyList<ScenarioManifest> LoadAll()
        => Directory
            .EnumerateFiles(ScenarioPath("Scenarios"), "*.scenario.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(Load)
            .ToArray();

    public static ScenarioManifest Load(string path)
    {
        using var stream = File.OpenRead(path);
        var manifest = JsonSerializer.Deserialize(stream, ScenarioJsonContext.Default.ScenarioManifest)
            ?? throw new InvalidDataException($"Scenario manifest '{path}' was empty.");
        ScenarioManifestValidator.Validate(manifest);
        return manifest;
    }

    public static string ScenarioPath(params string[] segments)
        => segments.Aggregate(AppContext.BaseDirectory, Path.Combine);
}

public static class ScenarioManifestValidator
{
    public static void Validate(ScenarioManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != ScenarioManifestLoader.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Scenario '{manifest.Id}' uses schema version {manifest.SchemaVersion}; expected {ScenarioManifestLoader.CurrentSchemaVersion}.");
        }

        RequireText(manifest.Id, "id");
        RequireText(manifest.Version, "version");
        RequireText(manifest.ReportedSymptom, "reportedSymptom");
        RequireText(manifest.GroundTruth, "groundTruth");
        RequireText(manifest.RequiredCausalityPosture, "requiredCausalityPosture");
        if (manifest.SupportedLivePlatforms.Count == 0)
        {
            throw new InvalidDataException($"Scenario '{manifest.Id}' must support at least one live-capture platform.");
        }

        if (manifest.Workload.ObservationSeconds < 1 || manifest.Budget.MaximumRuntimeSeconds < manifest.Workload.ObservationSeconds)
        {
            throw new InvalidDataException($"Scenario '{manifest.Id}' has an invalid observation/runtime budget.");
        }

        if (manifest.Budget.MaximumEvidenceItems is < 1 or > 100)
        {
            throw new InvalidDataException($"Scenario '{manifest.Id}' maximumEvidenceItems must be in [1, 100].");
        }

        if (manifest.ExpectedEvidence.Count == 0
            || manifest.ExpectedEvidence.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != manifest.ExpectedEvidence.Count)
        {
            throw new InvalidDataException($"Scenario '{manifest.Id}' must declare uniquely named evidence invariants.");
        }

        foreach (var invariant in manifest.ExpectedEvidence)
        {
            ValidateInvariant(manifest.Id, invariant);
        }

        RequireNonEmpty(manifest.TemptingWrongHypotheses, manifest.Id, "temptingWrongHypotheses");
        RequireNonEmpty(manifest.AcceptableHypotheses, manifest.Id, "acceptableHypotheses");
        RequireNonEmpty(manifest.AcceptableAttributions, manifest.Id, "acceptableAttributions");
        RequireNonEmpty(manifest.AcceptableNextActions, manifest.Id, "acceptableNextActions");
        RequireNonEmpty(manifest.ForbiddenConclusions, manifest.Id, "forbiddenConclusions");
    }

    private static void ValidateInvariant(string scenarioId, EvidenceInvariant invariant)
    {
        RequireText(invariant.Id, "expectedEvidence.id");
        var hasComparison = invariant.Comparison is not null && invariant.Threshold is not null;

        var valid = invariant.Kind switch
        {
            EvidenceInvariantKind.SignalPresent
                => HasText(invariant.Signal),
            EvidenceInvariantKind.SignalBucketMatch
                => HasText(invariant.Signal) && HasTerms(invariant.ContainsAny) && hasComparison,
            EvidenceInvariantKind.CounterComparison
                => HasText(invariant.Metric) && hasComparison,
            EvidenceInvariantKind.StackFrameMatch
                => HasTerms(invariant.ContainsAny) && invariant.MinimumMatches > 0,
            EvidenceInvariantKind.ThreadOwnerCorrelation
                => HasText(invariant.Relation) && HasText(invariant.OwnerWaitReason) && hasComparison,
            _ => false,
        };

        if (!valid)
        {
            throw new InvalidDataException(
                $"Scenario '{scenarioId}' invariant '{invariant.Id}' is incomplete for kind '{invariant.Kind}'.");
        }
    }

    private static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    private static bool HasTerms(IReadOnlyList<string>? values)
        => values is { Count: > 0 } && values.All(HasText);

    private static void RequireText(string? value, string field)
    {
        if (!HasText(value))
        {
            throw new InvalidDataException($"Scenario manifest field '{field}' is required.");
        }
    }

    private static void RequireNonEmpty(IReadOnlyList<string> values, string scenarioId, string field)
    {
        if (values.Count == 0 || values.Any(value => !HasText(value)))
        {
            throw new InvalidDataException($"Scenario '{scenarioId}' field '{field}' must contain non-empty values.");
        }
    }
}
