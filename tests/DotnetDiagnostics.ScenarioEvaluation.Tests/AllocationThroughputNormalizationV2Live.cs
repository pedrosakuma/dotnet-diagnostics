using System.Runtime.InteropServices;
using System.Text;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class AllocationThroughputNormalizationV2EnvironmentVariables
{
    public const string FixturePath =
        "DOTNET_DIAGNOSTICS_ALLOCATION_THROUGHPUT_NORMALIZATION_V2_FIXTURE_PATH";
    public const string ProtocolFreezePath =
        "DOTNET_DIAGNOSTICS_ALLOCATION_THROUGHPUT_NORMALIZATION_V2_PROTOCOL_FREEZE_PATH";
}

internal static class AllocationThroughputNormalizationV2Live
{
    public static async Task<AllocationDirectionalFixture> CaptureAsync(
        string ledgerPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerPath);
        var protocol =
            AllocationThroughputNormalizationV2Protocol.ValidateFrozen();
        var artifactRoot = Path.Combine(
            Path.GetTempPath(),
            "dotnet-diagnostics-allocation-throughput-normalization-v2",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(artifactRoot);
        await using var client = await AllocationDirectionalLive.StartMcpAsync(
            artifactRoot,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var pairs = new List<AllocationDirectionalPair>();
            foreach (var entry in
                     AllocationThroughputNormalizationV2CapturePlan.Entries)
            {
                for (var repetition = 1;
                     repetition <=
                     AllocationThroughputNormalizationV2CapturePlan
                         .RequiredPairs;
                     repetition++)
                {
                    pairs.Add(await CapturePairAsync(
                        client,
                        ledgerPath,
                        entry,
                        repetition,
                        cancellationToken).ConfigureAwait(false));
                }
            }

            AllocationDirectionalLedger.ValidateExactTerminalAccounting(
                ledgerPath,
                pairs.SelectMany(pair => new[]
                    {
                        pair.Baseline.AttemptId,
                        pair.Current.AttemptId,
                    })
                    .ToArray());
            return new(
                AllocationDirectionalJson.CurrentSchemaVersion,
                AllocationThroughputNormalizationV2Protocol.ProtocolId,
                AllocationThroughputNormalizationV2Protocol.ProtocolVersion,
                protocol.ProtocolSha256,
                protocol.ProtocolSha256,
                [],
                protocol.FrozenAtUtc,
                protocol.SourceBaseCommit,
                $"{RuntimeInformation.OSDescription}; {Environment.Version}; {RuntimeInformation.ProcessArchitecture}",
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
        string ledgerPath,
        AllocationDirectionalPlanEntry entry,
        int repetition,
        CancellationToken cancellationToken)
    {
        var pairId = $"{entry.PairPrefix}-{repetition:D2}";
        var baseline = await CaptureSideAsync(
            client,
            ledgerPath,
            $"{pairId}-baseline",
            entry.Baseline,
            cancellationToken).ConfigureAwait(false);
        var current = await CaptureSideAsync(
            client,
            ledgerPath,
            $"{pairId}-current",
            entry.Current,
            cancellationToken).ConfigureAwait(false);
        return new(
            pairId,
            entry.Scenario,
            entry.ComparisonAxis,
            baseline,
            current);
    }

    private static async Task<AllocationDirectionalObservation> CaptureSideAsync(
        ModelContextProtocol.Client.McpClient client,
        string ledgerPath,
        string attemptId,
        AllocationDirectionalPlannedSide side,
        CancellationToken cancellationToken)
    {
        AllocationDirectionalLedger.AppendStarted(
            ledgerPath,
            attemptId,
            DateTimeOffset.UtcNow);
        var observation =
            await AllocationDirectionalLive.CaptureObservationAsync(
                client,
                attemptId,
                side.WorkloadIdentity,
                side,
                AllocationThroughputNormalizationV2CapturePlan.CaptureSettings,
                cancellationToken).ConfigureAwait(false);
        AllocationDirectionalLedger.AppendCompleted(
            ledgerPath,
            observation,
            DateTimeOffset.UtcNow);
        return observation;
    }
}

public sealed class AllocationThroughputNormalizationV2ProtocolFreezeTests
{
    [EnvironmentRequiredFact(
        AllocationThroughputNormalizationV2EnvironmentVariables.ProtocolFreezePath,
        "Freezing the reviewed throughput-normalization protocol requires an explicit new output path.")]
    public void ReviewedProtocolFreeze_WritesImmutableDocumentOnce()
    {
        var path = Environment.GetEnvironmentVariable(
            AllocationThroughputNormalizationV2EnvironmentVariables
                .ProtocolFreezePath)
            ?? throw new InvalidOperationException(
                "The throughput-normalization freeze path was not configured.");
        File.Exists(path).Should().BeFalse(
            "a frozen protocol must never be overwritten");
        var document = AllocationThroughputNormalizationV2ProtocolDocument.Build(
            "frozen",
            DateTimeOffset.UtcNow.ToString("O"),
            includeFrozenHashes: true);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(document.GetRawText());
        writer.WriteLine();
    }
}

[Collection(ScenarioEvaluationLiveGroup.Name)]
public sealed class AllocationThroughputNormalizationV2LiveTests
{
    [EnvironmentRequiredFact(
        AllocationThroughputNormalizationV2EnvironmentVariables.FixturePath,
        "The frozen one-time throughput-normalization capture requires an explicit output path.",
        Timeout = 3_600_000)]
    [Trait("Category", "ScenarioEvaluationLive")]
    public async Task FrozenRealMcpCapture_WritesEverySideOnce()
    {
        var fixturePath = Environment.GetEnvironmentVariable(
            AllocationThroughputNormalizationV2EnvironmentVariables.FixturePath)
            ?? throw new InvalidOperationException(
                "The throughput-normalization fixture path was not configured.");
        var ledgerPath = Path.ChangeExtension(fixturePath, ".ledger.jsonl");
        var reportPath = Path.ChangeExtension(fixturePath, ".report.json");
        File.Exists(fixturePath).Should().BeFalse(
            "the frozen fixture must not overwrite or retry an earlier execution");
        File.Exists(ledgerPath).Should().BeFalse(
            "a ledger proves a prior partial or complete execution already started");
        File.Exists(reportPath).Should().BeFalse(
            "the frozen report must not overwrite an earlier execution");

        var fixture =
            await AllocationThroughputNormalizationV2Live.CaptureAsync(
                ledgerPath,
                CancellationToken.None);
        AllocationDirectionalJson.WriteFixture(fixturePath, fixture);
        fixture.Pairs.Should().HaveCount(
            AllocationThroughputNormalizationV2CapturePlan.TotalPairs);
        fixture.Pairs.SelectMany(pair =>
                new[] { pair.Baseline, pair.Current })
            .Should().HaveCount(
                AllocationThroughputNormalizationV2CapturePlan.LiveMcpSides);
        fixture.Pairs.Should().OnlyContain(pair =>
            pair.Baseline.AttemptId != pair.Current.AttemptId);

        var report =
            AllocationThroughputNormalizationV2Harness.Evaluate(fixture);
        using var stream = new FileStream(
            reportPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(
            AllocationThroughputNormalizationV2Harness.SerializeReport(report));
    }
}
