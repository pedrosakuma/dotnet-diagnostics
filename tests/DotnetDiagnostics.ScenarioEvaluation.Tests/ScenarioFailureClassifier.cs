using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public static class ScenarioFailureClassifier
{
    public static ScenarioFailureKind Classify(Exception exception, ScenarioFailureKind stage)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            PlatformNotSupportedException => ScenarioFailureKind.Environment,
            UnauthorizedAccessException => ScenarioFailureKind.Environment,
            ServerNotAvailableException => ScenarioFailureKind.Environment,
            HttpRequestException => ScenarioFailureKind.Workload,
            TaskCanceledException when stage == ScenarioFailureKind.Workload => ScenarioFailureKind.Workload,
            DiagnosticsClientException => ScenarioFailureKind.Collection,
            InvalidDataException => ScenarioFailureKind.Evaluation,
            _ => stage,
        };
    }
}

public sealed class ScenarioRunException : Exception
{
    public ScenarioRunException(
        string message,
        ScenarioFailureKind failureKind,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
    }

    public ScenarioFailureKind FailureKind { get; }
}
