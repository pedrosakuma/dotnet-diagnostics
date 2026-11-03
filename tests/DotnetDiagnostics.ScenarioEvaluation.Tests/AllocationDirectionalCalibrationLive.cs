using System.Runtime.InteropServices;
using System.Text.Json;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class AllocationDirectionalCalibrationEnvironmentVariables
{
    public const string FixturePath =
        "DOTNET_DIAGNOSTICS_ALLOCATION_DIRECTIONAL_CALIBRATION_FIXTURE_PATH";
}

internal sealed record AllocationDirectionalCalibrationFixture(
    int SchemaVersion,
    string CalibrationId,
    string CalibrationVersion,
    string CreatedAtUtc,
    string Environment,
    string WorkloadIdentity,
    string BaselineShape,
    string CurrentShape,
    int RequiredIndependentPairs,
    IReadOnlyList<AllocationDirectionalPair> Pairs);

internal sealed record AllocationDirectionalCalibrationReport(
    string CalibrationId,
    string CalibrationVersion,
    bool Passed,
    AllocationDirectionalThroughputGate ThroughputGate,
    IReadOnlyList<string> Notes);

internal sealed record AllocationDirectionalCalibrationStart(
    string CalibrationId,
    string CalibrationVersion,
    string StartedAtUtc);

internal static class AllocationDirectionalCalibrationJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static AllocationDirectionalCalibrationFixture ReadFixture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<AllocationDirectionalCalibrationFixture>(
                stream,
                Options)
            ?? throw new InvalidDataException(
                "The allocation directional calibration fixture was empty.");
    }

    public static void WriteFixture(
        string path,
        AllocationDirectionalCalibrationFixture fixture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fixture);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        JsonSerializer.Serialize(stream, fixture, Options);
    }

    public static string SerializeReport(
        AllocationDirectionalCalibrationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, Options);
    }

    public static string SerializeStart(
        AllocationDirectionalCalibrationStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        return JsonSerializer.Serialize(start, Options);
    }
}

internal static class AllocationDirectionalCalibrationLive
{
    public const string CalibrationId =
        "allocation-directional-throughput-pacing";
    public const string CalibrationVersion = "1.0.0";
    public const string WorkloadIdentity =
        "throughput-pacing-calibration-v1";

    private static readonly AllocationDirectionalPlannedSide Paced = new(
        WorkloadIdentity,
        "primary-paced",
        1,
        "steady",
        288_000,
        AllocationDirectionalCapturePlan.DefaultWarmupMilliseconds,
        0,
        true);

    private static readonly AllocationDirectionalPlannedSide PacedAa =
        Paced with { Phase = "aa" };

    private static readonly AllocationDirectionalPlannedSide Unpaced =
        Paced with
        {
            Shape = "primary",
            Phase = "effect",
        };

    public static async Task<AllocationDirectionalCalibrationFixture> CaptureAsync(
        CancellationToken cancellationToken)
    {
        var artifactRoot = Path.Combine(
            Path.GetTempPath(),
            "dotnet-diagnostics-allocation-directional-calibration",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(artifactRoot);
        await using var client = await AllocationDirectionalLive.StartMcpAsync(
            artifactRoot,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var pairs = new List<AllocationDirectionalPair>();
            for (var repetition = 1;
                 repetition <= AllocationDirectionalBounds.RequiredPairs;
                 repetition++)
            {
                pairs.Add(await CapturePairAsync(
                    client,
                    $"calibration-aa-{repetition:D2}",
                    Paced,
                    PacedAa,
                    cancellationToken).ConfigureAwait(false));
                pairs.Add(await CapturePairAsync(
                    client,
                    $"calibration-effect-{repetition:D2}",
                    Paced,
                    Unpaced,
                    cancellationToken).ConfigureAwait(false));
            }

            return new(
                1,
                CalibrationId,
                CalibrationVersion,
                DateTimeOffset.UtcNow.ToString("O"),
                $"{RuntimeInformation.OSDescription}; {Environment.Version}; {RuntimeInformation.ProcessArchitecture}",
                WorkloadIdentity,
                Paced.Shape,
                Unpaced.Shape,
                AllocationDirectionalBounds.RequiredPairs,
                pairs);
        }
        finally
        {
            try
            {
                Directory.Delete(artifactRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<AllocationDirectionalPair> CapturePairAsync(
        ModelContextProtocol.Client.McpClient client,
        string pairId,
        AllocationDirectionalPlannedSide baseline,
        AllocationDirectionalPlannedSide current,
        CancellationToken cancellationToken)
    {
        var baselineObservation =
            await AllocationDirectionalLive.CaptureObservationAsync(
                client,
                $"{pairId}-baseline",
                WorkloadIdentity,
                baseline,
                cancellationToken).ConfigureAwait(false);
        var currentObservation =
            await AllocationDirectionalLive.CaptureObservationAsync(
                client,
                $"{pairId}-current",
                WorkloadIdentity,
                current,
                cancellationToken).ConfigureAwait(false);
        return new(
            pairId,
            AllocationDirectionalScenario.ThroughputIncreaseStablePerOperation,
            "pacing",
            baselineObservation,
            currentObservation);
    }
}

[Collection(ScenarioEvaluationLiveGroup.Name)]
public sealed class AllocationDirectionalCalibrationLiveTests
{
    [EnvironmentRequiredFact(
        AllocationDirectionalCalibrationEnvironmentVariables.FixturePath,
        "The one-time throughput calibration requires an explicit output path.",
        Timeout = 1_800_000)]
    [Trait("Category", "ScenarioEvaluationLive")]
    public async Task ThroughputPacingCalibration_WritesPassingFixtureOnce()
    {
        var fixturePath = Environment.GetEnvironmentVariable(
            AllocationDirectionalCalibrationEnvironmentVariables.FixturePath)
            ?? throw new InvalidOperationException(
                "The calibration fixture path was not configured.");
        var reportPath = Path.ChangeExtension(fixturePath, ".report.json");
        var startedPath = Path.ChangeExtension(fixturePath, ".started.json");
        File.Exists(fixturePath).Should().BeFalse(
            "calibration evidence must not overwrite an earlier run");
        File.Exists(reportPath).Should().BeFalse(
            "calibration reports must not overwrite an earlier run");
        File.Exists(startedPath).Should().BeFalse(
            "a start marker proves an earlier calibration attempt already began");
        File.WriteAllText(
            startedPath,
            AllocationDirectionalCalibrationJson.SerializeStart(
                new(
                    AllocationDirectionalCalibrationLive.CalibrationId,
                    AllocationDirectionalCalibrationLive.CalibrationVersion,
                    DateTimeOffset.UtcNow.ToString("O"))));

        var fixture = await AllocationDirectionalCalibrationLive.CaptureAsync(
            CancellationToken.None);
        var gate = AllocationDirectionalCalibrationSelector.EvaluateThroughput(
            fixture.Pairs);
        var report = new AllocationDirectionalCalibrationReport(
            fixture.CalibrationId,
            fixture.CalibrationVersion,
            gate.Passed,
            gate,
            [
                "This is a pre-freeze workload calibration, not a directional-feasibility protocol or final inventory.",
                "Operations per second establishes the workload manipulation.",
                "Estimated sampled bytes per operation is the normalized allocation stability quantity.",
                "Estimated sampled bytes per second is descriptive only and does not decide the gate.",
            ]);

        AllocationDirectionalCalibrationJson.WriteFixture(fixturePath, fixture);
        File.WriteAllText(
            reportPath,
            AllocationDirectionalCalibrationJson.SerializeReport(report));
        gate.Passed.Should().BeTrue();
    }
}
