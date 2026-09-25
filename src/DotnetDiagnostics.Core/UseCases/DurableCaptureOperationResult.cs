using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>An arbitrary host result plus its authoritative durable outcome, without reflection.</summary>
public sealed record DurableCaptureOperationResult<T>(
    T? Result, bool HasResult, DiagnosticResult<object?> Outcome)
{
    public CaptureInfo? Capture => Outcome.Capture;
}

/// <summary>Typed host adapters for heterogeneous dispatchers; no runtime type-name inspection.</summary>
public static class DurableCaptureEnvelope
{
    public static DiagnosticResult<object?> Box<T>(DiagnosticResult<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(result.Summary, result.Hints, result.Error)
        {
            Data = result.Data,
            Signals = result.Signals,
            Handle = result.Handle,
            HandleExpiresAt = result.HandleExpiresAt,
            ResolvedProcess = result.ResolvedProcess,
            Cancelled = result.Cancelled,
            Capture = result.Capture,
        };
    }

    /// <summary>Applies only capture-side status; preserves the original concrete payload and metadata.</summary>
    public static DiagnosticResult<T> Apply<T>(DiagnosticResult<T> original, DiagnosticResult<object?> outcome)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(outcome);
        return original with
        {
            Capture = outcome.Capture,
            Error = original.Error ?? outcome.Error,
            Cancelled = original.Cancelled || outcome.Cancelled,
            Hints = outcome.Hints,
        };
    }
}
