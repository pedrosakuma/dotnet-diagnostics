using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

internal sealed record PrevalidationStartReceipt(string Schema, string SuiteId, string ManifestSha256,
    string AuthorizationSha256, string HistoricalReportSha256, long StartedTimestamp,
    MonitoredProcessIdentity Coordinator);

internal sealed record PrevalidationAttemptReceipt(string Schema, string ProbeId, int Ordinal,
    int Attempt, string ManifestSha256, long StartedTimestamp, long DeadlineTimestamp);

internal sealed record PrevalidationHarnessRequest(string Schema, string RepositoryRoot, string ManifestPath,
    string ManifestSha256, int Ordinal, MonitoredProcessIdentity Coordinator);

internal sealed record PrevalidationCoverage(string Schema, int Ordinal, string ProbeId, string Outcome,
    string? FailureCode, bool MonitoringComplete, int DeclaredFixtureSlots, int? ObservedFixtureFiles,
    int? MaximumContextIdentities, long? MaximumSuiteBytes, long? SourceOffered,
    long? SourceCommitted, string? SourceCoverage, IReadOnlyList<string> RequiredBoundaries,
    bool CampaignAdmissionGranted = false)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SampledLossMeasurement? SampledLoss { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SampledLossPopulation? ObservationPopulation => SampledLossPopulation.From(SampledLoss);
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SampledAdmissible { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureStage { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrevalidationSecondaryFailures? SecondaryFailures { get; init; }
}

internal sealed record PrevalidationReport(string Schema, string Scope, string SuiteId, string ManifestSha256,
    string HistoricalReportSha256, string Status, int DerivedSuiteIdentityBound,
    IReadOnlyList<PrevalidationCoverage> Probes, string Limitation, bool CampaignAdmissionGranted = false)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureCode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureStage { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrevalidationSecondaryFailures? SecondaryFailures { get; init; }
}

internal sealed record PrevalidationSecondaryFailures(string FirstCode, string FirstStage, int Count)
{
    internal static PrevalidationSecondaryFailures Add(PrevalidationSecondaryFailures? previous, string code, string stage)
        => previous is null ? new(PrevalidationFailureCodes.Normalize(code), stage, 1)
            : previous with { Count = previous.Count == int.MaxValue ? int.MaxValue : previous.Count + 1 };
}

internal static class PrevalidationFailureCodes
{
    internal static string Normalize(string code)
        => MonitoredSweepSummaryEncoding.IsBoundedToken(code, 64) ? code : "PrevalidationErrorCodeEncodingLimit";

    internal static void Validate(string? primary, PrevalidationSecondaryFailures? secondary)
        => PrevalidationProtocol.Require((primary is null
                || MonitoredSweepSummaryEncoding.IsBoundedToken(primary, 64))
            && (secondary is null || primary is not null && secondary.Count > 0
                && MonitoredSweepSummaryEncoding.IsBoundedToken(secondary.FirstCode, 64)
                && IsStage(secondary.FirstStage)),
            "PrevalidationFailureEvidenceInvalid");

    internal static bool IsStage(string? stage) => stage is "admission" or "admission-dispose"
        or "entry" or "fixture-preparation" or "harness" or "entry-cleanup" or "monitor-dispose"
        or "evidence-finalization" or "suite-finalization" or "suite-dispose" or "suite-freeze"
        or "worker" or "coverage" or "suite-stop";

    internal static void ValidateStage(string? code, string? stage)
        => PrevalidationProtocol.Require(code is null ? stage is null : IsStage(stage),
            "PrevalidationFailureStageInvalid");
}

internal sealed record PrevalidationSeal(string Schema, string SuiteId, string ManifestSha256,
    string ReportSha256, IReadOnlyList<MonitoredSealedArtifact> Artifacts);

internal static class PrevalidationExecutor
{
    internal static async Task<int> RunAsync(string repositoryRoot, string manifestPath,
        CancellationToken cancellationToken)
    {
        var suiteStarted = Stopwatch.GetTimestamp();
        var suiteWatch = Stopwatch.StartNew();
        using var suiteDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        suiteDeadline.CancelAfter(TimeSpan.FromSeconds(1_200));
        var validated = await PrevalidationAdmission.ValidateAsync(repositoryRoot, manifestPath, suiteDeadline.Token)
            .ConfigureAwait(false);
        var manifest = validated.Manifest;
        PrevalidationProtocol.Require(!Directory.EnumerateFileSystemEntries(manifest.PrivateRoot).Any(),
            "PrevalidationRootAlreadyUsed");
        using var self = Process.GetCurrentProcess();
        var coordinator = MonitoredProcessIdentity.Capture(self, MonitoredProcessRole.Harness);
        WriteImmutable(Path.Combine(manifest.PrivateRoot, "suite-start.json"),
            new PrevalidationStartReceipt("durable-prevalidation-start/1", manifest.SuiteId,
                validated.ManifestSha256, validated.AuthorizationSha256, manifest.HistoricalReport.Sha256,
                suiteStarted, coordinator));
        MonitoredStorageMonitor? admissionMonitor = null;
        PrevalidationReport? admissionFailure = null;
        try
        {
            using var admissionDeadline = CancellationTokenSource.CreateLinkedTokenSource(suiteDeadline.Token);
            var remainingAdmission = TimeSpan.FromSeconds(120) - suiteWatch.Elapsed;
            PrevalidationProtocol.Require(remainingAdmission > TimeSpan.Zero, "PrevalidationAdmissionDeadline");
            admissionDeadline.CancelAfter(remainingAdmission);
            var monitor = admissionMonitor = new MonitoredStorageMonitor(validated.Attribution, validated.Encoding,
                Path.Combine(manifest.PrivateRoot, "admission-monitor.jsonl"),
                new BoundedOutputBudget(1_048_576, 1_024, 64, 64), 571,
                prevalidationScope: new PrevalidationObservationScope([], coordinator),
                sampledLoss: manifest.SampledLoss is not null,
                observedUnlinked: DescriptorObservationPolicy.IsObservedUnlinked(manifest.SampledLoss),
                unifiedActive: DescriptorObservationPolicy.IsUnifiedActive(manifest.SampledLoss));
            monitor.AddProcess(coordinator);
            RequireSweep(await monitor.ObserveBoundaryAsync("suite-coordinator-admission", true, admissionDeadline.Token)
                .ConfigureAwait(false));
            _ = monitor.StartAsync();
            PrevalidationLayout.CopyControlInputs(validated, admissionDeadline.Token);
            RequireSweep(await monitor.ObserveBoundaryAsync("suite-control-inputs-pinned", true, admissionDeadline.Token)
                .ConfigureAwait(false));
            await monitor.StopAsync().ConfigureAwait(false);
            RequireMonitor(monitor);
            PrevalidationProtocol.Require(suiteWatch.Elapsed < TimeSpan.FromSeconds(120),
                "PrevalidationAdmissionDeadline");
        }
        catch (Exception exception) when (IsReportable(exception))
        {
            admissionFailure = RecordFailure(
                new PrevalidationReport(PrevalidationProtocol.ReportSchema, PrevalidationProtocol.Scope,
                    manifest.SuiteId, validated.ManifestSha256, manifest.HistoricalReport.Sha256,
                    "partial-unsealed:Admission", PrevalidationLayout.DeriveSuiteIdentityBound(),
                    manifest.Probes.Select(NotRun).ToArray(), "Admission failed before the first probe."),
                admissionMonitor?.TerminalAlarm ?? Code(exception), "admission");
            if (admissionMonitor?.TerminalAlarm is { } cause && cause != Code(exception))
            {
                admissionFailure = RecordFailure(admissionFailure, Code(exception), "admission");
            }
        }
        finally
        {
            if (admissionMonitor is not null)
            {
                try { await admissionMonitor.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (IsReportable(exception))
                {
                    admissionFailure = RecordFailure(admissionFailure ?? new PrevalidationReport(
                        PrevalidationProtocol.ReportSchema, PrevalidationProtocol.Scope, manifest.SuiteId,
                        validated.ManifestSha256, manifest.HistoricalReport.Sha256, "partial-unsealed:Admission",
                        PrevalidationLayout.DeriveSuiteIdentityBound(), manifest.Probes.Select(NotRun).ToArray(),
                        "Admission writer finalization failed before the first probe."), Code(exception), "admission-dispose");
                }
            }
        }
        if (admissionFailure is not null)
        {
            WriteImmutable(Path.Combine(manifest.PrivateRoot, "partial.json"), admissionFailure);
            return 3;
        }
        MonitoredFile.MakeReadOnly(Path.Combine(manifest.PrivateRoot, "admission-monitor.jsonl"));
        var outcomes = new List<PrevalidationCoverage>(8);
        var stopped = false;
        var partialRequired = false;
        foreach (var probe in manifest.Probes)
        {
            if (stopped || suiteDeadline.IsCancellationRequested || suiteWatch.Elapsed.TotalSeconds >= 1_080)
            {
                outcomes.Add(NotRun(probe));
                continue;
            }
            var started = Stopwatch.GetTimestamp();
            var deadlineTimestamp = checked(started + 120 * Stopwatch.Frequency);
            using var entryDeadline = CancellationTokenSource.CreateLinkedTokenSource(suiteDeadline.Token);
            // Five seconds of the same 120-second entry are reserved for owned cleanup.
            entryDeadline.CancelAfter(TimeSpan.FromSeconds(115));
            PrevalidationCoverage coverage;
            MonitoredStorageMonitor? monitor = null;
            var ownership = new PrevalidationOwnedProcessLedger();
            var contextRoot = PrevalidationLayout.ContextRoot(manifest, probe);
            var entryStage = "fixture-preparation";
            try
            {
                PrevalidationLayout.CreateDirectories(manifest, probe);
                var attempt = new PrevalidationAttemptReceipt("durable-prevalidation-attempt/1", probe.Id,
                    probe.Ordinal, 1, validated.ManifestSha256, started, deadlineTimestamp);
                WriteImmutable(Path.Combine(PrevalidationLayout.ExecutionRoot(manifest, probe), "attempt-start.json"), attempt);
                var context = PrevalidationLayout.Context(validated, probe, coordinator);
                var budget = new BoundedOutputBudget(PrevalidationMonitorControl.ReservedSummaryBytes, 2_048, 64, 64);
                monitor = new MonitoredStorageMonitor(context.Attribution, validated.Encoding,
                    Path.Combine(contextRoot, "coordinator-monitor.jsonl"), budget, 571,
                    includeIdentityEvidence: probe.Ordinal == 8,
                    prevalidationScope: context.Prevalidation, sampledLoss: context.UsesSampledLoss,
                    observedUnlinked: DescriptorObservationPolicy.IsObservedUnlinked(context.Manifest.SampledLoss),
                    unifiedActive: DescriptorObservationPolicy.IsUnifiedActive(context.Manifest.SampledLoss),
                    activeOwnedRoots: DescriptorObservationPolicy.IsUnifiedActive(context.Manifest.SampledLoss)
                        ? [context.Manifest.WorkspaceRoot] : null,
                    activeOwnedFiles: DescriptorObservationPolicy.IsUnifiedActive(context.Manifest.SampledLoss)
                        ? UnifiedActiveProtocol.MutableEntryStreams(contextRoot,
                            PrevalidationLayout.ExecutionRoot(manifest, probe)) : null);
                monitor.AddProcess(coordinator);
                monitor.SetStage("fixture-preparation", activeStorageStage: true);
                _ = monitor.StartAsync();
                PrevalidationLayout.CreateHistoryFixtures(
                    PrevalidationLayout.HistoryRoot(manifest, probe), probe.FixtureSlots, entryDeadline.Token);
                RequireSweep(await monitor.ObserveBoundaryAsync("fixtures-complete", true, entryDeadline.Token)
                    .ConfigureAwait(false));
                RequireMonitor(monitor);
                entryStage = "harness";
                var requestPath = Path.Combine(contextRoot, "harness-request.json");
                WriteImmutable(requestPath, new PrevalidationHarnessRequest("durable-prevalidation-harness/1",
                    validated.RepositoryRoot, validated.ManifestPath, validated.ManifestSha256, probe.Ordinal, coordinator));
                await RunHarnessProcessAsync(validated, probe, requestPath, monitor, ownership, entryDeadline.Token)
                    .ConfigureAwait(false);
                RequireSweep(await monitor.ObserveBoundaryAsync("harness-exit-quiescent", false, entryDeadline.Token)
                    .ConfigureAwait(false));
                RequireMonitor(monitor);
                coverage = PrevalidationProtocol.Read<PrevalidationCoverage>(Path.Combine(contextRoot, "harness-result.json"));
                PrevalidationProtocol.Require(coverage.Ordinal == probe.Ordinal && coverage.ProbeId == probe.Id
                    && coverage.Schema == PrevalidationProtocol.CoverageSchema && !coverage.CampaignAdmissionGranted,
                    "PrevalidationHarnessResultMismatch");
                PrevalidationFailureCodes.Validate(coverage.FailureCode, coverage.SecondaryFailures);
                PrevalidationFailureCodes.ValidateStage(coverage.FailureCode, coverage.FailureStage);
                coverage = coverage with
                {
                    MaximumSuiteBytes = Math.Max(coverage.MaximumSuiteBytes ?? 0, monitor.MaximumObservedSweepBytes),
                    MaximumContextIdentities = Math.Max(coverage.MaximumContextIdentities ?? 0,
                        monitor.MaximumCurrentContextIdentities),
                };
                PrevalidationProtocol.Require(Stopwatch.GetTimestamp() <= deadlineTimestamp, "PrevalidationEntryDeadline");
            }
            catch (Exception exception) when (IsReportable(exception))
            {
                coverage = Failed(probe, monitor?.TerminalAlarm ?? Code(exception), entryStage);
                if (monitor?.TerminalAlarm is { } cause && cause != Code(exception))
                {
                    coverage = RecordFailure(coverage, Code(exception), entryStage);
                }
            }
            var quiescent = false;
            try
            {
                using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(suiteDeadline.Token);
                var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadlineTimestamp);
                if (remaining > TimeSpan.Zero) cleanup.CancelAfter(remaining);
                else cleanup.Cancel();
                // Cleanup is attempted even if marking or monitoring already failed.
                var identities = ownership.Identities;
                monitor?.MarkOwnedCleanup(identities);
                var result = await PrevalidationOwnership.StopAsync(identities,
                    LinuxPrevalidationProcessOperations.Instance, cleanup.Token).ConfigureAwait(false);
                quiescent = result.Quiescent;
                WriteCleanupEvidence(Path.Combine(contextRoot, "cleanup.json"), result);
                PrevalidationProtocol.Require(quiescent, "PrevalidationOwnedProcessesUnconfirmed");
                PrevalidationProtocol.Require(result.Errors.Count == 0 && result.AdditionalErrorCount == 0,
                    "PrevalidationCleanupErrors");
                if (monitor is not null)
                {
                    monitor.RemoveConfirmedCleanup(identities);
                    RequireSweep(await monitor.ObserveBoundaryAsync("owned-cleanup-quiescent", false, cleanup.Token)
                        .ConfigureAwait(false));
                    await monitor.StopAsync().ConfigureAwait(false);
                    RequireMonitor(monitor);
                }
                PrevalidationProtocol.Require(Stopwatch.GetTimestamp() <= deadlineTimestamp, "PrevalidationCleanupDeadline");
            }
            catch (Exception exception) when (IsReportable(exception))
            {
                coverage = RecordFailure(coverage, Code(exception), "entry-cleanup");
                partialRequired = true;
            }
            finally
            {
                if (monitor is not null)
                {
                    try
                    {
                        await monitor.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception) when (IsReportable(exception))
                    {
                        coverage = RecordFailure(coverage, Code(exception), "monitor-dispose");
                        partialRequired = true;
                    }
                }
            }
            try
            {
                if (coverage.Outcome == "worker-completed-awaiting-entry-observation" && probe.Ordinal != 8)
                {
                    var raw = PrevalidationProtocol.Read<MonitoredCaseOutcome>(Path.Combine(
                        PrevalidationLayout.Settings(manifest, probe).OutputRoot, $"{probe.Ordinal:D2}-outcome.json"));
                    coverage = ProjectCoverage(probe, raw, manifest.PrivateRoot, requireEntryCompletion: true);
                }
                if (monitor is not null)
                {
                    coverage = coverage with
                    {
                        MaximumSuiteBytes = monitor.MaximumObservedSweepBytes,
                        MaximumContextIdentities = monitor.MaximumCurrentContextIdentities,
                        SampledLoss = monitor.LossTotals,
                        MonitoringComplete = coverage.MonitoringComplete && monitor.LossTotals?.HasLoss != true,
                        SampledAdmissible = manifest.SampledLoss is not null && !monitor.IsIncomplete
                            && monitor.TerminalAlarm is null && coverage.FailureCode is null,
                    };
                }
                WriteCoverage(manifest, probe, coverage);
                FreezeQuiescentContext(contextRoot, quiescent);
                if (Stopwatch.GetTimestamp() > deadlineTimestamp)
                {
                    coverage = RecordFailure(coverage, "PrevalidationContextFinalizationDeadline", "evidence-finalization");
                    partialRequired = true;
                }
            }
            catch (Exception exception) when (IsReportable(exception))
            {
                coverage = RecordFailure(coverage, Code(exception), "evidence-finalization");
                partialRequired = true;
            }
            outcomes.Add(coverage);
            stopped = !HasCoverage(coverage);
        }
        var report = new PrevalidationReport(PrevalidationProtocol.ReportSchema, PrevalidationProtocol.Scope,
            manifest.SuiteId, validated.ManifestSha256, manifest.HistoricalReport.Sha256,
            outcomes.All(HasCoverage) ? "coverage-observed-awaiting-independent-review" : "stopped-incomplete",
            PrevalidationLayout.DeriveSuiteIdentityBound(), outcomes,
            "Host/build-specific non-atomic observations only. Between-sweep native transients remain unknown; "
            + "F1/F2/F4/F5 still require component evidence and reviewed derivation. No comparison or backend approval.")
        {
            FailureCode = outcomes.FirstOrDefault(static item => item.Outcome == "incomplete")?.FailureCode,
            FailureStage = outcomes.FirstOrDefault(static item => item.Outcome == "incomplete")?.FailureStage,
        };
        using var finalDeadline = CancellationTokenSource.CreateLinkedTokenSource(suiteDeadline.Token);
        finalDeadline.CancelAfter(TimeSpan.FromSeconds(120));
        MonitoredStorageMonitor? finalMonitor = null;
        var finalizationFailed = false;
        try
        {
            PrevalidationProtocol.Require(!partialRequired, "PrevalidationOwnedCleanupOrFreezeIncomplete");
            var finalScope = new PrevalidationObservationScope(
                manifest.Probes.Select(probe => PrevalidationLayout.ContextRoot(manifest, probe)).ToArray(), coordinator);
            var monitor = finalMonitor = new MonitoredStorageMonitor(validated.Attribution, validated.Encoding,
                Path.Combine(manifest.PrivateRoot, "final-monitor.jsonl"), maximumEstablishedIdentities: 571,
                prevalidationScope: finalScope, sampledLoss: manifest.SampledLoss is not null,
                observedUnlinked: DescriptorObservationPolicy.IsObservedUnlinked(manifest.SampledLoss),
                unifiedActive: DescriptorObservationPolicy.IsUnifiedActive(manifest.SampledLoss));
            monitor.AddProcess(coordinator);
            RequireSweep(await monitor.ObserveBoundaryAsync("suite-final-enumeration", false, finalDeadline.Token)
                .ConfigureAwait(false));
            WriteImmutable(Path.Combine(manifest.PrivateRoot, "report.json"), report);
            await monitor.StopAsync().ConfigureAwait(false);
            var artifacts = Inventory(manifest.PrivateRoot, finalDeadline.Token);
            var seal = new PrevalidationSeal("durable-prevalidation-seal/1", manifest.SuiteId, validated.ManifestSha256,
                MonitoredFile.HashFile(Path.Combine(manifest.PrivateRoot, "report.json")), artifacts);
            var sealBytes = JsonSerializer.SerializeToUtf8Bytes(seal, PrevalidationProtocol.Json);
            PrevalidationProtocol.Require(sealBytes.Length <= 16_777_216
                && artifacts.Sum(static item => item.Length) <= 3_221_225_472 - sealBytes.Length,
                "PrevalidationSealBudget");
            PrevalidationProtocol.Require(suiteWatch.Elapsed < TimeSpan.FromSeconds(1_200),
                "PrevalidationSuiteDeadline");
            WriteImmutable(Path.Combine(manifest.PrivateRoot, "seal.json"), seal);
            PrevalidationProtocol.Require(!finalDeadline.IsCancellationRequested
                && suiteWatch.Elapsed < TimeSpan.FromSeconds(1_200), "PrevalidationFinalizationDeadline");
        }
        catch (Exception exception) when (IsReportable(exception))
        {
            report = RecordFailure(report, Code(exception), "suite-finalization") with { Status = "partial-unsealed:Finalization" };
            finalizationFailed = true;
        }
        finally
        {
            if (finalMonitor is not null)
            {
                try { await finalMonitor.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (IsReportable(exception))
                {
                    report = RecordFailure(report, Code(exception), "suite-dispose") with { Status = "partial-unsealed:Finalization" };
                    finalizationFailed = true;
                }
            }
        }
        if (finalizationFailed)
        {
            WriteImmutable(Path.Combine(manifest.PrivateRoot, "partial.json"), report);
            return 3;
        }
        try
        {
            FreezeContext(manifest.PrivateRoot);
            PrevalidationProtocol.Require(!finalDeadline.IsCancellationRequested, "PrevalidationFinalizationDeadline");
        }
        catch (Exception exception) when (IsReportable(exception))
        {
            MonitoredFile.MakePrivateDirectory(manifest.PrivateRoot);
            WriteImmutable(Path.Combine(manifest.PrivateRoot, "partial.json"),
                RecordFailure(report, Code(exception), "suite-freeze") with { Status = "partial-unsealed:Freeze" });
            return 3;
        }
        return outcomes.All(HasCoverage) ? 0 : 3;
    }

    internal static async Task<int> RunHarnessAsync(string requestPath)
    {
        PrevalidationProtocol.Require(await Console.In.ReadLineAsync().ConfigureAwait(false)
            == "prevalidation-start", "PrevalidationHarnessStartRelease");
        PrevalidationProtocol.EnsureImmutable(requestPath);
        var request = PrevalidationProtocol.Read<PrevalidationHarnessRequest>(requestPath);
        PrevalidationProtocol.Require(request.Schema == "durable-prevalidation-harness/1",
            "PrevalidationHarnessSchema");
        var validated = PrevalidationProtocol.Validate(request.RepositoryRoot, request.ManifestPath);
        PrevalidationProtocol.Require(request.ManifestSha256 == validated.ManifestSha256,
            "PrevalidationHarnessManifestMismatch");
        var probe = validated.Manifest.Probes.Single(item => item.Ordinal == request.Ordinal);
        var contextRoot = PrevalidationLayout.ContextRoot(validated.Manifest, probe);
        PrevalidationProtocol.Require(MonitoredPathRules.PathsEqual(requestPath,
            Path.Combine(contextRoot, "harness-request.json")), "PrevalidationHarnessPath");
        var attempt = ValidateReceipts(validated, probe, request.Coordinator);
        PrevalidationOwnership.RequireRegisteredSelf(Path.Combine(contextRoot, "ownership.jsonl"),
            MonitoredProcessRole.Harness);
        var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), attempt.DeadlineTimestamp)
            - TimeSpan.FromSeconds(5);
        PrevalidationProtocol.Require(remaining > TimeSpan.Zero, "PrevalidationEntryDeadline");
        using var deadline = new CancellationTokenSource(remaining);
        var context = PrevalidationLayout.Context(validated, probe, request.Coordinator);
        context = context with
        {
            Prevalidation = context.Prevalidation! with
            {
                Monitor = new PrevalidationMonitorClient(deadline.Token, context.UsesSampledLoss,
                    observedUnlinked: DescriptorObservationPolicy.IsObservedUnlinked(context.Manifest.SampledLoss),
                    unifiedActive: DescriptorObservationPolicy.IsUnifiedActive(context.Manifest.SampledLoss)),
            },
        };
        PrevalidationCoverage coverage;
        if (probe.Ordinal == 8)
        {
            coverage = await PrevalidationGeometry.RunAsync(validated, probe, context, deadline.Token).ConfigureAwait(false);
        }
        else
        {
            var outcome = await MonitoredCampaignRunner.RunExecutionAsync(context, probe.Execution,
                MonitoredToolWorkerLauncher.Instance, remaining, monitorHarnessProcess: true,
                deadline.Token).ConfigureAwait(false);
            // The authoritative log is still being written. Only the coordinator
            // projects it, after cleanup and closing the single writer.
            coverage = new(PrevalidationProtocol.CoverageSchema, probe.Ordinal, probe.Id,
                "worker-completed-awaiting-entry-observation",
                outcome.FailureCode is null ? null : PrevalidationFailureCodes.Normalize(outcome.FailureCode), false,
                probe.FixtureSlots, null, outcome.MaximumObservedIdentities, outcome.MaximumObservedSweepBytes,
                null, null, null, [])
            {
                FailureStage = outcome.FailureCode is null ? null : "worker",
            };
        }
        if (probe.Ordinal != 8)
        {
            WriteImmutable(Path.Combine(contextRoot, "harness-result.json"), coverage);
        }
        else
        {
            MonitoredFile.MakeReadOnly(Path.Combine(contextRoot, "harness-result.json"));
        }
        Console.WriteLine("prevalidation-harness-exit");
        await Console.Out.FlushAsync().ConfigureAwait(false);
        PrevalidationProtocol.Require(await Console.In.ReadLineAsync(deadline.Token).ConfigureAwait(false)
            == "prevalidation-release-exit", "PrevalidationHarnessExitRelease");
        return HasCoverage(coverage) || coverage.Outcome == "worker-completed-awaiting-entry-observation"
            && coverage.FailureCode is null ? 0 : 3;
    }

    internal static async Task RunHarnessProcessForComponentAsync(PrevalidationValidated validated, PrevalidationProbe probe,
        MonitoredStorageMonitor monitor, Func<ProcessStartInfo, Process> launcher, CancellationToken cancellationToken)
    {
        var ownership = new PrevalidationOwnedProcessLedger();
        try
        {
            await RunHarnessProcessAsync(validated, probe, "scripted-component-request", monitor,
                ownership, cancellationToken, launcher).ConfigureAwait(false);
        }
        finally
        {
            _ = await PrevalidationOwnership.StopAsync(ownership.Identities,
                LinuxPrevalidationProcessOperations.Instance, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task RunHarnessProcessAsync(PrevalidationValidated validated, PrevalidationProbe probe,
        string requestPath, MonitoredStorageMonitor monitor, PrevalidationOwnedProcessLedger ledger,
        CancellationToken cancellationToken,
        Func<ProcessStartInfo, Process>? componentLauncher = null)
    {
        var root = PrevalidationLayout.ContextRoot(validated.Manifest, probe);
        var start = new ProcessStartInfo(validated.Manifest.RuntimeBinary.Path)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(validated.Manifest.ToolBinary.Path)!,
        };
        foreach (var argument in new[] { validated.Manifest.ToolBinary.Path, "durable-capture-spike",
            "prevalidation-harness", "--descriptor", requestPath })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = (componentLauncher is null ? Process.Start(start) : componentLauncher(start))
            ?? throw PrevalidationProtocol.Error("PrevalidationHarnessLaunch", "Harness launch failed.");
        var identity = MonitoredProcessIdentity.Capture(process, MonitoredProcessRole.Harness);
        var ownership = Path.Combine(root, "ownership.jsonl");
        using var ioDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? stdout = null;
        Task? stderr = null;
        try
        {
            ledger.Register(ownership, identity);
            if (probe.Ordinal == 8)
            {
                monitor.RegisterGeometryFixtureOwner(identity);
            }
            monitor.AddProcess(identity);
            if (componentLauncher is null)
            {
                await process.StandardInput.WriteLineAsync("prevalidation-start").ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            stdout = CaptureHarnessExitAsync(process, identity, monitor,
                ownership, ledger, Path.Combine(root, "harness-stdout.log"), ioDeadline.Token);
            stderr = CaptureAsync(process.StandardError.BaseStream, Path.Combine(root, "harness-stderr.log"),
                65_536, ioDeadline.Token);
            var exit = process.WaitForExitAsync(cancellationToken);
            var pending = new List<Task> { exit, stdout, stderr };
            while (pending.Count != 0)
            {
                var terminal = await Task.WhenAny(pending.Append(monitor.TerminalIssue))
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                if (terminal == monitor.TerminalIssue)
                {
                    throw PrevalidationProtocol.Error(await monitor.TerminalIssue.ConfigureAwait(false),
                        "Coordinator monitoring stopped the suite.");
                }
                await terminal.ConfigureAwait(false);
                pending.Remove(terminal);
            }
            PrevalidationProtocol.Require(process.ExitCode is 0 or 3, "PrevalidationHarnessUnexpectedExit");
        }
        finally
        {
            ioDeadline.Cancel();
            if (stdout is not null && stderr is not null)
            {
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ioDeadline.IsCancellationRequested) { }
            }
        }
    }

    private static async Task CaptureHarnessExitAsync(Process process, MonitoredProcessIdentity identity,
        MonitoredStorageMonitor monitor, string ownership, PrevalidationOwnedProcessLedger ledger,
        string path, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var frames = 0;
        while (true)
        {
            var line = await PrevalidationMonitorControl.ReadLineAsync(process.StandardOutput, cancellationToken)
                .ConfigureAwait(false);
            if (line != "prevalidation-harness-exit")
            {
                PrevalidationProtocol.Require(++frames <= PrevalidationMonitorControl.MaximumFrames,
                    "PrevalidationControlRecordLimit");
            }
            var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (line == "prevalidation-harness-exit")
            {
                break;
            }
            var request = JsonSerializer.Deserialize<PrevalidationMonitorRequest>(line, PrevalidationProtocol.Json)
                ?? throw PrevalidationProtocol.Error("PrevalidationControlMissing", "Missing monitor request.");
            var reply = await PrevalidationMonitorControl.DispatchAsync(request, monitor, ownership,
                cancellationToken, ledger, releaseExit: async () =>
                {
                    await process.StandardInput.WriteLineAsync(PrevalidationMonitorControl.Encode(
                        PrevalidationMonitorControl.Snapshot(monitor))).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                })
                .ConfigureAwait(false);
            if (request.Operation == "exit") continue;
            await process.StandardInput.WriteLineAsync(PrevalidationMonitorControl.Encode(reply)).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        await monitor.ReleaseAndWaitForExitAsync(identity, async () =>
        {
            await process.StandardInput.WriteLineAsync("prevalidation-release-exit").ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        var extra = await process.StandardOutput.ReadAsync(new char[1].AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        PrevalidationProtocol.Require(extra == 0, "PrevalidationUnexpectedHarnessOutput");
    }

    private static async Task CaptureAsync(Stream source, string path, int cap, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var buffer = new byte[4_096];
        var total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            PrevalidationProtocol.Require(count <= cap - total, "PrevalidationHarnessOutputLimit");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            total += count;
        }
    }

    internal static PrevalidationAttemptReceipt ValidateReceipts(PrevalidationValidated validated,
        PrevalidationProbe probe, MonitoredProcessIdentity coordinator)
    {
        var startPath = Path.Combine(validated.Manifest.PrivateRoot, "suite-start.json");
        var attemptPath = Path.Combine(PrevalidationLayout.ExecutionRoot(validated.Manifest, probe), "attempt-start.json");
        PrevalidationProtocol.EnsureImmutable(startPath);
        PrevalidationProtocol.EnsureImmutable(attemptPath);
        var start = PrevalidationProtocol.Read<PrevalidationStartReceipt>(startPath);
        var attempt = PrevalidationProtocol.Read<PrevalidationAttemptReceipt>(attemptPath);
        PrevalidationProtocol.Require(start.Schema == "durable-prevalidation-start/1"
            && start.SuiteId == validated.Manifest.SuiteId && start.ManifestSha256 == validated.ManifestSha256
            && start.AuthorizationSha256 == validated.AuthorizationSha256
            && start.HistoricalReportSha256 == validated.Manifest.HistoricalReport.Sha256
            && coordinator.Role == MonitoredProcessRole.Harness
            && start.Coordinator == coordinator && LinuxProcessIdentity.Matches(coordinator),
            "PrevalidationStartReceiptMismatch");
        PrevalidationProtocol.Require(attempt.Schema == "durable-prevalidation-attempt/1"
            && attempt.ProbeId == probe.Id && attempt.Ordinal == probe.Ordinal && attempt.Attempt == 1
            && attempt.ManifestSha256 == validated.ManifestSha256
            && attempt.StartedTimestamp >= start.StartedTimestamp
            && attempt.DeadlineTimestamp - attempt.StartedTimestamp == 120 * Stopwatch.Frequency
            && Stopwatch.GetTimestamp() < attempt.DeadlineTimestamp
            && Stopwatch.GetElapsedTime(start.StartedTimestamp).TotalSeconds < 1_200,
            "PrevalidationAttemptReceiptMismatch");
        return attempt;
    }

    internal static PrevalidationCoverage ProjectCoverage(PrevalidationProbe probe,
        MonitoredCaseOutcome outcome, string root, bool requireEntryCompletion = false)
    {
        var boundaries = new HashSet<string>(StringComparer.Ordinal);
        SampledLossMeasurement? losses = outcome.SampledLoss is null ? null : SampledLossMeasurement.Empty(
            unifiedActive: outcome.SampledLoss.RootSampling is not null);
        var totalRecords = 0;
        foreach (var relative in outcome.MonitoringEvidenceFiles)
        {
            var path = MonitoredPathRules.ResolveContainedExistingFile(root, relative);
            using var stream = new MemoryStream(MonitoredFile.ReadBounded(path, 2_048 * 1_024));
            using var reader = new StreamReader(stream);
            var records = 0;
            while (reader.ReadLine() is { } line)
            {
                PrevalidationProtocol.Require(++records <= 2_048, "PrevalidationSummaryLimit");
                PrevalidationProtocol.Require(System.Text.Encoding.UTF8.GetByteCount(line) + 1 <= 1_024,
                    "PrevalidationSummaryWidth");
                var summary = JsonSerializer.Deserialize<MonitoredSweepSummary>(line, PrevalidationProtocol.Json)
                    ?? throw PrevalidationProtocol.Error("PrevalidationSummaryMissing", "A monitoring record was empty.");
                ValidateSummaryFailure(summary);
                PrevalidationProtocol.Require((summary.SampledLoss is null) == (losses is null),
                    "SampledLossCoveragePolicyMismatch");
                PrevalidationProtocol.Require((summary.SampledLoss?.ObservedUnlinked is not null)
                    == (outcome.SampledLoss?.ObservedUnlinked is not null), "ObservationPolicyMeasurementMismatch");
                if (summary.SampledLoss is { } measured)
                    losses = SampledLossMeasurement.Merge(losses!, measured);
                PrevalidationProtocol.Require(DescriptorObservationPolicy.Admissible(summary) && summary.Alarm is null,
                    summary.FirstErrorCode ?? summary.Alarm ?? "PrevalidationIncompleteObservation");
                if (summary.SampledLoss?.ObservedUnlinked is not null)
                    PrevalidationProtocol.Require(DescriptorObservationPolicy.SharedPackageBudgetFits(summary),
                        "ObservedUnlinkedSharedPackageLimit");
                PrevalidationProtocol.Require(summary.CurrentContextIdentities is > 0 and <= 571
                    && summary.CurrentContextRootedIdentities is >= 0 and <= 539
                    && summary.IdentityCount is > 0 and <= 4_096
                    && summary.IdentityCount >= summary.CurrentContextIdentities
                    && summary.DescriptorOnlyIdentityCount is >= 0 and <= 32
                    && summary.CurrentContextIdentities
                        == summary.CurrentContextRootedIdentities + summary.DescriptorOnlyIdentityCount
                    && summary.RetainedHistoryBytes is >= 0 and <= 2_147_483_648
                    && summary.ObservationKind is "boundary" or "periodic"
                    && summary.ObservedSweepBytes is >= 0 and <= 3_221_225_472,
                    "PrevalidationObservationScopeMissing");
                if (summary.ObservationKind == "boundary" || summary.Boundary is "live-warmup" or "live-eventpipe")
                {
                    boundaries.Add(summary.Boundary);
                }
            }
            totalRecords = checked(totalRecords + records);
        }
        var required = RequiredBoundaries(probe);
        var worker = outcome.Worker;
        var complete = (outcome.MonitoringComplete || outcome.SampledAdmissible) && outcome.MonitoringAlarm is null
            && outcome.FailureCode is null && outcome.Outcome == "pass" && worker is not null
            && outcome.Ordinal == probe.Ordinal && outcome.CaseId == probe.Workload
            && outcome.Candidate == probe.Candidate
            && outcome.MonitoringEvidenceFiles.Count == 1
            && totalRecords is > 0 and <= 2_048 && outcome.MonitorSummaryRecords is > 0
            && outcome.MonitorSummaryRecords <= totalRecords
            && required.All(boundaries.Contains)
            && (!requireEntryCompletion || boundaries.Contains("fixtures-complete")
                && boundaries.Contains("harness-exit-quiescent") && boundaries.Contains("owned-cleanup-quiescent"))
            && HasFrozenSourceCoverage(probe, worker)
            && (probe.Workload != "F3" || ValidateFaultSource(outcome, root, probe));
        return new(PrevalidationProtocol.CoverageSchema, probe.Ordinal, probe.Id,
            complete ? "coverage-observed" : "incomplete",
            complete ? null : PrevalidationFailureCodes.Normalize(
                outcome.FailureCode ?? outcome.MonitoringAlarm ?? "RequiredCoverageMissing"),
            complete && losses?.HasLoss != true, probe.FixtureSlots, probe.FixtureSlots * 4, outcome.MaximumObservedIdentities,
            outcome.MaximumObservedSweepBytes,
            probe.Candidate == "E" ? null : probe.Workload == "F3" ? complete ? 128 : null : worker?.Offered,
            probe.Candidate == "E" || probe.Workload == "F3" ? null : worker?.Committed,
            probe.Workload == "F3"
                ? "f3-source-barrier-and-recovery-descriptor;committed-source-count-unavailable"
                : worker?.SourceCoverage, required)
        {
            FailureStage = complete ? null : "coverage",
            SampledLoss = losses,
            SampledAdmissible = complete && losses is not null,
        };
    }

    internal static bool HasFrozenSourceCoverage(PrevalidationProbe probe, MonitoredWorkerResult worker)
        => probe.Workload switch
        {
            "O1" => worker.HasDurablePackage && worker.ScheduledSourceOffers == 50_000
                && worker.AttemptedSourceOffers is >= 47_500 and <= 50_000
                && worker.AchievedSourceOfferRatio is >= 0.95 and <= 1,
            "F3" => worker.HasDurablePackage && worker.RecoveryInvariantSatisfied,
            "L1" => worker.SourceKeys > 0
                && worker.TargetStartedAt is not null && worker.CounterSessionStartedAt is not null
                && worker.Requests is { Scheduled: 600 } && worker.CounterCollectionSeconds is >= 34 and <= 120
                && (probe.Candidate == "E"
                    ? worker.SourceTicks is null && !worker.HasDurablePackage
                        && worker.SourceCoverage == "shipping-eventpipe-counter-collector-first-latest-max;raw-tick-counts-unavailable"
                    : worker.HasDurablePackage && worker.SourceTicks > 0 && worker.Offered == worker.SourceTicks
                        && worker.SourceMalformedPayloads == 0 && worker.SourceAdmissionInvalid == 0
                        && worker.SourceRejectedNewKeys == 0 && worker.SourceAdmissionRejected == 0),
            _ => false,
        };

    private static bool ValidateFaultSource(MonitoredCaseOutcome outcome, string root, PrevalidationProbe probe)
    {
        var monitorPath = MonitoredPathRules.ResolveContainedExistingFile(root, outcome.MonitoringEvidenceFiles[0]);
        var directory = Path.Combine(Path.GetDirectoryName(monitorPath)!, "outputs",
            PrevalidationLayout.ExecutionName(probe));
        var recovery = PrevalidationProtocol.Read<MonitoredWorkerDescriptor>(
            Path.Combine(directory, "recovery-worker-descriptor.json"));
        var expectedOffered = Enumerable.Range(1, 128).Select(static value => (long)value);
        var expectedAcknowledged = Enumerable.Range(1, 64).Select(static value => (long)value);
        if (recovery.Schema != PrevalidationProtocol.WorkerSchema || recovery.Mode != MonitoredWorkerMode.Recover
            || recovery.Execution != probe.Execution || recovery.KnownOfferedSequences is null
            || !recovery.KnownOfferedSequences.SequenceEqual(expectedOffered)
            || recovery.ConfirmedAcknowledgements is null
            || !recovery.ConfirmedAcknowledgements.SequenceEqual(expectedAcknowledged))
        {
            return false;
        }
        var bytes = MonitoredFile.ReadBounded(Path.Combine(directory, "worker-stdout.jsonl"), 8_388_608);
        using var reader = new StreamReader(new MemoryStream(bytes));
        var controls = 0;
        var barriers = 0;
        while (reader.ReadLine() is { } line)
        {
            PrevalidationProtocol.Require(++controls <= 64, "PrevalidationControlRecordLimit");
            var item = JsonSerializer.Deserialize<MonitoredWorkerEvent>(line, PrevalidationProtocol.Json);
            if (item is { Type: "barrier", Barrier: "AfterCommitBeforeAcknowledgement",
                BatchOrdinal: 2, FirstSequence: 65, LastSequence: 128 })
            {
                barriers++;
            }
        }
        return barriers == 1;
    }

    internal static string[] RequiredBoundaries(PrevalidationProbe probe)
    {
        if (probe.Candidate == "E")
        {
            return ["live-baseline-no-durable-package", "live-warmup", "live-eventpipe",
                "pre-target-termination", "pre-worker-termination",
                "worker-exit-quiescent", "live-target-startup"];
        }
        var common = new[] { "after-manifest-and-seal-publication", "before-ordinary-reopen",
            "after-ordinary-reopen-and-first-query", "pre-worker-termination", "worker-exit-quiescent" };
        return probe.Workload == "F3"
            ? [.. common, "pre-kill-AfterCommitBeforeAcknowledgement",
                "post-kill-quiescent-root-inventory", "before-explicit-recovery", "after-explicit-recovery"]
            : probe.CaseClass == "live"
                ? [.. common, "live-before-package-create-and-admission", "live-after-drain",
                    "live-before-pre-seal-finalization", "live-after-pre-seal-finalization",
                    "live-warmup", "live-eventpipe", "pre-target-termination", "live-target-startup"]
                : [.. common, "before-package-create-and-admission", "after-drain",
                    "before-pre-seal-finalization", "after-pre-seal-finalization"];
    }

    internal static bool HasCoverage(PrevalidationCoverage outcome)
        => outcome.Outcome == "coverage-observed" && (outcome.MonitoringComplete
                || outcome.SampledAdmissible && outcome.SampledLoss is not null)
            && outcome.FailureCode is null && outcome.FailureStage is null
            && outcome.SecondaryFailures is null && !outcome.CampaignAdmissionGranted;

    private static PrevalidationCoverage NotRun(PrevalidationProbe probe)
        => Failed(probe, "SuiteStopped", "suite-stop") with { Outcome = "not-run", ObservedFixtureFiles = 0 };

    internal static PrevalidationCoverage Failed(PrevalidationProbe probe, string code, string stage = "entry")
        => new(PrevalidationProtocol.CoverageSchema, probe.Ordinal, probe.Id, "incomplete",
            PrevalidationFailureCodes.Normalize(code), false,
            probe.FixtureSlots, null, null, null, null, null, null, []) { FailureStage = stage };

    internal static PrevalidationCoverage RecordFailure(PrevalidationCoverage coverage, string code, string stage)
        => coverage with
        {
            Outcome = "incomplete",
            MonitoringComplete = false,
            SampledAdmissible = false,
            FailureCode = coverage.FailureCode ?? PrevalidationFailureCodes.Normalize(code),
            FailureStage = coverage.FailureCode is null ? stage : coverage.FailureStage,
            SecondaryFailures = coverage.FailureCode is null ? coverage.SecondaryFailures
                : PrevalidationSecondaryFailures.Add(coverage.SecondaryFailures, code, stage),
        };

    internal static PrevalidationReport RecordFailure(PrevalidationReport report, string code, string stage)
        => report with
        {
            FailureCode = report.FailureCode ?? PrevalidationFailureCodes.Normalize(code),
            FailureStage = report.FailureCode is null ? stage : report.FailureStage,
            SecondaryFailures = report.FailureCode is null ? report.SecondaryFailures
                : PrevalidationSecondaryFailures.Add(report.SecondaryFailures, code, stage),
        };

    internal static void RequireMonitor(MonitoredStorageMonitor monitor)
        => PrevalidationProtocol.Require(!monitor.IsIncomplete && monitor.TerminalAlarm is null,
            monitor.TerminalAlarm ?? "PrevalidationMonitoringIncomplete");

    internal static void ValidateSummaryFailure(MonitoredSweepSummary summary)
    {
        MonitoredSweepSummaryEncoding.ValidateDescriptorFailure(summary);
        PrevalidationFailureCodes.Validate(summary.FirstErrorCode, null);
        if (summary.SampledLoss is not null)
        {
            SampledLossProtocol.ValidateSummary(summary);
            return;
        }
        PrevalidationProtocol.Require(summary.ErrorCount >= 0 && summary.UnclassifiedCount >= 0
            && (summary.Complete
                ? summary.ErrorCount == 0 && summary.UnclassifiedCount == 0 && summary.FirstErrorCode is null
                : summary.FirstErrorCode is not null && (summary.ErrorCount > 0 || summary.UnclassifiedCount > 0)),
            "PrevalidationSummaryFailureMismatch");
    }

    internal static void RequireSweep(MonitoredSweepResult result)
        => PrevalidationProtocol.Require(DescriptorObservationPolicy.Admissible(result.Summary) && result.Summary.Alarm is null,
            result.Summary.FirstErrorCode ?? (result.Errors.Count != 0 ? PrevalidationFailureCodes.Normalize(result.Errors[0])
                : result.Summary.Alarm ?? "PrevalidationMonitoringIncomplete"));

    internal static void WriteImmutable<T>(string path, T value)
    {
        MonitoredFile.WriteNewJson(path, value);
        MonitoredFile.MakeReadOnly(path);
    }

    private static void WriteCleanupEvidence(string path, PrevalidationCleanupResult result)
    {
        using var output = new FileStream(path, File.Exists(path) ? FileMode.Truncate : FileMode.CreateNew,
            FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(output, result, PrevalidationProtocol.Json);
        output.Flush(flushToDisk: true);
    }

    private static void WriteCoverage(PrevalidationManifest manifest, PrevalidationProbe probe,
        PrevalidationCoverage coverage)
    {
        var path = Path.Combine(PrevalidationLayout.ContextRoot(manifest, probe), "coverage.json");
        if (probe.Ordinal == 8 && File.Exists(path))
        {
            PrevalidationProtocol.Require(new FileInfo(path).Length == 0, "PrevalidationGeometryResultSlotReused");
            using var output = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(output, coverage, PrevalidationProtocol.Json);
            output.Flush(flushToDisk: true);
            MonitoredFile.MakeReadOnly(path);
        }
        else
        {
            WriteImmutable(path, coverage);
        }
    }

    private static MonitoredSealedArtifact[] Inventory(string root, CancellationToken cancellationToken)
    {
        var files = new List<MonitoredSealedArtifact>();
        long bytes = 0;
        foreach (var path in MonitoredPathRules.EnumerateFilesRejectingLinks(root, 4_096, 4_096))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var native = LinuxStatxHandleMetadataObserver.Instance.Observe(handle);
            PrevalidationProtocol.Require(native.LinkCount == 1, "PrevalidationInventoryHardLink");
            bytes = checked(bytes + native.Length);
            PrevalidationProtocol.Require(bytes <= 3_221_225_472
                && files.Count + 2 <= PrevalidationLayout.DeriveSuiteIdentityBound(), "PrevalidationFinalInventoryLimit");
            files.Add(new(Path.GetRelativePath(root, path), native.Length, MonitoredFile.HashFile(path)));
        }
        return files.OrderBy(static item => item.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static void FreezeContext(string root)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw PrevalidationProtocol.Error("PrevalidationLinuxOnly", "Prevalidation context freezing requires Linux.");
        }
        foreach (var file in MonitoredPathRules.EnumerateFilesRejectingLinks(root, 4_096, 4_096))
        {
            MonitoredFile.MakeReadOnly(file);
        }
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderByDescending(static path => path.Length))
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    internal static void FreezeQuiescentContext(string root, bool quiescent)
    {
        PrevalidationProtocol.Require(quiescent, "PrevalidationMutableContextNotFrozen");
        FreezeContext(root);
    }

    private static bool IsReportable(Exception exception)
        => exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private static string Code(Exception exception)
        => PrevalidationFailureCodes.Normalize(
            exception is DurableStorageExperimentException storage ? storage.Code : exception.GetType().Name);
}

internal static class PrevalidationWorkerAdmission
{
    internal static IMonitoredExecutionManifest Validate(MonitoredWorkerDescriptor descriptor, string descriptorPath)
    {
        var validated = PrevalidationProtocol.Validate(descriptor.RepositoryRoot, descriptor.ManifestPath);
        var probe = validated.Manifest.Probes.Single(item => item.Ordinal == descriptor.Execution.Ordinal);
        PrevalidationProtocol.Require(probe.Ordinal < 8 && probe.Execution == descriptor.Execution
            && descriptor.ManifestSha256 == validated.ManifestSha256, "PrevalidationWorkerPlanMismatch");
        PrevalidationProtocol.Require(descriptor.CaptureId == PrevalidationLayout.OwnedIdentity(
                validated.Manifest.SuiteId, probe.Execution, descriptor.Mode, "capture")
            && descriptor.ArtifactId == PrevalidationLayout.OwnedIdentity(
                validated.Manifest.SuiteId, probe.Execution, descriptor.Mode, "artifact"),
            "PrevalidationWorkerNamespaceMismatch");
        var request = PrevalidationProtocol.Read<PrevalidationHarnessRequest>(
            Path.Combine(PrevalidationLayout.ContextRoot(validated.Manifest, probe), "harness-request.json"));
        PrevalidationProtocol.Require(request.Schema == "durable-prevalidation-harness/1"
            && request.ManifestSha256 == validated.ManifestSha256 && request.Ordinal == probe.Ordinal,
            "PrevalidationWorkerHarnessMismatch");
        PrevalidationExecutor.ValidateReceipts(validated, probe, request.Coordinator);
        PrevalidationOwnership.RequireRegisteredSelf(
            Path.Combine(PrevalidationLayout.ContextRoot(validated.Manifest, probe), "ownership.jsonl"),
            MonitoredProcessRole.Diagnostic);
        var context = PrevalidationLayout.Context(validated, probe, request.Coordinator);
        MonitoredWorkerExecutor.ValidateDescriptorPaths(descriptor, descriptorPath, context, probe.Execution);
        return context.Manifest;
    }
}
