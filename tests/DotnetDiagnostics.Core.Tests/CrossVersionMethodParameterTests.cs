using System.Collections.Immutable;
using System.Diagnostics;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.MethodParameters;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Requests;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Symbols;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Security;
using DotnetDiagnostics.Mcp.Tools;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Real allowlisted profiler capture of <c>MethodParameterFixture.Capture</c> in the
/// multi-targeted <c>samples/MultiVersionSample</c> on .NET 8, 9 and 10 (Linux x64 only; Windows
/// x64 keeps its existing net10 evidence in <c>LiveCoreClrProcessTests</c>). Each case asserts the
/// actual runtime major, known synthetic values, event/value caps, a drilldown query, and
/// cancellation / target-exit cleanup. Outside GitHub Actions a missing runtime or build skips;
/// on GitHub Actions Linux x64 it fails so a provisioned case can never silently skip.
/// </summary>
[Collection("LiveProcess")]
public class CrossVersionMethodParameterTests
{
    [Theory(Timeout = 180_000)]
    [InlineData("net8.0")]
    [InlineData("net9.0")]
    [InlineData("net10.0")]
    public async Task Capture_KnownValuesAndBounds_EndToEnd(string targetFramework)
    {
        await using var sample = await StartAsync(targetFramework);
        var major = Major(targetFramework);
        var handles = new MemoryDiagnosticHandleStore();
        var (collector, sharedPathCreated, captureStarted, _) = CreateCollector();

        var captureTask = MethodParameterCaptureUseCases.CollectAsync(
            collector,
            handles,
            new FixedProcessContextResolver(sample.ProcessId, major),
            sample.ProcessId,
            durationSeconds: 20,
            maxEvents: 2,
            previewCount: 2,
            methods: [Filter()],
            cancellationToken: CancellationToken.None);

        var sharedPath = await WaitForSignalAsync(sharedPathCreated.Task, captureTask, "profiler shared-path creation");
        await WaitForSignalAsync(captureStarted.Task, captureTask, "EventPipe Capturing/Start");
        var result = await captureTask.WaitAsync(TimeSpan.FromSeconds(60));

        var context = $"{targetFramework} ({sample.RuntimeDescription})";
        sample.RuntimeDescription.Should().Contain($".NET {major}.", $"the target must really run the {targetFramework} runtime");
        result.Error.Should().BeNull(context);
        result.Data.Should().NotBeNull(context);
        result.Handle.Should().NotBeNullOrWhiteSpace(context);
        result.Data!.CaptureCount.Should().Be(2, context);
        result.Data.StopReason.Should().Be("max_events_reached", context);
        result.Data.ValuesTruncated.Should().BeTrue(context);
        var method = result.Data.Events.Select(e => e.Method).Distinct().Should().ContainSingle().Which;
        method.ModuleName.Should().Be("MultiVersionSample.dll");
        method.TypeName.Should().Be("MethodParameterFixture");
        method.MethodName.Should().Be("Capture");
        method.Signature.Should().Equal("System.Int32", "System.String", "System.String");
        var parameters = result.Data.Events.SelectMany(e => e.Parameters).ToList();
        parameters.Where(p => p.Name == "sequence").Should()
            .OnlyContain(p => p.TypeName == "System.Int32" && (p.Value == "123" || p.Value == "124" || p.Value == "125"), context);
        parameters.Where(p => p.Name == "label").Should()
            .OnlyContain(p => p.Value == "known-label", context);
        parameters.Where(p => p.Name == "payload").Should().OnlyContain(p =>
            p.Value.StartsWith("known-prefix-", StringComparison.Ordinal)
            && p.Value.Length == 256
            && p.Truncated
            && p.Notes.Contains("value-cap", StringComparer.Ordinal)
            && p.Notes.Contains("preview-cap", StringComparer.Ordinal), context);
        Directory.Exists(sharedPath).Should().BeFalse("the control socket and profiler shared directory must be cleaned after capture");

        var securityOptions = new SecurityOptions { AllowMethodParameterCapture = true };
        var drilled = await QuerySnapshotTool.QuerySnapshot(
            handles,
            new NoopDumpInspector(),
            new SensitiveDataRedactor(new SecurityOptions()),
            new SensitiveValueGate(new SecurityOptions()),
            securityOptions,
            new ScopedPrincipalAccessor("eventpipe", "sensitive-parameter-read"),
            new ClrMdNativeAddressResolver(),
            new ThrowingFrameVariableResolver(),
            result.Handle!,
            view: "events",
            includeSensitiveValues: true,
            cancellationToken: CancellationToken.None);

        drilled.Error.Should().BeNull(context);
        var query = drilled.Data.Should().BeOfType<MethodParameterCaptureQueryResult>().Subject;
        query.Events!.Events.SelectMany(e => e.Parameters).Where(p => p.Name == "payload").Should().OnlyContain(p =>
            p.Value.StartsWith("known-prefix-", StringComparison.Ordinal)
            && System.Text.Encoding.UTF8.GetByteCount(p.Value) == 4_096
            && p.Truncated, context);
    }

    [Theory(Timeout = 180_000)]
    [InlineData("net8.0")]
    [InlineData("net9.0")]
    [InlineData("net10.0")]
    public async Task Capture_Cancellation_StopsCaptureAndCleansSharedPath(string targetFramework)
    {
        await using var sample = await StartAsync(targetFramework);
        var (collector, sharedPathCreated, captureStarted, captureStopped) = CreateCollector();
        using var cancellation = new CancellationTokenSource();
        var captureTask = collector.CollectAsync(
            sample.ProcessId,
            CreateRequest(sample.ProcessId, Major(targetFramework), TimeSpan.FromSeconds(60), maxEvents: 1_000),
            cancellation.Token);

        var sharedPath = await WaitForSignalAsync(sharedPathCreated.Task, captureTask, "profiler shared-path creation");
        await WaitForSignalAsync(captureStarted.Task, captureTask, "EventPipe Capturing/Start");
        cancellation.Cancel();
        var result = await captureTask.WaitAsync(TimeSpan.FromSeconds(60));

        result.Error.Should().BeNull();
        result.Cancelled.Should().BeTrue();
        result.Data!.StopReason.Should().Be("cancelled");
        captureStopped.Task.IsCompletedSuccessfully.Should().BeTrue("the profiler must acknowledge the stop command before teardown");
        Directory.Exists(sharedPath).Should().BeFalse("cancellation must clean the control socket and profiler shared directory");
    }

    [Theory(Timeout = 180_000)]
    [InlineData("net8.0")]
    [InlineData("net9.0")]
    [InlineData("net10.0")]
    public async Task Capture_TargetExit_ReturnsStructuredFailureAndCleansSharedPath(string targetFramework)
    {
        await using var sample = await StartAsync(targetFramework);
        var (collector, sharedPathCreated, captureStarted, _) = CreateCollector();
        var captureTask = collector.CollectAsync(
            sample.ProcessId,
            CreateRequest(sample.ProcessId, Major(targetFramework), TimeSpan.FromSeconds(60), maxEvents: 1_000),
            CancellationToken.None);

        var sharedPath = await WaitForSignalAsync(sharedPathCreated.Task, captureTask, "profiler shared-path creation");
        await WaitForSignalAsync(captureStarted.Task, captureTask, "EventPipe Capturing/Start");
        using (var target = Process.GetProcessById(sample.ProcessId))
        {
            target.Kill(entireProcessTree: true);
            await target.WaitForExitAsync();
        }

        var result = await captureTask.WaitAsync(TimeSpan.FromSeconds(60));

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("Internal");
        result.Error.Message.Should().Contain($"Target process {sample.ProcessId} exited while waiting for parameter capture stop");
        Directory.Exists(sharedPath).Should().BeFalse("target exit must clean the control socket and profiler shared directory");
    }

    private static int Major(string targetFramework)
        => int.Parse(targetFramework.AsSpan(3, targetFramework.IndexOf('.') - 3), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<MultiVersionSampleProcess> StartAsync(string targetFramework)
    {
        var supported = OperatingSystem.IsLinux()
            && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64;
        if (!supported)
        {
            throw SkipException.ForReason("cross-version method-parameter coverage is scoped to Linux x64.");
        }

        var provisioned = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        var major = Major(targetFramework);
        if (provisioned && !InstalledRuntimes.HasMajorVersion(major))
        {
            throw new Xunit.Sdk.XunitException($"Microsoft.NETCore.App {major}.x must be provisioned on GitHub Actions for {targetFramework}.");
        }

        if (provisioned && SampleLocator.LocateMultiVersionSampleDll(targetFramework) is null)
        {
            throw new Xunit.Sdk.XunitException($"MultiVersionSample ({targetFramework}) build output is missing on GitHub Actions.");
        }

        return await MultiVersionSampleProcess.StartAsync(targetFramework, methodParams: true);
    }

    private static MethodFilter Filter()
        => new("MultiVersionSample.dll", "MethodParameterFixture", "Capture")
        {
            Signature = ["System.Int32", "System.String", "System.String"],
        };

    private static MethodParameterCaptureRequest CreateRequest(int processId, int major, TimeSpan duration, int maxEvents)
        => new(
            [Filter()],
            duration,
            maxEvents,
            PreviewCount: 2,
            RuntimeVersion: $"{major}.0.0",
            new ProcessContext(processId, RuntimeFlavor.CoreClr, true, true, false, $"{major}.0.0", "explicit"));

    private static (
        MethodParameterCaptureCollector Collector,
        TaskCompletionSource<string> SharedPathCreated,
        TaskCompletionSource<bool> CaptureStarted,
        TaskCompletionSource<bool> CaptureStopped) CreateCollector()
    {
        var sharedPathCreated = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureStopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new MethodParameterCaptureLifecycleHooks(
            path => sharedPathCreated.TrySetResult(path),
            () => captureStarted.TrySetResult(true),
            () => captureStopped.TrySetResult(true));
        var collector = new MethodParameterCaptureCollector(
            new MvidReader(),
            new SensitiveDataRedactor(new SecurityOptions()),
            logger: null,
            lifecycleHooks: hooks);
        return (collector, sharedPathCreated, captureStarted, captureStopped);
    }

    private static async Task<TSignal> WaitForSignalAsync<TSignal, TData>(
        Task<TSignal> signal,
        Task<DiagnosticResult<TData>> captureTask,
        string stage)
    {
        Task completed;
        try
        {
            completed = await Task.WhenAny(signal, captureTask).WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException($"Timed out waiting for {stage}; the capture task was still running.");
        }

        if (completed == signal)
        {
            return await signal;
        }

        var result = await captureTask;
        throw new Xunit.Sdk.XunitException(
            $"Capture ended before {stage}: error={result.Error?.Kind ?? "<none>"}; message={result.Error?.Message ?? result.Summary}");
    }

    private sealed class FixedProcessContextResolver(int processId, int major) : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessContextResolution(
                new ProcessContext(requestedProcessId ?? processId, RuntimeFlavor.CoreClr, true, true, false, $"{major}.0.0", "explicit"),
                null));
    }

    private sealed class NoopDumpInspector : IDumpInspector
    {
        public Task<HeapSnapshotArtifact> InspectAsync(string dumpFilePath, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapSnapshotArtifact> InspectLiveAsync(int processId, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapObjectInspection> InspectObjectAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapGcRootInspection> InspectGcRootAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<HeapObjectSizeInspection> InspectObjectSizeAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ThrowingFrameVariableResolver : IFrameVariableResolver
    {
        public Task<FrameVariablesResult> ResolveAsync(ThreadSnapshotArtifact artifact, int managedThreadId, bool includeSensitiveValues, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ScopedPrincipalAccessor(params string[] scopes) : IPrincipalAccessor
    {
        private readonly BearerPrincipal _principal = new("test-principal", ImmutableHashSet.Create(scopes));

        public BearerPrincipal? Current => _principal;
    }
}
