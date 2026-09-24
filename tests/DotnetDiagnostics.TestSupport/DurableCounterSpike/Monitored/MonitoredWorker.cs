using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Tests.DurableCounterSpike.AppendFirst;
using DotnetDiagnostics.Core.Tests.DurableCounterSpike.Sqlite;
using DotnetDiagnostics.TestSupport;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.Monitored;

internal enum MonitoredWorkerMode
{
    Execute,
    Recover,
}

internal sealed record MonitoredWorkerDescriptor(
    string Schema,
    MonitoredWorkerMode Mode,
    string RepositoryRoot,
    string ManifestPath,
    string ManifestSha256,
    MonitoredExecutionSpec Execution,
    string ExecutionRoot,
    string PackageStagingRoot,
    string PackageRoot,
    string CaptureId,
    string ArtifactId,
    string? SourcePackageRoot,
    string? SourceCaptureId,
    string? SourceArtifactId,
    IReadOnlyList<long>? ConfirmedAcknowledgements,
    IReadOnlyList<long>? KnownOfferedSequences);

internal sealed record MonitoredWorkerEvent(
    string Type,
    string? Stage = null,
    bool? ActiveStorageStage = null,
    int? ProcessId = null,
    ulong? ProcessStartTimeTicks = null,
    string? ProcessRole = null,
    string? Barrier = null,
    int? BatchOrdinal = null,
    long? FirstSequence = null,
    long? LastSequence = null,
    string? Message = null);

internal sealed record MonitoredRequestMetrics(
    int Scheduled,
    int SkippedAtConcurrencyLimit,
    int Completed,
    int Succeeded,
    int Failed,
    int RetainedSamples,
    double? P50Milliseconds,
    double? P95Milliseconds,
    int EpisodeScheduled,
    int EpisodeSkippedAtConcurrencyLimit,
    int EpisodeCompleted,
    int EpisodeSucceeded,
    int EpisodeFailed,
    double SchedulingElapsedSeconds,
    double EpisodeElapsedSeconds);

internal sealed record MonitoredWorkerResult(
    string Schema,
    int Ordinal,
    string CaseId,
    string Candidate,
    string Outcome,
    string? FailureCode,
    string? FailureMessage,
    long Offered,
    long Admitted,
    long Rejected,
    long Committed,
    long FailedAfterAdmission,
    long UnknownCommitOutcome,
    long? SourceMalformedPayloads,
    long? SourceAdmissionInvalid,
    long? SourceRejectedNewKeys,
    long? SourceAdmissionRejected,
    long LogicalCommittedBytes,
    long PackageFinalBytes,
    double OfferSeconds,
    double DrainSeconds,
    double FinalizationSeconds,
    double ReopenAndFirstQuerySeconds,
    double DiagnosticCpuSeconds,
    bool StorageFaultTriggered,
    bool ConcurrentCaptureRejected,
    bool RecoveryInvariantSatisfied,
    int RecoveredRecords,
    MonitoredRequestMetrics? Requests,
    int? SourceTicks,
    int? SourceKeys,
    int? ScheduledSourceOffers,
    int? AttemptedSourceOffers,
    double? AchievedSourceOfferRatio,
    string SourceCoverage,
    DateTimeOffset? TargetStartedAt,
    DateTimeOffset? CounterSessionStartedAt,
    double? CounterCollectionSeconds,
    bool HasDurablePackage,
    string RecommendationScope);

internal static class MonitoredDeadlineRules
{
    internal static bool WithinFinalizationAndReopenBudgets(
        TimeSpan finalization,
        TimeSpan reopenAndFirstQuery)
        => finalization <= TimeSpan.FromSeconds(10)
            && reopenAndFirstQuery <= TimeSpan.FromSeconds(2);
}

internal static class MonitoredReopenMeasurement
{
    internal static async Task<TimeSpan> MeasureAsync(
        Func<Task> observeBefore,
        Func<Task> validateOpenAndQuery,
        Func<Task> observeAfter)
    {
        await observeBefore().ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        await validateOpenAndQuery().ConfigureAwait(false);
        stopwatch.Stop();
        await observeAfter().ConfigureAwait(false);
        return stopwatch.Elapsed;
    }
}

internal static class MonitoredRecoveryInvariant
{
    internal static bool Validate(
        string caseId,
        IReadOnlyList<long> acknowledgements,
        IReadOnlyList<long> offered,
        IReadOnlyList<long> recovered)
    {
        var subsetInvariant = acknowledgements.All(recovered.Contains)
            && recovered.All(offered.Contains)
            && recovered.Count == recovered.Distinct().Count();
        var completeBatches = recovered.Count % 64 == 0
            && recovered.SequenceEqual(
                Enumerable.Range(1, recovered.Count).Select(static value => (long)value));
        var barrierExpectation = caseId switch
        {
            "F2" => recovered.Count == 64,
            "F3" => recovered.Count == 128,
            "F4" => recovered.SequenceEqual(offered),
            _ => false,
        };
        return subsetInvariant && completeBatches && barrierExpectation;
    }
}

internal sealed record MonitoredConcurrentCaptureProbe(
    bool Rejected,
    TimeSpan Elapsed);

internal sealed record MonitoredOfferScheduleResult(
    long AcceptedBytes,
    int ScheduledOffers,
    int AttemptedOffers,
    TimeSpan Elapsed,
    double AchievedOfferRatio);

internal sealed class MonitoredSourceAdmissionEvidence
{
    private readonly HashSet<(string Provider, string Name)> _keys = [];

    internal int OfferedTicks { get; private set; }
    internal int SourceKeys => _keys.Count;
    internal long MalformedPayloads { get; private set; }
    internal long AdmissionInvalid { get; private set; }
    internal long RejectedNewKeys { get; private set; }
    internal long OtherRejected { get; private set; }

    internal void RecordMalformedPayload() => MalformedPayloads++;

    internal void Record(
        DurableCounterObservation observation,
        DurableCounterOfferResult result)
    {
        OfferedTicks++;
        if (_keys.Count < 129)
        {
            _keys.Add((observation.Counter.Provider, observation.Counter.Name));
        }
        switch (result.Status)
        {
            case DurableCounterOfferStatus.Accepted:
                break;
            case DurableCounterOfferStatus.Invalid:
                AdmissionInvalid++;
                break;
            case DurableCounterOfferStatus.TooManyKeys:
                RejectedNewKeys++;
                break;
            default:
                OtherRejected++;
                break;
        }
    }
}

internal static class MonitoredConcurrentCaptureGate
{
    internal static async Task<MonitoredConcurrentCaptureProbe> ProbeAsync(
        DurableCounterPipelineLimits limits,
        IDurableCounterSink firstSink,
        Func<IDurableCounterSink> secondSinkFactory)
    {
        var global = new DurableCounterGlobalBudget(limits);
        var writerGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = new DurableCounterPipeline(
            limits,
            global,
            firstSink,
            writerStartGate: writerGate.Task);
        if (first.TryWrite(DurableCounterFixture.Generate(1)).Status
            != DurableCounterOfferStatus.Accepted)
        {
            throw new DurableStorageExperimentException(
                "F5FirstCaptureAdmissionFailed",
                "The first capture did not establish the active-capture gate.");
        }

        var stopwatch = Stopwatch.StartNew();
        var rejected = false;
        try
        {
            await using var secondSink = new AsyncDisposableSink(secondSinkFactory());
            await using var second = new DurableCounterPipeline(
                limits,
                global,
                secondSink.Value);
        }
        catch (DurableCounterPipelineException exception)
            when (exception.Code == "ActiveCaptureLimit")
        {
            rejected = true;
        }
        stopwatch.Stop();
        writerGate.TrySetResult();
        await first.CancelAndDrainAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return new MonitoredConcurrentCaptureProbe(rejected, stopwatch.Elapsed);
    }

    private sealed class AsyncDisposableSink : IAsyncDisposable
    {
        private readonly IDurableCounterSink _value;

        internal AsyncDisposableSink(IDurableCounterSink value)
        {
            _value = value;
            Value = value;
        }

        internal IDurableCounterSink Value { get; }

        public ValueTask DisposeAsync()
            => _value is IAsyncDisposable disposable
                ? disposable.DisposeAsync()
                : ValueTask.CompletedTask;
    }
}

internal static class MonitoredWorkerEventWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object Gate = new();

    internal static void Write(MonitoredWorkerEvent workerEvent)
    {
        var bytes = EncodeLine(workerEvent);
        lock (Gate)
        {
            Console.Out.Write(System.Text.Encoding.UTF8.GetString(bytes));
            Console.Out.Flush();
        }
    }

    internal static byte[] EncodeLine(MonitoredWorkerEvent workerEvent)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(workerEvent, JsonOptions);
        if (payload.Length + 1 > 1_024)
        {
            throw new DurableStorageExperimentException(
                "WorkerEventLimit",
                "A newline-framed worker control event exceeded the 1,024-byte bound.");
        }
        var line = new byte[payload.Length + 1];
        payload.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        return line;
    }
}

internal static class MonitoredWorkerControl
{
    internal static async Task ObserveBoundaryAsync(string name, bool activeStorageStage)
    {
        MonitoredWorkerEventWriter.Write(new MonitoredWorkerEvent(
            "boundary",
            Stage: name,
            ActiveStorageStage: activeStorageStage));
        var response = await Console.In.ReadLineAsync().ConfigureAwait(false);
        ValidateBoundaryRelease(name, response);
    }

    internal static void ValidateBoundaryRelease(string name, string? response)
    {
        if (!string.Equals(response, $"release:{name}", StringComparison.Ordinal))
        {
            throw new DurableStorageExperimentException(
                "BoundaryReleaseMismatch",
                $"Harness did not release worker boundary '{name}' with the exact control token.");
        }
    }

    internal static async Task AnnounceProcessTerminationAsync(MonitoredProcessIdentity identity,
        CancellationToken cancellationToken = default)
    {
        MonitoredWorkerEventWriter.Write(new MonitoredWorkerEvent(
            "process-terminating",
            ProcessId: identity.ProcessId,
            ProcessStartTimeTicks: identity.LinuxStartTimeTicks,
            ProcessRole: identity.Role.ToString().ToLowerInvariant()));
        var expected = FormattableString.Invariant(
            $"release:process-termination:{identity.ProcessId}:{identity.LinuxStartTimeTicks}");
        // Console.In can implement ReadLineAsync synchronously. Bound the handoff
        // even when the harness never replies; failure ends this owned worker.
        var response = await Task.Run(() => Console.In.ReadLineAsync(), CancellationToken.None)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(response, expected, StringComparison.Ordinal))
        {
            throw new DurableStorageExperimentException(
                "ProcessTerminationReleaseMismatch",
                "Harness did not release the exact announced process identity for termination.");
        }
    }
}

internal static class MonitoredWorkerExecutor
{
    internal const string LiveReadinessPath = "/weatherforecast";
    private static readonly string[] LiveCounterProviderNames =
    [
        "System.Runtime",
        "Microsoft.AspNetCore.Hosting",
        "Microsoft-AspNetCore-Server-Kestrel",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    internal static async Task<int> RunAsync(string descriptorPath, bool prevalidation = false)
    {
        try
        {
            if (prevalidation)
            {
                PrevalidationProtocol.Require(await Console.In.ReadLineAsync().ConfigureAwait(false)
                    == "prevalidation-start", "PrevalidationWorkerStartRelease");
            }
            var resolvedDescriptorPath = MonitoredPathRules.ResolveExistingFile(descriptorPath);
            var descriptor = JsonSerializer.Deserialize<MonitoredWorkerDescriptor>(
                MonitoredFile.ReadBounded(
                    resolvedDescriptorPath,
                    1_048_576),
                JsonOptions)
                ?? throw Error("InvalidWorkerDescriptor", "The worker descriptor was empty.");
            ValidateDescriptor(descriptor, prevalidation);
            IMonitoredExecutionManifest settings;
            if (prevalidation)
            {
                settings = PrevalidationWorkerAdmission.Validate(descriptor, resolvedDescriptorPath);
            }
            else
            {
                var validated = MonitoredRunManifestValidator.Validate(
                    descriptor.RepositoryRoot,
                    descriptor.ManifestPath,
                    requireAuthorization: true);
                if (!string.Equals(
                        validated.ManifestSha256,
                        descriptor.ManifestSha256,
                        StringComparison.Ordinal))
                {
                    throw Error(
                        "WorkerManifestHashMismatch",
                        "The worker descriptor does not identify the validated resolved manifest.");
                }
                var expected = validated.Manifest.Plan.Executions.SingleOrDefault(
                    execution => execution.Ordinal == descriptor.Execution.Ordinal)
                    ?? throw Error("UnknownWorkerExecution", "The worker execution ordinal is not in the frozen plan.");
                if (expected != descriptor.Execution)
                {
                    throw Error(
                        "WorkerExecutionMismatch",
                        "The worker descriptor changed a frozen execution entry.");
                }
                ValidateDescriptorOwnership(
                    descriptor,
                    resolvedDescriptorPath,
                    validated,
                    expected);
                settings = validated.Manifest;
            }

            using var self = Process.GetCurrentProcess();
            var identity = MonitoredProcessIdentity.Capture(self, MonitoredProcessRole.Diagnostic);
            MonitoredWorkerEventWriter.Write(new MonitoredWorkerEvent(
                "process",
                ProcessId: identity.ProcessId,
                ProcessStartTimeTicks: identity.LinuxStartTimeTicks,
                ProcessRole: "diagnostic"));

            var startedCpu = self.TotalProcessorTime;
            var result = descriptor.Mode switch
            {
                MonitoredWorkerMode.Execute => await ExecuteAsync(descriptor, settings).ConfigureAwait(false),
                MonitoredWorkerMode.Recover => await RecoverAsync(descriptor, settings).ConfigureAwait(false),
                _ => throw Error("UnsupportedWorkerMode", "The worker mode is unsupported."),
            };
            self.Refresh();
            result = result with
            {
                DiagnosticCpuSeconds = (self.TotalProcessorTime - startedCpu).TotalSeconds,
            };
            var resultPath = Path.Combine(descriptor.ExecutionRoot, "worker-result.json");
            MonitoredFile.WriteNewJson(resultPath, result);
            MonitoredFile.MakeReadOnly(resultPath);
            await MonitoredWorkerControl.AnnounceProcessTerminationAsync(identity)
                .ConfigureAwait(false);
            MonitoredWorkerEventWriter.Write(new MonitoredWorkerEvent(
                "completed",
                Message: result.Outcome));
            return string.Equals(result.Outcome, "pass", StringComparison.Ordinal) ? 0 : 3;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            var code = exception switch
            {
                DurableStorageExperimentException storage => storage.Code,
                DurableCounterPipelineException pipeline => pipeline.Code,
                _ => exception.GetType().Name,
            };
            MonitoredWorkerEventWriter.Write(new MonitoredWorkerEvent(
                "failed",
                Message: $"{code}:{Bound(exception.Message, 512)}"));
            return 2;
        }
    }

    private static async Task<MonitoredWorkerResult> ExecuteAsync(
        MonitoredWorkerDescriptor descriptor,
        IMonitoredExecutionManifest manifest)
    {
        Directory.CreateDirectory(descriptor.ExecutionRoot);
        return descriptor.Execution.Candidate switch
        {
            "shared" => await ExecuteSharedReadinessAsync(descriptor).ConfigureAwait(false),
            "E" => await ExecuteLiveAsync(descriptor, manifest, factory: null).ConfigureAwait(false),
            "A" or "B" when descriptor.Execution.CaseClass == "live" =>
                await ExecuteLiveAsync(
                    descriptor,
                    manifest,
                    MonitoredAdapterRegistry.Require(descriptor.Execution.Candidate)).ConfigureAwait(false),
            "A" or "B" => await ExecuteCandidateAsync(
                    descriptor,
                    manifest,
                    MonitoredAdapterRegistry.Require(descriptor.Execution.Candidate))
                .ConfigureAwait(false),
            _ => throw Error("UnsupportedCandidate", "The worker candidate is unsupported."),
        };
    }

    private static async Task<MonitoredWorkerResult> ExecuteSharedReadinessAsync(
        MonitoredWorkerDescriptor descriptor)
    {
        if (!string.Equals(descriptor.Execution.CaseId, "P0", StringComparison.Ordinal))
        {
            throw Error("InvalidSharedCase", "The shared in-memory worker is valid only for P0.");
        }
        Stage("p0-shared-pipeline", active: false);
        await MonitoredWorkerControl.ObserveBoundaryAsync(
            "p0-before-admission",
            activeStorageStage: false).ConfigureAwait(false);
        var limits = Limits();
        var sink = new RecordingCounterSink();
        await using var pipeline = new DurableCounterPipeline(
            limits,
            new DurableCounterGlobalBudget(limits),
            sink);
        var expectedCommitted = 0;
        foreach (var batch in DurableCounterFixture.GenerateQ1().Chunk(64))
        {
            foreach (var observation in batch)
            {
                if (pipeline.TryWrite(observation).Status != DurableCounterOfferStatus.Accepted)
                {
                    throw Error("P0UnexpectedRejection", "P0 rejected a valid shared-pipeline fixture record.");
                }
            }
            expectedCommitted += batch.Length;
            await WaitForCommittedAsync(pipeline, expectedCommitted, TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
        var drainWatch = Stopwatch.StartNew();
        var drain = await pipeline.DrainAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        drainWatch.Stop();
        var accounting = pipeline.GetAccounting();
        await MonitoredWorkerControl.ObserveBoundaryAsync(
            "p0-after-drain",
            activeStorageStage: false).ConfigureAwait(false);
        var oracleMatch = sink.Records.Select(ToExpected).SequenceEqual(DurableCounterOracle.Q1());
        var pass = drain.CompletedWithinTimeout
            && accounting.IsCleanQuiescent
            && accounting.Committed == 1_024
            && oracleMatch;
        return BaseResult(descriptor, pass ? "pass" : "inconclusive-infra") with
        {
            FailureCode = pass ? null : "P0ReadinessFailed",
            Offered = accounting.Offered,
            Admitted = accounting.Admitted,
            Rejected = accounting.Rejected,
            Committed = accounting.Committed,
            FailedAfterAdmission = accounting.FailedAfterAdmission,
            UnknownCommitOutcome = accounting.UnknownCommitOutcome,
            LogicalCommittedBytes = sink.Records.Sum(static record => (long)record.EncodedBytes),
            DrainSeconds = drainWatch.Elapsed.TotalSeconds,
            SourceTicks = sink.Records.Count,
            SourceKeys = sink.Records.Select(static record => (record.Provider, record.Name)).Distinct().Count(),
            SourceMalformedPayloads = 0,
            SourceAdmissionInvalid = 0,
            SourceRejectedNewKeys = 0,
            SourceAdmissionRejected = 0,
            SourceCoverage = "shared-synthetic-source-offer-accounting",
        };
    }

    private static async Task<MonitoredWorkerResult> ExecuteCandidateAsync(
        MonitoredWorkerDescriptor descriptor,
        IMonitoredExecutionManifest manifest,
        IDurableCounterStorageAdapterFactory factory)
    {
        if (string.Equals(descriptor.Execution.CaseId, "F5", StringComparison.Ordinal))
        {
            return await ExecuteConcurrentCaptureGateAsync(descriptor, factory).ConfigureAwait(false);
        }

        await MonitoredWorkerControl.ObserveBoundaryAsync(
            "before-package-create-and-admission",
            activeStorageStage: true).ConfigureAwait(false);
        Directory.CreateDirectory(descriptor.PackageStagingRoot);
        var limits = PipelineLimitsForCase(descriptor.Execution.CaseId);
        var writerGate = descriptor.Execution.CaseId is "B1" or "M1"
            ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        var faultState = new WorkerFaultState(descriptor.Execution.CaseId);
        var faults = CreateFaultController(descriptor, faultState);
        await using var adapter = factory.Create(CreateAdapterRequest(descriptor, faults));
        var global = new DurableCounterGlobalBudget(limits);
        await using var pipeline = new DurableCounterPipeline(
            limits,
            global,
            adapter,
            writerStartGate: writerGate?.Task);

        Stage("admission", active: true);
        var offers = new List<DurableCounterOfferResult>(
            descriptor.Execution.CaseId == "Q2" ? 16 : 1_024);
        var sourceEvidence = new MonitoredSourceAdmissionEvidence();
        MonitoredOfferScheduleResult? schedule = null;
        var logicalAcceptedBytes = 0L;
        var heldWriterDidNotConsume = true;
        var offeredWatch = Stopwatch.StartNew();

        switch (descriptor.Execution.CaseId)
        {
            case "Q1":
                logicalAcceptedBytes = await OfferBarrierPacedAsync(
                    pipeline,
                    DurableCounterFixture.GenerateQ1(),
                    offers,
                    sourceEvidence,
                    expectedValidPerBarrier: true,
                    TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                break;
            case "Q2":
                logicalAcceptedBytes = await OfferBarrierPacedAsync(
                    pipeline,
                    DurableCounterFixture.LoadQ2(descriptor.RepositoryRoot),
                    offers,
                    sourceEvidence,
                    expectedValidPerBarrier: false,
                    TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                break;
            case "N1":
                schedule = await OfferScheduledAsync(
                        pipeline,
                        1_000,
                        100,
                        offers,
                        sourceEvidence,
                        TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                logicalAcceptedBytes = schedule.AcceptedBytes;
                break;
            case "B1":
            case "M1":
                logicalAcceptedBytes = OfferImmediate(
                    pipeline,
                    4_096,
                    offers,
                    sourceEvidence);
                heldWriterDidNotConsume = pipeline.GetAccounting().Committed == 0;
                writerGate!.TrySetResult();
                break;
            case "O1":
                schedule = await OfferScheduledAsync(
                        pipeline,
                        50_000,
                        5_000,
                        offers: null,
                        sourceEvidence,
                        TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
                logicalAcceptedBytes = schedule.AcceptedBytes;
                break;
            case "C1":
                schedule = await OfferScheduledAsync(
                        pipeline,
                        1_000,
                        1_000,
                        offers,
                        sourceEvidence,
                        TimeSpan.FromSeconds(1))
                    .ConfigureAwait(false);
                logicalAcceptedBytes = schedule.AcceptedBytes;
                break;
            case "F1":
                await OfferStorageFaultAsync(pipeline, offers, sourceEvidence).ConfigureAwait(false);
                break;
            case "F2":
            case "F3":
                await OfferKillFaultAsync(pipeline, offers, sourceEvidence).ConfigureAwait(false);
                throw Error("KillBarrierReturned", "A kill-boundary worker continued unexpectedly.");
            case "F4":
                OfferImmediate(pipeline, 128, offers, sourceEvidence);
                break;
            default:
                throw Error("UnsupportedCandidateCase", $"Case '{descriptor.Execution.CaseId}' is unsupported.");
        }
        offeredWatch.Stop();
        var feederWithinDeadline = descriptor.Execution.CaseId is not ("B1" or "M1")
            || offeredWatch.Elapsed <= TimeSpan.FromSeconds(5);

        Stage("drain", active: true);
        var drainWatch = Stopwatch.StartNew();
        var drain = await pipeline.DrainAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        drainWatch.Stop();
        var accounting = pipeline.GetAccounting();
        await MonitoredWorkerControl.ObserveBoundaryAsync(
            "after-drain",
            activeStorageStage: true).ConfigureAwait(false);

        if (string.Equals(descriptor.Execution.CaseId, "F1", StringComparison.Ordinal))
        {
            var triggered = faultState.Triggered
                || string.Equals(accounting.Rejections.Keys.FirstOrDefault(), "SqliteFull", StringComparison.Ordinal)
                || string.Equals(pipeline.TerminalError, "SqliteFull", StringComparison.Ordinal)
                || pipeline.TerminalError?.Contains("IOException", StringComparison.Ordinal) == true;
            var f1Pass = triggered && pipeline.State == DurableCounterPipelineState.Failed;
            return BaseResult(descriptor, f1Pass ? "pass" : "invalid-injection") with
            {
                FailureCode = f1Pass ? null : "F1InjectionDidNotTrigger",
                Offered = accounting.Offered,
                Admitted = accounting.Admitted,
                Rejected = accounting.Rejected,
                Committed = accounting.Committed,
                FailedAfterAdmission = accounting.FailedAfterAdmission,
                UnknownCommitOutcome = accounting.UnknownCommitOutcome,
                OfferSeconds = offeredWatch.Elapsed.TotalSeconds,
                DrainSeconds = drainWatch.Elapsed.TotalSeconds,
                StorageFaultTriggered = triggered,
                SourceMalformedPayloads = sourceEvidence.MalformedPayloads,
                SourceAdmissionInvalid = sourceEvidence.AdmissionInvalid,
                SourceRejectedNewKeys = sourceEvidence.RejectedNewKeys,
                SourceAdmissionRejected = sourceEvidence.OtherRejected,
                SourceTicks = sourceEvidence.OfferedTicks,
                SourceKeys = sourceEvidence.SourceKeys,
                SourceCoverage = "synthetic-source-offer-accounting",
            };
        }

        if (!drain.CompletedWithinTimeout)
        {
            return BaseResult(descriptor, "failed-candidate") with
            {
                FailureCode = "DrainDeadlineExceeded",
                Offered = accounting.Offered,
                Admitted = accounting.Admitted,
                Rejected = accounting.Rejected,
                Committed = accounting.Committed,
                FailedAfterAdmission = accounting.FailedAfterAdmission,
                UnknownCommitOutcome = accounting.UnknownCommitOutcome,
                DrainSeconds = drainWatch.Elapsed.TotalSeconds,
            };
        }

        Stage("finalization", active: true);
        var finalizationWatch = Stopwatch.StartNew();
        await MonitoredWorkerControl.ObserveBoundaryAsync(
            "before-pre-seal-finalization",
            activeStorageStage: true).ConfigureAwait(false);
        var preSeal = await FinalizeWithinSharedDeadlineAsync(
            pipeline,
            adapter,
            finalizationWatch).ConfigureAwait(false);
        await MonitoredWorkerControl.ObserveBoundaryAsync(
            "after-pre-seal-finalization",
            activeStorageStage: true).ConfigureAwait(false);
        if (string.Equals(descriptor.Execution.CaseId, "F4", StringComparison.Ordinal))
        {
            await HoldAtKillBarrierAsync(
                DurableStorageFaultBarrier.AfterIndexesBeforeSeal,
                batchOrdinal: 0,
                firstSequence: 1,
                lastSequence: 128).ConfigureAwait(false);
            throw Error("KillBarrierReturned", "The F4 worker continued after its required kill barrier.");
        }

        await adapter.DisposeAsync().ConfigureAwait(false);
        var package = await MonitoredPackagePublisher.PublishAsync(
            descriptor,
            manifest,
            factory,
            preSeal,
            pipeline.GetAccounting(),
            volatileTailUnknown: false,
            derivedFromCaptureId: null,
            recoveryReason: null,
            finalizationWatch,
            logicalAcceptedBytes).ConfigureAwait(false);

        var finalAccounting = pipeline.GetAccounting();
        bool semanticPass;
        try
        {
            semanticPass = await ValidateSemanticCaseAsync(
                descriptor,
                package.Reader,
                offers).ConfigureAwait(false);
        }
        finally
        {
            await package.Reader.DisposeAsync().ConfigureAwait(false);
        }

        var achievedOfferRatio = schedule?.AchievedOfferRatio;
        var scheduledSourceValid = schedule is null
            || (descriptor.Execution.CaseId == "C1"
                ? schedule.AttemptedOffers == schedule.ScheduledOffers
                : achievedOfferRatio >= 0.95);
        var normalAllCommitted = descriptor.Execution.CaseId != "N1"
            || finalAccounting.Rejected == 0 && finalAccounting.Committed == finalAccounting.Offered;
        var stressSemantics = descriptor.Execution.CaseId switch
        {
            "B1" => heldWriterDidNotConsume
                && finalAccounting.Rejections.ContainsKey("QueueFull"),
            "M1" => heldWriterDidNotConsume
                && finalAccounting.Rejections.ContainsKey("OwnedBudgetFull"),
            _ => true,
        };
        var pass = semanticPass
            && finalAccounting.IsCleanQuiescent
            && normalAllCommitted
            && stressSemantics
            && feederWithinDeadline
            && scheduledSourceValid
            && package.FinalBytes <= 268_435_456
            && MonitoredDeadlineRules.WithinFinalizationAndReopenBudgets(
                package.Finalization,
                package.ReopenAndFirstQuery);
        var outcome = !scheduledSourceValid || !feederWithinDeadline
            ? "inconclusive-source"
            : pass ? "pass" : "failed-candidate";
        return BaseResult(descriptor, outcome) with
        {
            FailureCode = pass
                ? null
                : !scheduledSourceValid
                    ? descriptor.Execution.CaseId == "C1"
                        ? "ExactOfferPopulationNotDelivered"
                        : "AchievedOfferRatioBelowMinimum"
                    : !feederWithinDeadline
                        ? "FeederDeadlineExceeded"
                        : DetermineCandidateFailure(
                            semanticPass,
                            normalAllCommitted,
                            package,
                            package.Finalization),
            Offered = finalAccounting.Offered,
            Admitted = finalAccounting.Admitted,
            Rejected = finalAccounting.Rejected,
            Committed = finalAccounting.Committed,
            FailedAfterAdmission = finalAccounting.FailedAfterAdmission,
            UnknownCommitOutcome = finalAccounting.UnknownCommitOutcome,
            SourceMalformedPayloads = sourceEvidence.MalformedPayloads,
            SourceAdmissionInvalid = sourceEvidence.AdmissionInvalid,
            SourceRejectedNewKeys = sourceEvidence.RejectedNewKeys,
            SourceAdmissionRejected = sourceEvidence.OtherRejected,
            LogicalCommittedBytes = package.LogicalCommittedBytes,
            PackageFinalBytes = package.FinalBytes,
            OfferSeconds = offeredWatch.Elapsed.TotalSeconds,
            DrainSeconds = drainWatch.Elapsed.TotalSeconds,
            FinalizationSeconds = package.Finalization.TotalSeconds,
            ReopenAndFirstQuerySeconds = package.ReopenAndFirstQuery.TotalSeconds,
            SourceTicks = sourceEvidence.OfferedTicks,
            SourceKeys = sourceEvidence.SourceKeys,
            ScheduledSourceOffers = schedule?.ScheduledOffers,
            AttemptedSourceOffers = schedule?.AttemptedOffers,
            AchievedSourceOfferRatio = schedule?.AchievedOfferRatio,
            SourceCoverage = "synthetic-source-offer-accounting",
            HasDurablePackage = true,
        };
    }

    private static async Task<MonitoredWorkerResult> ExecuteConcurrentCaptureGateAsync(
        MonitoredWorkerDescriptor descriptor,
        IDurableCounterStorageAdapterFactory factory)
    {
        await MonitoredWorkerControl.ObserveBoundaryAsync(
            "before-f5-package-create",
            activeStorageStage: true).ConfigureAwait(false);
        Directory.CreateDirectory(descriptor.PackageStagingRoot);
        var limits = Limits();
        var faults = NoDurableStorageFaults.Instance;
        await using var adapter = factory.Create(CreateAdapterRequest(descriptor, faults));
        var secondRoot = Path.Combine(descriptor.ExecutionRoot, "f5-second-package-staging");
        Directory.CreateDirectory(secondRoot);
        var probe = await MonitoredConcurrentCaptureGate.ProbeAsync(
                limits,
                adapter,
                () => factory.Create(CreateAdapterRequest(descriptor with
                {
                    CaptureId = $"{descriptor.CaptureId}-second",
                    ArtifactId = $"{descriptor.ArtifactId}-second",
                    PackageStagingRoot = secondRoot,
                }, faults)))
            .ConfigureAwait(false);
        await MonitoredWorkerControl.ObserveBoundaryAsync(
            "f5-after-drain",
            activeStorageStage: true).ConfigureAwait(false);
        var pass = probe.Rejected && probe.Elapsed < TimeSpan.FromSeconds(1);
        return BaseResult(descriptor, pass ? "pass" : "failed-candidate") with
        {
            FailureCode = pass ? null : "F5SecondCaptureNotImmediatelyRejected",
            ConcurrentCaptureRejected = probe.Rejected,
            OfferSeconds = probe.Elapsed.TotalSeconds,
        };
    }

    private static async Task<MonitoredWorkerResult> ExecuteLiveAsync(
        MonitoredWorkerDescriptor descriptor,
        IMonitoredExecutionManifest manifest,
        IDurableCounterStorageAdapterFactory? factory)
    {
        if (descriptor.Execution.CaseId is not ("P1" or "L1" or "L2" or "L3"))
        {
            throw Error("InvalidLiveCase", "Candidate E is valid only for frozen live executions.");
        }

        using var startupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var prevalidation = descriptor.Schema == PrevalidationProtocol.WorkerSchema;
        MonitoredProcessIdentity? reportedTarget = null;
        var sample = await LiveSampleProcess.StartPublishedAsync(
            "CoreClrSample",
            new LiveSampleOptions
            {
                WaitForHttpReady = true,
                HarvestListeningUrl = true,
                ReadinessPath = LiveReadinessPath,
                DiagnosticTimeout = TimeSpan.FromSeconds(10),
                HttpTimeout = TimeSpan.FromSeconds(10),
                ProcessStarted = prevalidation ? async process =>
                {
                    var started = MonitoredProcessIdentity.Capture(process, MonitoredProcessRole.Target);
                    MonitoredWorkerEventWriter.Write(new MonitoredWorkerEvent("process",
                        ProcessId: started.ProcessId, ProcessStartTimeTicks: started.LinuxStartTimeTicks,
                        ProcessRole: "target"));
                    reportedTarget = started;
                    await MonitoredWorkerControl.ObserveBoundaryAsync("live-target-startup", true).ConfigureAwait(false);
                } : null,
                BeforeTermination = async token =>
                {
                    if (reportedTarget is not { } identity) return;
                    await MonitoredWorkerControl.AnnounceProcessTerminationAsync(identity, token).ConfigureAwait(false);
                    MonitoredWorkerEventWriter.Write(new MonitoredWorkerEvent(
                        "process-terminated", ProcessId: identity.ProcessId,
                        ProcessStartTimeTicks: identity.LinuxStartTimeTicks, ProcessRole: "target"));
                },
            },
            startupDeadline.Token).ConfigureAwait(false);
        var sampleIdentity = MonitoredProcessIdentity.Capture(sample.Process, MonitoredProcessRole.Target);
        IDurableCounterStorageAdapter? adapter = null;
        DurableCounterPipeline? pipeline = null;
        MonitoredPackagePublishResult? package = null;
        try
        {
            if (!MonitoredPathRules.PathsEqual(sample.SampleDll, manifest.SampleBinary.Path))
            {
                throw Error(
                    "SampleBinaryIdentityMismatch",
                    "The live worker launched a sample binary other than the manifest-pinned binary.");
            }
            if (!prevalidation)
            {
                MonitoredWorkerEventWriter.Write(new MonitoredWorkerEvent(
                    "process",
                    ProcessId: sampleIdentity.ProcessId,
                    ProcessStartTimeTicks: sampleIdentity.LinuxStartTimeTicks,
                    ProcessRole: "target"));
            }
            reportedTarget = sampleIdentity;

            if (factory is not null)
            {
                await MonitoredWorkerControl.ObserveBoundaryAsync(
                    "live-before-package-create-and-admission",
                    activeStorageStage: true).ConfigureAwait(false);
                Directory.CreateDirectory(descriptor.PackageStagingRoot);
                adapter = factory.Create(CreateAdapterRequest(descriptor, NoDurableStorageFaults.Instance));
                pipeline = new DurableCounterPipeline(
                    Limits(),
                    new DurableCounterGlobalBudget(Limits()),
                    adapter);
            }
            else
            {
                await MonitoredWorkerControl.ObserveBoundaryAsync(
                    "live-baseline-no-durable-package",
                    activeStorageStage: false).ConfigureAwait(false);
            }

            Stage("live-warmup", active: DescriptorObservationPolicy.IsUnifiedActive(manifest.SampledLoss)
                && factory is not null);
            var requests = new BoundedLiveRequestLoad(sample.BaseUrl);
            var loadTask = requests.RunAsync(TimeSpan.FromSeconds(44), CancellationToken.None);
            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Stage("live-eventpipe", active: factory is not null);
            var capture = await CollectLiveTicksAsync(
                sample.ProcessId,
                pipeline,
                TimeSpan.FromSeconds(34),
                CancellationToken.None).ConfigureAwait(false);
            await loadTask.ConfigureAwait(false);

            var drainSeconds = 0d;
            var finalizationSeconds = 0d;
            var reopenSeconds = 0d;
            var packageBytes = 0L;
            var logicalBytes = 0L;
            DurableCounterAccounting? accounting = null;
            if (pipeline is not null && adapter is not null && factory is not null)
            {
                Stage("live-drain", active: true);
                var drainWatch = Stopwatch.StartNew();
                var drain = await pipeline.DrainAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                drainWatch.Stop();
                drainSeconds = drainWatch.Elapsed.TotalSeconds;
                await MonitoredWorkerControl.ObserveBoundaryAsync(
                    "live-after-drain",
                    activeStorageStage: true).ConfigureAwait(false);
                if (!drain.CompletedWithinTimeout)
                {
                    return BaseResult(descriptor, "failed-candidate") with
                    {
                        FailureCode = "LiveDrainDeadlineExceeded",
                        Requests = requests.Snapshot(),
                        OfferSeconds = requests.ElapsedSeconds,
                        DrainSeconds = drainSeconds,
                        SourceTicks = capture.SourceTicks,
                        SourceMalformedPayloads = capture.MalformedPayloads,
                        SourceAdmissionInvalid = capture.AdmissionInvalid,
                        SourceRejectedNewKeys = capture.RejectedNewKeys,
                        SourceAdmissionRejected = capture.OtherRejected,
                        SourceKeys = capture.SourceKeys,
                        SourceCoverage = capture.Coverage,
                        TargetStartedAt = sample.Process.StartTime.ToUniversalTime(),
                        CounterSessionStartedAt = capture.SessionStartedAt,
                        CounterCollectionSeconds = capture.CollectionElapsed.TotalSeconds,
                    };
                }
                accounting = pipeline.GetAccounting();
                Stage("live-finalization", active: true);
                var finalizationWatch = Stopwatch.StartNew();
                await MonitoredWorkerControl.ObserveBoundaryAsync(
                    "live-before-pre-seal-finalization",
                    activeStorageStage: true).ConfigureAwait(false);
                var preSeal = await FinalizeWithinSharedDeadlineAsync(
                    pipeline,
                    adapter,
                    finalizationWatch).ConfigureAwait(false);
                await MonitoredWorkerControl.ObserveBoundaryAsync(
                    "live-after-pre-seal-finalization",
                    activeStorageStage: true).ConfigureAwait(false);
                await adapter.DisposeAsync().ConfigureAwait(false);
                package = await MonitoredPackagePublisher.PublishAsync(
                    descriptor,
                    manifest,
                    factory,
                    preSeal,
                    accounting,
                    volatileTailUnknown: false,
                    derivedFromCaptureId: null,
                    recoveryReason: null,
                    finalizationWatch,
                    logicalCommittedBytes: 0).ConfigureAwait(false);
                finalizationSeconds = package.Finalization.TotalSeconds;
                reopenSeconds = package.ReopenAndFirstQuery.TotalSeconds;
                packageBytes = package.FinalBytes;
                logicalBytes = package.LogicalCommittedBytes;
                await package.Reader.DisposeAsync().ConfigureAwait(false);
            }

            var requestMetrics = requests.Snapshot();
            var coverageValid = capture.CoverageAvailable
                && capture.SourceKeys is > 0
                && requestMetrics.RetainedSamples <= 1_000;
            var commitsValid = accounting is null
                || capture.AdmissionInvalid == 0
                    && capture.RejectedNewKeys == 0
                    && capture.OtherRejected == 0
                    && accounting.Rejected == 0
                    && accounting.FailedAfterAdmission == 0
                    && accounting.UnknownCommitOutcome == 0
                    && accounting.Committed == accounting.Offered;
            var pass = coverageValid
                && commitsValid
                && BoundedLiveRequestLoad.HasCompleteSchedule(requestMetrics)
                && requestMetrics.Completed > 0
                && (factory is null
                    || package is not null
                        && MonitoredDeadlineRules.WithinFinalizationAndReopenBudgets(
                            TimeSpan.FromSeconds(finalizationSeconds),
                            TimeSpan.FromSeconds(reopenSeconds))
                        && packageBytes <= 268_435_456);
            return BaseResult(descriptor, pass ? "pass" : "inconclusive-source") with
            {
                FailureCode = pass ? null : "LiveEpisodeInvalid",
                Offered = accounting?.Offered ?? 0,
                Admitted = accounting?.Admitted ?? 0,
                Rejected = accounting?.Rejected ?? 0,
                Committed = accounting?.Committed ?? 0,
                FailedAfterAdmission = accounting?.FailedAfterAdmission ?? 0,
                UnknownCommitOutcome = accounting?.UnknownCommitOutcome ?? 0,
                SourceMalformedPayloads = capture.MalformedPayloads,
                SourceAdmissionInvalid = capture.AdmissionInvalid,
                SourceRejectedNewKeys = capture.RejectedNewKeys,
                SourceAdmissionRejected = capture.OtherRejected,
                LogicalCommittedBytes = logicalBytes,
                PackageFinalBytes = packageBytes,
                OfferSeconds = requests.ElapsedSeconds,
                DrainSeconds = drainSeconds,
                FinalizationSeconds = finalizationSeconds,
                ReopenAndFirstQuerySeconds = reopenSeconds,
                Requests = requestMetrics,
                SourceTicks = capture.SourceTicks,
                SourceKeys = capture.SourceKeys,
                SourceCoverage = capture.Coverage,
                TargetStartedAt = sample.Process.StartTime.ToUniversalTime(),
                CounterSessionStartedAt = capture.SessionStartedAt,
                CounterCollectionSeconds = capture.CollectionElapsed.TotalSeconds,
                HasDurablePackage = factory is not null,
            };
        }
        finally
        {
            try
            {
                if (pipeline is not null)
                {
                    await pipeline.DisposeAsync().ConfigureAwait(false);
                }
                if (adapter is not null)
                {
                    await adapter.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await sample.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<MonitoredWorkerResult> RecoverAsync(
        MonitoredWorkerDescriptor descriptor,
        IMonitoredExecutionManifest manifest)
    {
        if (descriptor.Execution.CaseId is not ("F2" or "F3" or "F4")
            || descriptor.Execution.Candidate is not ("A" or "B")
            || string.IsNullOrWhiteSpace(descriptor.SourcePackageRoot)
            || string.IsNullOrWhiteSpace(descriptor.SourceCaptureId)
            || string.IsNullOrWhiteSpace(descriptor.SourceArtifactId))
        {
            throw Error("InvalidRecoveryDescriptor", "Recovery is valid only for F2-F4 with explicit source identities.");
        }
        var factory = MonitoredAdapterRegistry.Require(descriptor.Execution.Candidate);
        Directory.CreateDirectory(descriptor.PackageStagingRoot);
        var sourceBefore = MonitoredPackagePublisher.HashTree(descriptor.SourcePackageRoot);
        Stage("recovery", active: true);
        var watch = Stopwatch.StartNew();
        await MonitoredWorkerControl.ObserveBoundaryAsync(
            "before-explicit-recovery",
            activeStorageStage: true).ConfigureAwait(false);
        var recovery = await factory.RecoverAsync(
            new DurableStorageRecoveryRequest(
                descriptor.SourceCaptureId,
                descriptor.SourceArtifactId,
                descriptor.CaptureId,
                descriptor.ArtifactId,
                descriptor.SourcePackageRoot,
                descriptor.PackageStagingRoot,
                $"explicit-{descriptor.Execution.CaseId}-recovery"),
            CancellationToken.None).ConfigureAwait(false);
        await MonitoredWorkerControl.ObserveBoundaryAsync(
            "after-explicit-recovery",
            activeStorageStage: true).ConfigureAwait(false);
        DurableStorageRecoveryRules.Validate(
            new DurableStorageRecoveryRequest(
                descriptor.SourceCaptureId,
                descriptor.SourceArtifactId,
                descriptor.CaptureId,
                descriptor.ArtifactId,
                descriptor.SourcePackageRoot,
                descriptor.PackageStagingRoot,
                $"explicit-{descriptor.Execution.CaseId}-recovery"),
            recovery);
        var sourceAfter = MonitoredPackagePublisher.HashTree(descriptor.SourcePackageRoot);
        if (!sourceBefore.SequenceEqual(sourceAfter))
        {
            throw Error("RecoveryModifiedSource", "Explicit recovery modified its source package.");
        }
        MonitoredPackagePublisher.MakeImmutable(descriptor.SourcePackageRoot);

        var package = await MonitoredPackagePublisher.PublishRecoveredAsync(
            descriptor,
            manifest,
            factory,
            recovery,
            watch).ConfigureAwait(false);
        List<long> recoveredSequences;
        try
        {
            recoveredSequences = await ReadAllSequencesAsync(package.Reader).ConfigureAwait(false);
        }
        finally
        {
            await package.Reader.DisposeAsync().ConfigureAwait(false);
        }
        var acknowledgements = descriptor.ConfirmedAcknowledgements ?? [];
        var offered = descriptor.KnownOfferedSequences ?? [];
        var recoveryInvariant = MonitoredRecoveryInvariant.Validate(
            descriptor.Execution.CaseId,
            acknowledgements,
            offered,
            recoveredSequences);
        var pass = recoveryInvariant
            && package.FinalBytes <= 268_435_456
            && package.CombinedSourceRecoveryBytes <= 268_435_456
            && MonitoredDeadlineRules.WithinFinalizationAndReopenBudgets(
                package.Finalization,
                package.ReopenAndFirstQuery);
        return BaseResult(descriptor, pass ? "pass" : "failed-candidate") with
        {
            FailureCode = pass ? null : "RecoveryInvariantFailed",
            PackageFinalBytes = package.FinalBytes,
            FinalizationSeconds = package.Finalization.TotalSeconds,
            ReopenAndFirstQuerySeconds = package.ReopenAndFirstQuery.TotalSeconds,
            RecoveryInvariantSatisfied = recoveryInvariant,
            RecoveredRecords = recoveredSequences.Count,
            HasDurablePackage = true,
        };
    }

    private static async Task<DurableStoragePreSealResult> FinalizeWithinSharedDeadlineAsync(
        DurableCounterPipeline pipeline,
        IDurableCounterStorageAdapter adapter,
        Stopwatch deadline)
    {
        if (!await pipeline.FinalizeAsync(Remaining(deadline, TimeSpan.FromSeconds(10))).ConfigureAwait(false))
        {
            throw Error("FinalizationDeadlineExceeded", "Pipeline finalization exceeded the shared 10-second deadline.");
        }
        var accounting = pipeline.GetAccounting();
        var quality = new DurableCounterQuery([], Limits()).Quality(accounting);
        using var cancellation = new CancellationTokenSource(Remaining(deadline, TimeSpan.FromSeconds(10)));
        var result = await adapter.FinalizePreSealAsync(quality, cancellation.Token).ConfigureAwait(false);
        if (deadline.Elapsed > TimeSpan.FromSeconds(10))
        {
            throw Error("FinalizationDeadlineExceeded", "Pre-seal finalization exceeded the shared 10-second deadline.");
        }
        return result;
    }

    private static async Task<long> OfferBarrierPacedAsync(
        DurableCounterPipeline pipeline,
        IReadOnlyList<DurableCounterObservation> observations,
        List<DurableCounterOfferResult> offers,
        MonitoredSourceAdmissionEvidence sourceEvidence,
        bool expectedValidPerBarrier,
        TimeSpan deadline)
    {
        using var timeout = new CancellationTokenSource(deadline);
        long expectedCommitted = 0;
        long acceptedBytes = 0;
        var acceptedBytesKnown = true;
        foreach (var batch in observations.Chunk(64))
        {
            foreach (var observation in batch)
            {
                var result = pipeline.TryWrite(observation);
                offers.Add(result);
                sourceEvidence.Record(observation, result);
                if (result.Status == DurableCounterOfferStatus.Accepted)
                {
                    expectedCommitted++;
                    if (observation.RequestedEncodedBytes.HasValue)
                    {
                        acceptedBytes = checked(
                            acceptedBytes + observation.RequestedEncodedBytes.Value);
                    }
                    else
                    {
                        acceptedBytesKnown = false;
                    }
                }
                else if (expectedValidPerBarrier)
                {
                    throw Error("UnexpectedFixtureRejection", "A valid barrier-paced fixture record was rejected.");
                }
            }
            await WaitUntilAsync(
                () => pipeline.GetAccounting().Committed >= expectedCommitted,
                timeout.Token).ConfigureAwait(false);
        }
        return acceptedBytesKnown ? acceptedBytes : 0;
    }

    internal static async Task<MonitoredOfferScheduleResult> OfferScheduledAsync(
        DurableCounterPipeline pipeline,
        int records,
        int perSecond,
        List<DurableCounterOfferResult>? offers,
        MonitoredSourceAdmissionEvidence sourceEvidence,
        TimeSpan duration,
        Func<TimeSpan>? elapsed = null,
        Func<TimeSpan, Task>? delay = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(records);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perSecond);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        var watch = Stopwatch.StartNew();
        elapsed ??= () => watch.Elapsed;
        delay ??= static value => Task.Delay(value);
        long acceptedBytes = 0;
        var scheduled = Math.Min(
            records,
            checked((int)Math.Ceiling(duration.TotalSeconds * perSecond)));
        var attempted = 0;
        for (var sequence = 1; sequence <= records; sequence++)
        {
            var target = TimeSpan.FromSeconds((sequence - 1d) / perSecond);
            if (target >= duration)
            {
                break;
            }
            var remaining = target - elapsed();
            if (remaining > TimeSpan.Zero)
            {
                await delay(remaining).ConfigureAwait(false);
            }
            if (elapsed() >= duration)
            {
                break;
            }
            var observation = DurableCounterFixture.Generate(sequence);
            var result = pipeline.TryWrite(observation);
            attempted++;
            offers?.Add(result);
            sourceEvidence.Record(observation, result);
            if (result.Status == DurableCounterOfferStatus.Accepted)
            {
                acceptedBytes = checked(acceptedBytes + (observation.RequestedEncodedBytes ?? 512));
            }
        }
        return new MonitoredOfferScheduleResult(
            acceptedBytes,
            scheduled,
            attempted,
            elapsed(),
            scheduled == 0 ? 0 : attempted / (double)scheduled);
    }

    private static long OfferImmediate(
        DurableCounterPipeline pipeline,
        int records,
        List<DurableCounterOfferResult> offers,
        MonitoredSourceAdmissionEvidence sourceEvidence)
    {
        long acceptedBytes = 0;
        for (var sequence = 1; sequence <= records; sequence++)
        {
            var observation = DurableCounterFixture.Generate(sequence);
            var result = pipeline.TryWrite(observation);
            offers.Add(result);
            sourceEvidence.Record(observation, result);
            if (result.Status == DurableCounterOfferStatus.Accepted)
            {
                acceptedBytes = checked(acceptedBytes + (observation.RequestedEncodedBytes ?? 512));
            }
        }
        return acceptedBytes;
    }

    private static async Task OfferStorageFaultAsync(
        DurableCounterPipeline pipeline,
        List<DurableCounterOfferResult> offers,
        MonitoredSourceAdmissionEvidence sourceEvidence)
    {
        for (var offset = 0; offset < 4_096 && pipeline.State == DurableCounterPipelineState.Accepting; offset += 64)
        {
            for (var sequence = offset + 1; sequence <= offset + 64; sequence++)
            {
                var observation = DurableCounterFixture.Generate(sequence);
                var result = pipeline.TryWrite(observation);
                offers.Add(result);
                sourceEvidence.Record(observation, result);
            }
            await WaitUntilAsync(
                () => pipeline.State != DurableCounterPipelineState.Accepting
                    || pipeline.GetAccounting().Committed >= Math.Min(offset + 64, 4_096),
                new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token).ConfigureAwait(false);
        }
    }

    private static async Task OfferKillFaultAsync(
        DurableCounterPipeline pipeline,
        List<DurableCounterOfferResult> offers,
        MonitoredSourceAdmissionEvidence sourceEvidence)
    {
        OfferImmediate(pipeline, 64, offers, sourceEvidence);
        await WaitUntilAsync(
            () => pipeline.GetAccounting().Committed >= 64,
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token).ConfigureAwait(false);
        MonitoredWorkerEventWriter.Write(new MonitoredWorkerEvent(
            "ack",
            FirstSequence: 1,
            LastSequence: 64));
        for (var sequence = 65; sequence <= 128; sequence++)
        {
            var observation = DurableCounterFixture.Generate(sequence);
            var result = pipeline.TryWrite(observation);
            offers.Add(result);
            sourceEvidence.Record(observation, result);
        }
        await pipeline.Completion.ConfigureAwait(false);
    }

    private static async Task<bool> ValidateSemanticCaseAsync(
        MonitoredWorkerDescriptor descriptor,
        IDurableCounterReadonlyStore reader,
        IReadOnlyList<DurableCounterOfferResult> offers)
    {
        if (descriptor.Execution.CaseId is not ("Q1" or "Q2"))
        {
            return true;
        }
        var rows = await ReadAllRowsAsync(reader).ConfigureAwait(false);
        var expected = descriptor.Execution.CaseId == "Q1"
            ? DurableCounterOracle.Q1()
            : DurableCounterOracle.Q2();
        var offerMatches = descriptor.Execution.CaseId == "Q1"
            ? offers.All(static offer => offer.Status == DurableCounterOfferStatus.Accepted)
            : offers.Select(static offer => new { offer.Sequence, offer.Status, offer.Reason })
                .SequenceEqual(expected.Select(static item => new { item.Sequence, item.Status, item.Reason }));
        var expectedRows = expected.Where(static item => item.Status == DurableCounterOfferStatus.Accepted);
        var rowsMatch = rows.Select(ToExpected).SequenceEqual(expectedRows);
        var summary = await reader.SummaryAsync(CancellationToken.None).ConfigureAwait(false);
        var quality = await reader.QualityAsync(CancellationToken.None).ConfigureAwait(false);
        return offerMatches
            && rowsMatch
            && summary.Sum(static row => row.RetainedCount) == rows.Count
            && quality.RetainedRecords == rows.Count;
    }

    private static async Task<IReadOnlyList<DurableCounterRecord>> ReadAllRowsAsync(
        IDurableCounterReadonlyStore reader)
    {
        var rows = new List<DurableCounterRecord>(1_024);
        for (var key = 0; key < 8; key++)
        {
            long? cursor = null;
            do
            {
                var page = await reader.SeriesAsync(
                    "Synthetic.Provider",
                    $"counter-{key}",
                    cursor,
                    100,
                    CancellationToken.None).ConfigureAwait(false);
                rows.AddRange(page.Rows);
                if (rows.Count > 1_024)
                {
                    throw Error("SemanticReadBoundExceeded", "Semantic validation exceeded the frozen fixture population.");
                }
                cursor = page.NextAfterSequence;
            }
            while (cursor.HasValue);
        }
        rows.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
        return rows;
    }

    private static async Task<List<long>> ReadAllSequencesAsync(IDurableCounterReadonlyStore reader)
    {
        var result = new List<long>(128);
        for (var key = 0; key < 8; key++)
        {
            long? cursor = null;
            do
            {
                var page = await reader.SeriesAsync(
                    "Synthetic.Provider",
                    $"counter-{key}",
                    cursor,
                    100,
                    CancellationToken.None).ConfigureAwait(false);
                result.AddRange(page.Rows.Select(static row => row.Sequence));
                if (result.Count > 4_096)
                {
                    throw Error("RecoveryReadBoundExceeded", "Recovery validation exceeded its bounded fault population.");
                }
                cursor = page.NextAfterSequence;
            }
            while (cursor.HasValue);
        }
        result.Sort();
        return result;
    }

    private static IDurableStorageFaultController CreateFaultController(
        MonitoredWorkerDescriptor descriptor,
        WorkerFaultState state)
    {
        if (descriptor.Execution.CaseId == "F1"
            && descriptor.Execution.Candidate == "A")
        {
            return new DurableSqliteFaultController(
                constrainPagesAfterFirstCommit: true,
                barrier: (context, _) =>
                {
                    if (context.Barrier == DurableStorageFaultBarrier.StorageFullNextBatch
                        && context.BatchOrdinal >= 2)
                    {
                        state.Armed = true;
                    }
                    return ValueTask.CompletedTask;
                });
        }
        return new WorkerFaultController(descriptor, state);
    }

    private static async Task HoldAtKillBarrierAsync(
        DurableStorageFaultBarrier barrier,
        int batchOrdinal,
        long firstSequence,
        long lastSequence)
    {
        MonitoredWorkerEventWriter.Write(new MonitoredWorkerEvent(
            "barrier",
            Stage: barrier.ToString(),
            ActiveStorageStage: true,
            Barrier: barrier.ToString(),
            BatchOrdinal: batchOrdinal,
            FirstSequence: firstSequence,
            LastSequence: lastSequence));
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
    }

    internal static async Task<LiveTickCapture> CollectLiveTicksAsync(
        int processId,
        DurableCounterPipeline? pipeline,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (pipeline is null)
        {
            var collector = new EventPipeCounterCollector();
            var stopwatch = Stopwatch.StartNew();
            var snapshot = await collector.CollectAsync(
                processId,
                duration,
                LiveCounterProviderNames,
                meters: null,
                intervalSeconds: 1,
                maxInstrumentTimeSeries: 1_000,
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            var coverageAvailable = snapshot.Counters.Count > 0
                && snapshot.FirstCounters?.Count == snapshot.Counters.Count
                && snapshot.MaxCounters?.Count == snapshot.Counters.Count;
            return new LiveTickCapture(
                SourceTicks: null,
                MalformedPayloads: null,
                AdmissionInvalid: null,
                RejectedNewKeys: null,
                OtherRejected: null,
                SourceKeys: snapshot.Counters.Count,
                CoverageAvailable: coverageAvailable,
                Coverage: "shipping-eventpipe-counter-collector-first-latest-max;raw-tick-counts-unavailable",
                SessionStartedAt: snapshot.StartedAt,
                CollectionElapsed: stopwatch.Elapsed);
        }

        var providers = LiveCounterProviderNames.Select(name => new EventPipeProvider(
            name,
            EventLevel.Verbose,
            (long)EventKeywords.All,
            new Dictionary<string, string>
            {
                ["EventCounterIntervalSec"] = "1",
            })).ToArray();
        var client = new DiagnosticsClient(processId);
        using var session = client.StartEventPipeSession(
            providers,
            requestRundown: false,
            circularBufferMB: 128);
        var sessionStartedAt = DateTimeOffset.UtcNow;
        var collectionWatch = Stopwatch.StartNew();
        var eventCount = 0;
        var sourceEvidence = new MonitoredSourceAdmissionEvidence();
        Exception? processingFailure = null;
        var processing = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Dynamic.All += traceEvent =>
                {
                    if (!string.Equals(traceEvent.EventName, "EventCounters", StringComparison.Ordinal))
                    {
                        return;
                    }
                    Interlocked.Increment(ref eventCount);
                    if (traceEvent.PayloadValue(0) is not IDictionary<string, object> outer
                        || !outer.TryGetValue("Payload", out var payload)
                        || payload is not IDictionary<string, object> data)
                    {
                        lock (sourceEvidence)
                        {
                            sourceEvidence.RecordMalformedPayload();
                        }
                        return;
                    }
                    var counter = EventPipeCounterCollector.ExtractCounterPayload(
                        traceEvent.ProviderName,
                        data);
                    if (counter is null)
                    {
                        lock (sourceEvidence)
                        {
                            sourceEvidence.RecordMalformedPayload();
                        }
                        return;
                    }
                    if (!TryCreateLiveObservation(
                            counter,
                            traceEvent.TimeStampRelativeMSec,
                            out var observation))
                    {
                        lock (sourceEvidence)
                        {
                            sourceEvidence.RecordMalformedPayload();
                        }
                        return;
                    }
                    var result = pipeline.TryWrite(observation);
                    lock (sourceEvidence)
                    {
                        sourceEvidence.Record(observation, result);
                    }
                };
                source.Process();
            }
            catch (Exception exception)
            {
                processingFailure = exception;
            }
        }, cancellationToken);

        await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        session.Stop();
        await processing.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        collectionWatch.Stop();
        if (processingFailure is not null)
        {
            throw Error(
                "LiveEventPipeProcessingFailed",
                $"EventPipe processing failed: {processingFailure.GetType().Name}.");
        }
        lock (sourceEvidence)
        {
            return new LiveTickCapture(
                eventCount,
                sourceEvidence.MalformedPayloads,
                sourceEvidence.AdmissionInvalid,
                sourceEvidence.RejectedNewKeys,
                sourceEvidence.OtherRejected,
                sourceEvidence.SourceKeys,
                CoverageAvailable: sourceEvidence.OfferedTicks > 0
                    && sourceEvidence.SourceKeys > 0,
                Coverage: "eventpipe-all-source-ticks-through-durable-admission",
                sessionStartedAt,
                collectionWatch.Elapsed);
        }
    }

    internal static bool TryCreateLiveObservation(
        CounterValue counter,
        double sourceMilliseconds,
        out DurableCounterObservation observation)
    {
        observation = null!;
        if (!double.IsFinite(sourceMilliseconds))
        {
            return false;
        }
        try
        {
            observation = new DurableCounterObservation(
                counter,
                checked((long)Math.Round(
                    sourceMilliseconds * TimeSpan.TicksPerMillisecond,
                    MidpointRounding.AwayFromZero)),
                new DurableCounterSourceClock(
                    "eventpipe-session-relative-100ns",
                    "TraceEvent.TimeStampRelativeMSec checked rounded away from zero"));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static MonitoredWorkerResult BaseResult(
        MonitoredWorkerDescriptor descriptor,
        string outcome)
        => new(
            MonitoredProtocolVersions.WorkerResultSchema,
            descriptor.Execution.Ordinal,
            descriptor.Execution.CaseId,
            descriptor.Execution.Candidate,
            outcome,
            FailureCode: null,
            FailureMessage: null,
            Offered: 0,
            Admitted: 0,
            Rejected: 0,
            Committed: 0,
            FailedAfterAdmission: 0,
            UnknownCommitOutcome: 0,
            SourceMalformedPayloads: null,
            SourceAdmissionInvalid: null,
            SourceRejectedNewKeys: null,
            SourceAdmissionRejected: null,
            LogicalCommittedBytes: 0,
            PackageFinalBytes: 0,
            OfferSeconds: 0,
            DrainSeconds: 0,
            FinalizationSeconds: 0,
            ReopenAndFirstQuerySeconds: 0,
            DiagnosticCpuSeconds: 0,
            StorageFaultTriggered: false,
            ConcurrentCaptureRejected: false,
            RecoveryInvariantSatisfied: false,
            RecoveredRecords: 0,
            Requests: null,
            SourceTicks: null,
            SourceKeys: null,
            ScheduledSourceOffers: null,
            AttemptedSourceOffers: null,
            AchievedSourceOfferRatio: null,
            SourceCoverage: "unavailable",
            TargetStartedAt: null,
            CounterSessionStartedAt: null,
            CounterCollectionSeconds: null,
            HasDurablePackage: false,
            RecommendationScope: "monitored-scope-only");

    private static string DetermineCandidateFailure(
        bool semanticPass,
        bool normalAllCommitted,
        MonitoredPackagePublishResult package,
        TimeSpan finalization)
    {
        if (!semanticPass)
        {
            return "SemanticOracleMismatch";
        }
        if (!normalAllCommitted)
        {
            return "NormalValidOffersNotCommitted";
        }
        if (package.FinalBytes > 268_435_456)
        {
            return "FinalPackageLimitExceeded";
        }
        if (finalization > TimeSpan.FromSeconds(10))
        {
            return "FinalizationDeadlineExceeded";
        }
        if (package.ReopenAndFirstQuery > TimeSpan.FromSeconds(2))
        {
            return "ReopenDeadlineExceeded";
        }
        return "CandidateHardGateFailed";
    }

    private static void ValidateDescriptor(MonitoredWorkerDescriptor descriptor, bool prevalidation)
    {
        if (!string.Equals(
                descriptor.Schema,
                prevalidation ? PrevalidationProtocol.WorkerSchema : MonitoredProtocolVersions.WorkerDescriptorSchema,
                StringComparison.Ordinal)
            || !MonitoredFile.IsSha256(descriptor.ManifestSha256)
            || !Path.IsPathRooted(descriptor.RepositoryRoot)
            || !Path.IsPathRooted(descriptor.ManifestPath)
            || !Path.IsPathRooted(descriptor.ExecutionRoot)
            || !Path.IsPathRooted(descriptor.PackageStagingRoot)
            || !Path.IsPathRooted(descriptor.PackageRoot)
            || descriptor.Execution.MaximumAttempts != 1
            || descriptor.Execution.MaximumSeconds != 120)
        {
            throw Error("InvalidWorkerDescriptor", "The worker descriptor is incomplete or outside frozen limits.");
        }
        RequireWorkerIdentity(descriptor.CaptureId, nameof(descriptor.CaptureId));
        RequireWorkerIdentity(descriptor.ArtifactId, nameof(descriptor.ArtifactId));
        if (string.Equals(descriptor.CaptureId, descriptor.SourceCaptureId, StringComparison.Ordinal)
            || string.Equals(descriptor.ArtifactId, descriptor.SourceArtifactId, StringComparison.Ordinal))
        {
            throw Error("RecoveryIdentityReuse", "Recovery worker identities must differ from source identities.");
        }
    }

    private static void ValidateDescriptorOwnership(
        MonitoredWorkerDescriptor descriptor,
        string descriptorPath,
        MonitoredValidatedManifest validated,
        MonitoredExecutionSpec execution)
    {
        ValidateDescriptorPaths(descriptor, descriptorPath,
            MonitoredExecutionContext.FromCampaign(validated), execution);
        ValidateReceipts(validated, execution, descriptor.ExecutionRoot);
    }

    internal static void ValidateDescriptorPaths(
        MonitoredWorkerDescriptor descriptor,
        string descriptorPath,
        MonitoredExecutionContext validated,
        MonitoredExecutionSpec execution)
    {
        EnsureReadOnly(descriptorPath, "WorkerDescriptorMutable");
        var executionName =
            $"{execution.Ordinal:D2}-{execution.Candidate.ToLowerInvariant()}-{execution.CaseId.ToLowerInvariant()}";
        var expectedExecutionRoot = MonitoredPathRules.CombineContained(
            validated.Manifest.OutputRoot,
            executionName);
        var expectedWorkspaceRoot = MonitoredPathRules.CombineContained(
            validated.Manifest.WorkspaceRoot,
            executionName);
        var expectedHistoryRoot = MonitoredPathRules.CombineContained(
            validated.Manifest.HistoryRoot,
            executionName);
        var expectedDescriptorPath = Path.Combine(
            expectedExecutionRoot,
            descriptor.Mode == MonitoredWorkerMode.Execute
                ? "worker-descriptor.json"
                : "recovery-worker-descriptor.json");
        var expectedStagingRoot = Path.Combine(
            expectedWorkspaceRoot,
            descriptor.Mode == MonitoredWorkerMode.Execute
                ? "package-staging"
                : "recovery-staging");
        var expectedPackageRoot = Path.Combine(
            expectedHistoryRoot,
            descriptor.Mode == MonitoredWorkerMode.Execute
                ? "package"
                : "recovery");
        if (!MonitoredPathRules.PathsEqual(descriptorPath, expectedDescriptorPath)
            || !MonitoredPathRules.PathsEqual(descriptor.ExecutionRoot, expectedExecutionRoot)
            || !MonitoredPathRules.PathsEqual(descriptor.PackageStagingRoot, expectedStagingRoot)
            || !MonitoredPathRules.PathsEqual(descriptor.PackageRoot, expectedPackageRoot))
        {
            throw Error(
                "WorkerPathOwnershipMismatch",
                "Worker paths must exactly match the harness-owned execution, workspace, and history roots.");
        }

        if (descriptor.Mode == MonitoredWorkerMode.Execute)
        {
            if (descriptor.SourcePackageRoot is not null
                || descriptor.SourceCaptureId is not null
                || descriptor.SourceArtifactId is not null
                || descriptor.ConfirmedAcknowledgements is not null
                || descriptor.KnownOfferedSequences is not null)
            {
                throw Error(
                    "UnexpectedRecoveryState",
                    "A primary execution descriptor cannot contain recovery state.");
            }
            return;
        }

        if (execution.CaseId is not ("F2" or "F3" or "F4"))
        {
            throw Error("InvalidRecoveryDescriptor", "Only F2-F4 may launch a recovery worker.");
        }
        var sourceDescriptorPath = Path.Combine(expectedExecutionRoot, "worker-descriptor.json");
        EnsureReadOnly(sourceDescriptorPath, "WorkerDescriptorMutable");
        var source = JsonSerializer.Deserialize<MonitoredWorkerDescriptor>(
            MonitoredFile.ReadBounded(sourceDescriptorPath, 1_048_576),
            JsonOptions)
            ?? throw Error("InvalidWorkerDescriptor", "The source worker descriptor was empty.");
        var expectedAcknowledgements = execution.CaseId == "F4"
            ? Array.Empty<long>()
            : Enumerable.Range(1, 64).Select(static value => (long)value).ToArray();
        var expectedOffered = Enumerable.Range(1, 128).Select(static value => (long)value).ToArray();
        if (source.Mode != MonitoredWorkerMode.Execute
            || !MonitoredPathRules.PathsEqual(
                descriptor.SourcePackageRoot ?? string.Empty,
                source.PackageStagingRoot)
            || !string.Equals(descriptor.SourceCaptureId, source.CaptureId, StringComparison.Ordinal)
            || !string.Equals(descriptor.SourceArtifactId, source.ArtifactId, StringComparison.Ordinal)
            || descriptor.ConfirmedAcknowledgements is null
            || !descriptor.ConfirmedAcknowledgements.SequenceEqual(expectedAcknowledgements)
            || descriptor.KnownOfferedSequences is null
            || !descriptor.KnownOfferedSequences.SequenceEqual(expectedOffered))
        {
            throw Error(
                "InvalidRecoveryDescriptor",
                "Recovery state must be derived from the exact killed source worker and frozen batch population.");
        }
    }

    private static void ValidateReceipts(
        MonitoredValidatedManifest validated,
        MonitoredExecutionSpec execution,
        string executionRoot)
    {
        var campaignReceiptPath = Path.Combine(validated.CampaignRoot, "campaign-start.json");
        var attemptReceiptPath = Path.Combine(executionRoot, "attempt-start.json");
        EnsureReadOnly(campaignReceiptPath, "CampaignReceiptMutable");
        EnsureReadOnly(attemptReceiptPath, "AttemptReceiptMutable");
        var campaign = JsonSerializer.Deserialize<MonitoredCampaignStartReceipt>(
            MonitoredFile.ReadBounded(campaignReceiptPath, 65_536),
            JsonOptions)
            ?? throw Error("InvalidCampaignReceipt", "The campaign start receipt was empty.");
        var attempt = JsonSerializer.Deserialize<MonitoredAttemptReceipt>(
            MonitoredFile.ReadBounded(attemptReceiptPath, 65_536),
            JsonOptions)
            ?? throw Error("InvalidAttemptReceipt", "The attempt start receipt was empty.");
        if (!string.Equals(campaign.Schema, "durable-monitored-campaign-start/1", StringComparison.Ordinal)
            || !string.Equals(campaign.CampaignId, validated.Manifest.CampaignId, StringComparison.Ordinal)
            || !string.Equals(campaign.ManifestSha256, validated.ManifestSha256, StringComparison.Ordinal)
            || !string.Equals(
                campaign.AuthorizationReceiptSha256,
                validated.AuthorizationSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                campaign.ProtocolJsonSha256,
                validated.Manifest.Plan.ProtocolJsonSha256,
                StringComparison.Ordinal)
            || campaign.PlannedExecutions != 35
            || campaign.MaximumAttemptsPerExecution != 1)
        {
            throw Error("InvalidCampaignReceipt", "The campaign start receipt does not authorize this worker.");
        }
        if (!string.Equals(attempt.Schema, "durable-monitored-attempt-start/1", StringComparison.Ordinal)
            || attempt.Ordinal != execution.Ordinal
            || !string.Equals(attempt.CaseId, execution.CaseId, StringComparison.Ordinal)
            || !string.Equals(attempt.Candidate, execution.Candidate, StringComparison.Ordinal)
            || attempt.Attempt != 1
            || !string.Equals(attempt.ManifestSha256, validated.ManifestSha256, StringComparison.Ordinal))
        {
            throw Error("InvalidAttemptReceipt", "The immutable attempt receipt does not match this worker.");
        }
    }

    private static void EnsureReadOnly(string path, string code)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw Error("UnsupportedPlatform", "Monitored worker execution is Linux-only.");
        }
        var resolved = MonitoredPathRules.ResolveExistingFile(path);
        var writableBits = UnixFileMode.UserWrite
            | UnixFileMode.GroupWrite
            | UnixFileMode.OtherWrite;
        if ((File.GetUnixFileMode(resolved) & writableBits) != 0)
        {
            throw Error(code, $"Required immutable worker artifact '{resolved}' remains writable.");
        }
    }

    private static void RequireWorkerIdentity(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || value.Contains("..", StringComparison.Ordinal)
            || value.Contains('/')
            || value.Contains('\\')
            || value.Any(char.IsControl))
        {
            throw Error("InvalidWorkerIdentity", $"Worker identity '{name}' is invalid.");
        }
    }

    internal static DurableCounterPipelineLimits PipelineLimitsForCase(string caseId)
        => string.Equals(caseId, "M1", StringComparison.Ordinal)
            ? Limits() with { OwnedBufferBytes = 262_144 }
            : Limits();

    internal static DurableStorageAdapterCreateRequest CreateAdapterRequest(
        MonitoredWorkerDescriptor descriptor,
        IDurableStorageFaultController faults)
        // M1 reduces producer-owned reservations, not the frozen adapter batch/query contract.
        => new(
            descriptor.CaptureId,
            descriptor.ArtifactId,
            descriptor.PackageStagingRoot,
            P1Configuration(),
            Limits(),
            faults);

    private static DurableCounterPipelineLimits Limits()
        => new(BatchMaxAge: TimeSpan.FromMilliseconds(100));

    private static JsonElement P1Configuration()
    {
        using var document = JsonDocument.Parse("""{"profile":"P1"}""");
        return document.RootElement.Clone();
    }

    private static DurableCounterExpected ToExpected(DurableCounterRecord record)
        => new(
            record.Sequence,
            DurableCounterOfferStatus.Accepted,
            null,
            record.Provider,
            record.Name,
            record.Value,
            record.Kind,
            record.SourceTimeTicks,
            record.CoverageGap,
            record.IntervalState,
            record.DisplayScaleState,
            record.ResetState);

    private static async Task WaitForCommittedAsync(
        DurableCounterPipeline pipeline,
        int expected,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        await WaitUntilAsync(
            () => pipeline.GetAccounting().Committed >= expected,
            cancellation.Token).ConfigureAwait(false);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan Remaining(Stopwatch elapsed, TimeSpan total)
    {
        var remaining = total - elapsed.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
    }

    private static void Stage(string name, bool active)
        => MonitoredWorkerEventWriter.Write(new MonitoredWorkerEvent(
            "stage",
            Stage: name,
            ActiveStorageStage: active));

    private static string Bound(string value, int maximumCharacters)
        => value.Length <= maximumCharacters ? value : value[..maximumCharacters];

    private static DurableStorageExperimentException Error(string code, string message) => new(code, message);

    private sealed class WorkerFaultState(string caseId)
    {
        internal string CaseId { get; } = caseId;
        internal bool Armed { get; set; }
        internal bool Triggered { get; set; }
    }

    private sealed class WorkerFaultController(
        MonitoredWorkerDescriptor descriptor,
        WorkerFaultState state) : IDurableStorageFaultController
    {
        public async ValueTask ReachAsync(
            DurableStorageFaultContext context,
            CancellationToken cancellationToken)
        {
            if (descriptor.Execution.CaseId == "F1"
                && descriptor.Execution.Candidate == "B"
                && context.Barrier == DurableStorageFaultBarrier.StorageFullNextBatch
                && context.BatchOrdinal == 2)
            {
                state.Triggered = true;
                throw new IOException("InjectedStorageFullAtCanonicalWrite");
            }
            if (descriptor.Execution.CaseId == "F2"
                && context.Barrier == DurableStorageFaultBarrier.BeforeCommit
                && context.BatchOrdinal == 2)
            {
                await HoldAtKillBarrierAsync(
                    context.Barrier,
                    context.BatchOrdinal,
                    context.Sequences[0],
                    context.Sequences[^1]).ConfigureAwait(false);
            }
            if (descriptor.Execution.CaseId == "F3"
                && context.Barrier == DurableStorageFaultBarrier.AfterCommitBeforeAcknowledgement
                && context.BatchOrdinal == 2)
            {
                await HoldAtKillBarrierAsync(
                    context.Barrier,
                    context.BatchOrdinal,
                    context.Sequences[0],
                    context.Sequences[^1]).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    internal sealed record LiveTickCapture(
        int? SourceTicks,
        long? MalformedPayloads,
        long? AdmissionInvalid,
        long? RejectedNewKeys,
        long? OtherRejected,
        int? SourceKeys,
        bool CoverageAvailable,
        string Coverage,
        DateTimeOffset SessionStartedAt,
        TimeSpan CollectionElapsed);
}

internal static class MonitoredAdapterRegistry
{
    private static readonly IReadOnlyDictionary<string, IDurableCounterStorageAdapterFactory> Factories =
        new IDurableCounterStorageAdapterFactory[]
        {
            new DurableSqliteStorageAdapterFactory(),
            new DurableAppendFirstStorageAdapterFactory(),
        }.ToDictionary(static factory => factory.Identity.Id, StringComparer.Ordinal);

    internal static IDurableCounterStorageAdapterFactory Require(string candidate)
        => Factories.TryGetValue(candidate, out var factory)
            ? factory
            : throw new DurableStorageExperimentException(
                "UnknownMonitoredCandidate",
                $"No monitored experimental adapter is registered for candidate '{candidate}'.");

    internal static IReadOnlyList<DurableStorageAdapterIdentity> Describe()
        => Factories.Values.Select(static factory => factory.Identity)
            .OrderBy(static identity => identity.Id, StringComparer.Ordinal)
            .ToArray();
}

internal sealed record MonitoredPackagePublishResult(
    IDurableCounterReadonlyStore Reader,
    long FinalBytes,
    long LogicalCommittedBytes,
    TimeSpan Finalization,
    TimeSpan ReopenAndFirstQuery,
    long CombinedSourceRecoveryBytes);

internal static class MonitoredPackagePublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions StrictJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };
    private static readonly JsonSerializerOptions ResultJsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<MonitoredPackagePublishResult> PublishAsync(
        MonitoredWorkerDescriptor descriptor,
        IMonitoredExecutionManifest runManifest,
        IDurableCounterStorageAdapterFactory factory,
        DurableStoragePreSealResult preSeal,
        DurableCounterAccounting accounting,
        bool volatileTailUnknown,
        string? derivedFromCaptureId,
        string? recoveryReason,
        Stopwatch finalizationDeadline,
        long logicalCommittedBytes,
        Func<string, bool, Task>? observeBoundary = null)
    {
        var quality = new DurableCounterQuery([], Limits()).Quality(accounting);
        var manifest = new DurableStoragePackageManifest(
            DurableStorageExperimentVersions.PackageContract,
            DurableStorageExperimentVersions.RecordSchema,
            descriptor.CaptureId,
            descriptor.ArtifactId,
            DateTimeOffset.UtcNow,
            factory.Identity,
            MonitoredProtocolVersions.SuccessorProtocolSha256,
            runManifest.FixtureManifestSha256,
            runManifest.SourceCommits.PipelineCommit,
            preSeal.CanonicalMembers.Concat(preSeal.QueryMembers).ToArray(),
            derivedFromCaptureId,
            recoveryReason,
            volatileTailUnknown,
            volatileTailUnknown ? null : quality);
        return await PublishCoreAsync(
            descriptor,
            factory,
            manifest,
            preSeal.CanonicalMembers,
            logicalCommittedBytes,
            sourceRoot: null,
            finalizationDeadline,
            observeBoundary).ConfigureAwait(false);
    }

    internal static async Task<MonitoredPackagePublishResult> PublishRepresentativeAsync(
        MonitoredWorkerDescriptor descriptor,
        IDurableCounterStorageAdapterFactory factory,
        DurableStoragePreSealResult preSeal,
        DurableCounterAccounting accounting,
        Stopwatch finalizationDeadline,
        long logicalCommittedBytes,
        Func<string, bool, Task> observeBoundary)
    {
        var quality = new DurableCounterQuery([], Limits()).Quality(accounting);
        var manifest = new DurableStoragePackageManifest(
            DurableStorageExperimentVersions.PackageContract,
            DurableStorageExperimentVersions.RecordSchema,
            descriptor.CaptureId,
            descriptor.ArtifactId,
            DateTimeOffset.UtcNow,
            factory.Identity,
            MonitoredProtocolVersions.SuccessorProtocolSha256,
            MonitoredFile.HashBytes("component-fixture"u8),
            MonitoredFile.HashBytes("component-pipeline"u8)[..40],
            preSeal.CanonicalMembers.Concat(preSeal.QueryMembers).ToArray(),
            DerivedFromCaptureId: null,
            RecoveryReason: null,
            VolatileTailUnknown: false,
            FinalPipelineQuality: quality);
        return await PublishCoreAsync(
            descriptor,
            factory,
            manifest,
            preSeal.CanonicalMembers,
            logicalCommittedBytes,
            sourceRoot: null,
            finalizationDeadline,
            observeBoundary).ConfigureAwait(false);
    }

    internal static async Task<MonitoredPackagePublishResult> PublishRecoveredAsync(
        MonitoredWorkerDescriptor descriptor,
        IMonitoredExecutionManifest runManifest,
        IDurableCounterStorageAdapterFactory factory,
        DurableStorageRecoveryResult recovery,
        Stopwatch finalizationDeadline)
    {
        var manifest = new DurableStoragePackageManifest(
            DurableStorageExperimentVersions.PackageContract,
            DurableStorageExperimentVersions.RecordSchema,
            descriptor.CaptureId,
            descriptor.ArtifactId,
            DateTimeOffset.UtcNow,
            factory.Identity,
            MonitoredProtocolVersions.SuccessorProtocolSha256,
            runManifest.FixtureManifestSha256,
            runManifest.SourceCommits.PipelineCommit,
            recovery.RecoveredMembers,
            recovery.DerivedFromCaptureId,
            recovery.RecoveryReason,
            VolatileTailUnknown: true,
            FinalPipelineQuality: null);
        return await PublishCoreAsync(
            descriptor,
            factory,
            manifest,
            recovery.RecoveredMembers.Where(static member =>
                member.Role == DurableStorageMemberRole.CanonicalData).ToArray(),
            logicalCommittedBytes: 0,
            descriptor.SourcePackageRoot,
            finalizationDeadline).ConfigureAwait(false);
    }

    internal static IReadOnlyList<(string Path, long Length, string Sha256)> HashTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }
        return MonitoredPathRules.EnumerateFilesRejectingLinks(
                root,
                maximumEntries: 4_096,
                maximumPathUtf8Bytes: 4_096)
            .Order(StringComparer.Ordinal)
            .Select(path => (
                Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                new FileInfo(path).Length,
                MonitoredFile.HashFile(path)))
            .ToArray();
    }

    private static async Task<MonitoredPackagePublishResult> PublishCoreAsync(
        MonitoredWorkerDescriptor descriptor,
        IDurableCounterStorageAdapterFactory factory,
        DurableStoragePackageManifest manifest,
        IReadOnlyList<DurableStorageMember> canonicalMembers,
        long logicalCommittedBytes,
        string? sourceRoot,
        Stopwatch finalizationDeadline,
        Func<string, bool, Task>? observeBoundary = null)
    {
        observeBoundary ??= MonitoredWorkerControl.ObserveBoundaryAsync;
        var manifestPath = Path.Combine(
            descriptor.PackageStagingRoot,
            DurableStoragePackageLayout.ManifestFile);
        MonitoredFile.WriteNewJson(manifestPath, manifest);
        var manifestMember = DescribeMember(
            manifestPath,
            DurableStoragePackageLayout.ManifestFile,
            DurableStorageMemberRole.Manifest);
        var seal = new DurableStoragePackageSeal(
            DurableStorageExperimentVersions.SealSchema,
            descriptor.CaptureId,
            descriptor.ArtifactId,
            manifestMember.Sha256,
            DurableStorageSealRules.CanonicalOrder(canonicalMembers));
        MonitoredFile.WriteNewJson(
            Path.Combine(descriptor.PackageStagingRoot, DurableStoragePackageLayout.SealFile),
            seal);
        await observeBoundary(
            "after-manifest-and-seal-publication",
            true).ConfigureAwait(false);

        var stagingBytes = ExactQuiescentBytes(descriptor.PackageStagingRoot);
        var sourceBytes = sourceRoot is null ? 0 : ExactQuiescentBytes(sourceRoot);
        if (stagingBytes > 268_435_456
            || sourceBytes > 268_435_456 - stagingBytes)
        {
            throw Error(
                "ExactQuiescentPackageLimitExceeded",
                "The exact quiescent package or combined source/recovery lengths exceed 256 MiB.");
        }
        if (finalizationDeadline.Elapsed > TimeSpan.FromSeconds(10))
        {
            throw Error(
                "FinalizationDeadlineExceeded",
                "Manifest and seal publication exceeded the shared 10-second deadline.");
        }

        if (Directory.Exists(descriptor.PackageRoot))
        {
            throw Error("PackageAlreadyExists", "A final package root already exists.");
        }
        Directory.Move(descriptor.PackageStagingRoot, descriptor.PackageRoot);
        MakeImmutable(descriptor.PackageRoot);
        finalizationDeadline.Stop();
        if (finalizationDeadline.Elapsed > TimeSpan.FromSeconds(10))
        {
            throw Error(
                "FinalizationDeadlineExceeded",
                "The shared pre-seal, manifest, seal, inventory, and immutability stages exceeded 10 seconds.");
        }

        IDurableCounterReadonlyStore? reader = null;
        IReadOnlyList<(string Path, long Length, string Sha256)>? immutableHash = null;
        TimeSpan reopenAndFirstQuery;
        try
        {
            reopenAndFirstQuery = await MonitoredReopenMeasurement.MeasureAsync(
                () => observeBoundary(
                    "before-ordinary-reopen",
                    false),
                async () =>
                {
                    var validatedPackage = ValidateImmutablePackage(
                        descriptor.PackageRoot,
                        manifest);
                    immutableHash = validatedPackage.Inventory;
                    var limits = Limits();
                    reader = factory.OpenReadonly(new DurableStorageOpenRequest(
                        descriptor.PackageRoot,
                        validatedPackage.Manifest,
                        limits));
                    var firstSummary = (await reader.SummaryAsync(CancellationToken.None)
                            .ConfigureAwait(false))
                        .ToArray();
                    if (firstSummary.Length > limits.DistinctKeys
                        || JsonSerializer.SerializeToUtf8Bytes(firstSummary, ResultJsonOptions).Length
                            > limits.ResultBytes)
                    {
                        throw Error(
                            "FirstQueryResultLimitExceeded",
                            "The first reopened typed query exceeded the frozen result bounds.");
                    }
                },
                () => observeBoundary(
                    "after-ordinary-reopen-and-first-query",
                    false)).ConfigureAwait(false);
            var observedLogicalBytes = logicalCommittedBytes > 0
                ? logicalCommittedBytes
                : await SumEncodedBytesAsync(reader!).ConfigureAwait(false);
            if (!immutableHash!.SequenceEqual(HashTree(descriptor.PackageRoot)))
            {
                throw Error("OrdinaryReopenMutatedPackage", "Fresh ordinary reopen changed a sealed package.");
            }
            return new MonitoredPackagePublishResult(
                reader!,
                stagingBytes,
                observedLogicalBytes,
                finalizationDeadline.Elapsed,
                reopenAndFirstQuery,
                checked(sourceBytes + stagingBytes));
        }
        catch
        {
            if (reader is not null)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    private static ValidatedImmutablePackage ValidateImmutablePackage(
        string packageRoot,
        DurableStoragePackageManifest expectedManifest)
    {
        var manifestPath = Path.Combine(packageRoot, DurableStoragePackageLayout.ManifestFile);
        var sealPath = Path.Combine(packageRoot, DurableStoragePackageLayout.SealFile);
        var manifestBytes = MonitoredFile.ReadBounded(manifestPath, 1_048_576);
        var expectedManifestBytes = JsonSerializer.SerializeToUtf8Bytes(expectedManifest, JsonOptions);
        if (!manifestBytes.AsSpan().SequenceEqual(expectedManifestBytes))
        {
            throw Error(
                "ImmutableManifestMismatch",
                "The published manifest differs from the manifest produced by finalization.");
        }
        var manifest = JsonSerializer.Deserialize<DurableStoragePackageManifest>(
                manifestBytes,
                StrictJsonOptions)
            ?? throw Error("ImmutableManifestInvalid", "The published manifest was empty.");
        var seal = JsonSerializer.Deserialize<DurableStoragePackageSeal>(
                MonitoredFile.ReadBounded(sealPath, 1_048_576),
                StrictJsonOptions)
            ?? throw Error("ImmutableSealInvalid", "The published seal was empty.");
        var manifestHash = MonitoredFile.HashBytes(manifestBytes);
        if (!string.Equals(
                seal.SealVersion,
                DurableStorageExperimentVersions.SealSchema,
                StringComparison.Ordinal)
            || !string.Equals(seal.CaptureId, manifest.CaptureId, StringComparison.Ordinal)
            || !string.Equals(seal.ArtifactId, manifest.ArtifactId, StringComparison.Ordinal)
            || !string.Equals(seal.ManifestSha256, manifestHash, StringComparison.Ordinal))
        {
            throw Error(
                "ImmutableSealInvalid",
                "The immutable package seal does not bind the published manifest identity.");
        }

        var expectedCanonical = DurableStorageSealRules.CanonicalOrder(
            manifest.Members.Where(static member =>
                member.Role == DurableStorageMemberRole.CanonicalData));
        if (!seal.CanonicalMembers.SequenceEqual(expectedCanonical))
        {
            throw Error(
                "ImmutableSealInvalid",
                "The immutable seal does not list the manifest's canonical members in canonical order.");
        }

        var inventory = HashTree(packageRoot);
        var observed = inventory.ToDictionary(static item => item.Path, StringComparer.Ordinal);
        var declaredPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            DurableStoragePackageLayout.ManifestFile,
            DurableStoragePackageLayout.SealFile,
        };
        foreach (var member in manifest.Members)
        {
            if (!IsPackageMemberPath(member)
                || !declaredPaths.Add(member.RelativePath)
                || !observed.TryGetValue(member.RelativePath, out var file)
                || file.Length != member.Length
                || !string.Equals(file.Sha256, member.Sha256, StringComparison.Ordinal))
            {
                throw Error(
                    "ImmutableMemberInvalid",
                    "An immutable package member is undeclared, duplicated, missing, or does not match its sealed identity.");
            }
        }
        if (observed.Count != declaredPaths.Count
            || declaredPaths.Any(path => !observed.ContainsKey(path)))
        {
            throw Error(
                "ImmutablePackageContainsUndeclaredFiles",
                "The immutable package contains files outside its manifest and seal.");
        }
        EnsureImmutableModes(packageRoot, observed.Keys);
        return new ValidatedImmutablePackage(manifest, inventory);
    }

    private static bool IsPackageMemberPath(DurableStorageMember member)
    {
        var directory = member.Role switch
        {
            DurableStorageMemberRole.CanonicalData => DurableStoragePackageLayout.CanonicalDirectory,
            DurableStorageMemberRole.QueryIndex => DurableStoragePackageLayout.QueryDirectory,
            _ => string.Empty,
        };
        return directory.Length > 0
            && member.RelativePath.StartsWith($"{directory}/", StringComparison.Ordinal)
            && !Path.IsPathRooted(member.RelativePath)
            && !member.RelativePath.Contains('\\')
            && !member.RelativePath.Contains(':')
            && !member.RelativePath.Split('/').Any(static part =>
                string.IsNullOrWhiteSpace(part) || part is "." or "..");
    }

    private static void EnsureImmutableModes(
        string packageRoot,
        IEnumerable<string> relativePaths)
    {
        const UnixFileMode writable = UnixFileMode.UserWrite
            | UnixFileMode.GroupWrite
            | UnixFileMode.OtherWrite;
        var checkedDirectories = new HashSet<string>(StringComparer.Ordinal);
        EnsureNotWritable(packageRoot, writable);
        checkedDirectories.Add(Path.GetFullPath(packageRoot));
        foreach (var relativePath in relativePaths)
        {
            var path = MonitoredPathRules.CombineContained(packageRoot, relativePath);
            EnsureNotWritable(path, writable);
            var directory = Path.GetDirectoryName(path);
            while (directory is not null
                && MonitoredPathRules.IsContained(packageRoot, directory)
                && checkedDirectories.Add(directory))
            {
                EnsureNotWritable(directory, writable);
                if (MonitoredPathRules.PathsEqual(directory, packageRoot))
                {
                    break;
                }
                directory = Path.GetDirectoryName(directory);
            }
        }
    }

    private static void EnsureNotWritable(string path, UnixFileMode writable)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw Error("UnsupportedPlatform", "Monitored package immutability is Linux-only.");
        }
        if ((File.GetUnixFileMode(path) & writable) != 0)
        {
            throw Error(
                "ImmutablePackageWritable",
                "The published package or one of its members remains writable.");
        }
    }

    private static DurableStorageMember DescribeMember(
        string path,
        string relativePath,
        DurableStorageMemberRole role)
        => new(relativePath, new FileInfo(path).Length, MonitoredFile.HashFile(path), role);

    private static async Task<long> SumEncodedBytesAsync(IDurableCounterReadonlyStore reader)
    {
        long total = 0;
        var rows = 0;
        var summary = await reader.SummaryAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var key in summary)
        {
            long? cursor = null;
            do
            {
                var page = await reader.SeriesAsync(
                    key.Provider,
                    key.Name,
                    cursor,
                    100,
                    CancellationToken.None).ConfigureAwait(false);
                foreach (var row in page.Rows)
                {
                    total = checked(total + row.EncodedBytes);
                    rows++;
                    if (rows > 50_000)
                    {
                        throw Error(
                            "LogicalByteCountBoundExceeded",
                            "Logical-byte accounting exceeded the frozen O1 population.");
                    }
                }
                cursor = page.NextAfterSequence;
            }
            while (cursor.HasValue);
        }
        return total;
    }

    private static long ExactQuiescentBytes(string root)
    {
        var identities = new HashSet<ApparentFileIdentity>();
        long bytes = 0;
        foreach (var path in MonitoredPathRules.EnumerateFilesRejectingLinks(
                     root,
                     maximumEntries: 4_096,
                     maximumPathUtf8Bytes: 4_096))
        {
            if (new FileInfo(path).LinkTarget is not null)
            {
                throw Error("PackageLinkRejected", "Package members cannot be symbolic links.");
            }
            using var handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var observed = LinuxStatxHandleMetadataObserver.Instance.Observe(handle);
            if (observed.LinkCount != 1 || !identities.Add(observed.Identity))
            {
                throw Error(
                    "PackageLinkRejected",
                    "Package members must be unique single-link regular files.");
            }
            bytes = checked(bytes + observed.Length);
        }
        return bytes;
    }

    internal static void MakeImmutable(string root)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw Error("UnsupportedPlatform", "Monitored package immutability is Linux-only.");
        }
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(static path => path.Length))
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private sealed record ValidatedImmutablePackage(
        DurableStoragePackageManifest Manifest,
        IReadOnlyList<(string Path, long Length, string Sha256)> Inventory);

    private static DurableCounterPipelineLimits Limits()
        => new(BatchMaxAge: TimeSpan.FromMilliseconds(100));

    private static DurableStorageExperimentException Error(string code, string message) => new(code, message);
}

internal sealed class BoundedLiveRequestLoad
{
    private const int MaximumRetainedSamples = 1_000;
    private static readonly TimeSpan MeasurementStart = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MeasurementEnd = TimeSpan.FromSeconds(42);
    private readonly Uri _endpoint;
    private readonly MonitoredRequestPopulation _population =
        new(MeasurementStart, MeasurementEnd, MaximumRetainedSamples);
    private readonly object _gate = new();
    private int _inFlight;

    internal BoundedLiveRequestLoad(string baseUrl)
        => _endpoint = new Uri(new Uri(baseUrl), "/cpu-burn?ms=10");

    internal async Task RunAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
        };
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        await RunAsync(client, duration, cancellationToken).ConfigureAwait(false);
    }

    internal async Task RunAsync(
        HttpClient client,
        TimeSpan duration,
        CancellationToken cancellationToken,
        Func<TimeSpan>? elapsed = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var tasks = new List<Task>(MaximumRetainedSamples);
        var watch = Stopwatch.StartNew();
        elapsed ??= () => watch.Elapsed;
        delay ??= static (value, token) => Task.Delay(value, token);
        var interval = TimeSpan.FromMilliseconds(50);
        var next = TimeSpan.Zero;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dispatchedAt = elapsed();
            if (dispatchedAt >= duration)
            {
                break;
            }
            var remaining = (next < duration ? next : duration) - dispatchedAt;
            if (remaining > TimeSpan.Zero)
            {
                await delay(remaining, cancellationToken).ConfigureAwait(false);
                continue;
            }
            // Cohort membership follows the fixed slot, not timer delivery jitter.
            var scheduledAt = next;
            var skipped = false;
            lock (_gate)
            {
                if (_inFlight >= 2)
                {
                    skipped = true;
                }
                else
                {
                    if (tasks.Count == MaximumRetainedSamples)
                    {
                        throw new DurableStorageExperimentException(
                            "LiveRequestTaskLimit",
                            "The live workload exceeded its 1,000-request retention budget.");
                    }
                    _inFlight++;
                }
            }
            _population.RecordScheduled(scheduledAt, skipped);
            next += interval;
            if (skipped)
            {
                continue;
            }
            tasks.Add(SendAsync(client, scheduledAt, cancellationToken));
        }
        _population.RecordSchedulingStopped(elapsed());
        await Task.WhenAll(tasks).ConfigureAwait(false);
        _population.RecordEpisodeCompleted(elapsed());
    }

    internal static bool HasCompleteSchedule(MonitoredRequestMetrics? metrics)
        => metrics is { Scheduled: 600, EpisodeScheduled: 880 }
            && double.IsFinite(metrics.SchedulingElapsedSeconds)
            && metrics.SchedulingElapsedSeconds >= 44
            && double.IsFinite(metrics.EpisodeElapsedSeconds)
            && metrics.EpisodeElapsedSeconds >= metrics.SchedulingElapsedSeconds;

    internal double ElapsedSeconds => _population.Snapshot().SchedulingElapsedSeconds;

    internal MonitoredRequestMetrics Snapshot() => _population.Snapshot();

    private async Task SendAsync(
        HttpClient client,
        TimeSpan scheduledAt,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var success = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            using var response = await client.GetAsync(
                _endpoint,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            success = response.IsSuccessStatusCode
                && (int)response.StatusCode is >= 200 and < 300
                && response.StatusCode is not (HttpStatusCode.Moved
                    or HttpStatusCode.Redirect
                    or HttpStatusCode.RedirectMethod
                    or HttpStatusCode.TemporaryRedirect
                    or HttpStatusCode.PermanentRedirect);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or OperationCanceledException)
        {
            success = false;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            lock (_gate)
            {
                _inFlight--;
            }
            _population.RecordCompleted(scheduledAt, elapsed, success);
        }
    }
}

internal sealed class MonitoredRequestPopulation(
    TimeSpan measurementStart,
    TimeSpan measurementEnd,
    int maximumRetainedSamples)
{
    private readonly object _gate = new();
    private readonly List<double> _measurementLatencies = new(maximumRetainedSamples);
    private int _scheduled;
    private int _skipped;
    private int _completed;
    private int _succeeded;
    private int _failed;
    private int _episodeScheduled;
    private int _episodeSkipped;
    private int _episodeCompleted;
    private int _episodeSucceeded;
    private int _episodeFailed;
    private double _schedulingElapsedSeconds;
    private double _episodeElapsedSeconds;
    private bool _retentionExceeded;

    internal void RecordScheduled(TimeSpan scheduledAt, bool skipped)
    {
        lock (_gate)
        {
            _episodeScheduled++;
            if (skipped)
            {
                _episodeSkipped++;
            }
            if (!IsMeasured(scheduledAt))
            {
                return;
            }
            _scheduled++;
            if (skipped)
            {
                _skipped++;
            }
        }
    }

    internal void RecordCompleted(TimeSpan scheduledAt, double elapsedMilliseconds, bool success)
    {
        lock (_gate)
        {
            _episodeCompleted++;
            if (success)
            {
                _episodeSucceeded++;
            }
            else
            {
                _episodeFailed++;
            }
            if (!IsMeasured(scheduledAt))
            {
                return;
            }
            _completed++;
            if (success)
            {
                _succeeded++;
            }
            else
            {
                _failed++;
            }
            if (_measurementLatencies.Count == maximumRetainedSamples)
            {
                _retentionExceeded = true;
            }
            else
            {
                _measurementLatencies.Add(elapsedMilliseconds);
            }
        }
    }

    internal void RecordSchedulingStopped(TimeSpan elapsed)
    {
        lock (_gate)
        {
            _schedulingElapsedSeconds = elapsed.TotalSeconds;
        }
    }

    internal void RecordEpisodeCompleted(TimeSpan elapsed)
    {
        lock (_gate)
        {
            _episodeElapsedSeconds = elapsed.TotalSeconds;
            if (_retentionExceeded)
            {
                throw new DurableStorageExperimentException(
                    "LiveRequestSampleLimit",
                    "The scored live population exceeded 1,000 retained request samples.");
            }
        }
    }

    internal MonitoredRequestMetrics Snapshot()
    {
        lock (_gate)
        {
            var ordered = _measurementLatencies.Order().ToArray();
            return new MonitoredRequestMetrics(
                _scheduled,
                _skipped,
                _completed,
                _succeeded,
                _failed,
                ordered.Length,
                Quantile(ordered, 0.50),
                Quantile(ordered, 0.95),
                _episodeScheduled,
                _episodeSkipped,
                _episodeCompleted,
                _episodeSucceeded,
                _episodeFailed,
                _schedulingElapsedSeconds,
                _episodeElapsedSeconds);
        }
    }

    private bool IsMeasured(TimeSpan scheduledAt)
        => scheduledAt >= measurementStart && scheduledAt < measurementEnd;

    private static double? Quantile(double[] ordered, double quantile)
    {
        if (ordered.Length == 0)
        {
            return null;
        }
        var index = (int)Math.Ceiling(quantile * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }
}
