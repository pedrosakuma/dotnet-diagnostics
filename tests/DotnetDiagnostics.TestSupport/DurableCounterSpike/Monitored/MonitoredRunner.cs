using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DotnetDiagnostics.TestSupport;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

internal static class MonitoredRunnerGeometry
{
    internal const int MaximumRetainedPackages = 64;
    internal const int MaximumFilesPerPackage = 4;
    internal const int MaximumCommonExecutionEvidenceFiles = 7;
    internal const int RecoveryExecutionCount = 6;
    internal const int MaximumAdditionalRecoveryEvidenceFiles = 4;
    internal const int PlannedExecutions = 35;
    internal const int MaximumCampaignControlFiles = 2;
    internal const int MaximumReadinessArtifactsInsideEvidenceRoot = 4;
    internal const int MaximumActivePackageAndRecoveryFiles = 8;
    internal const int MaximumOwnedWritableDescriptorOnlyFiles = 32;
    internal const int MaximumBoundarySummariesPerExecution = 64;
    internal const int MaximumPeriodicSummariesPerExecution = 1_201;
    internal const int MaximumRequiredSummaryRecords =
        MaximumBoundarySummariesPerExecution + MaximumPeriodicSummariesPerExecution;
    internal const int MaximumWorkerControlRecordsPerExecution = 64;
    internal const int MaximumRootedIdentities =
        MaximumRetainedPackages * MaximumFilesPerPackage
        + PlannedExecutions * MaximumCommonExecutionEvidenceFiles
        + RecoveryExecutionCount * MaximumAdditionalRecoveryEvidenceFiles
        + MaximumCampaignControlFiles
        + MaximumReadinessArtifactsInsideEvidenceRoot
        + MaximumActivePackageAndRecoveryFiles;
    internal const int MaximumSimultaneousIdentities =
        MaximumRootedIdentities
        + MaximumOwnedWritableDescriptorOnlyFiles;
    internal const long MaximumMonitorSummaryBytes =
        2_048L * 1_024;
    internal const long MaximumCombinedSummaryControlBytes =
        MaximumMonitorSummaryBytes + MaximumWorkerControlRecordsPerExecution * 1_024L;
}

internal sealed record MonitoredCampaignStartReceipt(
    string Schema,
    string CampaignId,
    string ManifestSha256,
    string AuthorizationReceiptSha256,
    string ProtocolJsonSha256,
    DateTimeOffset StartedAt,
    int PlannedExecutions,
    int MaximumAttemptsPerExecution,
    MonitoredHostFacts AdmissionHostFacts);

internal sealed record MonitoredAttemptReceipt(
    string Schema,
    int Ordinal,
    string CaseId,
    string Candidate,
    int Attempt,
    DateTimeOffset StartedAt,
    string ManifestSha256);

internal sealed record MonitoredCaseOutcome(
    int Ordinal,
    string CaseId,
    string Candidate,
    string Outcome,
    string? FailureCode,
    string? FailureMessage,
    bool MonitoringComplete,
    string? MonitoringAlarm,
    int? MonitorSummaryRecords,
    long? MonitorSummaryBytes,
    int? MaximumObservedIdentities,
    long? MaximumObservedSweepBytes,
    IReadOnlyList<string> MonitoringEvidenceFiles,
    MonitoredWorkerResult? Worker)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SampledLossMeasurement? SampledLoss { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SampledLossPopulation? ObservationPopulation => SampledLossPopulation.From(SampledLoss);
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SampledAdmissible { get; init; }
}

internal sealed record MonitoredCampaignDecision(
    string Recommendation,
    string Scope,
    bool CompleteEvidence,
    IReadOnlyList<string> Reasons);

internal sealed record MonitoredSealedArtifact(
    string RelativePath,
    long Length,
    string Sha256);

internal sealed record MonitoredCampaignSeal(
    string Schema,
    string CampaignId,
    DateTimeOffset SealedAt,
    string ManifestSha256,
    int EnumeratedOutcomes,
    IReadOnlyList<string> OutcomeSha256,
    IReadOnlyList<MonitoredSealedArtifact> Artifacts,
    MonitoredCampaignDecision Decision)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SampledLossMeasurement? SampledLoss { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObservationPolicy { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SampledLossPopulation? ObservationPopulation => SampledLossPopulation.From(SampledLoss);
}

internal interface IMonitoredWorkerLauncher
{
    Process Start(IMonitoredExecutionManifest manifest, string descriptorPath);
}

internal sealed class MonitoredToolWorkerLauncher : IMonitoredWorkerLauncher
{
    internal static MonitoredToolWorkerLauncher Instance { get; } = new();

    public Process Start(IMonitoredExecutionManifest manifest, string descriptorPath)
        => MonitoredCampaignRunner.StartToolWorker(manifest, descriptorPath);
}

internal static class MonitoredCampaignRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    internal static async Task<int> RunAsync(
        string repositoryRoot,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var validated = MonitoredRunManifestValidator.Validate(
            repositoryRoot,
            manifestPath,
            requireAuthorization: true);
        var admissionHostFacts = Preflight(validated);
        PrepareCampaignDirectories(validated);
        var startReceiptPath = Path.Combine(validated.CampaignRoot, "campaign-start.json");
        MonitoredFile.WriteNewJson(
            startReceiptPath,
            new MonitoredCampaignStartReceipt(
                "durable-monitored-campaign-start/1",
                validated.Manifest.CampaignId,
                validated.ManifestSha256,
                validated.AuthorizationSha256,
                validated.Manifest.Plan.ProtocolJsonSha256,
                DateTimeOffset.UtcNow,
                35,
                1,
                admissionHostFacts));
        MonitoredFile.MakeReadOnly(startReceiptPath);

        var campaignWatch = Stopwatch.StartNew();
        var outcomes = new List<MonitoredCaseOutcome>(35);
        var stopCampaign = false;
        for (var index = 0; index < validated.Manifest.Plan.Executions.Count; index++)
        {
            var execution = validated.Manifest.Plan.Executions[index];
            if (stopCampaign
                || campaignWatch.Elapsed >= TimeSpan.FromSeconds(
                    validated.Manifest.Plan.MaximumCampaignSeconds))
            {
                outcomes.Add(WriteNotRunOutcome(validated, execution, stopCampaign
                    ? "CampaignStoppedAfterSafetyOrMonitoringFailure"
                    : "CampaignDeadlineExceeded"));
                continue;
            }

            MonitoredCaseOutcome outcome;
            try
            {
                var remainingCampaign = TimeSpan.FromSeconds(
                    validated.Manifest.Plan.MaximumCampaignSeconds) - campaignWatch.Elapsed;
                using var executionDeadline =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                executionDeadline.CancelAfter(
                    remainingCampaign < TimeSpan.FromSeconds(execution.MaximumSeconds)
                        ? remainingCampaign
                        : TimeSpan.FromSeconds(execution.MaximumSeconds));
                outcome = await RunExecutionAsync(
                    MonitoredExecutionContext.FromCampaign(validated),
                    execution,
                    MonitoredToolWorkerLauncher.Instance,
                    TimeSpan.FromSeconds(execution.MaximumSeconds),
                    monitorHarnessProcess: true,
                    cancellationToken: executionDeadline.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException
                and not StackOverflowException
                and not AccessViolationException)
            {
                var code = exception switch
                {
                    DurableStorageExperimentException storage => storage.Code,
                    _ => exception.GetType().Name,
                };
                outcome = WriteOutcome(
                    validated,
                    execution,
                    new MonitoredCaseOutcome(
                        execution.Ordinal,
                        execution.CaseId,
                        execution.Candidate,
                        "inconclusive-unknown",
                        code,
                        Bound(exception.Message, 1_024),
                        MonitoringComplete: false,
                        MonitoringAlarm: code,
                        MonitorSummaryRecords: null,
                        MonitorSummaryBytes: null,
                        MaximumObservedIdentities: null,
                        MaximumObservedSweepBytes: null,
                        MonitoringEvidenceFiles: ExistingMonitoringEvidenceFiles(
                            validated,
                            execution),
                        Worker: null));
            }
            outcomes.Add(outcome);
            stopCampaign = !(outcome.MonitoringComplete || outcome.SampledAdmissible)
                || outcome.MonitoringAlarm is not null
                || outcome.FailureCode is "ContainmentViolation"
                    or "OwnedProcessIdentityMismatch"
                    or "MonitorOutputLimit"
                    or "TrackedIdentityLimitExceeded";
        }

        if (outcomes.Count != 35)
        {
            throw Error("OutcomeEnumerationIncomplete", "Campaign sealing requires all 35 outcomes, including not-run entries.");
        }
        var decision = MonitoredDecisionEngine.Decide(outcomes,
            validated.Manifest.SampledLoss is null ? null : validated.Manifest);
        var outcomeHashes = outcomes
            .OrderBy(static outcome => outcome.Ordinal)
            .Select(outcome => MonitoredFile.HashFile(OutcomePath(validated, outcome.Ordinal)))
            .ToArray();
        var sealedArtifacts = EnumerateAndFreezeCampaignArtifacts(validated.CampaignRoot);
        var campaignSealPath = Path.Combine(validated.CampaignRoot, "campaign-seal.json");
        MonitoredFile.WriteNewJson(
            campaignSealPath,
            new MonitoredCampaignSeal(
                "durable-monitored-campaign-seal/1",
                validated.Manifest.CampaignId,
                DateTimeOffset.UtcNow,
                validated.ManifestSha256,
                outcomes.Count,
                outcomeHashes,
                sealedArtifacts,
                decision)
            {
                ObservationPolicy = validated.Manifest.SampledLoss?.Policy,
                SampledLoss = validated.Manifest.SampledLoss is null ? null
                    : outcomes.Where(static outcome => outcome.SampledLoss is not null)
                        .Aggregate(SampledLossMeasurement.Empty(
                            unifiedActive: DescriptorObservationPolicy.IsUnifiedActive(validated.Manifest.SampledLoss)),
                            static (sum, outcome) =>
                            SampledLossMeasurement.Merge(sum, outcome.SampledLoss!,
                                35 * SampledLossMeasurement.MaximumCandidatesPerEntry)),
            });
        MonitoredFile.MakeReadOnly(campaignSealPath);
        FreezeCampaignDirectories(validated.CampaignRoot);
        return decision.CompleteEvidence ? 0 : 3;
    }

    internal static Task<MonitoredCaseOutcome> RunExecutionForComponentAsync(
        MonitoredValidatedManifest validated,
        MonitoredExecutionSpec execution,
        IMonitoredWorkerLauncher launcher,
        TimeSpan workerDeadline,
        CancellationToken cancellationToken)
        => RunExecutionAsync(
            MonitoredExecutionContext.FromCampaign(validated),
            execution,
            launcher,
            workerDeadline,
            monitorHarnessProcess: false,
            cancellationToken: cancellationToken);

    internal static async Task<MonitoredCaseOutcome> RunExecutionAsync(
        MonitoredExecutionContext validated,
        MonitoredExecutionSpec execution,
        IMonitoredWorkerLauncher launcher,
        TimeSpan workerDeadline,
        bool monitorHarnessProcess,
        CancellationToken cancellationToken)
    {
        var executionName =
            $"{execution.Ordinal:D2}-{execution.Candidate.ToLowerInvariant()}-{execution.CaseId.ToLowerInvariant()}";
        var outputRoot = Path.Combine(validated.Manifest.OutputRoot, executionName);
        var workspaceRoot = Path.Combine(validated.Manifest.WorkspaceRoot, executionName);
        var historyRoot = Path.Combine(validated.Manifest.HistoryRoot, executionName);
        Directory.CreateDirectory(outputRoot);
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(historyRoot);
        SetPrivateDirectory(outputRoot);
        SetPrivateDirectory(workspaceRoot);
        SetPrivateDirectory(historyRoot);

        var attemptReceiptPath = Path.Combine(outputRoot, "attempt-start.json");
        if (validated.Prevalidation is null)
        {
            MonitoredFile.WriteNewJson(
                attemptReceiptPath,
                new MonitoredAttemptReceipt(
                    "durable-monitored-attempt-start/1",
                    execution.Ordinal,
                    execution.CaseId,
                    execution.Candidate,
                    Attempt: 1,
                    DateTimeOffset.UtcNow,
                    validated.ManifestSha256));
            MonitoredFile.MakeReadOnly(attemptReceiptPath);
        }

        var descriptor = CreateDescriptor(
            validated,
            execution,
            outputRoot,
            workspaceRoot,
            historyRoot,
            MonitoredWorkerMode.Execute,
            sourcePackageRoot: null,
            sourceCaptureId: null,
            sourceArtifactId: null,
            acknowledgements: null,
            offered: null);
        var descriptorPath = Path.Combine(outputRoot, "worker-descriptor.json");
        MonitoredFile.WriteNewJson(descriptorPath, descriptor);
        MonitoredFile.MakeReadOnly(descriptorPath);
        var executionEvidenceBudget = new BoundedOutputBudget(
            validated.Encoding.MaximumStdoutUtf8Bytes - (validated.Prevalidation is null ? 0
                : PrevalidationMonitorControl.ReservedSummaryBytes + PrevalidationMonitorControl.ReservedControlBytes),
            validated.Encoding.MaximumSummaryRecordsPerExecution,
            MonitoredRunnerGeometry.MaximumBoundarySummariesPerExecution,
            MonitoredRunnerGeometry.MaximumWorkerControlRecordsPerExecution);
        var stderrBudget = new BoundedOutputBudget(
            validated.Encoding.MaximumStderrUtf8Bytes - (validated.Prevalidation is null ? 0 : 65_536));

        var first = await RunOwnedWorkerAsync(
            validated,
            descriptor,
            descriptorPath,
            outputRoot,
            logPrefix: "worker",
            expectKillBarrier: execution.CaseId is "F2" or "F3" or "F4",
            executionEvidenceBudget,
            stderrBudget,
            launcher,
            workerDeadline,
            monitorHarnessProcess,
            cancellationToken).ConfigureAwait(false);

        OwnedWorkerRun final = first;
        if (first.KilledAtBarrier
            && first.MonitoringComplete
            && first.FinalSweepComplete
            && first.MonitoringAlarm is null)
        {
            var recoveryWorkspace = Path.Combine(workspaceRoot, "recovery-staging");
            var recoveryPackage = Path.Combine(historyRoot, "recovery");
            var recoveryDescriptor = CreateDescriptor(
                validated,
                execution,
                outputRoot,
                recoveryWorkspace,
                recoveryPackage,
                MonitoredWorkerMode.Recover,
                sourcePackageRoot: descriptor.PackageStagingRoot,
                sourceCaptureId: descriptor.CaptureId,
                sourceArtifactId: descriptor.ArtifactId,
                acknowledgements: first.Acknowledgements,
                offered: Enumerable.Range(1, execution.CaseId == "F4" ? 128 : 128)
                    .Select(static value => (long)value)
                    .ToArray());
            var recoveryDescriptorPath = Path.Combine(outputRoot, "recovery-worker-descriptor.json");
            MonitoredFile.WriteNewJson(recoveryDescriptorPath, recoveryDescriptor);
            MonitoredFile.MakeReadOnly(recoveryDescriptorPath);
            final = await RunOwnedWorkerAsync(
                validated,
                recoveryDescriptor,
                recoveryDescriptorPath,
                outputRoot,
                logPrefix: "recovery-worker",
                expectKillBarrier: false,
                executionEvidenceBudget,
                stderrBudget,
                launcher,
                workerDeadline,
                monitorHarnessProcess,
                cancellationToken).ConfigureAwait(false);
        }

        var workerResult = final.Result;
        var losses = first.SampledLoss is null ? null
            : ReferenceEquals(first, final) || validated.Prevalidation is not null ? final.SampledLoss
            : SampledLossMeasurement.Merge(first.SampledLoss, final.SampledLoss!);
        var monitorComplete = first.MonitoringComplete
            && final.MonitoringComplete
            && Math.Max(first.MaximumObservedIdentities, final.MaximumObservedIdentities)
                <= validated.ComponentEvidence.DerivedMaximumSimultaneousIdentitiesEnforced;
        var alarm = first.MonitoringAlarm ?? final.MonitoringAlarm;
        var outcome = workerResult?.Outcome ?? "inconclusive-unknown";
        var failureCode = workerResult?.FailureCode ?? final.WorkerFailureCode;
        var failureMessage = workerResult?.FailureMessage ?? final.WorkerFailureMessage;
        if (!monitorComplete)
        {
            outcome = "inconclusive-unknown";
            failureCode = "IncompleteMonitoring";
        }
        else if (alarm is not null)
        {
            outcome = final.FinalSweepComplete
                && string.Equals(final.FinalSweepAlarm, alarm, StringComparison.Ordinal)
                && alarm.StartsWith("Observed", StringComparison.Ordinal)
                ? "failed-candidate"
                : "inconclusive-unknown";
            failureCode = alarm;
        }

        return WriteOutcome(
            validated,
            execution,
            new MonitoredCaseOutcome(
                execution.Ordinal,
                execution.CaseId,
                execution.Candidate,
                outcome,
                failureCode,
                failureMessage,
                monitorComplete && losses?.HasLoss != true,
                alarm,
                checked(first.SummaryRecords + (ReferenceEquals(first, final) ? 0 : final.SummaryRecords)),
                checked(first.SummaryBytes + (ReferenceEquals(first, final) ? 0 : final.SummaryBytes)),
                Math.Max(first.MaximumObservedIdentities, final.MaximumObservedIdentities),
                Math.Max(first.MaximumObservedSweepBytes, final.MaximumObservedSweepBytes),
                ReferenceEquals(first, final) || validated.Prevalidation is not null
                    ? [first.MonitorEvidencePath]
                    : [first.MonitorEvidencePath, final.MonitorEvidencePath],
                workerResult)
            {
                SampledLoss = losses,
                SampledAdmissible = validated.UsesSampledLoss && monitorComplete && alarm is null,
            });
    }

    private static async Task<OwnedWorkerRun> RunOwnedWorkerAsync(
        MonitoredExecutionContext validated,
        MonitoredWorkerDescriptor descriptor,
        string descriptorPath,
        string outputRoot,
        string logPrefix,
        bool expectKillBarrier,
        BoundedOutputBudget combinedOutputBudget,
        BoundedOutputBudget stderrBudget,
        IMonitoredWorkerLauncher launcher,
        TimeSpan workerDeadline,
        bool monitorHarnessProcess,
        CancellationToken cancellationToken)
    {
        var monitorPath = validated.Prevalidation is null
            ? Path.Combine(outputRoot, $"{logPrefix}-monitor.jsonl")
            : Path.Combine(Path.GetDirectoryName(validated.Prevalidation.OwnershipPath!)!, "coordinator-monitor.jsonl");
        await using var monitor = new MonitoredExecutionMonitor(validated, monitorPath, combinedOutputBudget,
            Path.GetDirectoryName(descriptor.PackageStagingRoot));
        if (DescriptorObservationPolicy.IsUnifiedActive(validated.Manifest.SampledLoss))
            monitor.SetStage(descriptor.Mode == MonitoredWorkerMode.Execute ? "worker-preparation" : "recovery", true);
        var process = launcher.Start(validated.Manifest, descriptorPath);
        var workerIdentity = MonitoredProcessIdentity.Capture(process, MonitoredProcessRole.Diagnostic);
        if (monitorHarnessProcess && validated.Prevalidation is null)
        {
            var harnessIdentity = MonitoredProcessIdentity.Capture(
                Process.GetCurrentProcess(),
                MonitoredProcessRole.Harness);
            monitor.AddProcess(harnessIdentity);
        }
        try
        {
            monitor.AddProcess(workerIdentity);
            if (validated.Prevalidation is not null)
            {
                await process.StandardInput.WriteLineAsync("prevalidation-start").ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            LinuxPrevalidationProcessOperations.Instance.Signal(workerIdentity);
            process.Dispose();
            throw;
        }
        _ = monitor.StartAsync();

        var events = Channel.CreateBounded<MonitoredWorkerEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        var stdoutPath = Path.Combine(outputRoot, $"{logPrefix}-stdout.jsonl");
        var stderrPath = Path.Combine(outputRoot, $"{logPrefix}-stderr.log");
        var stdoutTask = CaptureStdoutAsync(
            process.StandardOutput,
            stdoutPath,
            validated.Encoding.MaximumStdoutUtf8Bytes,
            combinedOutputBudget,
            events.Writer,
            cancellationToken);
        var stderrTask = CaptureRawAsync(
            process.StandardError.BaseStream,
            stderrPath,
            validated.Encoding.MaximumStderrUtf8Bytes,
            stderrBudget,
            cancellationToken);
        var exitTask = process.WaitForExitAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(workerDeadline);

        var acknowledgements = new List<long>(64);
        var trackedTargets = new List<MonitoredProcessIdentity>(1);
        var killedAtBarrier = false;
        var terminatedForMonitor = false;
        var workerTerminationAnnounced = false;
        MonitoredProcessIdentity? confirmedTargetTermination = null;
        var finalSweepComplete = false;
        string? finalSweepAlarm = null;
        string? workerFailureCode = null;
        string? workerFailureMessage = null;
        int? workerExitCode = null;
        try
        {
            while (true)
            {
                if (!events.Reader.TryRead(out var workerEvent))
                {
                    if (stdoutTask.IsCompleted)
                    {
                        await stdoutTask.ConfigureAwait(false);
                        break;
                    }
                    var waitToRead = events.Reader.WaitToReadAsync(deadline.Token).AsTask();
                    var completed = await Task.WhenAny(
                        stdoutTask,
                        waitToRead,
                        monitor.TerminalIssue).ConfigureAwait(false);
                    if (completed == monitor.TerminalIssue)
                    {
                        var postAbort = await AbortOwnedProcessesAsync(
                            process,
                            workerIdentity,
                            trackedTargets,
                            monitor,
                            deadline.Token).ConfigureAwait(false);
                        finalSweepComplete = DescriptorObservationPolicy.Admissible(postAbort.Summary);
                        finalSweepAlarm = postAbort.Summary.Alarm;
                        terminatedForMonitor = true;
                        break;
                    }
                    if (completed == stdoutTask)
                    {
                        await stdoutTask.ConfigureAwait(false);
                        continue;
                    }
                    if (!await waitToRead.ConfigureAwait(false))
                    {
                        break;
                    }
                    continue;
                }
                switch (workerEvent.Type)
                {
                    case "stage":
                        monitor.SetStage(
                            workerEvent.Stage ?? "worker-stage",
                            workerEvent.ActiveStorageStage == true);
                        break;
                    case "process" when workerEvent.ProcessRole == "target":
                        var target = new MonitoredProcessIdentity(
                            workerEvent.ProcessId
                                ?? throw Error("InvalidWorkerProcessEvent", "Target process event omitted pid."),
                            workerEvent.ProcessStartTimeTicks
                                ?? throw Error("InvalidWorkerProcessEvent", "Target process event omitted start time."),
                            MonitoredProcessRole.Target);
                        monitor.AddProcess(target);
                        trackedTargets.Add(target);
                        break;
                    case "process-terminating" when workerEvent.ProcessRole == "target":
                        var terminatingTarget = RequireTrackedTarget(workerEvent, trackedTargets);
                        var preTermination = await monitor.ObserveBoundaryAsync(
                            "pre-target-termination",
                            activeStorageStage: false,
                            deadline.Token).ConfigureAwait(false);
                        if (!DescriptorObservationPolicy.Admissible(preTermination.Summary) || preTermination.Summary.Alarm is not null)
                        {
                            var postAbort = await AbortOwnedProcessesAsync(
                                process,
                                workerIdentity,
                                trackedTargets,
                                monitor,
                                deadline.Token).ConfigureAwait(false);
                            finalSweepComplete = DescriptorObservationPolicy.Admissible(postAbort.Summary);
                            finalSweepAlarm = postAbort.Summary.Alarm;
                            terminatedForMonitor = true;
                            break;
                        }
                        using (var targetProcess = Process.GetProcessById(terminatingTarget.ProcessId))
                        {
                            await monitor.KillOwnedAsync(targetProcess, terminatingTarget, deadline.Token)
                                .ConfigureAwait(false);
                        }
                        confirmedTargetTermination = terminatingTarget;
                        var processRelease = FormattableString.Invariant(
                            $"release:process-termination:{terminatingTarget.ProcessId}:{terminatingTarget.LinuxStartTimeTicks}");
                        await process.StandardInput.WriteLineAsync(processRelease).ConfigureAwait(false);
                        await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                        break;
                    case "process-terminating" when workerEvent.ProcessRole == "diagnostic":
                        if (workerEvent.ProcessId != workerIdentity.ProcessId
                            || workerEvent.ProcessStartTimeTicks != workerIdentity.LinuxStartTimeTicks)
                        {
                            throw Error(
                                "OwnedProcessIdentityMismatch",
                                "Diagnostic termination event did not match the harness-launched worker.");
                        }
                        var preWorkerTermination = await monitor.ObserveBoundaryAsync(
                            "pre-worker-termination",
                            activeStorageStage: false,
                            deadline.Token).ConfigureAwait(false);
                        if (!DescriptorObservationPolicy.Admissible(preWorkerTermination.Summary)
                            || preWorkerTermination.Summary.Alarm is not null)
                        {
                            var postAbort = await AbortOwnedProcessesAsync(
                                process,
                                workerIdentity,
                                trackedTargets,
                                monitor,
                                deadline.Token).ConfigureAwait(false);
                            finalSweepComplete = DescriptorObservationPolicy.Admissible(postAbort.Summary);
                            finalSweepAlarm = postAbort.Summary.Alarm;
                            terminatedForMonitor = true;
                            break;
                        }
                        monitor.MarkIntentionalTermination(workerIdentity);
                        workerTerminationAnnounced = true;
                        var workerRelease = FormattableString.Invariant(
                            $"release:process-termination:{workerIdentity.ProcessId}:{workerIdentity.LinuxStartTimeTicks}");
                        await process.StandardInput.WriteLineAsync(workerRelease).ConfigureAwait(false);
                        await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                        break;
                    case "process-terminated" when workerEvent.ProcessRole == "target":
                        var terminatedTarget = RequireTrackedTarget(workerEvent, trackedTargets);
                        PrevalidationProtocol.Require(confirmedTargetTermination == terminatedTarget,
                            "IntentionalTerminationNotConfirmed");
                        trackedTargets.Remove(terminatedTarget);
                        confirmedTargetTermination = null;
                        break;
                    case "process" when workerEvent.ProcessRole == "diagnostic":
                        if (workerEvent.ProcessId != workerIdentity.ProcessId
                            || workerEvent.ProcessStartTimeTicks != workerIdentity.LinuxStartTimeTicks)
                        {
                            throw Error(
                                "OwnedProcessIdentityMismatch",
                                "Worker-reported identity did not match the harness-launched process.");
                        }
                        break;
                    case "ack":
                        if (workerEvent.FirstSequence.HasValue && workerEvent.LastSequence.HasValue)
                        {
                            for (var sequence = workerEvent.FirstSequence.Value;
                                 sequence <= workerEvent.LastSequence.Value;
                                 sequence++)
                            {
                                if (acknowledgements.Count == 4_096)
                                {
                                    throw Error("AcknowledgementLimitExceeded", "Fault acknowledgement evidence exceeded its bound.");
                                }
                                acknowledgements.Add(sequence);
                            }
                        }
                        break;
                    case "failed":
                        var failure = (workerEvent.Message ?? "WorkerFailed").Split(':', 2);
                        workerFailureCode = Bound(failure[0], 128);
                        workerFailureMessage = failure.Length == 2
                            ? Bound(failure[1], 1_024)
                            : "The diagnostic worker failed without a detailed message.";
                        break;
                    case "boundary":
                        var boundaryName = workerEvent.Stage
                            ?? throw Error("InvalidWorkerBoundary", "Worker boundary omitted its name.");
                        var boundary = await monitor.ObserveBoundaryAsync(
                            boundaryName,
                            workerEvent.ActiveStorageStage == true,
                            deadline.Token).ConfigureAwait(false);
                        if (!DescriptorObservationPolicy.Admissible(boundary.Summary) || boundary.Summary.Alarm is not null)
                        {
                            var postAbort = await AbortOwnedProcessesAsync(
                                process,
                                workerIdentity,
                                trackedTargets,
                                monitor,
                                deadline.Token).ConfigureAwait(false);
                            finalSweepComplete = DescriptorObservationPolicy.Admissible(postAbort.Summary);
                            finalSweepAlarm = postAbort.Summary.Alarm;
                            terminatedForMonitor = true;
                            break;
                        }
                        await process.StandardInput.WriteLineAsync($"release:{boundaryName}")
                            .ConfigureAwait(false);
                        await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                        break;
                    case "barrier":
                        if (!expectKillBarrier)
                        {
                            throw Error("UnexpectedKillBarrier", "A worker reached an undeclared kill barrier.");
                        }
                        var preKill = await monitor.ObserveBoundaryAsync(
                            $"pre-kill-{workerEvent.Barrier}",
                            activeStorageStage: true,
                            deadline.Token).ConfigureAwait(false);
                        await monitor.KillOwnedAsync(process, workerIdentity, deadline.Token).ConfigureAwait(false);
                        var postKill = await monitor.ObserveBoundaryAsync(
                            "post-kill-quiescent-root-inventory",
                            activeStorageStage: false,
                            deadline.Token).ConfigureAwait(false);
                        finalSweepComplete = DescriptorObservationPolicy.Admissible(preKill.Summary)
                            && preKill.Summary.Alarm is null
                            && DescriptorObservationPolicy.Admissible(postKill.Summary);
                        finalSweepAlarm = postKill.Summary.Alarm;
                        killedAtBarrier = true;
                        break;
                    case "completed":
                        break;
                    default:
                        throw Error(
                            "InvalidWorkerEvent",
                            $"Worker emitted unsupported event type '{workerEvent.Type}'.");
                }
                if (killedAtBarrier || terminatedForMonitor)
                {
                    break;
                }
            }

            if (!killedAtBarrier && !terminatedForMonitor)
            {
                await exitTask.WaitAsync(deadline.Token).ConfigureAwait(false);
                if (workerTerminationAnnounced)
                {
                    monitor.ConfirmTerminatedAndRemove(workerIdentity);
                }
                var finalSweep = await monitor.ObserveBoundaryAsync(
                    "worker-exit-quiescent",
                    activeStorageStage: false,
                    deadline.Token).ConfigureAwait(false);
                finalSweepComplete = DescriptorObservationPolicy.Admissible(finalSweep.Summary);
                finalSweepAlarm = finalSweep.Summary.Alarm;
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested && validated.Prevalidation is null)
        {
            await monitor.StopAsync().ConfigureAwait(false);
            if (LinuxProcessIdentity.Matches(workerIdentity))
            {
                monitor.MarkIntentionalTermination(workerIdentity);
                OwnedProcessTerminator.KillExact(process, workerIdentity);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None).ConfigureAwait(false);
                monitor.ConfirmTerminatedAndRemove(workerIdentity);
            }
            throw Error(
                "ExecutionDeadlineExceeded",
                FormattableString.Invariant(
                    $"A worker exceeded its {workerDeadline.TotalSeconds}-second execution deadline."));
        }
        finally
        {
            events.Writer.TryComplete();
            await monitor.StopAsync().ConfigureAwait(false);
            if (validated.Prevalidation is null && !process.HasExited && LinuxProcessIdentity.Matches(workerIdentity))
            {
                monitor.MarkIntentionalTermination(workerIdentity);
                OwnedProcessTerminator.KillExact(process, workerIdentity);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None).ConfigureAwait(false);
                monitor.ConfirmTerminatedAndRemove(workerIdentity);
            }
            foreach (var target in validated.Prevalidation is null ? trackedTargets : [])
            {
                if (!LinuxProcessIdentity.Matches(target))
                {
                    continue;
                }
                monitor.MarkIntentionalTermination(target);
                using var targetProcess = Process.GetProcessById(target.ProcessId);
                OwnedProcessTerminator.KillExact(targetProcess, target);
                await targetProcess.WaitForExitAsync(CancellationToken.None).WaitAsync(
                    TimeSpan.FromSeconds(5),
                    CancellationToken.None).ConfigureAwait(false);
                monitor.ConfirmTerminatedAndRemove(target);
            }
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(
                validated.Prevalidation is null ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
            foreach (var path in validated.Prevalidation is null
                ? new[] { monitorPath, stdoutPath, stderrPath } : new[] { stdoutPath, stderrPath })
            {
                if (File.Exists(path))
                {
                    MonitoredFile.MakeReadOnly(path);
                }
            }
            if (process.HasExited)
            {
                workerExitCode = process.ExitCode;
            }
            process.Dispose();
        }

        MonitoredWorkerResult? result = null;
        var resultPath = Path.Combine(descriptor.ExecutionRoot, "worker-result.json");
        if (!killedAtBarrier && File.Exists(resultPath))
        {
            result = JsonSerializer.Deserialize<MonitoredWorkerResult>(
                MonitoredFile.ReadBounded(resultPath, 1_048_576),
                JsonOptions);
            if (result is null
                || !string.Equals(
                    result.Schema,
                    MonitoredProtocolVersions.WorkerResultSchema,
                    StringComparison.Ordinal)
                || result.Ordinal != descriptor.Execution.Ordinal
                || !string.Equals(result.CaseId, descriptor.Execution.CaseId, StringComparison.Ordinal)
                || !string.Equals(result.Candidate, descriptor.Execution.Candidate, StringComparison.Ordinal)
                || result.Outcome is not ("pass"
                    or "failed-candidate"
                    or "inconclusive-infra"
                    or "inconclusive-source"
                    or "inconclusive-unknown"
                    or "invalid-injection")
                || !string.Equals(
                    result.RecommendationScope,
                    "monitored-scope-only",
                    StringComparison.Ordinal)
                || result.Offered < 0
                || result.Admitted < 0
                || result.Rejected < 0
                || result.Committed < 0
                || result.FailedAfterAdmission < 0
                || result.UnknownCommitOutcome < 0
                || result.SourceMalformedPayloads is < 0
                || result.SourceAdmissionInvalid is < 0
                || result.SourceRejectedNewKeys is < 0
                || result.SourceAdmissionRejected is < 0
                || result.LogicalCommittedBytes < 0
                || result.PackageFinalBytes < 0
                || !double.IsFinite(result.OfferSeconds)
                || result.OfferSeconds < 0
                || !double.IsFinite(result.DrainSeconds)
                || result.DrainSeconds < 0
                || !double.IsFinite(result.FinalizationSeconds)
                || result.FinalizationSeconds < 0
                || !double.IsFinite(result.ReopenAndFirstQuerySeconds)
                || result.ReopenAndFirstQuerySeconds < 0
                || !double.IsFinite(result.DiagnosticCpuSeconds)
                || result.DiagnosticCpuSeconds < 0
                || result.RecoveredRecords < 0
                || result.SourceTicks is < 0
                || result.SourceKeys is < 0
                || result.ScheduledSourceOffers is < 0
                || result.AttemptedSourceOffers is < 0
                || result.AchievedSourceOfferRatio is < 0 or > 1
                || result.AchievedSourceOfferRatio.HasValue
                    && !double.IsFinite(result.AchievedSourceOfferRatio.Value)
                || string.IsNullOrWhiteSpace(result.SourceCoverage)
                || result.CounterCollectionSeconds is < 0
                || result.CounterCollectionSeconds.HasValue
                    && !double.IsFinite(result.CounterCollectionSeconds.Value))
            {
                throw Error("InvalidWorkerResult", "The worker result did not match its frozen execution.");
            }
            var expectedExitCode = string.Equals(result.Outcome, "pass", StringComparison.Ordinal)
                ? 0
                : 3;
            if (workerExitCode != expectedExitCode)
            {
                throw Error(
                    "WorkerExitCodeMismatch",
                    "The diagnostic worker exit code did not match its persisted result.");
            }
        }
        else if (!killedAtBarrier && workerExitCode == 0)
        {
            throw Error(
                "MissingWorkerResult",
                "The diagnostic worker exited successfully without a persisted result.");
        }

        return new OwnedWorkerRun(
            result,
            killedAtBarrier,
            monitor.IsIncomplete,
            monitor.TerminalAlarm,
            monitor.SummaryRecords,
            monitor.SummaryBytes,
            monitor.MaximumCurrentContextIdentities,
            monitor.MaximumObservedSweepBytes,
            Path.GetRelativePath(validated.CampaignRoot, monitorPath)
                .Replace(Path.DirectorySeparatorChar, '/'),
            finalSweepComplete,
            finalSweepAlarm,
            acknowledgements,
            workerFailureCode,
            workerFailureMessage) { SampledLoss = monitor.LossTotals };
    }

    private static async Task<MonitoredSweepResult> AbortOwnedProcessesAsync(
        Process worker,
        MonitoredProcessIdentity workerIdentity,
        List<MonitoredProcessIdentity> trackedTargets,
        MonitoredExecutionMonitor monitor,
        CancellationToken cancellationToken)
    {
        await monitor.StopAsync().ConfigureAwait(false);
        if (LinuxProcessIdentity.Matches(workerIdentity))
        {
            monitor.MarkIntentionalTermination(workerIdentity);
            OwnedProcessTerminator.KillExact(worker, workerIdentity);
            await worker.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            monitor.ConfirmTerminatedAndRemove(workerIdentity);
        }
        foreach (var target in trackedTargets.ToArray())
        {
            if (!LinuxProcessIdentity.Matches(target))
            {
                continue;
            }
            monitor.MarkIntentionalTermination(target);
            using var targetProcess = Process.GetProcessById(target.ProcessId);
            OwnedProcessTerminator.KillExact(targetProcess, target);
            await targetProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            monitor.ConfirmTerminatedAndRemove(target);
            trackedTargets.Remove(target);
        }
        return await monitor.ObserveBoundaryAsync(
            "post-monitor-abort-quiescent-root-inventory",
            activeStorageStage: false,
            cancellationToken).ConfigureAwait(false);
    }

    private static MonitoredProcessIdentity RequireTrackedTarget(
        MonitoredWorkerEvent workerEvent,
        IReadOnlyList<MonitoredProcessIdentity> trackedTargets)
    {
        var processId = workerEvent.ProcessId
            ?? throw Error("InvalidWorkerProcessEvent", "Target lifecycle event omitted pid.");
        var startTime = workerEvent.ProcessStartTimeTicks
            ?? throw Error("InvalidWorkerProcessEvent", "Target lifecycle event omitted start time.");
        return trackedTargets.SingleOrDefault(target =>
                target.ProcessId == processId
                && target.LinuxStartTimeTicks == startTime
                && target.Role == MonitoredProcessRole.Target)
            ?? throw Error(
                "OwnedProcessIdentityMismatch",
                "Target lifecycle event did not match a harness-observed process identity.");
    }

    private static MonitoredWorkerDescriptor CreateDescriptor(
        MonitoredExecutionContext validated,
        MonitoredExecutionSpec execution,
        string outputRoot,
        string packageWorkspace,
        string packageHistory,
        MonitoredWorkerMode mode,
        string? sourcePackageRoot,
        string? sourceCaptureId,
        string? sourceArtifactId,
        IReadOnlyList<long>? acknowledgements,
        IReadOnlyList<long>? offered)
    {
        var suffix = mode == MonitoredWorkerMode.Execute ? "source" : "recovered";
        var stagingRoot = mode == MonitoredWorkerMode.Execute
            ? Path.Combine(packageWorkspace, "package-staging")
            : packageWorkspace;
        var packageRoot = mode == MonitoredWorkerMode.Execute
            ? Path.Combine(packageHistory, "package")
            : packageHistory;
        return new MonitoredWorkerDescriptor(
            validated.Prevalidation is null
                ? MonitoredProtocolVersions.WorkerDescriptorSchema
                : PrevalidationProtocol.WorkerSchema,
            mode,
            validated.RepositoryRoot,
            validated.ManifestPath,
            validated.ManifestSha256,
            execution,
            outputRoot,
            stagingRoot,
            packageRoot,
            validated.Prevalidation is null
                ? $"capture-{execution.Ordinal:D2}-{execution.Candidate.ToLowerInvariant()}-{suffix}"
                : PrevalidationLayout.OwnedIdentity(Path.GetFileName(validated.CampaignRoot), execution, mode, "capture"),
            validated.Prevalidation is null
                ? $"artifact-{execution.Ordinal:D2}-{execution.Candidate.ToLowerInvariant()}-{suffix}"
                : PrevalidationLayout.OwnedIdentity(Path.GetFileName(validated.CampaignRoot), execution, mode, "artifact"),
            sourcePackageRoot,
            sourceCaptureId,
            sourceArtifactId,
            acknowledgements,
            offered);
    }

    internal static Process StartToolWorker(
        IMonitoredExecutionManifest manifest,
        string descriptorPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = manifest.RuntimeBinary.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(manifest.ToolBinary.Path)!,
        };
        startInfo.ArgumentList.Add(manifest.ToolBinary.Path);
        startInfo.ArgumentList.Add("durable-capture-spike");
        startInfo.ArgumentList.Add(manifest is PrevalidationExecutionSettings
            ? "prevalidation-worker" : "monitored-worker");
        startInfo.ArgumentList.Add("--descriptor");
        startInfo.ArgumentList.Add(descriptorPath);
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        if (manifest is PrevalidationExecutionSettings)
        {
            startInfo.Environment["PATH"] = Path.GetDirectoryName(manifest.RuntimeBinary.Path)
                + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }
        return Process.Start(startInfo)
            ?? throw Error("WorkerLaunchFailed", "The monitored diagnostic worker could not be launched.");
    }

    private static async Task CaptureStdoutAsync(
        StreamReader reader,
        string path,
        int maximumBytes,
        BoundedOutputBudget combinedOutputBudget,
        ChannelWriter<MonitoredWorkerEvent> events,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            4_096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        long total = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            var next = checked(total + bytes.Length + 1);
            if (bytes.Length > 8_192 || next > maximumBytes)
            {
                throw Error("WorkerStdoutLimit", "Worker stdout exceeded its bounded geometry.");
            }
            combinedOutputBudget.ConsumeControl(bytes.Length + 1L);
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            total = next;
            MonitoredWorkerEvent? workerEvent;
            try
            {
                workerEvent = JsonSerializer.Deserialize<MonitoredWorkerEvent>(bytes, JsonOptions);
            }
            catch (JsonException)
            {
                throw Error("InvalidWorkerEvent", "Worker stdout contained a non-JSON control line.");
            }
            if (workerEvent is not null)
            {
                await events.WriteAsync(workerEvent, cancellationToken).ConfigureAwait(false);
            }
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    internal static async Task CaptureRawAsync(
        Stream input,
        string path,
        int maximumBytes,
        BoundedOutputBudget combinedBudget,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            4_096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        var buffer = new byte[4_096];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (read > maximumBytes - total)
            {
                throw Error("WorkerStderrLimit", "Worker stderr exceeded its bounded geometry.");
            }
            try
            {
                combinedBudget.Consume(read);
            }
            catch (DurableStorageExperimentException exception)
                when (exception.Code == "CombinedOutputLimit")
            {
                throw Error("WorkerStderrLimit", "Combined source/recovery stderr exceeded its shared execution budget.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static MonitoredHostFacts Preflight(MonitoredValidatedManifest validated)
    {
        var evidenceRoot = validated.Manifest.EvidenceRoot;
        var admissionHostFacts = MonitoredRunManifestValidator.ValidateCurrentHost(
            validated.Manifest.Host,
            evidenceRoot);
        if (Directory.Exists(validated.CampaignRoot)
            || File.Exists(Path.Combine(validated.CampaignRoot, "campaign-start.json")))
        {
            throw Error(
                "CampaignRerunRejected",
                "The private campaign root or its persistent start receipt already exists.");
        }
        var allowedReadinessFiles = new[]
        {
            validated.ManifestPath,
            validated.Manifest.AttributionMap,
            validated.Manifest.EvidenceEncoding,
            validated.Manifest.MonitorComponentEvidence,
            validated.Manifest.AuthorizationReceipt,
        }.Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
        var existingEvidenceFiles = MonitoredPathRules.EnumerateFilesRejectingLinks(
                evidenceRoot,
                maximumEntries: 4_096,
                maximumPathUtf8Bytes: 4_096)
            .Select(Path.GetFullPath)
            .ToArray();
        if (existingEvidenceFiles.Length > MonitoredRunnerGeometry.MaximumReadinessArtifactsInsideEvidenceRoot
            || existingEvidenceFiles.Any(path => !allowedReadinessFiles.Contains(path)))
        {
            throw Error(
                "EvidenceRootNotPrivate",
                "The evidence root may contain at most four declared readiness artifacts before campaign start.");
        }
        if (validated.ComponentEvidence.DerivedMaximumSimultaneousIdentitiesEnforced
            != MonitoredRunnerGeometry.MaximumSimultaneousIdentities
            || validated.ComponentEvidence.DerivedMaximumRootedIdentities
                != MonitoredRunnerGeometry.MaximumRootedIdentities
            || validated.ComponentEvidence.MaximumDescriptorOnlyIdentitiesEnforced
                != MonitoredRunnerGeometry.MaximumOwnedWritableDescriptorOnlyFiles
            || validated.ComponentEvidence.DerivedMaximumSummaryRecordsRequired
                != MonitoredRunnerGeometry.MaximumRequiredSummaryRecords
            || validated.ComponentEvidence.MaximumSummaryRecordsEncoded != 2_048
            || validated.ComponentEvidence.DerivedMaximumCombinedSummaryControlBytes
                != checked(MonitoredRunnerGeometry.MaximumMonitorSummaryBytes
                    + MonitoredRunnerGeometry.MaximumWorkerControlRecordsPerExecution
                        * (long)validated.ComponentEvidence.MaximumWorkerControlUtf8BytesObserved))
        {
            throw Error(
                "RunnerGeometryEvidenceMismatch",
                "Component evidence does not match the runner's enforced O1/live/history identity and output geometry.");
        }
        return admissionHostFacts;
    }

    private static void PrepareCampaignDirectories(MonitoredValidatedManifest validated)
    {
        Directory.CreateDirectory(validated.CampaignRoot);
        SetPrivateDirectory(validated.CampaignRoot);
        Directory.CreateDirectory(validated.Manifest.HistoryRoot);
        Directory.CreateDirectory(validated.Manifest.WorkspaceRoot);
        Directory.CreateDirectory(validated.Manifest.OutputRoot);
        SetPrivateDirectory(validated.Manifest.HistoryRoot);
        SetPrivateDirectory(validated.Manifest.WorkspaceRoot);
        SetPrivateDirectory(validated.Manifest.OutputRoot);
    }

    private static void SetPrivateDirectory(string path)
        => MonitoredFile.MakePrivateDirectory(path);

    private static MonitoredCaseOutcome WriteNotRunOutcome(
        MonitoredValidatedManifest validated,
        MonitoredExecutionSpec execution,
        string reason)
        => WriteOutcome(
            validated,
            execution,
            new MonitoredCaseOutcome(
                execution.Ordinal,
                execution.CaseId,
                execution.Candidate,
                "not-run",
                reason,
                null,
                MonitoringComplete: false,
                MonitoringAlarm: reason,
                MonitorSummaryRecords: null,
                MonitorSummaryBytes: null,
                MaximumObservedIdentities: null,
                MaximumObservedSweepBytes: null,
                MonitoringEvidenceFiles: [],
                Worker: null));

    private static MonitoredCaseOutcome WriteOutcome(
        MonitoredValidatedManifest validated,
        MonitoredExecutionSpec execution,
        MonitoredCaseOutcome outcome)
        => WriteOutcome(MonitoredExecutionContext.FromCampaign(validated), execution, outcome);

    private static MonitoredCaseOutcome WriteOutcome(
        MonitoredExecutionContext validated,
        MonitoredExecutionSpec execution,
        MonitoredCaseOutcome outcome)
    {
        var path = Path.Combine(validated.Manifest.OutputRoot, $"{execution.Ordinal:D2}-outcome.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        MonitoredFile.WriteNewJson(path, outcome);
        MonitoredFile.MakeReadOnly(path);
        return outcome;
    }

    private static string OutcomePath(MonitoredValidatedManifest validated, int ordinal)
        => Path.Combine(validated.Manifest.OutputRoot, $"{ordinal:D2}-outcome.json");

    private static string[] ExistingMonitoringEvidenceFiles(
        MonitoredValidatedManifest validated,
        MonitoredExecutionSpec execution)
    {
        var executionName =
            $"{execution.Ordinal:D2}-{execution.Candidate.ToLowerInvariant()}-{execution.CaseId.ToLowerInvariant()}";
        var outputRoot = Path.Combine(validated.Manifest.OutputRoot, executionName);
        if (!Directory.Exists(outputRoot))
        {
            return [];
        }
        return Directory.EnumerateFiles(outputRoot, "*-monitor.jsonl", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetRelativePath(validated.CampaignRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static List<MonitoredSealedArtifact> EnumerateAndFreezeCampaignArtifacts(
        string campaignRoot)
    {
        var artifacts = new List<MonitoredSealedArtifact>(
            MonitoredRunnerGeometry.MaximumSimultaneousIdentities);
        foreach (var path in MonitoredPathRules.EnumerateFilesRejectingLinks(
                     campaignRoot,
                     maximumEntries: 4_096,
                     maximumPathUtf8Bytes: 4_096).Order(StringComparer.Ordinal))
        {
            using var handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var observation = LinuxStatxHandleMetadataObserver.Instance.Observe(handle);
            if (observation.LinkCount != 1)
            {
                throw Error(
                    "CampaignArtifactLinkRejected",
                    "Campaign sealing rejects linked evidence artifacts.");
            }
            MonitoredFile.MakeReadOnly(path);
            artifacts.Add(new MonitoredSealedArtifact(
                Path.GetRelativePath(campaignRoot, path)
                    .Replace(Path.DirectorySeparatorChar, '/'),
                observation.Length,
                MonitoredFile.HashFile(path)));
            if (artifacts.Count + 1 > MonitoredRunnerGeometry.MaximumSimultaneousIdentities)
            {
                throw Error(
                    "CampaignArtifactGeometryExceeded",
                    "Campaign evidence exceeded the established maximum identity geometry before sealing.");
            }
        }
        return artifacts;
    }

    private static void FreezeCampaignDirectories(string campaignRoot)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw Error("UnsupportedPlatform", "Monitored campaign sealing is Linux-only.");
        }
        foreach (var path in Directory.EnumerateDirectories(
                     campaignRoot,
                     "*",
                     SearchOption.AllDirectories)
                 .OrderByDescending(static path => path.Length))
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        File.SetUnixFileMode(
            campaignRoot,
            UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static string Bound(string value, int maximum)
        => value.Length <= maximum ? value : value[..maximum];

    private static DurableStorageExperimentException Error(string code, string message) => new(code, message);

    private sealed record OwnedWorkerRun(
        MonitoredWorkerResult? Result,
        bool KilledAtBarrier,
        bool MonitoringIncomplete,
        string? MonitoringAlarm,
        int SummaryRecords,
        long SummaryBytes,
        int MaximumObservedIdentities,
        long MaximumObservedSweepBytes,
        string MonitorEvidencePath,
        bool FinalSweepComplete,
        string? FinalSweepAlarm,
        IReadOnlyList<long> Acknowledgements,
        string? WorkerFailureCode,
        string? WorkerFailureMessage)
    {
        internal bool MonitoringComplete => !MonitoringIncomplete;
        internal SampledLossMeasurement? SampledLoss { get; init; }
    }
}

internal static class MonitoredDecisionEngine
{
    internal static MonitoredCampaignDecision Decide(IReadOnlyList<MonitoredCaseOutcome> outcomes,
        MonitoredRunManifest? sampledCampaign = null)
    {
        var sampledReadinessValidated = sampledCampaign is not null;
        if (sampledReadinessValidated)
        {
            DescriptorObservationPolicy.ValidateReadiness(sampledCampaign!);
            if (outcomes.Any(static outcome => outcome.SampledLoss is null))
                return Inconclusive("The prospective sampled policy is missing from one or more outcomes.");
            foreach (var outcome in outcomes)
                DescriptorObservationPolicy.ValidateMeasurement(sampledCampaign!.SampledLoss, outcome.SampledLoss);
        }
        else if (outcomes.Any(static outcome => outcome.SampledLoss is not null || outcome.SampledAdmissible))
        {
            return Inconclusive("Sampled evidence cannot enter the strict decision gate.");
        }
        var decision = DecideCore(outcomes, sampledReadinessValidated);
        return sampledReadinessValidated ? decision with
        {
            Scope = DescriptorObservationPolicy.IsUnifiedActive(sampledCampaign!.SampledLoss)
                ? "unified-active-policy/1-conditional-no-product-decision"
                : DescriptorObservationPolicy.IsObservedUnlinked(sampledCampaign!.SampledLoss)
                ? "observed-unlinked-policy/1-conditional-no-product-decision"
                : "sampled-loss-policy/1-conditional-no-product-decision",
        }
            : decision;
    }

    private static MonitoredCampaignDecision DecideCore(IReadOnlyList<MonitoredCaseOutcome> outcomes,
        bool sampledReadinessValidated)
    {
        if (outcomes.Count != 35
            || outcomes.Any(static outcome => outcome.Outcome is "not-run"
                or "inconclusive-infra"
                or "inconclusive-source"
                or "inconclusive-unknown"
                or "invalid-injection")
            || outcomes.Any(outcome => !outcome.MonitoringComplete
                && !(sampledReadinessValidated && outcome.SampledAdmissible)))
        {
            return new MonitoredCampaignDecision(
                "inconclusive",
                "monitored-scope-only",
                CompleteEvidence: false,
                ["One or more required executions or monitor records are missing, invalid, or inconclusive."]);
        }

        var baseline = outcomes.Where(static outcome => outcome.Candidate == "E")
            .Select(static outcome => outcome.Worker?.Requests)
            .ToArray();
        if (baseline.Length != 4 || baseline.Any(static item => item is null))
        {
            return Inconclusive("The candidate-blind/live baseline population is incomplete.");
        }
        var liveBaselines = outcomes.Where(static outcome =>
                outcome.Candidate == "E" && outcome.CaseId.StartsWith('L'))
            .Select(static outcome => outcome.Worker!.Requests!)
            .ToArray();
        if (liveBaselines.Length != 3
            || liveBaselines.Any(static item =>
                item.Scheduled == 0 || item.Succeeded < 0.95 * item.Scheduled)
            || Ratio(
                liveBaselines.Max(static item => item.P95Milliseconds ?? 0),
                liveBaselines.Min(static item => item.P95Milliseconds ?? 0)) > 1.5)
        {
            return Inconclusive("The three live baselines fail the frozen validity screens.");
        }

        var eligible = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var candidate in new[] { "A", "B" })
        {
            var candidateOutcomes = outcomes.Where(outcome =>
                string.Equals(outcome.Candidate, candidate, StringComparison.Ordinal)).ToArray();
            eligible[candidate] = candidateOutcomes.Length == 15
                && candidateOutcomes.All(static outcome => outcome.Outcome == "pass")
                && LiveScreensPass(candidate, outcomes);
        }
        if (eligible["A"] && !eligible["B"])
        {
            return Recommend("A", "Only candidate A satisfies every monitored hard gate.");
        }
        if (eligible["B"] && !eligible["A"])
        {
            return Recommend("B", "Only candidate B satisfies every monitored hard gate.");
        }
        if (!eligible["A"] && !eligible["B"])
        {
            return CompleteInconclusive("Neither candidate satisfies every monitored hard gate.");
        }

        var a = ComparativeMetrics("A", outcomes);
        var b = ComparativeMetrics("B", outcomes);
        var aWithin = a.O1Committed >= 0.95 * b.O1Committed
            && Ratio(a.CpuPerRecord, b.CpuPerRecord) <= 1.10
            && Ratio(a.FinalBytesPerLogicalByte, b.FinalBytesPerLogicalByte) <= 1.10
            && Ratio(a.ReopenSeconds, b.ReopenSeconds) <= 1.10;
        var bWithin = b.O1Committed >= 0.95 * a.O1Committed
            && Ratio(b.CpuPerRecord, a.CpuPerRecord) <= 1.10
            && Ratio(b.FinalBytesPerLogicalByte, a.FinalBytesPerLogicalByte) <= 1.10
            && Ratio(b.ReopenSeconds, a.ReopenSeconds) <= 1.10;
        if (aWithin && bWithin)
        {
            return Recommend("A", "Both candidates are within reciprocal tolerance; the frozen simplicity tie-break selects A.");
        }
        if (aWithin)
        {
            return Recommend("A", "Candidate A meets the frozen reciprocal comparative screens.");
        }
        if (bWithin)
        {
            return Recommend("B", "Candidate B meets the frozen reciprocal comparative screens.");
        }
        return CompleteInconclusive(
            "Both candidates pass hard gates, but neither dominates under the frozen reciprocal screens.");
    }

    internal static bool LiveScreensPass(
        string candidate,
        IReadOnlyList<MonitoredCaseOutcome> outcomes)
    {
        var paired = new List<(MonitoredRequestMetrics Baseline, MonitoredRequestMetrics Candidate)>(3);
        foreach (var caseId in new[] { "L1", "L2", "L3" })
        {
            var baseline = outcomes.Single(outcome =>
                outcome.CaseId == caseId && outcome.Candidate == "E").Worker?.Requests;
            var current = outcomes.Single(outcome =>
                outcome.CaseId == caseId && outcome.Candidate == candidate).Worker?.Requests;
            if (baseline is null || current is null || baseline.Scheduled == 0)
            {
                return false;
            }
            paired.Add((baseline, current));
        }

        var completionRatios = paired.Select(pair =>
            Ratio(pair.Candidate.Succeeded, pair.Baseline.Succeeded)).Order().ToArray();
        var errorIncreases = paired.Select(pair =>
            Ratio(pair.Candidate.Failed, Math.Max(1, pair.Candidate.Scheduled))
            - Ratio(pair.Baseline.Failed, Math.Max(1, pair.Baseline.Scheduled))).Order().ToArray();
        var baselineP95 = paired
            .Select(static pair => pair.Baseline.P95Milliseconds ?? double.NaN)
            .Order()
            .ToArray();
        var p95Increases = paired
            .Select(static pair =>
                (pair.Candidate.P95Milliseconds ?? double.NaN)
                - (pair.Baseline.P95Milliseconds ?? double.NaN))
            .Order()
            .ToArray();
        var medianBaselineP95 = Median(baselineP95);
        var medianP95Increase = Median(p95Increases);
        var allowedP95Increase = Math.Max(5, 0.15 * medianBaselineP95);
        return completionRatios[1] >= 0.95
            && errorIncreases[1] <= 0.005
            && double.IsFinite(medianBaselineP95)
            && double.IsFinite(medianP95Increase)
            && medianP95Increase <= allowedP95Increase;
    }

    private static ComparativeCandidateMetrics ComparativeMetrics(
        string candidate,
        IReadOnlyList<MonitoredCaseOutcome> outcomes)
    {
        var selected = outcomes.Where(outcome =>
            outcome.Candidate == candidate && outcome.CaseId is "N1" or "O1")
            .Select(static outcome => outcome.Worker!)
            .ToArray();
        if (selected.Length != 2
            || selected.Any(static item => item.Committed <= 0 || item.LogicalCommittedBytes <= 0))
        {
            return new ComparativeCandidateMetrics(0, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        }
        var cpu = selected.Select(static item => item.DiagnosticCpuSeconds / item.Committed)
            .Order().ToArray();
        var bytes = selected.Select(static item => (double)item.PackageFinalBytes / item.LogicalCommittedBytes)
            .Order().ToArray();
        var reopen = selected.Select(static item => item.ReopenAndFirstQuerySeconds)
            .Order().ToArray();
        return new ComparativeCandidateMetrics(
            selected.Single(static item => item.CaseId == "O1").Committed,
            Median(cpu),
            Median(bytes),
            Median(reopen));
    }

    private static double Median(double[] values)
        => values.Length % 2 == 1
            ? values[values.Length / 2]
            : (values[(values.Length / 2) - 1] + values[values.Length / 2]) / 2;

    private static double Ratio(double numerator, double denominator)
        => denominator > 0 ? numerator / denominator : double.PositiveInfinity;

    private static MonitoredCampaignDecision Recommend(string candidate, string reason)
        => new(candidate, "monitored-scope-only", CompleteEvidence: true, [reason]);

    private static MonitoredCampaignDecision Inconclusive(string reason)
        => new("inconclusive", "monitored-scope-only", CompleteEvidence: false, [reason]);

    private static MonitoredCampaignDecision CompleteInconclusive(string reason)
        => new("inconclusive", "monitored-scope-only", CompleteEvidence: true, [reason]);

    private sealed record ComparativeCandidateMetrics(
        long O1Committed,
        double CpuPerRecord,
        double FinalBytesPerLogicalByte,
        double ReopenSeconds);
}

internal static class MonitoredComponentEvidenceGenerator
{
    private static readonly JsonSerializerOptions IndentedJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

    private static readonly JsonSerializerOptions StrictJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    internal static async Task<MonitoredComponentEvidence> GenerateAsync(
        string outputPath,
        string runnerCommit,
        string monitorCommit,
        string attributionMapPath,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new DurableStorageExperimentException(
                "UnsupportedPlatform",
                "Monitor component evidence is Linux-only.");
        }
        if (runnerCommit.Length is < 40 or > 64
            || monitorCommit.Length is < 40 or > 64
            || !runnerCommit.All(Uri.IsHexDigit)
            || !monitorCommit.All(Uri.IsHexDigit)
            || runnerCommit.Distinct().Count() < 2
            || monitorCommit.Distinct().Count() < 2)
        {
            throw new DurableStorageExperimentException(
                "InvalidSourceCommit",
                "Component evidence requires resolved hexadecimal runner and monitor commits.");
        }
        var attributionBytes = MonitoredFile.ReadBounded(
            MonitoredPathRules.ResolveExistingFile(attributionMapPath),
            1_048_576);
        var suppliedAttribution = JsonSerializer.Deserialize<MonitoredAttributionMap>(
            attributionBytes,
            StrictJsonOptions)
            ?? throw new DurableStorageExperimentException(
                "InvalidAttributionMap",
                "The attribution map was empty.");
        if (suppliedAttribution.RuntimeOnlyDescriptorProofs.Count != 1)
        {
            throw new DurableStorageExperimentException(
                "RuntimeProofMissing",
                "Component evidence requires exactly one pre-frozen .NET double-mapper proof.");
        }

        var root = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
            $"component-{Guid.NewGuid():N}");
        var history = Path.Combine(root, "history");
        var workspace = Path.Combine(root, "workspace");
        var outputs = Path.Combine(root, "outputs");
        Directory.CreateDirectory(history);
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outputs);
        var rootFile = Path.Combine(workspace, "root-file.bin");
        await File.WriteAllBytesAsync(rootFile, new byte[128], cancellationToken).ConfigureAwait(false);

        await using var sample = await LiveSampleProcess.StartPublishedAsync(
            "CoreClrSample",
            new LiveSampleOptions
            {
                DiagnosticTimeout = TimeSpan.FromSeconds(10),
            },
            cancellationToken).ConfigureAwait(false);
        var sampleIdentity = MonitoredProcessIdentity.Capture(
            sample.Process,
            MonitoredProcessRole.Target);
        var dependencyRoots = suppliedAttribution.ReadOnlyDependencyRoots
            .Append(Path.GetDirectoryName(sample.SampleDll)!)
            .Append(Path.GetDirectoryName(
                suppliedAttribution.RuntimeOnlyDescriptorProofs[0].RuntimeNativeBinary.Path)!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var attribution = suppliedAttribution with
        {
            Roots =
            [
                new("history", history, true),
                new("workspace", workspace, true),
                new("outputs", outputs, true),
                new("evidence", root, true),
            ],
            ReadOnlyDependencyRoots = dependencyRoots,
        };
        var encoding = new MonitoredEvidenceEncoding(
            MonitoredProtocolVersions.EvidenceEncodingSchema,
            "json-lines",
            "UTF-8 without BOM",
            MonitoredSweepSummaryEncoding.Schema,
            MonitoredSweepSummaryEncoding.FieldMapSha256,
            1_024,
            2_048,
            8_388_608,
            8_388_608,
            FullPathListsRepeatedPerSweep: false,
            NonAtomicSweepsClaimInstantaneousPeaks: false);
        var monitorPath = Path.Combine(outputs, "component-monitor.jsonl");
        MonitoredSweepResult positive;
        MonitoredSweepResult postKill;
        int periodicSummaries;
        double maximumPeriodicGap;
        int maximumSummaryBytes;
        long maximumObservedSweepBytes;
        await using (var monitor = new MonitoredStorageMonitor(
            attribution,
            encoding,
            monitorPath,
            maximumEstablishedIdentities: MonitoredRunnerGeometry.MaximumSimultaneousIdentities,
            includeIdentityEvidence: true))
        {
            monitor.AddProcess(sampleIdentity);
            monitor.SetStage("component-periodic-active", activeStorageStage: true);
            _ = monitor.StartAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(450), cancellationToken).ConfigureAwait(false);
            await monitor.StopAsync().ConfigureAwait(false);
            positive = await monitor.ObserveBoundaryAsync(
                "component-positive-boundary",
                activeStorageStage: true,
                cancellationToken).ConfigureAwait(false);
            if (!positive.Summary.Complete
                || positive.Summary.Alarm is not null
                || monitor.IsIncomplete)
            {
                throw new DurableStorageExperimentException(
                    "UnexpectedComponentMonitoringIncomplete",
                    $"The positive managed-process probe was incomplete: {monitor.TerminalAlarm ?? "unknown"}.");
            }
            await monitor.ObserveBoundaryAsync(
                "component-pre-kill",
                activeStorageStage: true,
                cancellationToken).ConfigureAwait(false);
            monitor.MarkIntentionalTermination(sampleIdentity);
            OwnedProcessTerminator.KillExact(sample.Process, sampleIdentity);
            await sample.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            monitor.ConfirmTerminatedAndRemove(sampleIdentity);
            postKill = await monitor.ObserveBoundaryAsync(
                "component-post-kill",
                activeStorageStage: false,
                cancellationToken).ConfigureAwait(false);
            periodicSummaries = monitor.PeriodicSummaryRecords;
            maximumPeriodicGap = monitor.MaximumPeriodicGapMilliseconds;
            maximumSummaryBytes = monitor.MaximumSummaryRecordBytes;
            maximumObservedSweepBytes = monitor.MaximumObservedSweepBytes;
        }

        using var rootHandle = File.OpenHandle(
            rootFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var rootIdentity = LinuxStatxHandleMetadataObserver.Instance.Observe(rootHandle).Identity.Value;
        var rootObserved = positive.IdentityEvidence.Any(item =>
            string.Equals(item.Identity, rootIdentity, StringComparison.Ordinal)
            && item.SeenInRoot
            && item.Charged
            && item.Length == 128);

        var negativeControls = await ProveNegativeControlsAsync(
            root,
            attribution,
            encoding,
            cancellationToken).ConfigureAwait(false);
        var identityReuseRejected = ProveIdentityReuseRejected(
            root,
            attribution,
            encoding);

        var worstCaseSummary = await PersistWorstCaseSummaryAsync(
            root,
            encoding,
            cancellationToken).ConfigureAwait(false);
        var outputLimit = await ProveOutputLimitAsync(root, cancellationToken).ConfigureAwait(false);
        var identityLimit = await ProveIdentityLimitsAsync(
            attribution,
            encoding,
            root,
            cancellationToken).ConfigureAwait(false);
        await PersistDerivedIdentityGeometryAsync(
            root,
            cancellationToken).ConfigureAwait(false);
        var sourceRecovery = await ProveSourceRecoverySharedBudgetAsync(
            root,
            encoding,
            cancellationToken).ConfigureAwait(false);
        var packageInventories = await ObserveRepresentativeCandidatePackagesAsync(
            root,
            encoding,
            cancellationToken).ConfigureAwait(false);
        var maximumWorkerControlBytes = ProveWorkerControlEncoding();
        var rawInventory = MonitoredFile.HashTreeInventory(
            root,
            maximumEntries: 4_096,
            maximumPathUtf8Bytes: 4_096);
        MonitoredPackagePublisher.MakeImmutable(root);
        var evidence = new MonitoredComponentEvidence(
            MonitoredProtocolVersions.ComponentEvidenceSchema,
            DateTimeOffset.UtcNow,
            "Linux",
            runnerCommit,
            monitorCommit,
            MonitoredFile.HashBytes(attributionBytes),
            MonitoredSweepSummaryEncoding.Schema,
            MonitoredSweepSummaryEncoding.FieldMapSha256,
            MonitoredRunnerGeometry.MaximumSimultaneousIdentities,
            MonitoredRunnerGeometry.MaximumRootedIdentities,
            MonitoredRunnerGeometry.MaximumOwnedWritableDescriptorOnlyFiles,
            Math.Max(
                positive.Summary.IdentityCount - positive.Summary.DescriptorOnlyIdentityCount,
                packageInventories.MaximumRootedIdentitiesObserved),
            Math.Max(
                positive.Summary.DescriptorOnlyIdentityCount,
                packageInventories.MaximumDescriptorOnlyIdentitiesObserved),
            DescriptorOnlyCampaignFeasibilityEstablished: false,
            maximumSummaryBytes,
            worstCaseSummary.Utf8Bytes,
            worstCaseSummary.Sha256,
            worstCaseSummary.RelativePath,
            maximumWorkerControlBytes,
            MonitoredRunnerGeometry.MaximumRequiredSummaryRecords,
            2_048,
            checked(MonitoredRunnerGeometry.MaximumMonitorSummaryBytes
                + MonitoredRunnerGeometry.MaximumWorkerControlRecordsPerExecution
                    * (long)maximumWorkerControlBytes),
            sourceRecovery.SummaryRecords,
            sourceRecovery.SummaryBytes,
            sourceRecovery.ControlRecords,
            sourceRecovery.ControlBytes,
            sourceRecovery.SharedExhaustionObserved,
            sourceRecovery.SharedCancellationObserved,
            periodicSummaries,
            maximumPeriodicGap,
            maximumObservedSweepBytes,
            PositiveMonitoringComplete: positive.Summary.Complete,
            ManagedProcessObserved: positive.Summary.TargetPeakRssBytes > 0,
            RuntimeMemoryClassificationObserved: positive.Summary.RuntimeOnlyExcludedCount > 0,
            RuntimeMemoryExcludedBytes: positive.Summary.RuntimeOnlyExcludedBytes,
            RootFileObserved: rootObserved,
            WritableDescriptorOutsideRootRejected: negativeControls.WritableOutsideRejected,
            OpenUnlinkedDescriptorRejected: negativeControls.OpenUnlinkedRejected,
            ProcessIdentityReuseRejected: identityReuseRejected,
            KillHandoffReleasedObserverReferences: postKill.Summary.Complete
                && postKill.Summary.Alarm is null,
            RepresentativeCandidatePackageInventoriesObserved:
                packageInventories.BothCandidatesObserved,
            RepresentativeMaximumFinalPackageFilesObserved:
                packageInventories.MaximumFinalPackageFilesObserved,
            RepresentativeMaximumTransientPackageFilesObserved:
                packageInventories.MaximumTransientPackageFilesObserved,
            RepresentativeSqliteWalShmObserved:
                packageInventories.SqliteWalShmObserved,
            OutputLimitEnforced: outputLimit,
            IdentityLimitEnforced: identityLimit,
            RawEvidenceRoot: root,
            RawEvidenceFiles: rawInventory.FileCount,
            EvidenceSha256: rawInventory.Sha256);
        MonitoredFile.WriteNewJson(outputPath, evidence);
        MonitoredFile.MakeReadOnly(outputPath);
        return evidence;
    }

    private static async Task PersistDerivedIdentityGeometryAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var evidence = new
        {
            kind = "derived-enforced-geometry-not-observed-peak",
            retainedPackages = new
            {
                count = MonitoredRunnerGeometry.MaximumRetainedPackages,
                filesPerPackage = MonitoredRunnerGeometry.MaximumFilesPerPackage,
            },
            executions = new
            {
                count = MonitoredRunnerGeometry.PlannedExecutions,
                commonEvidenceFiles = MonitoredRunnerGeometry.MaximumCommonExecutionEvidenceFiles,
                recoveryExecutionCount = MonitoredRunnerGeometry.RecoveryExecutionCount,
                additionalRecoveryEvidenceFiles =
                    MonitoredRunnerGeometry.MaximumAdditionalRecoveryEvidenceFiles,
            },
            campaignControlFiles = MonitoredRunnerGeometry.MaximumCampaignControlFiles,
            readinessArtifacts =
                MonitoredRunnerGeometry.MaximumReadinessArtifactsInsideEvidenceRoot,
            activeSourceRecoveryFiles =
                MonitoredRunnerGeometry.MaximumActivePackageAndRecoveryFiles,
            derivedMaximumRootedIdentities = MonitoredRunnerGeometry.MaximumRootedIdentities,
            enforcedDescriptorOnlyIdentities =
                MonitoredRunnerGeometry.MaximumOwnedWritableDescriptorOnlyFiles,
            derivedMaximumSimultaneousIdentities =
                MonitoredRunnerGeometry.MaximumSimultaneousIdentities,
        };
        await File.WriteAllBytesAsync(
            Path.Combine(root, "derived-runner-geometry.json"),
            JsonSerializer.SerializeToUtf8Bytes(evidence, IndentedJsonOptions),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WorstCaseSummaryEvidence> PersistWorstCaseSummaryAsync(
        string root,
        MonitoredEvidenceEncoding encoding,
        CancellationToken cancellationToken)
    {
        var bytes = MonitoredSweepSummaryEncoding.EncodeLine(
            MonitoredSweepSummaryEncoding.CreateWorstCaseFixture());
        if (bytes.Length > encoding.MaximumSummaryUtf8Bytes)
        {
            throw new DurableStorageExperimentException(
                "WorstCaseSummaryEncodingExceeded",
                $"The compact worst-case real monitor summary requires {bytes.Length} bytes, exceeding the {encoding.MaximumSummaryUtf8Bytes}-byte limit.");
        }
        var relativePath = "worst-case-monitor-summary.jsonl";
        await File.WriteAllBytesAsync(
            Path.Combine(root, relativePath),
            bytes,
            cancellationToken).ConfigureAwait(false);
        return new WorstCaseSummaryEvidence(
            bytes.Length,
            MonitoredFile.HashBytes(bytes),
            relativePath);
    }

    private static async Task<bool> ProveOutputLimitAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, "output-limit.jsonl");
        var budget = new BoundedOutputBudget(
            8_388_608,
            2_048);
        await using var writer = new BoundedJsonLineWriter(
            path,
            1_024,
            2_048,
            MonitoredRunnerGeometry.MaximumMonitorSummaryBytes,
            budget);
        var emptyBytes = JsonSerializer.SerializeToUtf8Bytes(new { value = string.Empty }).Length;
        var maximumRecord = new { value = new string('x', 1_023 - emptyBytes) };
        if (JsonSerializer.SerializeToUtf8Bytes(maximumRecord).Length + 1 != 1_024)
        {
            throw new DurableStorageExperimentException(
                "OutputGeometryEvidenceFailed",
                "Could not construct the exact 1,024-byte newline-framed monitor summary fixture.");
        }
        for (var index = 0; index < 2_048; index++)
        {
            await writer.WriteAsync(maximumRecord, cancellationToken).ConfigureAwait(false);
        }
        var countLimit = false;
        try
        {
            await writer.WriteAsync(maximumRecord, cancellationToken).ConfigureAwait(false);
        }
        catch (DurableStorageExperimentException exception)
            when (exception.Code == "MonitorSummaryCountLimit")
        {
            countLimit = true;
        }

        return countLimit
            && writer.Records == 2_048
            && writer.Bytes == MonitoredRunnerGeometry.MaximumMonitorSummaryBytes
            && budget.UsedBytes == MonitoredRunnerGeometry.MaximumMonitorSummaryBytes;
    }

    private static async Task<SourceRecoveryBudgetEvidence> ProveSourceRecoverySharedBudgetAsync(
        string root,
        MonitoredEvidenceEncoding encoding,
        CancellationToken cancellationToken)
    {
        var probeRoot = Path.Combine(root, "shared-source-recovery");
        var sourceRoot = Path.Combine(probeRoot, "source-root");
        var recoveryRoot = Path.Combine(probeRoot, "recovery-root");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(recoveryRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(sourceRoot, "source.bin"),
            [1],
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(
            Path.Combine(recoveryRoot, "recovery.bin"),
            [2],
            cancellationToken).ConfigureAwait(false);

        var sharedBudget = new BoundedOutputBudget(
            encoding.MaximumStdoutUtf8Bytes,
            encoding.MaximumSummaryRecordsPerExecution,
            MonitoredRunnerGeometry.MaximumBoundarySummariesPerExecution,
            MonitoredRunnerGeometry.MaximumWorkerControlRecordsPerExecution);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var sourceAttribution = ComponentProbeAttribution(sourceRoot);
        var recoveryAttribution = ComponentProbeAttribution(recoveryRoot);
        await using (var source = new MonitoredStorageMonitor(
            sourceAttribution,
            encoding,
            Path.Combine(probeRoot, "source-monitor.jsonl"),
            sharedBudget))
        {
            source.SetStage("component-source-periodic", activeStorageStage: true);
            _ = source.StartAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(230), deadline.Token).ConfigureAwait(false);
            await source.StopAsync().ConfigureAwait(false);
            await source.ObserveBoundaryAsync(
                "component-source-boundary",
                activeStorageStage: true,
                deadline.Token).ConfigureAwait(false);
        }
        ConsumeComponentControl(
            sharedBudget,
            new MonitoredWorkerEvent("boundary", Stage: "component-source-boundary"));
        await using (var recovery = new MonitoredStorageMonitor(
            recoveryAttribution,
            encoding,
            Path.Combine(probeRoot, "recovery-monitor.jsonl"),
            sharedBudget))
        {
            recovery.SetStage("component-recovery-periodic", activeStorageStage: true);
            _ = recovery.StartAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(230), deadline.Token).ConfigureAwait(false);
            await recovery.StopAsync().ConfigureAwait(false);
            await recovery.ObserveBoundaryAsync(
                "component-recovery-boundary",
                activeStorageStage: true,
                deadline.Token).ConfigureAwait(false);
        }
        ConsumeComponentControl(
            sharedBudget,
            new MonitoredWorkerEvent("boundary", Stage: "component-recovery-boundary"));

        var sharedExhaustionObserved = await ProveSharedBudgetExhaustionAsync(
            probeRoot,
            encoding,
            sourceAttribution,
            recoveryAttribution,
            cancellationToken).ConfigureAwait(false);
        var sharedCancellationObserved = await ProveSharedDeadlineCancellationAsync(
            probeRoot,
            encoding,
            sourceAttribution,
            recoveryAttribution,
            cancellationToken).ConfigureAwait(false);
        var evidence = new SourceRecoveryBudgetEvidence(
            sharedBudget.SummaryRecords,
            sharedBudget.SummaryBytes,
            sharedBudget.ControlRecords,
            sharedBudget.ControlBytes,
            sharedExhaustionObserved,
            sharedCancellationObserved);
        MonitoredFile.WriteNewJson(
            Path.Combine(probeRoot, "shared-budget-observation.json"),
            evidence);
        return evidence;
    }

    private static MonitoredAttributionMap ComponentProbeAttribution(string root)
        => new(
            MonitoredProtocolVersions.AttributionSchema,
            [new("workspace", root, true)],
            ["package", "package-staging"],
            ["recovery", "recovery-staging"],
            [AppContext.BaseDirectory],
            ["/dev/null", "/dev/urandom"],
            [],
            4_096,
            4_096,
            MonitoredRunnerGeometry.MaximumOwnedWritableDescriptorOnlyFiles);

    private static void ConsumeComponentControl(
        BoundedOutputBudget budget,
        MonitoredWorkerEvent workerEvent)
    {
        var encoded = MonitoredWorkerEventWriter.EncodeLine(workerEvent);
        budget.ConsumeControl(encoded.Length);
    }

    private static async Task<bool> ProveSharedBudgetExhaustionAsync(
        string probeRoot,
        MonitoredEvidenceEncoding encoding,
        MonitoredAttributionMap sourceAttribution,
        MonitoredAttributionMap recoveryAttribution,
        CancellationToken cancellationToken)
    {
        var budget = new BoundedOutputBudget(
            maximumBytes: 8_192,
            maximumSummaryRecords: 1,
            maximumBoundarySummaryRecords: 1,
            maximumControlRecords: 1);
        await using var source = new MonitoredStorageMonitor(
            sourceAttribution,
            encoding,
            Path.Combine(probeRoot, "exhaustion-source.jsonl"),
            budget);
        await using var recovery = new MonitoredStorageMonitor(
            recoveryAttribution,
            encoding,
            Path.Combine(probeRoot, "exhaustion-recovery.jsonl"),
            budget);
        await source.ObserveBoundaryAsync(
            "exhaustion-source",
            activeStorageStage: true,
            cancellationToken).ConfigureAwait(false);
        var summaryRejected = false;
        try
        {
            await recovery.ObserveBoundaryAsync(
                "exhaustion-recovery",
                activeStorageStage: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DurableStorageExperimentException exception)
            when (exception.Code is "MonitorSummaryCountLimit"
                or "MonitorBoundarySummaryCountLimit")
        {
            summaryRejected = true;
        }

        ConsumeComponentControl(
            budget,
            new MonitoredWorkerEvent("boundary", Stage: "exhaustion-source"));
        var controlRejected = false;
        try
        {
            ConsumeComponentControl(
                budget,
                new MonitoredWorkerEvent("boundary", Stage: "exhaustion-recovery"));
        }
        catch (DurableStorageExperimentException exception)
            when (exception.Code == "WorkerControlRecordLimit")
        {
            controlRejected = true;
        }
        return summaryRejected && controlRejected;
    }

    private static async Task<bool> ProveSharedDeadlineCancellationAsync(
        string probeRoot,
        MonitoredEvidenceEncoding encoding,
        MonitoredAttributionMap sourceAttribution,
        MonitoredAttributionMap recoveryAttribution,
        CancellationToken cancellationToken)
    {
        var budget = new BoundedOutputBudget(
            encoding.MaximumStdoutUtf8Bytes,
            encoding.MaximumSummaryRecordsPerExecution,
            MonitoredRunnerGeometry.MaximumBoundarySummariesPerExecution,
            MonitoredRunnerGeometry.MaximumWorkerControlRecordsPerExecution);
        using var outer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(outer.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        await using var source = new MonitoredStorageMonitor(
            sourceAttribution,
            encoding,
            Path.Combine(probeRoot, "cancel-source.jsonl"),
            budget);
        await source.ObserveBoundaryAsync(
            "cancel-source",
            activeStorageStage: true,
            deadline.Token).ConfigureAwait(false);
        outer.Cancel();
        await using var recovery = new MonitoredStorageMonitor(
            recoveryAttribution,
            encoding,
            Path.Combine(probeRoot, "cancel-recovery.jsonl"),
            budget);
        var cancelledMonitors = 0;
        foreach (var monitor in new[] { source, recovery })
        {
            try
            {
                await monitor.ObserveBoundaryAsync(
                    "cancel-shared-deadline",
                    activeStorageStage: true,
                    deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                cancelledMonitors++;
            }
        }
        return cancelledMonitors == 2;
    }

    private static async Task<bool> ProveIdentityLimitsAsync(
        MonitoredAttributionMap attribution,
        MonitoredEvidenceEncoding encoding,
        string root,
        CancellationToken cancellationToken)
    {
        var limited = attribution with { MaximumTrackedIdentitiesPerSweep = 1 };
        var first = Path.Combine(root, "workspace", "identity-1");
        var second = Path.Combine(root, "workspace", "identity-2");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [2]);
        var path = Path.Combine(root, "outputs", "identity-limit.jsonl");
        using var monitor = new AsyncDisposeBridge(new MonitoredStorageMonitor(limited, encoding, path));
        var result = monitor.Value.Sweep();
        var hardLimit = result.Errors.Any(static error => error is
            "TrackedIdentityLimitExceeded"
            or "TrackedPathLimitExceeded"
            or "DescriptorIdentityLimitExceeded");
        var descriptorRoot = Path.Combine(root, "descriptor-limit");
        Directory.CreateDirectory(descriptorRoot);
        var descriptors = new List<FileStream>();
        try
        {
            for (var index = 0;
                 index <= MonitoredRunnerGeometry.MaximumOwnedWritableDescriptorOnlyFiles;
                 index++)
            {
                var file = Path.Combine(
                    Path.GetTempPath(),
                    $"dc5-descriptor-limit-{Guid.NewGuid():N}.tmp");
                var stream = new FileStream(
                    file,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete);
                await stream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                descriptors.Add(stream);
            }
            var descriptorAttribution = attribution with
            {
                Roots = [new("workspace", descriptorRoot, true)],
            };
            await using var descriptorMonitor = new MonitoredStorageMonitor(
                descriptorAttribution,
                encoding,
                Path.Combine(root, "outputs", "descriptor-limit.jsonl"),
                includeIdentityEvidence: true);
            using var self = Process.GetCurrentProcess();
            descriptorMonitor.AddProcess(MonitoredProcessIdentity.Capture(
                self,
                MonitoredProcessRole.Harness));
            var descriptorSweep = descriptorMonitor.Sweep();
            return hardLimit
                && descriptorSweep.Errors.Contains("DescriptorOnlyIdentityLimitExceeded");
        }
        finally
        {
            foreach (var descriptor in descriptors)
            {
                var file = descriptor.Name;
                await descriptor.DisposeAsync().ConfigureAwait(false);
                File.Delete(file);
            }
        }
    }

    private static async Task<RepresentativePackageInventoryEvidence>
        ObserveRepresentativeCandidatePackagesAsync(
            string root,
            MonitoredEvidenceEncoding encoding,
            CancellationToken cancellationToken)
    {
        var candidates = new List<RepresentativeCandidateInventory>(2);
        var maximumRootedIdentities = 0;
        var maximumDescriptorOnlyIdentities = 0;
        foreach (var candidate in new[] { "A", "B" })
        {
            var candidateRoot = Path.Combine(root, "representative-packages", candidate);
            var executionRoot = Path.Combine(candidateRoot, "execution");
            var stagingRoot = Path.Combine(candidateRoot, "package-staging");
            var packageRoot = Path.Combine(candidateRoot, "package");
            Directory.CreateDirectory(executionRoot);
            Directory.CreateDirectory(stagingRoot);
            var execution = MonitoredExecutionPlanner.Expand().Single(item =>
                item.CaseId == "Q1" && item.Candidate == candidate);
            var descriptor = new MonitoredWorkerDescriptor(
                MonitoredProtocolVersions.WorkerDescriptorSchema,
                MonitoredWorkerMode.Execute,
                root,
                Path.Combine(candidateRoot, "component-manifest.json"),
                MonitoredFile.HashBytes("component-manifest"u8),
                execution,
                executionRoot,
                stagingRoot,
                packageRoot,
                $"component-capture-{candidate}",
                $"component-artifact-{candidate}",
                SourcePackageRoot: null,
                SourceCaptureId: null,
                SourceArtifactId: null,
                ConfirmedAcknowledgements: null,
                KnownOfferedSequences: null);
            var factory = MonitoredAdapterRegistry.Require(candidate);
            var limits = new DurableCounterPipelineLimits(
                BatchMaxAge: TimeSpan.FromMilliseconds(100));
            using var configurationDocument = JsonDocument.Parse("""{"profile":"P1"}""");
            var stages = new List<RepresentativeInventoryStage>(8);
            var walShmObserved = false;
            var maximumTransientFiles = 0;
            void CaptureStage(string stage)
            {
                var activeRoot = Directory.Exists(stagingRoot) ? stagingRoot : packageRoot;
                var files = Directory.Exists(activeRoot)
                    ? MonitoredPathRules.EnumerateFilesRejectingLinks(
                            activeRoot,
                            maximumEntries: 64,
                            maximumPathUtf8Bytes: 4_096)
                        .Select(path => Path.GetRelativePath(activeRoot, path)
                            .Replace(Path.DirectorySeparatorChar, '/'))
                        .Order(StringComparer.Ordinal)
                        .ToArray()
                    : [];
                maximumTransientFiles = Math.Max(maximumTransientFiles, files.Length);
                walShmObserved |= files.Any(static path =>
                    path.EndsWith("-wal", StringComparison.Ordinal)
                    || path.EndsWith("-shm", StringComparison.Ordinal));
                stages.Add(new RepresentativeInventoryStage(stage, files));
            }

            var attribution = ComponentProbeAttribution(candidateRoot);
            await using var monitor = new MonitoredStorageMonitor(
                attribution,
                encoding,
                Path.Combine(candidateRoot, "inventory-monitor.jsonl"),
                maximumEstablishedIdentities:
                    MonitoredRunnerGeometry.MaximumSimultaneousIdentities);
            async Task ObserveStageAsync(string stage, bool active)
            {
                CaptureStage(stage);
                var sweep = await monitor.ObserveBoundaryAsync(
                    stage,
                    active,
                    cancellationToken).ConfigureAwait(false);
                if (!sweep.Summary.Complete || sweep.Summary.Alarm is not null)
                {
                    throw new DurableStorageExperimentException(
                        "RepresentativePackageMonitoringIncomplete",
                        $"Representative candidate {candidate} package monitoring failed at '{stage}'.");
                }
                maximumRootedIdentities = Math.Max(
                    maximumRootedIdentities,
                    sweep.Summary.IdentityCount - sweep.Summary.DescriptorOnlyIdentityCount);
                maximumDescriptorOnlyIdentities = Math.Max(
                    maximumDescriptorOnlyIdentities,
                    sweep.Summary.DescriptorOnlyIdentityCount);
            }

            var adapter = factory.Create(new DurableStorageAdapterCreateRequest(
                descriptor.CaptureId,
                descriptor.ArtifactId,
                stagingRoot,
                configurationDocument.RootElement.Clone(),
                limits,
                NoDurableStorageFaults.Instance));
            await ObserveStageAsync("representative-after-create", active: true)
                .ConfigureAwait(false);
            await using var pipeline = new DurableCounterPipeline(
                limits,
                new DurableCounterGlobalBudget(limits),
                adapter);
            foreach (var observation in DurableCounterFixture.GenerateQ1().Take(64))
            {
                if (pipeline.TryWrite(observation).Status != DurableCounterOfferStatus.Accepted)
                {
                    throw new DurableStorageExperimentException(
                        "RepresentativePackageAdmissionFailed",
                        $"Representative candidate {candidate} rejected a valid fixture record.");
                }
            }
            var drain = await pipeline.DrainAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (!drain.CompletedWithinTimeout)
            {
                throw new DurableStorageExperimentException(
                    "RepresentativePackageDrainFailed",
                    $"Representative candidate {candidate} did not drain its fixed batch.");
            }
            await ObserveStageAsync("representative-after-drain", active: true)
                .ConfigureAwait(false);
            var finalization = Stopwatch.StartNew();
            if (!await pipeline.FinalizeAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
            {
                throw new DurableStorageExperimentException(
                    "RepresentativePackageFinalizeFailed",
                    $"Representative candidate {candidate} did not finalize its fixed batch.");
            }
            var accounting = pipeline.GetAccounting();
            var quality = new DurableCounterQuery([], limits).Quality(accounting);
            var preSeal = await adapter.FinalizePreSealAsync(
                quality,
                cancellationToken).ConfigureAwait(false);
            await ObserveStageAsync("representative-after-preseal", active: true)
                .ConfigureAwait(false);
            if (adapter is IAsyncDisposable asyncAdapter)
            {
                await asyncAdapter.DisposeAsync().ConfigureAwait(false);
            }
            await ObserveStageAsync("representative-after-adapter-dispose", active: true)
                .ConfigureAwait(false);
            var published = await MonitoredPackagePublisher.PublishRepresentativeAsync(
                descriptor,
                factory,
                preSeal,
                accounting,
                finalization,
                logicalCommittedBytes: 64 * 512,
                ObserveStageAsync).ConfigureAwait(false);
            try
            {
                var summary = await published.Reader.SummaryAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (summary.Sum(static item => item.RetainedCount) != 64)
                {
                    throw new DurableStorageExperimentException(
                        "RepresentativePackageQueryMismatch",
                        $"Representative candidate {candidate} did not reopen all fixed records.");
                }
            }
            finally
            {
                await published.Reader.DisposeAsync().ConfigureAwait(false);
            }
            CaptureStage("representative-final-package");
            var finalFiles = MonitoredPackagePublisher.HashTree(packageRoot)
                .Select(static item => item.Path)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var finalHasWalShm = finalFiles.Any(static path =>
                path.EndsWith("-wal", StringComparison.Ordinal)
                || path.EndsWith("-shm", StringComparison.Ordinal));
            candidates.Add(new RepresentativeCandidateInventory(
                candidate,
                finalFiles,
                maximumTransientFiles,
                walShmObserved,
                finalHasWalShm,
                stages));
        }

        var evidence = new RepresentativePackageInventoryEvidence(
            BothCandidatesObserved: candidates.Count == 2
                && candidates.All(static item =>
                    item.FinalFiles.Count is > 0
                        and <= MonitoredRunnerGeometry.MaximumFilesPerPackage
                    && !item.FinalHasWalShm),
            MaximumFinalPackageFilesObserved:
                candidates.Max(static item => item.FinalFiles.Count),
            MaximumTransientPackageFilesObserved:
                candidates.Max(static item => item.MaximumTransientFiles),
            SqliteWalShmObserved: candidates.Single(static item => item.Candidate == "A")
                .WalShmObserved,
            MaximumRootedIdentitiesObserved: maximumRootedIdentities,
            MaximumDescriptorOnlyIdentitiesObserved: maximumDescriptorOnlyIdentities,
            Candidates: candidates);
        MonitoredFile.WriteNewJson(
            Path.Combine(root, "representative-package-inventories.json"),
            evidence);
        return evidence;
    }

    private static async Task<NegativeControlEvidence> ProveNegativeControlsAsync(
        string root,
        MonitoredAttributionMap attribution,
        MonitoredEvidenceEncoding encoding,
        CancellationToken cancellationToken)
    {
        var linkedPath = Path.Combine(Path.GetTempPath(), $"dc5-monitor-{Guid.NewGuid():N}.tmp");
        var unlinkedPath = Path.Combine(Path.GetTempPath(), $"dc5-monitor-{Guid.NewGuid():N}.tmp");
        await using var linked = new FileStream(
            linkedPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        await using var unlinked = new FileStream(
            unlinkedPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        try
        {
            await linked.WriteAsync(new byte[173], cancellationToken).ConfigureAwait(false);
            await unlinked.WriteAsync(new byte[257], cancellationToken).ConfigureAwait(false);
            await linked.FlushAsync(cancellationToken).ConfigureAwait(false);
            await unlinked.FlushAsync(cancellationToken).ConfigureAwait(false);
            var linkedIdentity = LinuxStatxHandleMetadataObserver.Instance.Observe(
                linked.SafeFileHandle).Identity.Value;
            var unlinkedIdentity = LinuxStatxHandleMetadataObserver.Instance.Observe(
                unlinked.SafeFileHandle).Identity.Value;
            File.Delete(unlinkedPath);

            var negativeAttribution = attribution with
            {
                Roots = [new("workspace", Path.Combine(root, "negative-root"), true)],
            };
            Directory.CreateDirectory(negativeAttribution.Roots[0].Path);
            await using var monitor = new MonitoredStorageMonitor(
                negativeAttribution,
                encoding,
                Path.Combine(root, "outputs", "negative-monitor.jsonl"),
                includeIdentityEvidence: true);
            using var self = Process.GetCurrentProcess();
            monitor.AddProcess(MonitoredProcessIdentity.Capture(
                self,
                MonitoredProcessRole.Harness));
            var sweep = await monitor.ObserveBoundaryAsync(
                "intentional-unclassified-negative-control",
                activeStorageStage: true,
                cancellationToken).ConfigureAwait(false);
            var linkedObserved = sweep.IdentityEvidence.Any(item =>
                item.Identity == linkedIdentity
                && item.SeenInDescriptor
                && !item.SeenInRoot
                && item.Charged
                && !item.IsUnlinked
                && item.Length == 173);
            var unlinkedObserved = sweep.IdentityEvidence.Any(item =>
                item.Identity == unlinkedIdentity
                && item.SeenInDescriptor
                && !item.SeenInRoot
                && item.Charged
                && item.IsUnlinked
                && item.Length == 257);
            return new NegativeControlEvidence(
                WritableOutsideRejected: linkedObserved && !sweep.Summary.Complete,
                OpenUnlinkedRejected: unlinkedObserved && !sweep.Summary.Complete);
        }
        finally
        {
            File.Delete(linkedPath);
            if (File.Exists(unlinkedPath))
            {
                File.Delete(unlinkedPath);
            }
        }
    }

    private static bool ProveIdentityReuseRejected(
        string root,
        MonitoredAttributionMap attribution,
        MonitoredEvidenceEncoding encoding)
    {
        var identityAttribution = attribution with
        {
            Roots = [new("workspace", Path.Combine(root, "identity-reuse-root"), true)],
        };
        Directory.CreateDirectory(identityAttribution.Roots[0].Path);
        using var monitor = new AsyncDisposeBridge(new MonitoredStorageMonitor(
            identityAttribution,
            encoding,
            Path.Combine(root, "outputs", "identity-reuse.jsonl")));
        using var self = Process.GetCurrentProcess();
        var identity = MonitoredProcessIdentity.Capture(self, MonitoredProcessRole.Harness);
        monitor.Value.AddProcess(identity);
        try
        {
            monitor.Value.AddProcess(identity with
            {
                LinuxStartTimeTicks = identity.LinuxStartTimeTicks + 1,
            });
            return false;
        }
        catch (DurableStorageExperimentException exception)
            when (exception.Code == "ProcessIdentityAmbiguous")
        {
            return true;
        }
    }

    private static int ProveWorkerControlEncoding()
    {
        var events = new[]
        {
            new MonitoredWorkerEvent(
                "process",
                ProcessId: int.MaxValue,
                ProcessStartTimeTicks: ulong.MaxValue,
                ProcessRole: "diagnostic"),
            new MonitoredWorkerEvent(
                "boundary",
                Stage: new string('b', 64),
                ActiveStorageStage: true),
            new MonitoredWorkerEvent(
                "barrier",
                Stage: nameof(DurableStorageFaultBarrier.AfterCommitBeforeAcknowledgement),
                ActiveStorageStage: true,
                Barrier: nameof(DurableStorageFaultBarrier.AfterCommitBeforeAcknowledgement),
                BatchOrdinal: int.MaxValue,
                FirstSequence: long.MaxValue - 1,
                LastSequence: long.MaxValue),
            new MonitoredWorkerEvent("failed", Message: new string('m', 768)),
        };
        return events.Max(static item => MonitoredWorkerEventWriter.EncodeLine(item).Length);
    }

    private sealed class AsyncDisposeBridge(IAsyncDisposable value) : IDisposable
    {
        internal MonitoredStorageMonitor Value { get; } = (MonitoredStorageMonitor)value;

        public void Dispose() => value.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed record NegativeControlEvidence(
        bool WritableOutsideRejected,
        bool OpenUnlinkedRejected);

    private sealed record WorstCaseSummaryEvidence(
        int Utf8Bytes,
        string Sha256,
        string RelativePath);

    private sealed record SourceRecoveryBudgetEvidence(
        int SummaryRecords,
        long SummaryBytes,
        int ControlRecords,
        long ControlBytes,
        bool SharedExhaustionObserved,
        bool SharedCancellationObserved);

    private sealed record RepresentativeInventoryStage(
        string Stage,
        IReadOnlyList<string> Files);

    private sealed record RepresentativeCandidateInventory(
        string Candidate,
        IReadOnlyList<string> FinalFiles,
        int MaximumTransientFiles,
        bool WalShmObserved,
        bool FinalHasWalShm,
        IReadOnlyList<RepresentativeInventoryStage> Stages);

    private sealed record RepresentativePackageInventoryEvidence(
        bool BothCandidatesObserved,
        int MaximumFinalPackageFilesObserved,
        int MaximumTransientPackageFilesObserved,
        bool SqliteWalShmObserved,
        int MaximumRootedIdentitiesObserved,
        int MaximumDescriptorOnlyIdentitiesObserved,
        IReadOnlyList<RepresentativeCandidateInventory> Candidates);
}
