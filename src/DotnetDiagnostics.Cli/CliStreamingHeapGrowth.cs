using System.ComponentModel;
using System.Diagnostics;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli;

internal static partial class CliStreamingProtocol
{
    private const int MinimumHeapGrowthStoreCapacity = 2;

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

        var growth = HeapGrowthDiff.Build(
            baseline,
            baselineHandle,
            current,
            currentHandle,
            rankBy,
            minDeltaPct,
            topN);

        await writer.WriteAsync(new
        {
            type = "query",
            requestId,
            handle = currentHandle,
            baselineHandle,
            view = HeapGrowthView,
            result = ProjectHeapGrowth(growth, baseline.Origin, current.Origin),
        }, cancellationToken).ConfigureAwait(false);
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
