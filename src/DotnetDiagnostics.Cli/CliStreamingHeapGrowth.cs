using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli;

internal static partial class CliStreamingProtocol
{
    private const int MinimumHeapGrowthStoreCapacity = 2;

    private sealed record HeapGrowthAttemptRequest(
        string AttemptId,
        RetainedGrowthAttemptOutcome Outcome,
        string? BaselineHandle,
        string? CurrentHandle,
        string? FailureReason,
        int? ObservedGen2Collections,
        RetainedGrowthAllocationObservation? Allocation);

    private static object ProjectHeapGrowthCapability(IServiceProvider services)
    {
        var handles = services.GetService<IDiagnosticHandleStore>();
        var storeCapacity = handles is MemoryDiagnosticHandleStore memoryStore
            ? memoryStore.Capacity
            : services.GetService<DiagnosticHandleStoreOptions>()?.MaxEntries;
        return new
        {
            version = 1,
            available = storeCapacity is >= MinimumHeapGrowthStoreCapacity,
            storeCapacity,
            minimumStoreCapacity = MinimumHeapGrowthStoreCapacity,
            queryView = HeapGrowthView,
            handleKind = HeapInspectionUseCases.HeapSnapshotKind,
            baselineField = "baselineHandle",
            objectiveTargetField = "typeFilter",
            comparisonPairsField = "comparisonPairs",
            objectiveSchema = RetainedGrowthEvidenceResult.SchemaV1,
            maxAttempts = RetainedGrowthEvidenceBounds.MaximumAttempts,
            minimumRobustPairs = RetainedGrowthEvidenceBounds.MinimumRobustPairs,
            rankBy = new[] { HeapGrowthDiff.RankByBytes, HeapGrowthDiff.RankByInstances },
            defaultMinDeltaPct = 5.0,
            defaultTopN = 25,
            maxTopN = 500,
            requiresSameProcess = true,
            requiresSameProvenance = true,
            storage = "in-memory",
            boundedLifetime = true,
            storePolicy = "shared-configured-bounded",
        };
    }

    private static bool TryParseHeapGrowthAttempts(
        JsonElement root,
        bool isHeapGrowth,
        out IReadOnlyList<HeapGrowthAttemptRequest> attempts,
        out string? error)
    {
        attempts = [];
        error = null;
        if (!root.TryGetProperty("comparisonPairs", out var pairsElement)
            || pairsElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (!isHeapGrowth)
        {
            error = "'comparisonPairs' is supported only for view 'growth'.";
            return false;
        }

        if (pairsElement.ValueKind != JsonValueKind.Array)
        {
            error = $"'comparisonPairs' must be an array with at most {RetainedGrowthEvidenceBounds.MaximumAttempts - 1} additional attempts.";
            return false;
        }

        var pairElements = pairsElement.EnumerateArray().ToArray();
        if (pairElements.Length > RetainedGrowthEvidenceBounds.MaximumAttempts - 1)
        {
            error = $"'comparisonPairs' accepts at most {RetainedGrowthEvidenceBounds.MaximumAttempts - 1} additional attempts because the primary handle pair counts toward the {RetainedGrowthEvidenceBounds.MaximumAttempts}-attempt bound.";
            return false;
        }

        var parsed = new List<HeapGrowthAttemptRequest>(pairElements.Length);
        var attemptIds = new HashSet<string>(StringComparer.Ordinal) { "primary" };
        var completedPairs = new HashSet<string>(StringComparer.Ordinal);
        if (TryGetString(root, "baselineHandle", out var primaryBaseline)
            && TryGetString(root, "handle", out var primaryCurrent))
        {
            completedPairs.Add(primaryBaseline + "\0" + primaryCurrent);
        }

        for (var index = 0; index < pairElements.Length; index++)
        {
            var element = pairElements[index];
            if (element.ValueKind != JsonValueKind.Object)
            {
                error = $"'comparisonPairs[{index}]' must be an object.";
                return false;
            }

            if (!TryGetString(element, "attemptId", out var attemptId)
                || attemptId is not { Length: <= 128 }
                || string.IsNullOrWhiteSpace(attemptId)
                || attemptId.Any(char.IsControl)
                || !attemptIds.Add(attemptId))
            {
                error = $"'comparisonPairs[{index}].attemptId' must be a unique non-empty string of at most 128 characters without control characters; 'primary' is reserved.";
                return false;
            }

            var outcome = RetainedGrowthAttemptOutcome.Completed;
            if (element.TryGetProperty("outcome", out var outcomeElement)
                && outcomeElement.ValueKind != JsonValueKind.Null)
            {
                var outcomeText = outcomeElement.ValueKind == JsonValueKind.String
                    ? outcomeElement.GetString()
                    : null;
                outcome = outcomeText switch
                {
                    "completed" => RetainedGrowthAttemptOutcome.Completed,
                    "failed" => RetainedGrowthAttemptOutcome.Failed,
                    "aborted" => RetainedGrowthAttemptOutcome.Aborted,
                    _ => (RetainedGrowthAttemptOutcome)(-1),
                };
                if (!Enum.IsDefined(outcome))
                {
                    error = $"'comparisonPairs[{index}].outcome' must be 'completed', 'failed', or 'aborted'.";
                    return false;
                }
            }

            string? baselineHandle = null;
            string? currentHandle = null;
            string? failureReason = null;
            if (outcome == RetainedGrowthAttemptOutcome.Completed)
            {
                if (!TryGetString(element, "baselineHandle", out baselineHandle)
                    || !IsValidProtocolHandle(baselineHandle)
                    || !TryGetString(element, "currentHandle", out currentHandle)
                    || !IsValidProtocolHandle(currentHandle))
                {
                    error = $"'comparisonPairs[{index}]' completed attempts require valid non-empty 'baselineHandle' and 'currentHandle' strings.";
                    return false;
                }

                if (string.Equals(baselineHandle, currentHandle, StringComparison.Ordinal))
                {
                    error = $"'comparisonPairs[{index}]' baseline and current handles must be different captures.";
                    return false;
                }

                if (!completedPairs.Add(baselineHandle + "\0" + currentHandle))
                {
                    error = $"'comparisonPairs[{index}]' duplicates a handle pair already present in this request; repeated evidence requires distinct capture pairs.";
                    return false;
                }
            }
            else
            {
                if (!TryGetString(element, "failureReason", out failureReason)
                    || failureReason is not { Length: <= 512 }
                    || string.IsNullOrWhiteSpace(failureReason)
                    || failureReason.Any(char.IsControl))
                {
                    error = $"'comparisonPairs[{index}].failureReason' is required for failed or aborted attempts and must be at most 512 characters without control characters.";
                    return false;
                }

                if (element.TryGetProperty("baselineHandle", out _)
                    || element.TryGetProperty("currentHandle", out _))
                {
                    error = $"'comparisonPairs[{index}]' failed or aborted attempts must not provide heap handles.";
                    return false;
                }
            }

            int? observedGen2Collections = null;
            if (element.TryGetProperty("observedGen2Collections", out var gen2Element)
                && gen2Element.ValueKind != JsonValueKind.Null)
            {
                if (!gen2Element.TryGetInt32(out var gen2) || gen2 < 0)
                {
                    error = $"'comparisonPairs[{index}].observedGen2Collections' must be a non-negative Int32.";
                    return false;
                }
                observedGen2Collections = gen2;
            }

            RetainedGrowthAllocationObservation? allocation = null;
            if (element.TryGetProperty("allocation", out var allocationElement)
                && allocationElement.ValueKind != JsonValueKind.Null)
            {
                if (allocationElement.ValueKind != JsonValueKind.Object
                    || !allocationElement.TryGetProperty("sampledBytes", out var sampledBytesElement)
                    || !sampledBytesElement.TryGetInt64(out var sampledBytes)
                    || sampledBytes < 0
                    || !allocationElement.TryGetProperty("sampleEvents", out var sampleEventsElement)
                    || !sampleEventsElement.TryGetInt64(out var sampleEvents)
                    || sampleEvents < 0
                    || !allocationElement.TryGetProperty("lostOrDropped", out var lostElement)
                    || !lostElement.TryGetInt64(out var lostOrDropped)
                    || lostOrDropped < 0)
                {
                    error = $"'comparisonPairs[{index}].allocation' must contain non-negative Int64 sampledBytes, sampleEvents, and lostOrDropped fields.";
                    return false;
                }

                allocation = new RetainedGrowthAllocationObservation(
                    sampledBytes,
                    sampleEvents,
                    lostOrDropped);
            }

            parsed.Add(new HeapGrowthAttemptRequest(
                attemptId,
                outcome,
                baselineHandle,
                currentHandle,
                failureReason,
                observedGen2Collections,
                allocation));
        }

        attempts = parsed;
        return true;
    }

    private static async Task HandleHeapGrowthQueryAsync(
        string requestId,
        ProtocolWriter writer,
        IDiagnosticHandleStore handles,
        string currentHandle,
        HandleLookup currentLookup,
        string baselineHandle,
        string rankBy,
        double minDeltaPct,
        int topN,
        string? objectiveTargetType,
        IReadOnlyList<HeapGrowthAttemptRequest> comparisonAttempts,
        CancellationToken cancellationToken)
    {
        if (string.Equals(baselineHandle, currentHandle, StringComparison.Ordinal))
        {
            await WriteGrowthErrorAsync(
                writer,
                requestId,
                "invalid_query",
                "The baseline and current heap handles must be different captures.",
                reason: "same_handle").ConfigureAwait(false);
            return;
        }

        var baselineResult = handles.LookupWithKind(baselineHandle);
        if (baselineResult.Lookup is not { } baselineLookup)
        {
            await WriteGrowthHandleUnavailableAsync(
                writer,
                requestId,
                baselineHandle,
                "baseline",
                baselineResult.Status).ConfigureAwait(false);
            return;
        }

        if (baselineLookup.Kind != HeapInspectionUseCases.HeapSnapshotKind)
        {
            await WriteGrowthUnsupportedHandleKindAsync(
                writer,
                requestId,
                baselineHandle,
                "baseline",
                baselineLookup.Kind).ConfigureAwait(false);
            return;
        }

        if (currentLookup.Artifact is not HeapSnapshotArtifact current
            || baselineLookup.Artifact is not HeapSnapshotArtifact baseline)
        {
            await WriteGrowthErrorAsync(
                writer,
                requestId,
                "query_failed",
                "One or both heap-snapshot handles contain an incompatible artifact payload. Capture fresh heap snapshots and retry.",
                reason: "invalid_payload").ConfigureAwait(false);
            return;
        }

        if (baseline.ProcessId != current.ProcessId)
        {
            await WriteGrowthErrorAsync(
                writer,
                requestId,
                "invalid_query",
                $"Heap growth comparison requires snapshots from the same process; baseline pid is {baseline.ProcessId} and current pid is {current.ProcessId}.",
                reason: "process_mismatch").ConfigureAwait(false);
            return;
        }

        if (baseline.Origin != current.Origin)
        {
            await WriteGrowthErrorAsync(
                writer,
                requestId,
                "invalid_query",
                $"Heap growth comparison requires matching capture provenance; baseline origin is '{baseline.Origin}' and current origin is '{current.Origin}'.",
                reason: "provenance_mismatch").ConfigureAwait(false);
            return;
        }

        if (current.CapturedAt < baseline.CapturedAt)
        {
            await WriteGrowthErrorAsync(
                writer,
                requestId,
                "invalid_query",
                $"The current heap snapshot was captured at {current.CapturedAt:O}, before baseline {baseline.CapturedAt:O}. Pass the earlier handle as 'baselineHandle'.",
                reason: "reversed_chronology").ConfigureAwait(false);
            return;
        }

        if (current.Origin is HeapSnapshotOrigin.Live or HeapSnapshotOrigin.GcDump)
        {
            if (!TryGetRunningProcessStartTime(current.ProcessId, out var processStartedAt, out var failure))
            {
                await WriteGrowthErrorAsync(
                    writer,
                    requestId,
                    "invalid_query",
                    $"Heap growth comparison cannot validate target pid {current.ProcessId}: {failure}",
                    reason: "target_process_unavailable").ConfigureAwait(false);
                return;
            }

            if (processStartedAt > baseline.CapturedAt || processStartedAt > current.CapturedAt)
            {
                await WriteGrowthErrorAsync(
                    writer,
                    requestId,
                    "invalid_query",
                    $"Heap growth comparison rejected pid {current.ProcessId} because its current lifetime started at {processStartedAt:O}, after one or both captures ({baseline.CapturedAt:O}, {current.CapturedAt:O}). The PID was reused or the capture identity is stale.",
                    reason: "process_lifetime_mismatch").ConfigureAwait(false);
                return;
            }
        }

        var objectiveAttempts = objectiveTargetType is null
            ? null
            : new List<RetainedGrowthEvidenceAttempt>(
                Math.Min(RetainedGrowthEvidenceBounds.MaximumAttempts, comparisonAttempts.Count + 1))
            {
                new(
                    "primary",
                    RetainedGrowthAttemptOutcome.Completed,
                    baselineHandle,
                    baseline,
                    currentHandle,
                    current),
            };

        if (objectiveAttempts is not null)
        {
            foreach (var attempt in comparisonAttempts)
            {
                if (attempt.Outcome != RetainedGrowthAttemptOutcome.Completed)
                {
                    objectiveAttempts.Add(new RetainedGrowthEvidenceAttempt(
                        attempt.AttemptId,
                        attempt.Outcome,
                        null,
                        null,
                        null,
                        null)
                    {
                        FailureReason = attempt.FailureReason,
                        ObservedGen2Collections = attempt.ObservedGen2Collections,
                        Allocation = attempt.Allocation,
                    });
                    continue;
                }

                var additionalBaselineResult = handles.LookupWithKind(attempt.BaselineHandle!);
                if (additionalBaselineResult.Lookup is not { } additionalBaselineLookup)
                {
                    await WriteGrowthHandleUnavailableAsync(
                        writer,
                        requestId,
                        attempt.BaselineHandle!,
                        $"comparisonPairs[{attempt.AttemptId}].baseline",
                        additionalBaselineResult.Status).ConfigureAwait(false);
                    return;
                }

                var additionalCurrentResult = handles.LookupWithKind(attempt.CurrentHandle!);
                if (additionalCurrentResult.Lookup is not { } additionalCurrentLookup)
                {
                    await WriteGrowthHandleUnavailableAsync(
                        writer,
                        requestId,
                        attempt.CurrentHandle!,
                        $"comparisonPairs[{attempt.AttemptId}].current",
                        additionalCurrentResult.Status).ConfigureAwait(false);
                    return;
                }

                if (additionalBaselineLookup.Kind != HeapInspectionUseCases.HeapSnapshotKind
                    || additionalCurrentLookup.Kind != HeapInspectionUseCases.HeapSnapshotKind
                    || additionalBaselineLookup.Artifact is not HeapSnapshotArtifact additionalBaseline
                    || additionalCurrentLookup.Artifact is not HeapSnapshotArtifact additionalCurrent)
                {
                    await WriteGrowthErrorAsync(
                        writer,
                        requestId,
                        "invalid_query",
                        $"Comparison attempt '{attempt.AttemptId}' requires two heap-snapshot handles.",
                        reason: "comparison_pair_invalid_payload").ConfigureAwait(false);
                    return;
                }

                if (!TryValidateGrowthPair(
                        additionalBaseline,
                        attempt.BaselineHandle!,
                        additionalCurrent,
                        attempt.CurrentHandle!,
                        out var validationMessage,
                        out var validationReason))
                {
                    await WriteGrowthErrorAsync(
                        writer,
                        requestId,
                        "invalid_query",
                        $"Comparison attempt '{attempt.AttemptId}' is invalid: {validationMessage}",
                        validationReason).ConfigureAwait(false);
                    return;
                }

                objectiveAttempts.Add(new RetainedGrowthEvidenceAttempt(
                    attempt.AttemptId,
                    RetainedGrowthAttemptOutcome.Completed,
                    attempt.BaselineHandle,
                    additionalBaseline,
                    attempt.CurrentHandle,
                    additionalCurrent)
                {
                    ObservedGen2Collections = attempt.ObservedGen2Collections,
                    Allocation = attempt.Allocation,
                });
            }
        }

        var growth = HeapGrowthDiff.Build(
            baseline,
            baselineHandle,
            current,
            currentHandle,
            rankBy,
            minDeltaPct,
            topN,
            objectiveTargetType is null ? null : new TypeIdentity(objectiveTargetType),
            objectiveAttempts);

        await writer.WriteAsync(new
        {
            type = "query",
            requestId,
            handle = currentHandle,
            baselineHandle,
            view = HeapGrowthView,
            result = objectiveTargetType is null
                ? ProjectHeapGrowth(growth, baseline.Origin, current.Origin)
                : ProjectObjectiveRetainedGrowth(growth, baseline.Origin, current.Origin),
        }, cancellationToken).ConfigureAwait(false);
    }

    private static object ProjectObjectiveRetainedGrowth(
        HeapGrowthResult growth,
        HeapSnapshotOrigin baselineOrigin,
        HeapSnapshotOrigin currentOrigin) => new
    {
        growth.BaselineHandle,
        growth.CurrentHandle,
        growth.ProcessId,
        growth.BaselineCapturedAt,
        growth.CurrentCapturedAt,
        growth.Elapsed,
        baselineOrigin,
        currentOrigin,
        objectiveRetainedGrowth = growth.ObjectiveRetainedGrowth,
    };

    private static bool TryValidateGrowthPair(
        HeapSnapshotArtifact baseline,
        string baselineHandle,
        HeapSnapshotArtifact current,
        string currentHandle,
        out string message,
        out string reason)
    {
        if (string.Equals(baselineHandle, currentHandle, StringComparison.Ordinal))
        {
            message = "The baseline and current heap handles must be different captures.";
            reason = "same_handle";
            return false;
        }

        if (baseline.ProcessId != current.ProcessId)
        {
            message = $"Heap growth comparison requires snapshots from the same process; baseline pid is {baseline.ProcessId} and current pid is {current.ProcessId}.";
            reason = "process_mismatch";
            return false;
        }

        if (baseline.Origin != current.Origin)
        {
            message = $"Heap growth comparison requires matching capture provenance; baseline origin is '{baseline.Origin}' and current origin is '{current.Origin}'.";
            reason = "provenance_mismatch";
            return false;
        }

        if (current.CapturedAt < baseline.CapturedAt)
        {
            message = $"The current heap snapshot was captured at {current.CapturedAt:O}, before baseline {baseline.CapturedAt:O}. Pass the earlier handle as 'baselineHandle'.";
            reason = "reversed_chronology";
            return false;
        }

        if (current.Origin is HeapSnapshotOrigin.Live or HeapSnapshotOrigin.GcDump)
        {
            if (!TryGetRunningProcessStartTime(current.ProcessId, out var processStartedAt, out var failure))
            {
                message = $"Heap growth comparison cannot validate target pid {current.ProcessId}: {failure}";
                reason = "target_process_unavailable";
                return false;
            }

            if (processStartedAt > baseline.CapturedAt || processStartedAt > current.CapturedAt)
            {
                message = $"Heap growth comparison rejected pid {current.ProcessId} because its current lifetime started at {processStartedAt:O}, after one or both captures ({baseline.CapturedAt:O}, {current.CapturedAt:O}). The PID was reused or the capture identity is stale.";
                reason = "process_lifetime_mismatch";
                return false;
            }
        }

        message = string.Empty;
        reason = string.Empty;
        return true;
    }

    private static object ProjectHeapGrowth(
        HeapGrowthResult growth,
        HeapSnapshotOrigin baselineOrigin,
        HeapSnapshotOrigin currentOrigin) => new
    {
        growth.BaselineHandle,
        growth.CurrentHandle,
        growth.ProcessId,
        growth.BaselineCapturedAt,
        growth.CurrentCapturedAt,
        growth.Elapsed,
        growth.RankBy,
        growth.MinDeltaPct,
        growth.BaselineHeapBytes,
        growth.CurrentHeapBytes,
        growth.TotalHeapGrowthBytes,
        growth.TotalGrowers,
        growers = growth.Growers.Select(static grower => new
        {
            grower.TypeFullName,
            grower.ModuleName,
            grower.BaselineBytes,
            grower.CurrentBytes,
            grower.BytesDelta,
            grower.BytesDeltaPercent,
            grower.BaselineInstances,
            grower.CurrentInstances,
            grower.InstancesDelta,
            grower.InstancesDeltaPercent,
            grower.IsNew,
            grower.Identity,
            retentionPaths = grower.RetentionPaths?.Select(ProjectRetentionPath),
            grower.TotalRetentionPaths,
            grower.OmittedRetentionPaths,
        }),
        growth.Verdict,
        baselineOrigin,
        currentOrigin,
        growth.Notes,
        growth.BaselineQuality,
        growth.CurrentQuality,
    };

    private static bool TryGetRunningProcessStartTime(
        int processId,
        out DateTimeOffset startedAt,
        out string failure)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                startedAt = default;
                failure = "the process has exited.";
                return false;
            }

            startedAt = new DateTimeOffset(process.StartTime).ToUniversalTime();
            if (process.HasExited)
            {
                startedAt = default;
                failure = "the process exited while its identity was being validated.";
                return false;
            }

            failure = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidOperationException
            or NotSupportedException
            or Win32Exception)
        {
            startedAt = default;
            failure = ex.Message;
            return false;
        }
    }

    private static Task WriteGrowthErrorAsync(
        ProtocolWriter writer,
        string requestId,
        string code,
        string message,
        string reason) =>
        writer.WriteAsync(new
        {
            type = "error",
            requestId,
            code,
            message,
            reason,
        }, CancellationToken.None);

    private static Task WriteGrowthHandleUnavailableAsync(
        ProtocolWriter writer,
        string requestId,
        string handle,
        string handleRole,
        DiagnosticHandleLookupStatus lookupStatus)
    {
        var status = lookupStatus switch
        {
            DiagnosticHandleLookupStatus.Expired => "expired",
            DiagnosticHandleLookupStatus.CapacityEvicted => "capacity_evicted",
            _ => "unknown",
        };
        var roleLabel = string.Equals(handleRole, "baseline", StringComparison.Ordinal)
            || handleRole.EndsWith(".baseline", StringComparison.Ordinal)
            ? "Baseline"
            : "Current";
        var message = status switch
        {
            "expired" => $"{roleLabel} handle '{handle}' expired after its TTL elapsed.",
            "capacity_evicted" => $"{roleLabel} handle '{handle}' was evicted because the in-memory handle store reached capacity.",
            _ => $"{roleLabel} handle '{handle}' is not known to this CLI stream.",
        };
        return writer.WriteAsync(new
        {
            type = "error",
            requestId,
            code = "handle_unavailable",
            message,
            handle,
            handleRole,
            status,
        }, CancellationToken.None);
    }

    private static Task WriteGrowthUnsupportedHandleKindAsync(
        ProtocolWriter writer,
        string requestId,
        string handle,
        string handleRole,
        string actualKind)
    {
        var roleLabel = string.Equals(handleRole, "baseline", StringComparison.Ordinal)
            ? "Baseline"
            : "Current";
        return writer.WriteAsync(new
        {
            type = "error",
            requestId,
            code = "unsupported_handle_kind",
            message = $"{roleLabel} handle '{handle}' was captured as '{actualKind}', but view 'growth' requires two heap-snapshot handles.",
            handle,
            handleRole,
            actualKind,
            requiredKind = HeapInspectionUseCases.HeapSnapshotKind,
        }, CancellationToken.None);
    }
}
