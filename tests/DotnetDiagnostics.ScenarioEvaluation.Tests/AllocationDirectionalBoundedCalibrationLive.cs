using System.Runtime.InteropServices;
using System.Text.Json;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;
using ModelContextProtocol.Client;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class AllocationDirectionalBoundedCalibrationEnvironmentVariables
{
    public const string FixturePath =
        "DOTNET_DIAGNOSTICS_ALLOCATION_BOUNDED_CALIBRATION_FIXTURE_PATH";
}

internal static class AllocationDirectionalBoundedCalibrationLive
{
    public static async Task<AllocationDirectionalBoundedCalibrationFixture> CaptureAsync(
        CancellationToken cancellationToken)
    {
        var artifactRoot = Path.Combine(
            Path.GetTempPath(),
            "dotnet-diagnostics-allocation-bounded-calibration",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(artifactRoot);
        await using var client = await AllocationDirectionalLive.StartMcpAsync(
            artifactRoot,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var probeEvidencePath = Path.Combine(
                RepositoryRoot(),
                "tests",
                "DotnetDiagnostics.ScenarioEvaluation.Tests",
                "Fixtures",
                "AllocationDirectional",
                AllocationDirectionalBoundedCalibrationPlan.ProbeEvidenceFileName);
            using var predecessor = JsonDocument.Parse(
                File.ReadAllText(probeEvidencePath));
            var probes = predecessor.RootElement.GetProperty("probes")
                .Deserialize<AllocationDirectionalObservation[]>(
                    AllocationDirectionalJson.Options)
                ?? throw new InvalidDataException(
                    "The predecessor bounded calibration probes were empty.");

            var pairs = new List<AllocationDirectionalPair>();
            foreach (var entry in AllocationDirectionalBoundedCalibrationPlan.Entries)
            {
                for (var repetition = 1;
                     repetition <= AllocationDirectionalBounds.RequiredPairs;
                     repetition++)
                {
                    pairs.Add(await CapturePairAsync(
                        client,
                        entry,
                        repetition,
                        cancellationToken).ConfigureAwait(false));
                }
            }

            return new(
                SchemaVersion: 1,
                AllocationDirectionalBoundedCalibrationPlan.CalibrationId,
                AllocationDirectionalBoundedCalibrationPlan.CalibrationVersion,
                DateTimeOffset.UtcNow.ToString("O"),
                $"{RuntimeInformation.OSDescription}; {Environment.Version}; {RuntimeInformation.ProcessArchitecture}",
                $"{AllocationDirectionalBoundedCalibrationPlan.ProbeEvidenceFileName}; sha256={AllocationDirectionalProtocol.Sha256(probeEvidencePath)}",
                AllocationDirectionalBounds.RequiredPairs,
                AllocationDirectionalBoundedCalibrationPlan.ProbeSettings,
                AllocationDirectionalBoundedCalibrationPlan.OperatingSettings,
                probes,
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

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "DotnetDiagnostics.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException(
                "Could not locate the repository root for bounded allocation calibration.");
    }

    private static async Task<AllocationDirectionalPair> CapturePairAsync(
        McpClient client,
        AllocationDirectionalPlanEntry entry,
        int repetition,
        CancellationToken cancellationToken)
    {
        var pairId = $"{entry.PairPrefix}-{repetition:D2}";
        var baseline =
            await AllocationDirectionalLive.CaptureObservationAsync(
                client,
                $"{pairId}-baseline",
                entry.Baseline.WorkloadIdentity,
                entry.Baseline,
                AllocationDirectionalBoundedCalibrationPlan.OperatingSettings,
                cancellationToken).ConfigureAwait(false);
        var current =
            await AllocationDirectionalLive.CaptureObservationAsync(
                client,
                $"{pairId}-current",
                entry.Current.WorkloadIdentity,
                entry.Current,
                AllocationDirectionalBoundedCalibrationPlan.OperatingSettings,
                cancellationToken).ConfigureAwait(false);
        return new(
            pairId,
            entry.Scenario,
            entry.ComparisonAxis,
            baseline,
            current);
    }
}

[Collection(ScenarioEvaluationLiveGroup.Name)]
public sealed class AllocationDirectionalBoundedCalibrationLiveTests
{
    [EnvironmentRequiredFact(
        AllocationDirectionalBoundedCalibrationEnvironmentVariables.FixturePath,
        "The one-time bounded allocation calibration requires an explicit output path.",
        Timeout = 1_800_000)]
    [Trait("Category", "ScenarioEvaluationLive")]
    public async Task BoundedLowAndPeriodicCalibration_WritesPassingFixtureOnce()
    {
        var fixturePath = Environment.GetEnvironmentVariable(
            AllocationDirectionalBoundedCalibrationEnvironmentVariables.FixturePath)
            ?? throw new InvalidOperationException(
                "The bounded calibration fixture path was not configured.");
        var reportPath = Path.ChangeExtension(fixturePath, ".report.json");
        var startedPath = Path.ChangeExtension(fixturePath, ".started.json");
        File.Exists(fixturePath).Should().BeFalse(
            "calibration evidence must not overwrite an earlier run");
        File.Exists(reportPath).Should().BeFalse(
            "calibration reports must not overwrite an earlier run");
        File.Exists(startedPath).Should().BeFalse(
            "a start marker proves an earlier calibration attempt already began");
        WriteNew(
            startedPath,
            JsonSerializer.Serialize(
                new
                {
                    AllocationDirectionalBoundedCalibrationPlan.CalibrationId,
                    AllocationDirectionalBoundedCalibrationPlan.CalibrationVersion,
                    startedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
                AllocationDirectionalJson.Options));

        var fixture =
            await AllocationDirectionalBoundedCalibrationLive.CaptureAsync(
                CancellationToken.None);
        var report =
            AllocationDirectionalBoundedCalibrationSelector.Evaluate(fixture);
        WriteNew(
            fixturePath,
            AllocationDirectionalBoundedCalibrationSelector.SerializeFixture(
                fixture));
        WriteNew(
            reportPath,
            AllocationDirectionalBoundedCalibrationSelector.SerializeReport(
                report));

        report.Passed.Should().BeTrue();
    }

    private static void WriteNew(string path, string content)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new DirectoryNotFoundException());
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
        writer.WriteLine();
    }
}
