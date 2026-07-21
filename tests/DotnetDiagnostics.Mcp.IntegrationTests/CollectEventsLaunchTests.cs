using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Launch;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Startup;
using DotnetDiagnostics.Mcp.Security;
using DotnetDiagnostics.Mcp.Tools;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

/// <summary>
/// Gate/validation coverage for <c>collect_events(kind="startup", launch=...)</c> (issue #665 Part A).
/// Mirrors <see cref="MethodParameterCaptureSecurityTests"/>'s style of calling the tool entry point
/// directly with stub collectors — the launch path's own collection logic (EventPipe cold-start
/// capture over a real spawned process) is covered end-to-end by
/// <see cref="CollectEventsLaunchLiveTests"/>.
/// </summary>
public sealed class CollectEventsLaunchSecurityTests
{
    private static readonly LaunchSpec ImpossibleLaunch = new("dotnet", new[] { "--version" });

    [Fact]
    public async Task Launch_RejectedWhenTransportIsNotStdio()
    {
        var result = await InvokeAsync(
            principalAccessor: TestPrincipalAccessors.Root,
            securityOptions: new SecurityOptions { AllowProcessLaunch = true },
            launch: ImpossibleLaunch);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("NotSupported");
    }

    [Fact]
    public async Task Launch_RejectedWhenServerPolicyDisabled()
    {
        var result = await InvokeAsync(
            principalAccessor: StdioRootPrincipalAccessor.Instance,
            securityOptions: new SecurityOptions(),
            launch: ImpossibleLaunch);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("ProcessLaunchDisabled");
    }

    [Fact]
    public async Task Launch_RejectedForNonStartupKind()
    {
        var result = await InvokeAsync(
            principalAccessor: StdioRootPrincipalAccessor.Instance,
            securityOptions: new SecurityOptions { AllowProcessLaunch = true },
            launch: ImpossibleLaunch,
            kind: "counters");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Detail.Should().Be("launch");
    }

    [Fact]
    public async Task Launch_RejectedWhenProcessIdAlsoSupplied()
    {
        var result = await InvokeAsync(
            principalAccessor: StdioRootPrincipalAccessor.Instance,
            securityOptions: new SecurityOptions { AllowProcessLaunch = true },
            launch: ImpossibleLaunch,
            processId: 4242);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Detail.Should().Be("launch");
    }

    [Fact]
    public async Task Launch_RejectedWhenConnectTimeoutOutOfRange()
    {
        var result = await InvokeAsync(
            principalAccessor: StdioRootPrincipalAccessor.Instance,
            securityOptions: new SecurityOptions { AllowProcessLaunch = true },
            launch: new LaunchSpec("dotnet", new[] { "--version" }, ConnectTimeoutSeconds: 1e308));

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Detail.Should().Be("launch.connectTimeoutSeconds");
    }

    private static Task<DiagnosticResult<CollectEventsEnvelope>> InvokeAsync(
        IPrincipalAccessor principalAccessor,
        SecurityOptions securityOptions,
        LaunchSpec launch,
        string kind = "startup",
        int? processId = null)
        => CollectEventsTool.CollectEvents(
            counterCollector: null!,
            exceptionCollector: null!,
            crashGuardCollector: null!,
            gcCollector: null!,
            gcDatasCollector: null!,
            activityCollector: null!,
            eventSourceCollector: null!,
            eventCatalogCollector: null!,
            logCollector: null!,
            jitCollector: null!,
            threadPoolCollector: null!,
            contentionCollector: null!,
            dbCollector: null!,
            kestrelCollector: null!,
            networkingCollector: null!,
            inFlightRequestCollector: null!,
            startupCollector: null!,
            processResourcesCollector: null!,
            gatedCaptureCollector: null!,
            cpuSampler: null!,
            threadSnapshotInspector: null!,
            dumpInspector: null!,
            processDumper: null!,
            resolver: null!,
            handles: new MemoryDiagnosticHandleStore(),
            allowlist: new EventSourceAllowlist(null),
            sensitiveGate: new SensitiveValueGate(new SecurityOptions()),
            principalAccessor: principalAccessor,
            securityOptions: securityOptions,
            loggerFactory: null,
            kind: kind,
            processId: processId,
            launch: launch,
            cancellationToken: CancellationToken.None);
}

/// <summary>
/// Live end-to-end coverage: spawns CoreClrSample suspended via the full
/// <c>collect_events(kind="startup", launch=...)</c> entry point (not the bare
/// <see cref="SuspendedColdStartLauncher"/> primitive) and proves pre-attach DI container build
/// (ServiceProviderBuilt) is captured — an event the post-attach path always misses.
/// </summary>
public sealed class CollectEventsLaunchLiveTests
{
    [Fact(Timeout = 60_000)]
    public async Task CollectEvents_Startup_Launch_CapturesPreAttach_DiServiceProviderBuilt()
    {
        var sampleDll = SampleLocator.LocateSampleDll("CoreClrSample")
            ?? throw SkipException.ForReason("CoreClrSample.dll not found. Build the sample before running this test.");

        var launch = new LaunchSpec("dotnet", new[] { sampleDll, "--urls", "http://127.0.0.1:0" }, ConnectTimeoutSeconds: 30);

        var result = await CollectEventsTool.CollectEvents(
            counterCollector: null!,
            exceptionCollector: null!,
            crashGuardCollector: null!,
            gcCollector: null!,
            gcDatasCollector: null!,
            activityCollector: null!,
            eventSourceCollector: null!,
            eventCatalogCollector: null!,
            logCollector: null!,
            jitCollector: null!,
            threadPoolCollector: null!,
            contentionCollector: null!,
            dbCollector: null!,
            kestrelCollector: null!,
            networkingCollector: null!,
            inFlightRequestCollector: null!,
            startupCollector: new EventPipeStartupCollector(),
            processResourcesCollector: null!,
            gatedCaptureCollector: null!,
            cpuSampler: null!,
            threadSnapshotInspector: null!,
            dumpInspector: null!,
            processDumper: null!,
            resolver: null!,
            handles: new MemoryDiagnosticHandleStore(),
            allowlist: new EventSourceAllowlist(null),
            sensitiveGate: new SensitiveValueGate(new SecurityOptions()),
            principalAccessor: StdioRootPrincipalAccessor.Instance,
            securityOptions: new SecurityOptions { AllowProcessLaunch = true },
            loggerFactory: null,
            kind: "startup",
            durationSeconds: 8,
            launch: launch,
            cancellationToken: CancellationToken.None);

        result.Error.Should().BeNull(result.Error?.Message);
        result.Data.Should().NotBeNull();
        result.Data!.Startup.Should().NotBeNull();
        result.Data.Startup!.TotalDiEvents.Should().BeGreaterThan(0, "cold-start arms EventPipe before DI is built");
        result.Data.Startup.DiServiceProviderBuiltCount.Should().BeGreaterThanOrEqualTo(1);
        result.Handle.Should().NotBeNullOrWhiteSpace();
    }
}
