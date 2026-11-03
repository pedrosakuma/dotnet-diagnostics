using System.Runtime.InteropServices;
using System.Text.Json;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal static class AllocationDirectionalEnvironmentVariables
{
    public const string FixturePath =
        "DOTNET_DIAGNOSTICS_ALLOCATION_DIRECTIONAL_FIXTURE_PATH";
}

public static class AllocationDirectionalLive
{
    private static readonly TimeSpan CaptureDuration = TimeSpan.FromSeconds(
        AllocationDirectionalCapturePlan.CaptureDurationSeconds);
    private static readonly TimeSpan ArmDelay = TimeSpan.FromMilliseconds(
        AllocationDirectionalCapturePlan.ArmDelayMilliseconds);

    public static async Task<AllocationDirectionalFixture> CaptureAsync(
        string ledgerPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerPath);
        var protocol = AllocationDirectionalProtocol.ValidateFrozen();
        var artifactRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "dotnet-diagnostics-allocation-directional",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(artifactRoot);
        await using var client = await StartMcpAsync(artifactRoot, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var pairs = new List<AllocationDirectionalPair>();
            foreach (var plan in AllocationDirectionalCapturePlan.Entries)
            {
                for (var repetition = 1;
                     repetition <= AllocationDirectionalBounds.RequiredPairs;
                     repetition++)
                {
                    pairs.Add(await CapturePairAsync(
                        client,
                        ledgerPath,
                        plan.Scenario,
                        $"{plan.PairPrefix}-{repetition:D2}",
                        plan.Baseline.WorkloadIdentity,
                        plan.Current.WorkloadIdentity,
                        plan.ComparisonAxis,
                        plan.Baseline,
                        plan.Current,
                        cancellationToken).ConfigureAwait(false));
                }
            }

            AddPartialAttributionControls(pairs);
            return new(
                AllocationDirectionalJson.CurrentSchemaVersion,
                AllocationDirectionalHarness.ProtocolId,
                AllocationDirectionalHarness.ProtocolVersion,
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
        McpClient client,
        string ledgerPath,
        AllocationDirectionalScenario scenario,
        string pairId,
        string baselineWorkloadIdentity,
        string currentWorkloadIdentity,
        string comparisonAxis,
        AllocationDirectionalPlannedSide baseline,
        AllocationDirectionalPlannedSide current,
        CancellationToken cancellationToken)
    {
        var baselineObservation = await CaptureWithLedgerAsync(
            client,
            ledgerPath,
            $"{pairId}-baseline",
            baselineWorkloadIdentity,
            baseline,
            cancellationToken).ConfigureAwait(false);
        var currentObservation = await CaptureWithLedgerAsync(
            client,
            ledgerPath,
            $"{pairId}-current",
            currentWorkloadIdentity,
            current,
            cancellationToken).ConfigureAwait(false);
        return new(
            pairId,
            scenario,
            comparisonAxis,
            baselineObservation,
            currentObservation);
    }

    private static async Task<AllocationDirectionalObservation> CaptureWithLedgerAsync(
        McpClient client,
        string ledgerPath,
        string attemptId,
        string workloadIdentity,
        AllocationDirectionalPlannedSide workload,
        CancellationToken cancellationToken)
    {
        AppendLedger(ledgerPath, attemptId, "started", null);
        var observation = await CaptureObservationAsync(
            client,
            attemptId,
            workloadIdentity,
            workload,
            cancellationToken).ConfigureAwait(false);
        AppendLedger(
            ledgerPath,
            attemptId,
            "completed",
            observation.Outcome.ToString());
        return observation;
    }

    private static async Task<AllocationDirectionalObservation> CaptureObservationAsync(
        McpClient client,
        string attemptId,
        string workloadIdentity,
        AllocationDirectionalPlannedSide workload,
        CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(
            AllocationDirectionalCapturePlan.AttemptTimeoutSeconds));
        Task<CallToolResult>? collectTask = null;
        try
        {
            await using var sample = await LiveSampleProcess.StartPublishedAsync(
                "BadCodeSample",
                new LiveSampleOptions
                {
                    HarvestListeningUrl = true,
                    WaitForHttpReady = true,
                    ReadinessPath = "/",
                    DiagnosticTimeout = TimeSpan.FromSeconds(20),
                    HttpTimeout = TimeSpan.FromSeconds(20),
                    Environment = new Dictionary<string, string>(
                        AllocationDirectionalCapturePlan.RuntimeEnvironment,
                        StringComparer.Ordinal),
                },
                attempt.Token).ConfigureAwait(false);
            using var http = new HttpClient
            {
                BaseAddress = new Uri(sample.BaseUrl),
                Timeout = TimeSpan.FromSeconds(30),
            };
            if (workload.WarmupMilliseconds > 0)
            {
                using var warmup = await http.GetAsync(
                    WorkloadPath(
                        workload.Shape,
                        workload.WarmupMilliseconds,
                        workload.RetainedBytes),
                    attempt.Token).ConfigureAwait(false);
                warmup.EnsureSuccessStatusCode();
            }

            collectTask = client.CallToolAsync(
                "collect_sample",
                new Dictionary<string, object?>
                {
                    ["kind"] = "allocation",
                    ["processId"] = sample.ProcessId,
                    ["durationSeconds"] = (int)CaptureDuration.TotalSeconds,
                    ["topN"] = AllocationDirectionalCapturePlan.TopN,
                },
                cancellationToken: attempt.Token).AsTask();
            await Task.Delay(ArmDelay, attempt.Token).ConfigureAwait(false);
            var workloadResults = workload.DriveWorkload
                ? await Task.WhenAll(
                    Enumerable.Range(0, workload.Workers)
                        .Select(_ => RunWorkloadAsync(
                            http,
                            workload.Shape,
                            workload.RetainedBytes,
                            attempt.Token)))
                    .ConfigureAwait(false)
                : [];
            var collected = await collectTask.WaitAsync(
                TimeSpan.FromSeconds(
                    AllocationDirectionalCapturePlan.McpCompletionTimeoutSeconds),
                attempt.Token).ConfigureAwait(false);
            var raw = ResponseJson(collected);
            using var response = JsonDocument.Parse(raw);
            if (collected.IsError == true
                || response.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                throw new InvalidOperationException("The real MCP allocation capture failed.");
            }

            var data = response.RootElement.GetProperty("data");
            var operations = workloadResults.Sum(value => value.Operations);
            var requestedPayload = workloadResults.Length == 0
                ? 0
                : workloadResults.Select(value => value.RequestedPayloadBytesPerOperation)
                    .Distinct()
                    .Single();
            if (requestedPayload != workload.ExpectedPayloadBytesPerOperation)
            {
                throw new InvalidDataException(
                    $"Workload '{workload.Shape}' returned {requestedPayload} requested payload bytes per operation; "
                    + $"the frozen plan requires {workload.ExpectedPayloadBytesPerOperation}.");
            }
            var retained = workloadResults.Length == 0
                ? workload.RetainedBytes
                : workloadResults.Select(value => value.RetainedBytes).Distinct().Single();
            return new(
                attemptId,
                AllocationDirectionalAttemptOutcome.Passed,
                AllocationDirectionalOrigin.LiveMcp,
                workloadIdentity,
                workload.Shape,
                workload.Phase,
                workload.Workers,
                CaptureDuration.TotalSeconds,
                operations,
                requestedPayload,
                retained,
                data.GetProperty("totalBytes").GetInt64(),
                data.GetProperty("totalEvents").GetInt64(),
                data.TryGetProperty("topBySite", out var sites)
                    && sites.ValueKind == JsonValueKind.Array
                    && sites.GetArrayLength() > 0
                        ? "complete"
                        : "no-site",
                Rows(data.GetProperty("topByBytes"), "typeName"),
                data.TryGetProperty("topBySite", out sites)
                    ? Rows(sites, "frame", nestedKey: "method")
                    : [],
                null,
                [
                    "Collected once through the repository-built MCP server over real stdio.",
                    "The target process was started independently for this observation and terminated after capture.",
                    "The operation denominator came from the workload response, not from allocation ticks.",
                ]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(attemptId, workloadIdentity, workload, "attempt_timeout", aborted: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(
                attemptId,
                workloadIdentity,
                workload,
                exception.GetType().Name,
                aborted: false);
        }
        finally
        {
            if (collectTask is { IsCompleted: false })
            {
                await attempt.CancelAsync().ConfigureAwait(false);
            }

            if (collectTask is not null)
            {
                try
                {
                    await collectTask.ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }
        }
    }

    private static async Task<WorkloadResult> RunWorkloadAsync(
        HttpClient http,
        string shape,
        int retainedBytes,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
            WorkloadPath(
                shape,
                AllocationDirectionalCapturePlan.WorkloadMilliseconds,
                retainedBytes),
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        return new(
            root.GetProperty("operations").GetInt64(),
            root.GetProperty("requestedPayloadBytesPerOperation").GetInt64(),
            root.GetProperty("retainedBytes").GetInt64());
    }

    private static string WorkloadPath(
        string shape,
        int milliseconds,
        int retainedBytes)
        => $"/allocation-shape?shape={Uri.EscapeDataString(shape)}&milliseconds={milliseconds}&retainedBytes={retainedBytes}";

    private static AllocationDirectionalRankedRow[] Rows(
        JsonElement rows,
        string keyProperty,
        string? nestedKey = null)
    {
        if (rows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return rows.EnumerateArray()
            .Take(AllocationDirectionalBounds.MaximumRankedRows)
            .Select(row =>
            {
                var key = nestedKey is null
                    ? row.GetProperty(keyProperty).GetString()
                    : row.GetProperty(keyProperty).GetProperty(nestedKey).GetString();
                return new AllocationDirectionalRankedRow(
                    key ?? "<unknown>",
                    row.GetProperty("totalBytes").GetInt64(),
                    row.GetProperty("eventCount").GetInt64());
            })
            .ToArray();
    }

    private static AllocationDirectionalObservation Failure(
        string attemptId,
        string workloadIdentity,
        AllocationDirectionalPlannedSide workload,
        string category,
        bool aborted)
        => new(
            attemptId,
            aborted
                ? AllocationDirectionalAttemptOutcome.Aborted
                : AllocationDirectionalAttemptOutcome.Failed,
            AllocationDirectionalOrigin.LiveMcp,
            workloadIdentity,
            workload.Shape,
            workload.Phase,
            workload.Workers,
            CaptureDuration.TotalSeconds,
            0,
            0,
            0,
            0,
            0,
            "unavailable",
            [],
            [],
            category,
            ["The failed or aborted observation remains in its frozen pair denominator."]);

    private static void AddPartialAttributionControls(
        List<AllocationDirectionalPair> pairs)
    {
        var increases = pairs.Where(pair =>
                pair.Scenario == AllocationDirectionalScenario.TrueIncrease)
            .ToArray();
        for (var index = 0; index < AllocationDirectionalBounds.RequiredPairs; index++)
        {
            var increase = increases[index];
            pairs.Add(new(
                $"partial-{index + 1:D2}",
                AllocationDirectionalScenario.PartialAttribution,
                "allocation-volume",
                Project(
                    increase.Baseline,
                    $"partial-{index + 1:D2}-baseline",
                    "partial-attribution-v1"),
                Project(
                    increase.Current,
                    $"partial-{index + 1:D2}-current",
                    "partial-attribution-v1",
                    attributionState: "partial",
                    removeSites: true)));
        }
    }

    private static AllocationDirectionalObservation Project(
        AllocationDirectionalObservation source,
        string attemptId,
        string workloadIdentity,
        string? attributionState = null,
        bool removeSites = false,
        bool zeroWeight = false)
        => source with
        {
            AttemptId = attemptId,
            Origin = AllocationDirectionalOrigin.ControlledProjection,
            WorkloadIdentity = workloadIdentity,
            Phase = source.Phase,
            RetainedBytes = source.RetainedBytes,
            SampledEstimatedBytes = zeroWeight
                ? 0
                : source.SampledEstimatedBytes,
            AttributionState = attributionState ?? source.AttributionState,
            Types = zeroWeight
                ? source.Types.Select(row => row with { EstimatedBytes = 0 }).ToArray()
                : source.Types,
            Sites = removeSites
                ? []
                : zeroWeight
                    ? source.Sites.Select(row => row with { EstimatedBytes = 0 }).ToArray()
                    : source.Sites,
            Provenance =
            [
                .. source.Provenance,
                "Controlled projection derived from one retained live MCP observation; it is not an additional independent live attempt.",
            ],
        };

    private static void AppendLedger(
        string ledgerPath,
        string attemptId,
        string state,
        string? outcome)
    {
        var entry = JsonSerializer.Serialize(new
        {
            attemptId,
            state,
            outcome,
            recordedAtUtc = DateTimeOffset.UtcNow,
        });
        File.AppendAllText(ledgerPath, entry + Environment.NewLine);
    }

    private static async Task<McpClient> StartMcpAsync(
        string artifactRoot,
        CancellationToken cancellationToken)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [FindServerDll(), "--stdio"],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["MCP_ARTIFACT_ROOT"] = artifactRoot,
                ["Orchestrator__Enabled"] = "false",
                ["AzureDiscovery__Enabled"] = "false",
            },
        });
        return await McpClient.CreateAsync(
            transport,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string FindServerDll()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(System.IO.Path.Combine(directory.FullName, "DotnetDiagnostics.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new FileNotFoundException(
                "Could not locate the repository root for the real MCP subprocess.");
        }

        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        var framework = new DirectoryInfo(AppContext.BaseDirectory).Name;
        var dll = System.IO.Path.Combine(
            directory.FullName,
            "src",
            "DotnetDiagnostics.Mcp",
            "bin",
            configuration,
            framework,
            "DotnetDiagnostics.Mcp.dll");
        return File.Exists(dll)
            ? dll
            : throw new FileNotFoundException(
                "The real MCP subprocess must be built before the allocation directional study.",
                dll);
    }

    private static string ResponseJson(CallToolResult result)
        => result.StructuredContent?.GetRawText()
            ?? result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text
            ?? throw new JsonException("The real MCP response contained no structured or text content.");

    private sealed record WorkloadResult(
        long Operations,
        long RequestedPayloadBytesPerOperation,
        long RetainedBytes);
}

[Collection(ScenarioEvaluationLiveGroup.Name)]
public sealed class AllocationDirectionalLiveTests
{
    [EnvironmentRequiredFact(
        AllocationDirectionalEnvironmentVariables.FixturePath,
        "The frozen one-time allocation directional fixture capture requires an explicit output path.",
        Timeout = 4_000_000)]
    [Trait("Category", "ScenarioEvaluationLive")]
    public async Task FrozenRealMcpCapture_WritesEveryAttemptOnce()
    {
        var fixturePath = Environment.GetEnvironmentVariable(
            AllocationDirectionalEnvironmentVariables.FixturePath)
            ?? throw new InvalidOperationException("The frozen fixture path was not configured.");
        fixturePath.Should().NotBeNullOrWhiteSpace();
        var ledgerPath = System.IO.Path.ChangeExtension(fixturePath, ".ledger.jsonl");
        var reportPath = System.IO.Path.ChangeExtension(fixturePath, ".report.json");
        File.Exists(fixturePath).Should().BeFalse(
            "the frozen fixture must not overwrite or retry an earlier execution");
        File.Exists(ledgerPath).Should().BeFalse(
            "an attempt ledger proves a prior partial or complete execution already started");
        File.Exists(reportPath).Should().BeFalse(
            "the frozen report must not overwrite an earlier execution");

        var fixture = await AllocationDirectionalLive.CaptureAsync(
            ledgerPath,
            CancellationToken.None);
        AllocationDirectionalJson.WriteFixture(fixturePath, fixture);

        fixture.Pairs.Should().HaveCount(39);
        fixture.Pairs.SelectMany(pair => new[] { pair.Baseline, pair.Current })
            .Count(observation => observation.Origin == AllocationDirectionalOrigin.LiveMcp)
            .Should().Be(72);
        fixture.Pairs.Should().OnlyContain(pair =>
            pair.Baseline.AttemptId != pair.Current.AttemptId);
        var report = AllocationDirectionalHarness.Evaluate(fixture);
        using var reportStream = new FileStream(
            reportPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var writer = new StreamWriter(reportStream);
        writer.Write(AllocationDirectionalJson.SerializeReport(report));
    }
}
