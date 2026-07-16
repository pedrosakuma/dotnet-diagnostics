using System.Runtime.Serialization;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.MethodParameters;
using DotnetDiagnostics.Core.NativeAlloc;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Symbols;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Security;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class MethodParameterCaptureSecurityTests
{
    [Fact]
    public async Task CollectSample_MethodParams_RequiresExplicitSensitiveScope()
    {
        var result = await InvokeCollectAsync(
            securityOptions: new SecurityOptions { AllowMethodParameterCapture = true },
            principalAccessor: TestPrincipalAccessors.Root,
            includeSensitiveValues: true);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("Forbidden");
    }

    [Fact]
    public async Task CollectSample_MethodParams_RequiresServerPolicy()
    {
        var result = await InvokeCollectAsync(
            securityOptions: new SecurityOptions(),
            principalAccessor: TestPrincipalAccessors.WithScopes("eventpipe", "sensitive-parameter-read"),
            includeSensitiveValues: true);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("MethodParameterCaptureDisabled");
    }

    [Fact]
    public async Task CollectSample_MethodParams_RequiresIncludeSensitiveValues()
    {
        var result = await InvokeCollectAsync(
            securityOptions: new SecurityOptions { AllowMethodParameterCapture = true },
            principalAccessor: TestPrincipalAccessors.WithScopes("eventpipe", "sensitive-parameter-read"),
            includeSensitiveValues: false);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Detail.Should().Be("includeSensitiveValues");
    }

    [Fact]
    public async Task CollectSample_MethodParams_RejectsInheritedKnobs()
    {
        var requestContext = CreateRequestContext(new CallToolRequestParams
        {
            Name = CollectSampleTool.ToolName,
            Arguments = new Dictionary<string, JsonElement>
            {
                ["kind"] = JsonSerializer.SerializeToElement("method-params"),
                ["topN"] = JsonSerializer.SerializeToElement(5),
            },
        });

        var result = await InvokeCollectAsync(
            securityOptions: new SecurityOptions { AllowMethodParameterCapture = true },
            principalAccessor: TestPrincipalAccessors.WithScopes("eventpipe", "sensitive-parameter-read"),
            includeSensitiveValues: true,
            requestContext: requestContext);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Detail.Should().Be("topN");
    }

    [Fact]
    public async Task CollectSample_MethodParams_Succeeds_WhenAllGatesPass()
    {
        var result = await InvokeCollectAsync(
            securityOptions: new SecurityOptions { AllowMethodParameterCapture = true },
            principalAccessor: TestPrincipalAccessors.WithScopes("eventpipe", "sensitive-parameter-read"),
            includeSensitiveValues: true);

        result.Error.Should().BeNull();
        result.Data.Should().NotBeNull();
        result.Data!.Kind.Should().Be("method-params");
        result.Data.MethodParams.Should().NotBeNull();
        result.Handle.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task QuerySnapshot_MethodParams_Events_RequiresExplicitSensitiveScope()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(4242, MethodParameterCaptureUseCases.HandleKind, Artifact(), TimeSpan.FromMinutes(10), origin: HandleOrigin.Live);

        var result = await QuerySnapshotTool.QuerySnapshot(
            store,
            new StubDumpInspector(),
            new SensitiveDataRedactor(new SecurityOptions()),
            new SensitiveValueGate(new SecurityOptions()),
            new SecurityOptions { AllowMethodParameterCapture = true },
            TestPrincipalAccessors.WithScopes("eventpipe"),
            new ClrMdNativeAddressResolver(),
            new StubFrameResolver(),
            handle.Id,
            view: "events",
            includeSensitiveValues: true,
            cancellationToken: CancellationToken.None);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("Forbidden");
    }

    [Fact]
    public async Task QuerySnapshot_MethodParams_Summary_RequiresExplicitSensitiveScope()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(4242, MethodParameterCaptureUseCases.HandleKind, Artifact(), TimeSpan.FromMinutes(10), origin: HandleOrigin.Live);

        var result = await QuerySnapshotTool.QuerySnapshot(
            store,
            new StubDumpInspector(),
            new SensitiveDataRedactor(new SecurityOptions()),
            new SensitiveValueGate(new SecurityOptions()),
            new SecurityOptions(),
            TestPrincipalAccessors.WithScopes("eventpipe"),
            new ClrMdNativeAddressResolver(),
            new StubFrameResolver(),
            handle.Id,
            view: "summary",
            cancellationToken: CancellationToken.None);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("Forbidden");
    }

    [Fact]
    public async Task QuerySnapshot_MethodParams_Summary_DoesNotRequireValuePolicyOrOptIn()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handle = store.Register(4242, MethodParameterCaptureUseCases.HandleKind, Artifact(), TimeSpan.FromMinutes(10), origin: HandleOrigin.Live);

        var result = await QuerySnapshotTool.QuerySnapshot(
            store,
            new StubDumpInspector(),
            new SensitiveDataRedactor(new SecurityOptions()),
            new SensitiveValueGate(new SecurityOptions()),
            new SecurityOptions(),
            TestPrincipalAccessors.WithScopes("eventpipe", "sensitive-parameter-read"),
            new ClrMdNativeAddressResolver(),
            new StubFrameResolver(),
            handle.Id,
            view: "summary",
            cancellationToken: CancellationToken.None);

        result.Error.Should().BeNull();
        result.Data.Should().BeOfType<MethodParameterCaptureQueryResult>();
    }

    private static Task<DiagnosticResult<CollectSampleEnvelope>> InvokeCollectAsync(
        SecurityOptions securityOptions,
        IPrincipalAccessor principalAccessor,
        bool includeSensitiveValues,
        RequestContext<CallToolRequestParams>? requestContext = null)
        => CollectSampleTool.CollectSample(
            cpuSampler: null!,
            offCpuSampler: null!,
            allocationSampler: null!,
            nativeAllocSampler: null!,
            methodParameterCollector: new StubMethodParameterCollector(),
            handles: new MemoryDiagnosticHandleStore(),
            resolver: new EchoResolver(),
            symbolServerAllowlist: new SymbolServerAllowlist(null),
            securityOptions: securityOptions,
            principalAccessor: principalAccessor,
            loggerFactory: null,
            kind: CollectSampleTool.KindMethodParams,
            processId: 4242,
            durationSeconds: 2,
            maxEvents: 10,
            previewCount: 5,
            includeSensitiveValues: includeSensitiveValues,
            methods: new[]
            {
                new MethodFilter("CoreClrSample.dll", "Program", "BurnCpu"),
            },
            requestContext: requestContext,
            cancellationToken: CancellationToken.None);

    private static MethodParameterCaptureArtifact Artifact() => new(
        ProcessId: 4242,
        CapturedAtUtc: DateTimeOffset.UtcNow,
        RequestedDuration: TimeSpan.FromSeconds(2),
        RuntimeFlavor: "CoreCLR",
        RuntimeVersion: "10.0.0",
        MethodFilters: new[] { new MethodFilter("CoreClrSample.dll", "Program", "BurnCpu") },
        ResolvedMethods: new[]
        {
            new ResolvedMethodIdentity("CoreClrSample.dll", Guid.NewGuid().ToString("D"), "Program", "BurnCpu", 0, 123, new[] { "System.Int32" }),
        },
        MaxEvents: 10,
        PreviewCount: 5,
        CaptureCount: 1,
        DroppedCount: 0,
        TruncatedValueCount: 0,
        RedactedValueCount: 0,
        ValuesTruncated: false,
        ValuesRedacted: false,
        StopReason: "duration_elapsed",
        Events: new[]
        {
            new MethodParameterInvocation(
                1,
                DateTimeOffset.UtcNow,
                new ResolvedMethodIdentity("CoreClrSample.dll", Guid.NewGuid().ToString("D"), "Program", "BurnCpu", 0, 123, new[] { "System.Int32" }),
                new[] { new CapturedParameterValue("milliseconds", "System.Int32", "123", false, false) }),
        });

    private static RequestContext<CallToolRequestParams> CreateRequestContext(CallToolRequestParams parameters)
    {
        var context = (RequestContext<CallToolRequestParams>)FormatterServices.GetUninitializedObject(typeof(RequestContext<CallToolRequestParams>));
        typeof(RequestContext<CallToolRequestParams>).GetProperty(nameof(RequestContext<CallToolRequestParams>.Params))!
            .SetValue(context, parameters);
        return context;
    }

    private sealed class StubMethodParameterCollector : IMethodParameterCaptureCollector
    {
        public Task<DiagnosticResult<MethodParameterCaptureArtifact>> CollectAsync(int processId, MethodParameterCaptureRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(DiagnosticResult.Ok(Artifact(), "stub capture"));
    }

    private sealed class EchoResolver : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(int? processId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessContextResolution(
                new ProcessContext(
                    processId ?? 4242,
                    DotnetDiagnostics.Core.Capabilities.RuntimeFlavor.CoreClr,
                    true,
                    true,
                    false,
                    ".NET 10.0.0"),
                null));
    }

    private sealed class StubDumpInspector : IDumpInspector
    {
        public Task<HeapSnapshotArtifact> InspectAsync(string dumpFilePath, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<HeapSnapshotArtifact> InspectLiveAsync(int processId, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<HeapObjectInspection> InspectObjectAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<HeapGcRootInspection> InspectGcRootAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<HeapObjectSizeInspection> InspectObjectSizeAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubFrameResolver : DotnetDiagnostics.Core.Threads.IFrameVariableResolver
    {
        public Task<DotnetDiagnostics.Core.Threads.FrameVariablesResult> ResolveAsync(DotnetDiagnostics.Core.Threads.ThreadSnapshotArtifact artifact, int managedThreadId, bool includeSensitiveValues, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
