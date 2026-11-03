using System.ComponentModel;
using System.Net.Sockets;
using System.Security;
using DotnetDiagnostics.Core.CpuSampling;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnostics.Core.UseCases;

internal static class AllocationSampleFailureClassifier
{
    private const string EventPipeStartOperation = "allocation.eventpipe.start";
    private const string DefaultOperation = "allocation.capture";

    internal static DiagnosticResult<AllocationSample> Classify(int processId, Exception exception)
    {
        var staged = exception as AllocationSampleOperationException;
        var operation = staged?.Operation ?? DefaultOperation;
        var cause = staged?.InnerException ?? exception;

        if (staged?.TargetExited == true || HasTargetExitEvidence(cause))
        {
            return DiagnosticResult.Fail<AllocationSample>(
                $"collect_sample(kind=\"allocation\") stopped because pid {processId} exited or was replaced during capture.",
                new DiagnosticError(
                    "ProcessExited",
                    "The resolved target process was no longer the same live process when allocation capture failed.",
                    operation),
                InspectProcessHint(processId, "Re-list processes and select the current target before retrying."));
        }

        if (string.Equals(operation, EventPipeStartOperation, StringComparison.Ordinal)
            && HasDiagnosticEndpointPermissionEvidence(cause))
        {
            return DiagnosticResult.Fail<AllocationSample>(
                $"collect_sample(kind=\"allocation\") could not open the diagnostic endpoint for pid {processId} because access was denied.",
                new DiagnosticError(
                    "PermissionDenied",
                    "The target diagnostic endpoint rejected access.",
                    operation),
                InspectProcessHint(
                    processId,
                    "Verify diagnostic-socket visibility and UID or integrity-level alignment before retrying."));
        }

        if (string.Equals(operation, EventPipeStartOperation, StringComparison.Ordinal)
            && IsDiagnosticEndpointUnavailable(cause))
        {
            return DiagnosticResult.Fail<AllocationSample>(
                $"collect_sample(kind=\"allocation\") could not start because the diagnostic endpoint for pid {processId} is unavailable.",
                new DiagnosticError(
                    "EndpointUnavailable",
                    "The target diagnostic endpoint was unavailable or diagnostics were disabled.",
                    operation),
                InspectProcessHint(
                    processId,
                    "Re-list processes and confirm the target exposes diagnostics before retrying."));
        }

        if (cause is UnsupportedCommandException or UnsupportedProtocolException or NotSupportedException)
        {
            return DiagnosticResult.Fail<AllocationSample>(
                $"collect_sample(kind=\"allocation\") is not supported by the target runtime for pid {processId}.",
                new DiagnosticError(
                    "NotSupported",
                    "The target runtime does not support the required EventPipe allocation operation.",
                    operation),
                InspectProcessHint(
                    processId,
                    "Check the target runtime and capability matrix before choosing another collector."));
        }

        if (cause is TimeoutException)
        {
            return DiagnosticResult.Fail<AllocationSample>(
                $"collect_sample(kind=\"allocation\") timed out while operating on pid {processId}.",
                new DiagnosticError(
                    "CaptureTimeout",
                    "The bounded allocation capture operation exceeded its deadline.",
                    operation),
                InspectProcessHint(
                    processId,
                    "Check target responsiveness before retrying with a shorter collection window."));
        }

        if (cause is InvalidOperationException
            or InvalidDataException
            or IOException
            or ServerErrorException
            or OperationCanceledException)
        {
            return DiagnosticResult.Fail<AllocationSample>(
                $"collect_sample(kind=\"allocation\") could not complete because its EventPipe capture state became invalid for pid {processId}.",
                new DiagnosticError(
                    "CollectionFailed",
                    "The allocation EventPipe session or captured trace could not be completed.",
                    operation),
                InspectProcessHint(
                    processId,
                    "Check target availability and retry with a fresh allocation capture."));
        }

        return DiagnosticResult.Fail<AllocationSample>(
            $"collect_sample(kind=\"allocation\") failed unexpectedly while processing pid {processId}.",
            new DiagnosticError(
                "Internal",
                "An unexpected allocation capture failure occurred.",
                operation),
            InspectProcessHint(
                processId,
                "Inspect target capabilities and server logs before retrying."));
    }

    private static NextActionHint InspectProcessHint(int processId, string reason)
        => new(
            "inspect_process",
            reason,
            new Dictionary<string, object?>
            {
                ["view"] = "capabilities",
                ["processId"] = processId,
            });

    private static bool IsDiagnosticEndpointUnavailable(Exception exception)
        => FindInChain<ServerNotAvailableException>(exception) is not null
           || FindInChain<SocketException>(exception) is { SocketErrorCode: not SocketError.AccessDenied };

    private static bool HasDiagnosticEndpointPermissionEvidence(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException or SecurityException)
            {
                return true;
            }

            if (current is SocketException { SocketErrorCode: SocketError.AccessDenied })
            {
                return true;
            }

            if (current is Win32Exception { NativeErrorCode: 1 or 5 or 13 })
            {
                return true;
            }

            var message = current.Message;
            if (message.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                || message.Contains("access denied", StringComparison.OrdinalIgnoreCase)
                || message.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase)
                || message.Contains("EACCES", StringComparison.OrdinalIgnoreCase)
                || message.Contains("EPERM", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasTargetExitEvidence(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("process exited", StringComparison.OrdinalIgnoreCase)
                || message.Contains("target exited", StringComparison.OrdinalIgnoreCase)
                || message.Contains("process has exited", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static TException? FindInChain<TException>(Exception exception)
        where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException match)
            {
                return match;
            }
        }

        return null;
    }
}
