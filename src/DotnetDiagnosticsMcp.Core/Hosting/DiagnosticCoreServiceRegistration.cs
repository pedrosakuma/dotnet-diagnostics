using DotnetDiagnosticsMcp.Core.Activities;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Bytes;
using DotnetDiagnosticsMcp.Core.Contention;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Db;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Jit;
using DotnetDiagnosticsMcp.Core.Logs;
using DotnetDiagnosticsMcp.Core.Exceptions;
using DotnetDiagnosticsMcp.Core.Gc;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Core.Symbols;
using DotnetDiagnosticsMcp.Core.ThreadPool;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnosticsMcp.Core.Hosting;

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
    public static IServiceCollection AddDiagnosticCoreServices(
        this IServiceCollection services,
        SecurityOptions securityOptions,
        string? configuredSymbolPath = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(securityOptions);

        // B4 security gates (issue #165). The caller binds SecurityOptions from the
        // `Diagnostics` configuration section; B5 (issue #166) will retrofit these into the
        // per-tool scope system.
        services.AddSingleton(securityOptions);
        services.AddSingleton<SensitiveDataRedactor>(_ => new SensitiveDataRedactor(securityOptions));
        services.AddSingleton<SensitiveValueGate>(_ => new SensitiveValueGate(securityOptions));
        services.AddSingleton<EventSourceAllowlist>(_ => new EventSourceAllowlist(securityOptions));
        services.AddSingleton<SymbolServerAllowlist>(_ => new SymbolServerAllowlist(securityOptions));

        services.AddSingleton(new SymbolPathBuilder(configuredSymbolPath));
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Artifacts.IArtifactRootProvider, DotnetDiagnosticsMcp.Core.Artifacts.EnvironmentArtifactRootProvider>();
        services.AddSingleton<IProcessDiscovery, LocalProcessDiscovery>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Container.IContainerSignalsCollector, DotnetDiagnosticsMcp.Core.Container.CgroupV2SignalsCollector>();
        services.AddSingleton<ICapabilityDetector, CapabilityDetector>();
        services.AddSingleton<ISessionTargetBindingStore, MemorySessionTargetBindingStore>();
        services.AddSingleton<IProcessContextResolver, ProcessContextResolver>();
        services.AddSingleton<ICounterCollector, EventPipeCounterCollector>();
        services.AddSingleton<MvidReader>();
        services.AddSingleton<FileChunkReader>();
        services.AddSingleton<ClrMdMethodInstantiationEnricher>();
        services.AddSingleton<EventPipeCpuSampler>();
        services.AddSingleton<EventPipeAllocationSampler>();
        services.AddSingleton<PerfNativeAotCpuSampler>();
        services.AddSingleton<EtwNativeAotCpuSampler>();
        services.AddSingleton<ICpuSampler, RoutingCpuSampler>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.OffCpu.PerfSchedOffCpuSampler>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.OffCpu.EtwOffCpuSampler>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.OffCpu.IOffCpuSampler, DotnetDiagnosticsMcp.Core.OffCpu.RoutingOffCpuSampler>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.NativeAlloc.PerfNativeAllocSampler>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.NativeAlloc.INativeAllocSampler>(
            sp => sp.GetRequiredService<DotnetDiagnosticsMcp.Core.NativeAlloc.PerfNativeAllocSampler>());
        services.AddSingleton<IExceptionCollector, EventPipeExceptionCollector>();
        services.AddSingleton<IGcCollector, EventPipeGcCollector>();
        services.AddSingleton<IEventSourceCollector, EventPipeEventSourceCollector>();
        services.AddSingleton<IActivityCollector, EventPipeActivityCollector>();
        services.AddSingleton<ILogCollector, EventPipeLogCollector>();
        services.AddSingleton<IJitCollector, EventPipeJitCollector>();
        services.AddSingleton<IThreadPoolCollector, EventPipeThreadPoolCollector>();
        services.AddSingleton<IContentionCollector, EventPipeContentionCollector>();
        services.AddSingleton<IDbCollector, EventPipeDbCollector>();
        services.AddSingleton<IProcessDumper, DiagnosticsClientDumper>();
        services.AddSingleton<IModuleByteSource, ClrMdModuleByteSource>();
        services.AddSingleton<IDumpByteSource, FileSystemDumpByteSource>();
        services.AddSingleton<IDumpInspector, ClrMdDumpInspector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.ClrMdThreadSnapshotInspector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.LinuxNativeThreadSnapshotInspector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.PerfReplayThreadSnapshotInspector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.EtwNativeThreadSnapshotInspector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.IThreadSnapshotBackend, DotnetDiagnosticsMcp.Core.Threads.ClrMdThreadSnapshotBackend>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.IThreadSnapshotBackend, DotnetDiagnosticsMcp.Core.Threads.LinuxNativeThreadSnapshotBackend>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.IThreadSnapshotBackend, DotnetDiagnosticsMcp.Core.Threads.EtwNativeThreadSnapshotBackend>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.IThreadSnapshotBackend, DotnetDiagnosticsMcp.Core.Threads.PerfReplayThreadSnapshotBackend>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Threads.IThreadSnapshotInspector, DotnetDiagnosticsMcp.Core.Threads.RoutingThreadSnapshotInspector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Symbols.INativeAddressResolver, DotnetDiagnosticsMcp.Core.Symbols.ClrMdNativeAddressResolver>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.JitCapture.IJitMethodCapturer, DotnetDiagnosticsMcp.Core.JitCapture.ClrMdJitMethodCapturer>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Investigation.IInvestigationPlanner>(_ =>
            new DotnetDiagnosticsMcp.Core.Investigation.InvestigationPlanner());
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Memory.IProvenanceCollector, DotnetDiagnosticsMcp.Core.Memory.EnvironmentProvenanceCollector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Memory.IInvestigationSummaryExporter, DotnetDiagnosticsMcp.Core.Memory.InvestigationSummaryExporter>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Memory.ISummaryComparer, DotnetDiagnosticsMcp.Core.Memory.SummaryComparer>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Memory.IMemoryTrendCollector, DotnetDiagnosticsMcp.Core.Memory.MemoryTrendCollector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.ProcessDiscovery.IRuntimeConfigInspector, DotnetDiagnosticsMcp.Core.ProcessDiscovery.RuntimeConfigInspector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.ProcessDiscovery.IProcessResourcesCollector, DotnetDiagnosticsMcp.Core.ProcessDiscovery.ProcessResourcesCollector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.ProcessDiscovery.IRequestsNowCollector, DotnetDiagnosticsMcp.Core.ProcessDiscovery.RequestsNowCollector>();
        services.AddSingleton<DotnetDiagnosticsMcp.Core.Drilldown.IDiagnosticHandleStore>(_ =>
            new DotnetDiagnosticsMcp.Core.Drilldown.MemoryDiagnosticHandleStore(maxEntries: 32));

        return services;
    }
}
