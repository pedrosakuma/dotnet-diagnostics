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
        "DOTNET_DIAGNOSTICS_ALLOCATION_DIRECTIONAL_V2_FIXTURE_PATH";
    public const string ReportReevaluationFixturePath =
        "DOTNET_DIAGNOSTICS_ALLOCATION_DIRECTIONAL_V2_REEVALUATE_FIXTURE_PATH";
    public const string V3FixturePath =
        "DOTNET_DIAGNOSTICS_ALLOCATION_DIRECTIONAL_V3_FIXTURE_PATH";
    public const string V3ProtocolFreezePath =
        "DOTNET_DIAGNOSTICS_ALLOCATION_DIRECTIONAL_V3_PROTOCOL_FREEZE_PATH";
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
        => await CaptureCoreAsync(
            ledgerPath,
            AllocationDirectionalCapturePlan.Entries,
            AllocationDirectionalProtocol.ValidateFrozen,
            AllocationDirectionalHarness.ProtocolId,
            AllocationDirectionalHarness.ProtocolVersion,
            "v2-partial",
            "partial-attribution-v2",
            cancellationToken).ConfigureAwait(false);

    public static async Task<AllocationDirectionalFixture> CaptureV3Async(
        string ledgerPath,
        CancellationToken cancellationToken)
        => await CaptureCoreAsync(
            ledgerPath,
            AllocationDirectionalV3CapturePlan.Entries,
            AllocationDirectionalV3Protocol.ValidateFrozen,
            AllocationDirectionalV3Protocol.ProtocolId,
            AllocationDirectionalV3Protocol.ProtocolVersion,
            "v3-partial",
            "partial-attribution-v3",
            cancellationToken).ConfigureAwait(false);

    private static async Task<AllocationDirectionalFixture> CaptureCoreAsync(
        string ledgerPath,
        IReadOnlyList<AllocationDirectionalPlanEntry> entries,
        Func<AllocationDirectionalProtocolProvenance> validateFrozen,
        string protocolId,
        string protocolVersion,
        string partialPairPrefix,
        string partialWorkloadIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerPath);
        var protocol = validateFrozen();
        ValidateFrozenRuntimeResolution();
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
            foreach (var plan in entries)
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

            AddPartialAttributionControls(
                pairs,
                partialPairPrefix,
                partialWorkloadIdentity);
            AllocationDirectionalLedger.ValidateExactTerminalAccounting(
                ledgerPath,
                pairs.Where(pair => pair.Baseline.Origin == AllocationDirectionalOrigin.LiveMcp)
                    .SelectMany(pair => new[]
                    {
                        pair.Baseline.AttemptId,
                        pair.Current.AttemptId,
                    })
                    .ToArray());
            return new(
                AllocationDirectionalJson.CurrentSchemaVersion,
                protocolId,
                protocolVersion,
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
        AllocationDirectionalLedger.AppendStarted(
            ledgerPath,
            attemptId,
            DateTimeOffset.UtcNow);
        var observation = await CaptureObservationAsync(
            client,
            attemptId,
            workloadIdentity,
            workload,
            cancellationToken).ConfigureAwait(false);
        AllocationDirectionalLedger.AppendCompleted(
            ledgerPath,
            observation,
            DateTimeOffset.UtcNow);
        return observation;
    }

    internal static async Task<AllocationDirectionalObservation> CaptureObservationAsync(
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
        LiveSampleProcess? sample = null;
        AllocationDirectionalObservation? observation = null;
        var processIdentity = $"not-started-{attemptId}";
        var stage = AllocationDirectionalAttemptStage.ProcessStart;
        var operationId = "process.start-published";
        Exception? cleanupException = null;
        try
        {
            sample = await LiveSampleProcess.StartPublishedAsync(
                "BadCodeSample",
                new LiveSampleOptions
                {
                    HarvestListeningUrl = true,
                    WaitForHttpReady = false,
                    DiagnosticTimeout = TimeSpan.FromSeconds(20),
                    HttpTimeout = TimeSpan.FromSeconds(20),
                    Environment = new Dictionary<string, string>(
                        AllocationDirectionalCapturePlan.RuntimeEnvironment,
                        StringComparer.Ordinal),
                },
                attempt.Token).ConfigureAwait(false);
            processIdentity =
                $"pid-{sample.ProcessId}-started-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}";
            stage = AllocationDirectionalAttemptStage.ProcessReadiness;
            operationId = "process.http-readiness";
            var baseUrl = await sample.WaitForListeningUrlAsync(
                TimeSpan.FromSeconds(20),
                "/",
                attempt.Token).ConfigureAwait(false);
            using var http = new HttpClient
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(30),
            };
            if (workload.WarmupMilliseconds > 0)
            {
                stage = AllocationDirectionalAttemptStage.Warmup;
                operationId = "workload.warmup-http";
                using var warmup = await http.GetAsync(
                    WorkloadPath(
                        workload.Shape,
                        workload.WarmupMilliseconds,
                        workload.RetainedBytes),
                    attempt.Token).ConfigureAwait(false);
                warmup.EnsureSuccessStatusCode();
            }

            stage = AllocationDirectionalAttemptStage.McpTransport;
            operationId = "mcp.collect-sample-start";
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
            stage = AllocationDirectionalAttemptStage.Workload;
            operationId = "workload.capture-http";
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
            stage = AllocationDirectionalAttemptStage.McpTransport;
            operationId = "mcp.collect-sample-await";
            var collected = await collectTask.WaitAsync(
                TimeSpan.FromSeconds(
                    AllocationDirectionalCapturePlan.McpCompletionTimeoutSeconds),
                attempt.Token).ConfigureAwait(false);
            stage = AllocationDirectionalAttemptStage.McpResponse;
            operationId = "mcp.response-json";
            var raw = ResponseJson(collected);
            stage = AllocationDirectionalAttemptStage.Projection;
            operationId = "projection.data-allocation";
            var projected = AllocationDirectionalProjection.Project(
                attemptId,
                workloadIdentity,
                raw,
                collected.IsError == true);
            if (!projected.Succeeded)
            {
                observation = Failure(
                    attemptId,
                    workloadIdentity,
                    workload,
                    projected.Evidence,
                    aborted: false);
                goto Complete;
            }

            stage = AllocationDirectionalAttemptStage.Invariant;
            operationId = "invariant.workload-denominator";
            var operations = workloadResults.Sum(value => value.Operations);
            var requestedPayloads = workloadResults
                .Select(value => value.RequestedPayloadBytesPerOperation)
                .Distinct()
                .ToArray();
            var requestedPayload = requestedPayloads.Length == 0
                ? 0
                : requestedPayloads[0];
            if (requestedPayloads.Length > 1
                || requestedPayload != workload.ExpectedPayloadBytesPerOperation
                || workload.DriveWorkload && operations <= 0)
            {
                observation = Failure(
                    attemptId,
                    workloadIdentity,
                    workload,
                    AllocationDirectionalProjection.ExternalFailure(
                        attemptId,
                        workloadIdentity,
                        stage,
                        operationId,
                        AllocationDirectionalFailureCategory.DenominatorInvariant),
                    aborted: false);
                goto Complete;
            }

            operationId = "invariant.retained-value";
            var retainedValues = workloadResults.Select(value => value.RetainedBytes)
                .Distinct()
                .ToArray();
            var retained = retainedValues.Length == 0
                ? workload.RetainedBytes
                : retainedValues[0];
            if (retainedValues.Length > 1 || retained != workload.RetainedBytes)
            {
                observation = Failure(
                    attemptId,
                    workloadIdentity,
                    workload,
                    AllocationDirectionalProjection.ExternalFailure(
                        attemptId,
                        workloadIdentity,
                        stage,
                        operationId,
                        AllocationDirectionalFailureCategory.RetainedValueInvariant),
                    aborted: false);
                goto Complete;
            }

            var allocation = projected.Allocation!;
            observation = new(
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
                allocation.TotalBytes,
                allocation.TotalEvents,
                allocation.AttributionState,
                allocation.Types,
                allocation.Sites,
                null,
                [
                    "Collected once through the repository-built MCP server over real stdio.",
                    "The target process was started independently for this observation and terminated after capture.",
                    "The operation denominator came from the workload response, not from allocation ticks.",
                    "GCAllocationTick events provide fixed-stride sampled weights; ticks are sampler provenance and never object counts.",
                ])
            {
                AttemptEvidence = projected.Evidence,
                CaptureId = $"capture-{attemptId}",
                ProcessIdentity = $"process-{attemptId}",
                ComparisonRole = Role(attemptId),
                QuantityEvidence = QuantityEvidence(
                    attemptId,
                    operations,
                    available: operations > 0 && allocation.TotalBytes > 0),
                TypeAvailability = allocation.Types.Count > 0
                    ? "available"
                    : "unavailable",
                SiteAvailability = allocation.Sites.Count > 0
                    ? "available"
                    : "unavailable",
            };
            goto Complete;
        }
        catch (Exception exception)
        {
            var split = AllocationDirectionalAttemptClassifier.SplitOwnedStartupFailure(
                stage,
                exception);
            cleanupException ??= split.CleanupException;
            var category = AllocationDirectionalAttemptClassifier.ClassifyException(
                stage,
                split.PrimaryException,
                cancellationToken.IsCancellationRequested);
            observation = Failure(
                attemptId,
                workloadIdentity,
                workload,
                AllocationDirectionalProjection.ExternalFailure(
                    attemptId,
                    workloadIdentity,
                    stage,
                    operationId,
                    category,
                    split.PrimaryException),
                AllocationDirectionalAttemptClassifier.IsAborted(category));
            goto Complete;
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
                    await collectTask.WaitAsync(
                            TimeSpan.FromSeconds(5),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    if (AllocationDirectionalAttemptClassifier.IsCollectDrainFailure(
                            collectTask.IsCompleted,
                            exception))
                    {
                        cleanupException ??= exception;
                    }
                }
            }

            if (sample is not null)
            {
                try
                {
                    await sample.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupException ??= exception;
                }
            }

            if (observation?.AttemptEvidence is { } evidence)
            {
                var cleanup = AllocationDirectionalAttemptClassifier.FinalizeCleanup(
                    evidence,
                    observation.Outcome == AllocationDirectionalAttemptOutcome.Passed,
                    cleanupException);
                if (cleanup.ReplacePassedObservation)
                {
                    observation = Failure(
                        attemptId,
                        workloadIdentity,
                        workload,
                        cleanup.Evidence,
                        aborted: false);
                }
                else
                {
                    observation = observation with
                    {
                        AttemptEvidence = cleanup.Evidence,
                    };
                }
            }
        }

Complete:
        return (observation
            ?? Failure(
                attemptId,
                workloadIdentity,
                workload,
                AllocationDirectionalProjection.ExternalFailure(
                    attemptId,
                    workloadIdentity,
                    stage,
                    operationId,
                    AllocationDirectionalFailureCategory.UnexpectedHarnessException),
                aborted: false)) with
            {
                ProcessIdentity = processIdentity,
            };
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

    private static AllocationDirectionalObservation Failure(
        string attemptId,
        string workloadIdentity,
        AllocationDirectionalPlannedSide workload,
        AllocationDirectionalAttemptEvidence evidence,
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
            evidence.FailureCategory?.ToString(),
            ["The failed or aborted observation remains in its frozen pair denominator."])
        {
            AttemptEvidence = evidence,
            CaptureId = $"capture-{attemptId}",
            ProcessIdentity = $"process-{attemptId}",
            ComparisonRole = Role(attemptId),
            QuantityEvidence = QuantityEvidence(
                attemptId,
                denominatorValue: 0,
                available: false),
            TypeAvailability = "unavailable",
            SiteAvailability = "unavailable",
        };

    private static void AddPartialAttributionControls(
        List<AllocationDirectionalPair> pairs,
        string pairPrefix,
        string workloadIdentity)
    {
        var increases = pairs.Where(pair =>
                pair.Scenario == AllocationDirectionalScenario.TrueIncrease
                && pair.Current.Phase == "effect")
            .ToArray();
        if (increases.Length != AllocationDirectionalBounds.RequiredPairs)
        {
            throw new InvalidDataException(
                "Partial-attribution controls require exactly the three predeclared increase-effect pairs.");
        }
        for (var index = 0; index < AllocationDirectionalBounds.RequiredPairs; index++)
        {
            var increase = increases[index];
            pairs.Add(new(
                $"{pairPrefix}-{index + 1:D2}",
                AllocationDirectionalScenario.PartialAttribution,
                "allocation-volume",
                Project(
                    increase.Baseline,
                    $"{pairPrefix}-{index + 1:D2}-baseline",
                    workloadIdentity),
                Project(
                    increase.Current,
                    $"{pairPrefix}-{index + 1:D2}-current",
                    workloadIdentity,
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
            CaptureId = $"projection-{attemptId}",
            ProcessIdentity = source.ProcessIdentity,
            ComparisonRole = Role(attemptId),
            QuantityEvidence = source.QuantityEvidence is null
                ? null
                : source.QuantityEvidence with
                {
                    BaselineCurrentSemantics = Role(attemptId),
                },
            TypeAvailability = source.Types.Count > 0
                ? "available"
                : "unavailable",
            SiteAvailability = removeSites
                ? "unavailable-partial-attribution"
                : source.Sites.Count > 0
                    ? "available"
                    : "unavailable",
        };

    private static AllocationDirectionalQuantityEvidence QuantityEvidence(
        string attemptId,
        long denominatorValue,
        bool available)
        => new(
            "estimated sampled allocation bytes",
            "estimated sampled bytes",
            Role(attemptId),
            "completed workload operations",
            "operations",
            denominatorValue,
            "estimated sampled bytes divided by completed workload operations",
            LoadSensitive: false,
            available ? "available" : "unavailable",
            available
                ? null
                : "A positive operation denominator and positive sampled weight are required.",
            [
                "Baseline and current are interpreted only when workload identity and normalization semantics are compatible.",
                "The operation denominator is reported by the controlled workload and is independent of allocation ticks.",
                "Estimated sampled bytes are fixed-stride EventPipe sampling weights, not exact allocated bytes.",
                "Type and site inventories have independent availability and are never joined.",
            ]);

    private static string Role(string attemptId)
        => attemptId.EndsWith("-baseline", StringComparison.Ordinal)
            ? "baseline"
            : attemptId.EndsWith("-current", StringComparison.Ordinal)
                ? "current"
                : "controlled-projection";

    internal static async Task<McpClient> StartMcpAsync(
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

        var dll = System.IO.Path.Combine(
            directory.FullName,
            "src",
            "DotnetDiagnostics.Mcp",
            "bin",
            "Release",
            "net10.0",
            "DotnetDiagnostics.Mcp.dll");
        return File.Exists(dll)
            ? dll
            : throw new FileNotFoundException(
                "The real MCP subprocess must be built before the allocation directional study.",
                dll);
    }

    private static void ValidateFrozenRuntimeResolution()
    {
        var server = FindServerDll();
        var sample = SampleLocator.LocateSampleDll("BadCodeSample")
            ?? throw new FileNotFoundException(
                "The frozen BadCodeSample Release binary was not found.");
        var root = Directory.GetParent(
                System.IO.Path.GetDirectoryName(server)
                ?? throw new DirectoryNotFoundException())?.Parent?.Parent?.Parent?.Parent?.FullName
            ?? throw new DirectoryNotFoundException(
                "Could not resolve the repository root from the frozen MCP binary.");
        var expectedSample = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            root,
            "samples",
            "BadCodeSample",
            "bin",
            "Release",
            "net10.0",
            "BadCodeSample.dll"));
        if (!string.Equals(
                System.IO.Path.GetFullPath(sample),
                expectedSample,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The live allocation study must launch the frozen Release BadCodeSample binary.");
        }
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
        Timeout = 7_200_000)]
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

        fixture.Pairs.Should().HaveCount(AllocationDirectionalCapturePlan.TotalPairs);
        fixture.Pairs.SelectMany(pair => new[] { pair.Baseline, pair.Current })
            .Count(observation => observation.Origin == AllocationDirectionalOrigin.LiveMcp)
            .Should().Be(AllocationDirectionalCapturePlan.LiveMcpSides);
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

public sealed class AllocationDirectionalReportTests
{
    [EnvironmentRequiredFact(
        AllocationDirectionalEnvironmentVariables.ReportReevaluationFixturePath,
        "Report-only reevaluation requires the retained v2 fixture path.")]
    public void RetainedFixture_ReevaluatesWithoutLiveCapture()
    {
        var fixturePath = Environment.GetEnvironmentVariable(
            AllocationDirectionalEnvironmentVariables.ReportReevaluationFixturePath)
            ?? throw new InvalidOperationException(
                "The retained v2 fixture path was not configured.");
        var reportPath = System.IO.Path.ChangeExtension(
            fixturePath,
            ".report.json");
        File.Exists(reportPath).Should().BeFalse(
            "the execution-time report must be preserved separately before corrected reevaluation");
        var fixture = AllocationDirectionalJson.ReadFixture(fixturePath);
        var report = AllocationDirectionalHarness.Evaluate(fixture);
        using var stream = new FileStream(
            reportPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(AllocationDirectionalJson.SerializeReport(report));
    }

    [Collection(ScenarioEvaluationLiveGroup.Name)]
    public sealed class AllocationDirectionalV3LiveTests
    {
        [EnvironmentRequiredFact(
            AllocationDirectionalEnvironmentVariables.V3FixturePath,
            "The frozen one-time allocation directional v3 fixture capture requires an explicit output path.",
            Timeout = 7_200_000)]
        [Trait("Category", "ScenarioEvaluationLive")]
        public async Task FrozenRealMcpV3Capture_WritesEveryAttemptOnce()
        {
            var fixturePath = Environment.GetEnvironmentVariable(
                AllocationDirectionalEnvironmentVariables.V3FixturePath)
                ?? throw new InvalidOperationException(
                    "The frozen v3 fixture path was not configured.");
            fixturePath.Should().NotBeNullOrWhiteSpace();
            var ledgerPath = System.IO.Path.ChangeExtension(
                fixturePath,
                ".ledger.jsonl");
            var reportPath = System.IO.Path.ChangeExtension(
                fixturePath,
                ".report.json");
            File.Exists(fixturePath).Should().BeFalse(
                "the frozen v3 fixture must not overwrite or retry an earlier execution");
            File.Exists(ledgerPath).Should().BeFalse(
                "a v3 attempt ledger proves a prior partial or complete execution already started");
            File.Exists(reportPath).Should().BeFalse(
                "the frozen v3 report must not overwrite an earlier execution");

            var fixture = await AllocationDirectionalLive.CaptureV3Async(
                ledgerPath,
                CancellationToken.None);
            AllocationDirectionalJson.WriteFixture(fixturePath, fixture);

            fixture.Pairs.Should().HaveCount(
                AllocationDirectionalV3CapturePlan.TotalPairs);
            fixture.Pairs.SelectMany(pair => new[] { pair.Baseline, pair.Current })
                .Count(observation =>
                    observation.Origin == AllocationDirectionalOrigin.LiveMcp)
                .Should().Be(AllocationDirectionalV3CapturePlan.LiveMcpSides);
            fixture.Pairs.Should().OnlyContain(pair =>
                pair.Baseline.AttemptId != pair.Current.AttemptId);
            var report = AllocationDirectionalV3Harness.Evaluate(fixture);
            using var reportStream = new FileStream(
                reportPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            using var writer = new StreamWriter(reportStream);
            writer.Write(AllocationDirectionalJson.SerializeReport(report));
        }
    }
}
