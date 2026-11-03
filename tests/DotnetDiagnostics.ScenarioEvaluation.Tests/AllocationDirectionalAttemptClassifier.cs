namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal sealed record AllocationDirectionalCleanupClassification(
    AllocationDirectionalAttemptEvidence Evidence,
    bool ReplacePassedObservation);

internal sealed record AllocationDirectionalExceptionClassification(
    Exception PrimaryException,
    Exception? CleanupException);

internal static class AllocationDirectionalAttemptClassifier
{
    public static AllocationDirectionalExceptionClassification SplitOwnedStartupFailure(
        AllocationDirectionalAttemptStage stage,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (stage == AllocationDirectionalAttemptStage.ProcessStart
            && exception is AggregateException aggregate
            && aggregate.InnerExceptions.Count == 2)
        {
            return new(
                aggregate.InnerExceptions[0],
                aggregate.InnerExceptions[1]);
        }

        return new(exception, null);
    }

    public static AllocationDirectionalFailureCategory ClassifyException(
        AllocationDirectionalAttemptStage stage,
        Exception exception,
        bool callerCancellationRequested)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException)
        {
            return callerCancellationRequested
                ? AllocationDirectionalFailureCategory.Cancellation
                : AllocationDirectionalFailureCategory.Timeout;
        }

        if (exception is TimeoutException
            || stage == AllocationDirectionalAttemptStage.ProcessReadiness
                && string.Equals(
                    exception.GetType().Name,
                    "SkipException",
                    StringComparison.Ordinal))
        {
            return AllocationDirectionalFailureCategory.Timeout;
        }

        return stage switch
        {
            AllocationDirectionalAttemptStage.ProcessStart =>
                AllocationDirectionalFailureCategory.ProcessStart,
            AllocationDirectionalAttemptStage.ProcessReadiness =>
                AllocationDirectionalFailureCategory.ProcessReadiness,
            AllocationDirectionalAttemptStage.Warmup =>
                AllocationDirectionalFailureCategory.Warmup,
            AllocationDirectionalAttemptStage.Workload =>
                AllocationDirectionalFailureCategory.Workload,
            AllocationDirectionalAttemptStage.McpTransport
                or AllocationDirectionalAttemptStage.McpResponse =>
                AllocationDirectionalFailureCategory.McpTransport,
            _ => AllocationDirectionalFailureCategory.UnexpectedHarnessException,
        };
    }

    public static bool IsAborted(AllocationDirectionalFailureCategory category)
        => category is AllocationDirectionalFailureCategory.Timeout
            or AllocationDirectionalFailureCategory.Cancellation;

    public static bool IsCollectDrainFailure(
        bool collectTaskCompleted,
        Exception exception)
        => exception is TimeoutException && !collectTaskCompleted;

    public static AllocationDirectionalCleanupClassification FinalizeCleanup(
        AllocationDirectionalAttemptEvidence evidence,
        bool passed,
        Exception? cleanupException)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (cleanupException is null)
        {
            return new(
                AllocationDirectionalProjection.WithCleanup(
                    evidence,
                    AllocationDirectionalCleanupState.Succeeded),
                ReplacePassedObservation: false);
        }

        if (!passed)
        {
            return new(
                AllocationDirectionalProjection.WithCleanup(
                    evidence,
                    AllocationDirectionalCleanupState.Failed),
                ReplacePassedObservation: false);
        }

        return new(
            AllocationDirectionalProjection.ExternalFailure(
                evidence.AttemptId,
                evidence.WorkloadIdentity,
                AllocationDirectionalAttemptStage.Cleanup,
                "cleanup.owned-resources",
                AllocationDirectionalFailureCategory.Cleanup,
                cleanupException,
                AllocationDirectionalCleanupState.Failed),
            ReplacePassedObservation: true);
    }
}
