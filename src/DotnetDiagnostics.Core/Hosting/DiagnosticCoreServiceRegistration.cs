using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Bytes;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Kestrel;
using DotnetDiagnostics.Core.Requests;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.MethodParameters;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Startup;
using DotnetDiagnostics.Core.Symbols;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.Drilldown;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Core.Hosting;

/// <summary>
/// Host-neutral registration of the entire diagnostic engine — every Core sampler, collector,
/// security gate, and store the tool layer (or any future front-end, e.g. the <c>diag</c> CLI in
/// issue #283) depends on. Deliberately references <b>Core only</b>: it has no knowledge of MCP,
/// HTTP, or any specific host. The MCP server composes this with its own host-specific
/// registrations (the legacy-flag deprecation singleton, the MCP task store, and the handle
/// eviction <c>IHostedService</c>) in <c>DiagnosticServiceRegistration.AddDiagnosticCoreServices</c>.
/// </summary>
public static class DiagnosticCoreServiceRegistration
{
    /// <summary>
    /// Registers every Core collector / sampler / planner / store behind the diagnostic tools.
    /// Idempotent per <see cref="IServiceCollection"/>; safe to call from any host builder.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="securityOptions">
    /// Bound B4 security gates (issue #165). The caller owns binding this from configuration so
    /// Core stays free of any <c>Microsoft.Extensions.Configuration</c> dependency.
    /// </param>
    /// <param name="configuredSymbolPath">Optional symbol search path forwarded to <see cref="SymbolPathBuilder"/>.</param>
    /// <param name="handleStoreOptions">Validated bounded handle-store settings supplied by the host.</param>
    /// <param name="captureStoreOptions">Optional host-configured durable capture bounds; registration creates no capture files.</param>
    public static IServiceCollection AddDiagnosticCoreServices(
        this IServiceCollection services,
        SecurityOptions securityOptions,
        string? configuredSymbolPath = null,
        DiagnosticHandleStoreOptions? handleStoreOptions = null,
        Captures.CaptureStoreOptions? captureStoreOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(securityOptions);
        handleStoreOptions ??= new DiagnosticHandleStoreOptions();
        handleStoreOptions.Validate();
        captureStoreOptions ??= new Captures.CaptureStoreOptions();
        captureStoreOptions.Validate();
        services.AddSingleton(captureStoreOptions);
        services.AddSingleton<Captures.SqliteCaptureStore>();
        services.AddSingleton<UseCases.DurableCaptureUseCases>();

        // B4 security gates (issue #165). The caller binds SecurityOptions from the
        // `Diagnostics` configuration section; B5 (issue #166) will retrofit these into the
        // per-tool scope system.
        services.AddSingleton(securityOptions);
        services.AddSingleton<SensitiveDataRedactor>(_ => new SensitiveDataRedactor(securityOptions));
        services.AddSingleton<SensitiveValueGate>(_ => new SensitiveValueGate(securityOptions));
        services.AddSingleton<EventSourceAllowlist>(_ => new EventSourceAllowlist(securityOptions));
        services.AddSingleton<SymbolServerAllowlist>(_ => new SymbolServerAllowlist(securityOptions));

        services.AddSingleton(new SymbolPathBuilder(configuredSymbolPath));
        services.AddSingleton<DotnetDiagnostics.Core.Artifacts.IArtifactRootProvider, DotnetDiagnostics.Core.Artifacts.EnvironmentArtifactRootProvider>();
        services.AddSingleton<DotnetDiagnostics.Core.Artifacts.IArtifactLifecycle, DotnetDiagnostics.Core.Artifacts.FileSystemArtifactLifecycle>();
        services.AddHostedService<DotnetDiagnostics.Core.Artifacts.ArtifactReaperBackgroundService>();
        services.AddSingleton<IProcessDiscovery, LocalProcessDiscovery>();
        services.AddSingleton<DotnetDiagnostics.Core.Container.IContainerSignalsCollector, DotnetDiagnostics.Core.Container.CgroupV2SignalsCollector>();
        services.AddSingleton<ICapabilityDetector, CapabilityDetector>();
        services.AddSingleton<DotnetDiagnostics.Core.Preflight.IPreflightInspector, DotnetDiagnostics.Core.Preflight.PreflightInspector>();
        services.AddSingleton<ISessionTargetBindingStore, MemorySessionTargetBindingStore>();
        services.AddSingleton<IProcessContextResolver, ProcessContextResolver>();
        services.AddSingleton<EventPipeCounterCollector>();
        services.AddSingleton<ICounterCollector>(provider => provider.GetRequiredService<EventPipeCounterCollector>());
        services.AddSingleton<ICounterSessionFactory>(provider => provider.GetRequiredService<EventPipeCounterCollector>());
        services.AddSingleton(_ => new MvidReader(capacity: 128));
        services.AddSingleton<FileChunkReader>();
        services.AddSingleton<ClrMdMethodInstantiationEnricher>();
        services.AddSingleton<EventPipeCpuSampler>();
        services.AddSingleton<EventPipeAllocationSampler>();
        services.AddSingleton<PerfNativeAotCpuSampler>();
        services.AddSingleton<EtwNativeAotCpuSampler>();
        services.AddSingleton<ICpuSampler, RoutingCpuSampler>();
        services.AddSingleton<DotnetDiagnostics.Core.OffCpu.PerfSchedOffCpuSampler>();
        services.AddSingleton<DotnetDiagnostics.Core.OffCpu.EtwOffCpuSampler>();
        services.AddSingleton<DotnetDiagnostics.Core.OffCpu.IOffCpuSampler, DotnetDiagnostics.Core.OffCpu.RoutingOffCpuSampler>();
        services.AddSingleton<DotnetDiagnostics.Core.NativeAlloc.PerfNativeAllocSampler>();
        services.AddSingleton<DotnetDiagnostics.Core.NativeAlloc.EtwNativeAllocSampler>();
        services.AddSingleton<DotnetDiagnostics.Core.NativeAlloc.INativeAllocSampler, DotnetDiagnostics.Core.NativeAlloc.RoutingNativeAllocSampler>();
        services.AddSingleton<DotnetDiagnostics.Core.CpuEfficiency.PerfStatCpuEfficiencySampler>();
        services.AddSingleton<DotnetDiagnostics.Core.CpuEfficiency.EtwPmcCpuEfficiencySampler>();
        services.AddSingleton<DotnetDiagnostics.Core.CpuEfficiency.ICpuEfficiencySampler, DotnetDiagnostics.Core.CpuEfficiency.RoutingCpuEfficiencySampler>();
        services.AddSingleton<DotnetDiagnostics.Core.NativeLockContention.PerfNativeLockContentionSampler>();
        services.AddSingleton<DotnetDiagnostics.Core.NativeLockContention.WindowsNativeLockContentionSampler>();
        services.AddSingleton<DotnetDiagnostics.Core.NativeLockContention.INativeLockContentionSampler, DotnetDiagnostics.Core.NativeLockContention.RoutingNativeLockContentionSampler>();
        services.AddSingleton<EventPipeExceptionCollector>();
        services.AddSingleton<IExceptionCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeExceptionCollector>());
        services.AddSingleton<IStreamingExceptionCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeExceptionCollector>());
        services.AddSingleton<EventPipeGcCollector>();
        services.AddSingleton<IGcCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeGcCollector>());
        services.AddSingleton<IStreamingGcCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeGcCollector>());
        services.AddSingleton<DotnetDiagnostics.Core.Gc.IGcSessionFactory>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeGcCollector>());
        services.AddSingleton<IGcDatasCollector, EventPipeGcDatasCollector>();
        services.AddSingleton<EventPipeEventSourceCollector>();
        services.AddSingleton<IEventSourceCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeEventSourceCollector>());
        services.AddSingleton<IStreamingEventSourceCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeEventSourceCollector>());
        services.AddSingleton<EventPipeEventCatalogCollector>();
        services.AddSingleton<IEventCatalogCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeEventCatalogCollector>());
        services.AddSingleton<IStreamingEventCatalogCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeEventCatalogCollector>());
        services.AddSingleton<EventPipeActivityCollector>();
        services.AddSingleton<IActivityCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeActivityCollector>());
        services.AddSingleton<IStreamingActivityCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeActivityCollector>());
        services.AddSingleton<EventPipeLogCollector>();
        services.AddSingleton<ILogCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeLogCollector>());
        services.AddSingleton<IStreamingLogCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeLogCollector>());
        services.AddSingleton<EventPipeCrashGuardCollector>();
        services.AddSingleton<ICrashGuardCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeCrashGuardCollector>());
        services.AddSingleton<IStreamingCrashGuardCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeCrashGuardCollector>());
        services.AddSingleton<EventPipeJitCollector>();
        services.AddSingleton<IJitCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeJitCollector>());
        services.AddSingleton<IStreamingJitCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeJitCollector>());
        services.AddSingleton<EventPipeThreadPoolCollector>();
        services.AddSingleton<IThreadPoolCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeThreadPoolCollector>());
        services.AddSingleton<IStreamingThreadPoolCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeThreadPoolCollector>());
        services.AddSingleton<EventPipeContentionCollector>();
        services.AddSingleton<IContentionCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeContentionCollector>());
        services.AddSingleton<IStreamingContentionCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeContentionCollector>());
        services.AddSingleton<EventPipeDbCollector>();
        services.AddSingleton<IDbCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeDbCollector>());
        services.AddSingleton<IStreamingDbCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeDbCollector>());
        services.AddSingleton<EventPipeKestrelCollector>();
        services.AddSingleton<IKestrelCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeKestrelCollector>());
        services.AddSingleton<IStreamingKestrelCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeKestrelCollector>());
        services.AddSingleton<EventPipeInFlightRequestCollector>();
        services.AddSingleton<IInFlightRequestCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeInFlightRequestCollector>());
        services.AddSingleton<IStreamingInFlightRequestCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeInFlightRequestCollector>());
        services.AddSingleton<EventPipeNetworkingCollector>();
        services.AddSingleton<INetworkingCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeNetworkingCollector>());
        services.AddSingleton<IStreamingNetworkingCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeNetworkingCollector>());
        services.AddSingleton<EventPipeStartupCollector>();
        services.AddSingleton<IStartupCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeStartupCollector>());
        services.AddSingleton<IStreamingStartupCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<EventPipeStartupCollector>());
        services.AddSingleton<IMethodParameterCaptureCollector, MethodParameterCaptureCollector>();
        services.AddSingleton<DotnetDiagnostics.Core.GatedCapture.IThresholdGatedCaptureCollector, DotnetDiagnostics.Core.GatedCapture.ThresholdGatedCaptureCollector>();
        services.AddSingleton<IProcessDumper, DiagnosticsClientDumper>();
        services.AddSingleton<IModuleByteSource, ClrMdModuleByteSource>();
        services.AddSingleton<IDumpByteSource, FileSystemDumpByteSource>();
        services.AddSingleton<IDumpInspector, ClrMdDumpInspector>();
        services.AddSingleton<IGcDumpHeapSnapshotCollector, GcDumpHeapSnapshotCollector>();
        services.AddSingleton<DotnetDiagnostics.Core.Threads.ClrMdThreadSnapshotInspector>();
        services.AddSingleton<DotnetDiagnostics.Core.Threads.LinuxNativeThreadSnapshotInspector>();
        services.AddSingleton<DotnetDiagnostics.Core.Threads.PerfReplayThreadSnapshotInspector>();
        services.AddSingleton<DotnetDiagnostics.Core.Threads.EtwNativeThreadSnapshotInspector>();
        services.AddSingleton<DotnetDiagnostics.Core.Threads.IThreadSnapshotBackend, DotnetDiagnostics.Core.Threads.ClrMdThreadSnapshotBackend>();
        services.AddSingleton<DotnetDiagnostics.Core.Threads.IThreadSnapshotBackend, DotnetDiagnostics.Core.Threads.LinuxNativeThreadSnapshotBackend>();
        services.AddSingleton<DotnetDiagnostics.Core.Threads.IThreadSnapshotBackend, DotnetDiagnostics.Core.Threads.EtwNativeThreadSnapshotBackend>();
        services.AddSingleton<DotnetDiagnostics.Core.Threads.IThreadSnapshotBackend, DotnetDiagnostics.Core.Threads.PerfReplayThreadSnapshotBackend>();
        services.AddSingleton<DotnetDiagnostics.Core.Threads.IThreadSnapshotInspector, DotnetDiagnostics.Core.Threads.RoutingThreadSnapshotInspector>();
        services.AddSingleton<DotnetDiagnostics.Core.Symbols.INativeAddressResolver, DotnetDiagnostics.Core.Symbols.ClrMdNativeAddressResolver>();
        services.AddSingleton<DotnetDiagnostics.Core.Threads.IFrameVariableResolver, DotnetDiagnostics.Core.Threads.ClrMdFrameVariableResolver>();
        services.AddSingleton<DotnetDiagnostics.Core.JitCapture.IJitMethodCapturer, DotnetDiagnostics.Core.JitCapture.ClrMdJitMethodCapturer>();
        services.AddSingleton<DotnetDiagnostics.Core.Investigation.IInvestigationPlanner>(_ =>
            new DotnetDiagnostics.Core.Investigation.InvestigationPlanner());
        services.AddSingleton<DotnetDiagnostics.Core.Memory.IProvenanceCollector, DotnetDiagnostics.Core.Memory.EnvironmentProvenanceCollector>();
        services.AddSingleton<DotnetDiagnostics.Core.Memory.IInvestigationSummaryExporter, DotnetDiagnostics.Core.Memory.InvestigationSummaryExporter>();
        services.AddSingleton<DotnetDiagnostics.Core.Memory.ISummaryComparer, DotnetDiagnostics.Core.Memory.SummaryComparer>();
        services.AddSingleton<DotnetDiagnostics.Core.Memory.MemoryTrendCollector>();
        services.AddSingleton<DotnetDiagnostics.Core.Memory.IMemoryTrendCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<DotnetDiagnostics.Core.Memory.MemoryTrendCollector>());
        services.AddSingleton<DotnetDiagnostics.Core.Memory.IStreamingMemoryTrendCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<DotnetDiagnostics.Core.Memory.MemoryTrendCollector>());
        services.AddSingleton<DotnetDiagnostics.Core.ProcessDiscovery.IRuntimeConfigInspector, DotnetDiagnostics.Core.ProcessDiscovery.RuntimeConfigInspector>();
        services.AddSingleton<DotnetDiagnostics.Core.ProcessDiscovery.ProcessResourcesCollector>();
        services.AddSingleton<DotnetDiagnostics.Core.ProcessDiscovery.IProcessResourcesCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<DotnetDiagnostics.Core.ProcessDiscovery.ProcessResourcesCollector>());
        services.AddSingleton<DotnetDiagnostics.Core.ProcessDiscovery.IStreamingProcessResourcesCollector>(serviceProvider =>
            serviceProvider.GetRequiredService<DotnetDiagnostics.Core.ProcessDiscovery.ProcessResourcesCollector>());
        services.AddSingleton<DotnetDiagnostics.Core.ProcessDiscovery.IRequestsNowCollector, DotnetDiagnostics.Core.ProcessDiscovery.RequestsNowCollector>();
        services.AddSingleton(handleStoreOptions);
        services.AddSingleton<IDiagnosticHandleStore>(serviceProvider =>
            new MemoryDiagnosticHandleStore(
                handleStoreOptions.MaxEntries,
                logger: serviceProvider.GetService<Microsoft.Extensions.Logging.ILogger<MemoryDiagnosticHandleStore>>()));

        return services;
    }
}
