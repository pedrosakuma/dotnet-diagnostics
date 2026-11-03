using System.Text.Json;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.UseCases;
using FluentAssertions;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnostics.Core.Tests;

public sealed class AllocationSampleFailureClassificationTests
{
    private const int ProcessId = 424242;

    [Fact]
    public async Task DiagnosticSocketPermissionEvidence_MapsToPermissionDeniedWithoutLeakingRawDiagnostics()
    {
        const string sensitive = "Permission denied opening /tmp/private/dotnet-diagnostic-424242; token=secret";
        var exception = Staged(
            "allocation.eventpipe.start",
            new ServerNotAvailableException(sensitive));

        var result = await CollectAsync(exception);

        result.Error!.Kind.Should().Be("PermissionDenied");
        result.Error.Detail.Should().Be("allocation.eventpipe.start");
        AssertBoundedSafeEnvelope(result, sensitive, "/tmp/private", "token=secret");
    }

    [Fact]
    public async Task NonPermissionInvalidOperation_DoesNotMapToPermissionDenied()
    {
        const string sensitive = "session state invalid at C:\\private\\capture.nettrace";
        var result = await CollectAsync(Staged(
            "allocation.eventpipe.session",
            new InvalidOperationException(sensitive)));

        result.Error!.Kind.Should().Be("CollectionFailed");
        result.Error.Detail.Should().Be("allocation.eventpipe.session");
        AssertBoundedSafeEnvelope(result, sensitive, "C:\\private");
    }

    [Theory]
    [InlineData("The diagnostics server is not available.")]
    [InlineData("DOTNET_EnableDiagnostics is disabled for this target.")]
    public async Task DiagnosticsUnavailableOrDisabled_MapsToEndpointUnavailable(string rawMessage)
    {
        var result = await CollectAsync(Staged(
            "allocation.eventpipe.start",
            new ServerNotAvailableException(rawMessage)));

        result.Error!.Kind.Should().Be("EndpointUnavailable");
        result.Error.Detail.Should().Be("allocation.eventpipe.start");
        AssertBoundedSafeEnvelope(result, rawMessage);
    }

    [Fact]
    public async Task TargetExitEvidence_MapsToProcessExited()
    {
        var result = await CollectAsync(new AllocationSampleOperationException(
            "allocation.eventpipe.session",
            targetExited: true,
            new InvalidOperationException("stream closed")));

        result.Error!.Kind.Should().Be("ProcessExited");
        result.Error.Detail.Should().Be("allocation.eventpipe.session");
    }

    [Fact]
    public async Task EventPipeInvalidState_MapsToCollectionFailed()
    {
        var result = await CollectAsync(Staged(
            "allocation.eventpipe.start",
            new InvalidOperationException("session already stopped")));

        result.Error!.Kind.Should().Be("CollectionFailed");
        result.Error.Detail.Should().Be("allocation.eventpipe.start");
    }

    [Fact]
    public async Task MalformedCapturedTrace_MapsToCollectionFailed()
    {
        var result = await CollectAsync(Staged(
            "allocation.trace.aggregate",
            new InvalidDataException("malformed trace payload at a private path")));

        result.Error!.Kind.Should().Be("CollectionFailed");
        result.Error.Detail.Should().Be("allocation.trace.aggregate");
        AssertBoundedSafeEnvelope(result, "malformed trace payload", "private path");
    }

    [Fact]
    public async Task UnsupportedEventPipeOperation_MapsToNotSupported()
    {
        var result = await CollectAsync(Staged(
            "allocation.eventpipe.start",
            new UnsupportedCommandException("unsupported command with target details")));

        result.Error!.Kind.Should().Be("NotSupported");
        result.Error.Detail.Should().Be("allocation.eventpipe.start");
        AssertBoundedSafeEnvelope(result, "target details");
    }

    [Fact]
    public async Task Timeout_MapsToCaptureTimeout()
    {
        var result = await CollectAsync(Staged(
            "allocation.eventpipe.start",
            new TimeoutException("diagnostic IPC deadline included a private endpoint")));

        result.Error!.Kind.Should().Be("CaptureTimeout");
        result.Error.Detail.Should().Be("allocation.eventpipe.start");
        AssertBoundedSafeEnvelope(result, "private endpoint");
    }

    [Fact]
    public async Task UnexpectedFailure_MapsToInternalWithoutLeakingMessageOrStackData()
    {
        const string sensitive = "unexpected parser failure at C:\\users\\private\\capture.nettrace";
        var result = await CollectAsync(Staged(
            "allocation.trace.aggregate",
            new UnexpectedAllocationException(sensitive)));

        result.Error!.Kind.Should().Be("Internal");
        result.Error.Detail.Should().Be("allocation.trace.aggregate");
        AssertBoundedSafeEnvelope(result, sensitive, "C:\\users\\private");
    }

    [Fact]
    public async Task PermissionWordingOutsideDiagnosticSocketStart_DoesNotMapToPermissionDenied()
    {
        var result = await CollectAsync(Staged(
            "allocation.trace.aggregate",
            new InvalidOperationException("permission denied while opening a local trace path")));

        result.Error!.Kind.Should().Be("CollectionFailed");
    }

    [Fact]
    public async Task ClientCancellation_PreservesCancellationEnvelope()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var sampler = new CancellingSampler();

        var result = await SamplerUseCases.CollectAllocationSample(
            sampler,
            new MemoryDiagnosticHandleStore(),
            new StubResolver(),
            ProcessId,
            cancellationToken: cancellation.Token);

        result.Cancelled.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Data.Should().BeNull();
        result.ResolvedProcess.Should().NotBeNull();
    }

    private static AllocationSampleOperationException Staged(string operation, Exception cause)
        => new(operation, targetExited: false, cause);

    private static Task<DiagnosticResult<AllocationSample>> CollectAsync(Exception exception)
        => SamplerUseCases.CollectAllocationSample(
            new ThrowingSampler(exception),
            new MemoryDiagnosticHandleStore(),
            new StubResolver(),
            ProcessId,
            cancellationToken: CancellationToken.None);

    private static void AssertBoundedSafeEnvelope(
        DiagnosticResult<AllocationSample> result,
        params string[] forbidden)
    {
        var json = JsonSerializer.Serialize(result);
        json.Length.Should().BeLessThan(2048);
        foreach (var value in forbidden)
        {
            json.Should().NotContain(value);
        }

        json.Should().NotContain("StackTrace");
        json.Should().NotContain(nameof(ServerNotAvailableException));
    }

    private sealed class ThrowingSampler(Exception exception) : IAllocationSampler
    {
        public Task<AllocationSampleResult> SampleAsync(
            int processId,
            TimeSpan duration,
            int topN = 25,
            CancellationToken cancellationToken = default)
            => Task.FromException<AllocationSampleResult>(exception);
    }

    private sealed class CancellingSampler : IAllocationSampler
    {
        public Task<AllocationSampleResult> SampleAsync(
            int processId,
            TimeSpan duration,
            int topN = 25,
            CancellationToken cancellationToken = default)
            => Task.FromCanceled<AllocationSampleResult>(cancellationToken);
    }

    private sealed class UnexpectedAllocationException(string message) : Exception(message);

    private sealed class StubResolver : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(
            int? requestedProcessId,
            CancellationToken cancellationToken)
            => Task.FromResult(new ProcessContextResolution(
                new ProcessContext(
                    requestedProcessId ?? ProcessId,
                    RuntimeFlavor.CoreClr,
                    CanSampleCpu: true,
                    CanCollectGcDump: true,
                    AutoResolved: false),
                Error: null));
    }
}
