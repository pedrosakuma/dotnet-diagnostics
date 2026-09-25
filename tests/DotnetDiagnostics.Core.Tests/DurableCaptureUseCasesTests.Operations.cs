using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.Tests;

public sealed partial class DurableCaptureUseCasesTests
{
    [Fact]
    public async Task HeterogeneousOperationProjectsTypedPayloadAndAppliesCaptureWithoutReflection()
    {
        var diagnostic = DiagnosticResult.Ok(Snapshot, "original", new NextActionHint("query_snapshot", "inspect"));
        var original = Host(diagnostic);
        var result = await Service().CaptureOperationAsync("capture", "counters", Owner, async _ =>
        {
            Assert.NotNull(CaptureRecordingContext.Current);
            await Task.Yield();
            return original;
        }, host => host.Outcome);
        Assert.True(result.HasResult);
        Assert.Same(original, result.Result);
        Assert.Same(diagnostic.Data, result.Outcome.Data);
        Assert.Equal(CaptureState.Sealed, result.Capture!.State);
        var applied = Assert.IsType<DiagnosticResult<CounterSnapshot>>(result.Result!.Apply(result.Outcome));
        Assert.Equal(diagnostic, applied with { Capture = null });
        Assert.Same(result.Capture, applied.Capture);
        Assert.Null(diagnostic.Capture);
    }

    [Fact]
    public async Task HeterogeneousStructuredFailureCannotBeSealedAsWrapperSuccess()
    {
        var diagnostic = DiagnosticResult.Fail<CounterSnapshot>("failed", new("OriginalFailure", "original"))
            with { Data = Snapshot, Cancelled = true };
        var result = await Service().CaptureOperationAsync("capture", "counters", Owner,
            _ => Task.FromResult(Host(diagnostic)), host => host.Outcome);
        Assert.True(result.HasResult);
        Assert.True(result.Outcome.Cancelled);
        Assert.Same(diagnostic.Error, result.Outcome.Error);
        Assert.Equal(CaptureState.Interrupted, result.Capture!.State);
        var applied = Assert.IsType<DiagnosticResult<CounterSnapshot>>(result.Result!.Apply(result.Outcome));
        Assert.Equal(diagnostic, applied with { Capture = null });
    }

    [Fact]
    public async Task HeterogeneousPersistenceFailureRetainsOriginalAndExplicitOutcomeFailure()
    {
        var diagnostic = DiagnosticResult.Ok(Snapshot, "done");
        var result = await Service(new() { MaxSnapshotBytes = 32 }).CaptureOperationAsync(
            "capture", "counters", Owner, _ => Task.FromResult(Host(diagnostic)), host => host.Outcome);
        Assert.True(result.HasResult);
        Assert.Equal("CapturePersistenceFailed", result.Outcome.Error!.Kind);
        var applied = Assert.IsType<DiagnosticResult<CounterSnapshot>>(result.Result!.Apply(result.Outcome));
        Assert.Same(diagnostic.Data, applied.Data);
        Assert.Equal(diagnostic.Summary, applied.Summary);
        Assert.True(applied.IsError);
        Assert.Equal(CaptureState.Interrupted, applied.Capture!.State);
    }

    [Fact]
    public async Task HeterogeneousThrownOperationDistinguishesMissingHostResultFromDefaultValue()
    {
        var result = await Service().CaptureOperationAsync<HostEnvelope>(
            "capture", "counters", Owner, _ => throw new OperationCanceledException(), host => host.Outcome);
        Assert.False(result.HasResult);
        Assert.Null(result.Result);
        Assert.True(result.Outcome.Cancelled);
        Assert.Equal(CaptureState.Interrupted, result.Capture!.State);
        Assert.Null(CaptureRecordingContext.Current);
    }

    private static HostEnvelope Host<T>(DiagnosticResult<T> result)
        => new(DurableCaptureEnvelope.Box(result), outcome => DurableCaptureEnvelope.Apply(result, outcome));

    private sealed record HostEnvelope(
        DiagnosticResult<object?> Outcome, Func<DiagnosticResult<object?>, object> Apply);
}
