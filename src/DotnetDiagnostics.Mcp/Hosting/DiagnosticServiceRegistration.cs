using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Bytes;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Hosting;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Symbols;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Mcp.Azure;
using DotnetDiagnostics.Mcp.Azure.Discovery;
using DotnetDiagnostics.Mcp.Orchestrator;
using DotnetDiagnostics.Mcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Hosting;

/// <summary>
/// Shared DI + MCP-server registrations used by both transports (HTTP, see
/// <c>Program.cs</c>'s WebApplication path; and stdio, see #74 — invoked when the binary
/// is launched with <c>--stdio</c>, e.g. when an MCP client like Copilot CLI spawns the
/// server as a per-session subprocess). Keeping the registrations in one place ensures
/// every tool, prompt, and resource works identically across transports.
/// </summary>
internal static class DiagnosticServiceRegistration
{
    /// <summary>
    /// Registers every Core collector / planner / store the tool layer depends on, by delegating
    /// to the host-neutral <see cref="DiagnosticCoreServiceRegistration.AddDiagnosticCoreServices"/>
    /// (#284) and then adding the few registrations that intentionally stay host-specific (the
    /// legacy-flag deprecation singleton, the MCP task store, and the handle eviction hosted
    /// service). Idempotent per IServiceCollection; safe to call from both WebApplicationBuilder
    /// and HostApplicationBuilder.
    /// </summary>
    public static IServiceCollection AddDiagnosticCoreServices(this IServiceCollection services, string? configuredSymbolPath = null, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // B4 security gates (issue #165). Bound from the `Diagnostics` configuration
        // section; B5 (issue #166) will retrofit these into the per-tool scope system.
        // Binding lives here (Server) so Core stays free of a Configuration dependency;
        // the bound options are handed to the Core registration below.
        var securityOptions = new SecurityOptions();
        configuration?.GetSection(SecurityOptions.SectionName).Bind(securityOptions);
        var handleStoreOptions = new DiagnosticHandleStoreOptions();
        configuration?.GetSection(DiagnosticHandleStoreOptions.SectionName).Bind(handleStoreOptions);

        // The entire Core diagnostic engine (samplers, collectors, security gates, stores)
        // is registered by the host-neutral Core entry point (#284). Everything below is the
        // small set of registrations that intentionally stay host-specific.
        services.AddDiagnosticCoreServices(securityOptions, configuredSymbolPath, handleStoreOptions);

        // B5.4 / docs/authorization.md#backward-compatibility — once-per-process deprecation warnings when a legacy
        // Diagnostics:Allow* flag is the path that unlocks a sensitive operation for a
        // principal lacking the matching modifier scope. Singleton so the once-flags
        // survive across requests. Server-owned (not a Core type).
        services.AddSingleton<Security.LegacyDiagnosticsFlagDeprecation>();

        services.AddSingleton<ModelContextProtocol.IMcpTaskStore>(_ =>
            new ModelContextProtocol.InMemoryMcpTaskStore(
                defaultTtl: System.TimeSpan.FromMinutes(10),
                pollInterval: System.TimeSpan.FromSeconds(1),
                maxTasks: 32,
                maxTasksPerSession: 32));
        services.AddHostedService<HandleEvictionBackgroundService>();

        // #426 — opt-in OpenTelemetry emission of exported investigation summaries.
        // Disabled by default; gated by the `Observability:InvestigationTelemetry:Enabled`
        // config flag or the `MCP_INVESTIGATION_OTEL` environment flag. Registered here so
        // the export tool always resolves the emitter; the ActivitySource is wired into the
        // OTel tracing pipeline by AddOrchestratorObservability.
        var telemetryOptions = new Observability.InvestigationTelemetryOptions();
        configuration?.GetSection(Observability.InvestigationTelemetryOptions.SectionName).Bind(telemetryOptions);
        telemetryOptions.Enabled = telemetryOptions.Enabled || IsEnabledEnvironmentFlag("MCP_INVESTIGATION_OTEL");
        services.TryAddSingleton(telemetryOptions);
        services.TryAddSingleton<Observability.IInvestigationTelemetryEmitter, Observability.InvestigationTelemetry>();

        return services;
    }

    private static bool IsEnabledEnvironmentFlag(string variableName)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        return string.Equals(raw, "1", StringComparison.Ordinal) ||
               string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Registers the central Kubernetes orchestrator services (issue #20). Idempotent;
    /// callers must also call <see cref="AddDiagnosticMcpServer"/> with the same enable
    /// flag so the MCP tool registration matches the DI graph.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="configuration">Configuration root; binds the <c>Orchestrator</c> section onto <see cref="OrchestratorOptions"/>.</param>
    /// <returns>True when <c>Orchestrator:Enabled</c> is true and services were registered; false otherwise.</returns>
    public static bool AddOrchestratorServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new OrchestratorOptions();
        configuration.GetSection(OrchestratorOptions.SectionName).Bind(options);
        if (!options.Enabled) return false;

        services.AddSingleton(options);
        services.AddSingleton<IKubernetesClientFactory, DefaultKubernetesClientFactory>();
        // #234 — kubeconfig handle plumbing. Registered here (orchestrator scope) so the
        // Kubernetes client factory always has the context + store seam wired, regardless
        // of whether Azure discovery is also enabled. TryAdd lets AddAzureDiscoveryServices
        // share the same singletons without duplicate registration.
        //
        // FIX 4 (#234 review): the store ctor takes AzureDiscoveryOptions?, but MS.DI does
        // NOT honor the nullable annotation — it would throw resolving the missing options
        // type. Register through a factory so orchestrator-only deployments (no
        // AddAzureDiscoveryServices call) still resolve the store cleanly. When Azure
        // discovery IS enabled, GetService returns the bound options and we honor them.
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IKubeconfigContext, AsyncLocalKubeconfigContext>();
        services.TryAddSingleton<IKubeconfigHandleStore>(sp => new InMemoryKubeconfigHandleStore(
            sp.GetService<AzureDiscoveryOptions>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IKubernetesPodsApi, KubernetesPodsApi>();
        services.AddSingleton<IPodInventory, KubernetesPodInventory>();
        services.AddSingleton<Orchestrator.Investigations.IInvestigationStore, Orchestrator.Investigations.MemoryInvestigationStore>();
        services.AddSingleton<Orchestrator.Investigations.IInvestigationSessionBinder, Orchestrator.Investigations.MemoryInvestigationSessionBinder>();
        services.AddSingleton<Orchestrator.Investigations.IPortForwardManager, Orchestrator.Investigations.KubernetesPortForwardManager>();
        services.AddSingleton<Orchestrator.Investigations.IInvestigationProxyClient, Orchestrator.Investigations.PodLocalInvestigationProxyClient>();
        services.AddSingleton<Orchestrator.Investigations.IPodAttachOrchestrator, Orchestrator.Investigations.KubernetesPodAttachOrchestrator>();
        services.AddSingleton<Orchestrator.Investigations.InvestigationCloser>();
        services.AddHostedService<InvestigationHandleReaperBackgroundService>();
        return true;
    }

    /// <summary>
    /// Registers the Azure ARM client factory (issue #231, parent #230). Idempotent
    /// foundation seam: when <c>AzureDiscovery:Enabled</c> is true the factory is
    /// added as a singleton so future Azure discovery tooling (#232) can resolve it.
    /// When disabled (default) nothing is added and the Azure SDK is never reached.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="configuration">Configuration root; binds the <c>AzureDiscovery</c> section onto <see cref="AzureDiscoveryOptions"/>.</param>
    /// <returns>True when <c>AzureDiscovery:Enabled</c> is true and services were registered; false otherwise.</returns>
    public static bool AddAzureDiscoveryServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new AzureDiscoveryOptions();
        configuration.GetSection(AzureDiscoveryOptions.SectionName).Bind(options);
        if (!options.Enabled) return false;

        services.AddSingleton(options);
        services.AddSingleton<IAzureArmClientFactory, DefaultAzureArmClientFactory>();

        // #233 — App Service + Container Apps backends are real implementations
        // mediated by adapter seams so unit tests can substitute fakes without
        // touching the Azure SDK.
        services.AddSingleton<IAzureWebSiteCollectionAdapter, DefaultAzureWebSiteCollectionAdapter>();
        services.AddSingleton<IAzureContainerAppCollectionAdapter, DefaultAzureContainerAppCollectionAdapter>();
        services.AddSingleton<IAzureWebAppsDiscovery, DefaultAzureWebAppsDiscovery>();
        services.AddSingleton<IAzureContainerAppsDiscovery, DefaultAzureContainerAppsDiscovery>();

        // #234 — AKS cluster discovery + kubeconfig handle subsystem. The handle store
        // and ambient context are TryAdded so AddOrchestratorServices may have already
        // registered them; either way they end up as singletons shared across both
        // surfaces. TimeProvider.System is the production clock; tests substitute a
        // synthetic one.
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<IKubeconfigContext, AsyncLocalKubeconfigContext>();
        services.TryAddSingleton<IKubeconfigHandleStore>(sp => new InMemoryKubeconfigHandleStore(
            sp.GetService<AzureDiscoveryOptions>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IAzureManagedClusterCollectionAdapter, AzureManagedClusterCollectionAdapter>();
        services.AddSingleton<IAzureAksDiscovery, AzureAksDiscovery>();
        return true;
    }

    /// <summary>
    /// Registers <c>AddMcpServer</c> with the tools/prompts/resources surface and the
    /// shared ToolErrorSurfaceFilter. <paramref name="loggerFactoryAccessor"/> is held by
    /// closure and read lazily after the host is built, mirroring the original Program.cs
    /// pattern (the filter cannot resolve services itself).
    ///
    /// <paramref name="enableOrchestratorTools"/> controls whether the
    /// <see cref="OrchestratorTools"/> surface is exposed to clients. Must be true only
    /// when <see cref="AddOrchestratorServices"/> returned true on the same container.
    /// </summary>
    public static IMcpServerBuilder AddDiagnosticMcpServer(
        this IServiceCollection services,
        Func<ILoggerFactory?> loggerFactoryAccessor,
        bool enableOrchestratorTools = false,
        Func<IServiceProvider?>? servicesAccessor = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(loggerFactoryAccessor);

        // #232 — detect whether AzureDiscovery is enabled by inspecting the service
        // collection. AddAzureDiscoveryServices registers AzureDiscoveryOptions as a
        // singleton only when the master switch is on, so this is a stable, side-
        // effect-free flag without threading another bool through Program.cs.
        var enableAzureDiscoveryTools = false;
        foreach (var d in services)
        {
            if (d.ServiceType == typeof(AzureDiscoveryOptions))
            {
                enableAzureDiscoveryTools = true;
                break;
            }
        }

        var builder = services
            .AddMcpServer(options =>
            {
                options.Filters.Request.CallToolFilters.Add(
                    ToolErrorSurfaceFilter.Create(
                        () => loggerFactoryAccessor()?.CreateLogger(typeof(ToolErrorSurfaceFilter).FullName!)));

                options.Filters.Request.ListToolsFilters.Add(
                    BuildScopeListToolsFilter(
                        servicesAccessor,
                        enableOrchestratorTools,
                        enableAzureDiscoveryTools));

                // B5.2 / docs/authorization.md#scopes — per-tool authorization. Sits AFTER ToolErrorSurfaceFilter
                // in the registration order, which means it runs BEFORE it in the dispatch
                // pipeline (filters wrap last-in-first-out), so a forbidden envelope short-
                // circuits before the surface filter and is returned verbatim. The scope index
                // is built lazily on first call so unit tests that hit Build() without the
                // full tool surface keep working.
                options.Filters.Request.CallToolFilters.Add(
                    BuildScopeAuthorizationFilter(
                        servicesAccessor,
                        loggerFactoryAccessor,
                        enableOrchestratorTools,
                        enableAzureDiscoveryTools));

                if (enableOrchestratorTools && servicesAccessor is not null)
                {
                    // Adds after ToolErrorSurfaceFilter so an exception escaping the proxy
                    // intercept (e.g. SDK protocol violation) is still surfaced as a
                    // structured error result rather than the SDK's generic terminal mask.
                    options.Filters.Request.CallToolFilters.Add(
                        BuildInvestigationProxyFilter(servicesAccessor, loggerFactoryAccessor));
                }

                // #213 — alias removal wave complete. Every legacy
                // deprecated surrogate tool has been deleted; no deprecation filter
                // is registered because there are no deprecated tools left to notify on.

                options.ProtocolVersion = "2025-11-25";

                options.ServerInfo = new Implementation
                {
                    Name = "dotnet-diagnostics-mcp",
                    Title = ".NET Diagnostics",
                    Version = typeof(DiagnosticServiceRegistration).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                    Description =
                        "On-demand performance diagnostics for running .NET applications " +
                        "(CoreCLR and NativeAOT) over the runtime diagnostic IPC socket. " +
                        "Standard EventPipe and ClrMD paths need no prior target instrumentation; " +
                        "method-parameter capture is an explicit, privileged, gated dynamic profiler attach. " +
                        "Designed for K8s sidecar deployments.",
                    WebsiteUrl = "https://github.com/pedrosakuma/dotnet-diagnostics",
                };

                options.ServerInstructions = ServerInstructionsText;
            })
            .WithTools<DiagnosticTools>()
            .WithTools<CollectEventsTool>()
            .WithTools<CollectSampleTool>()
            .WithTools<GetBytesTool>()
            .WithTools<InspectProcessTool>()
            .WithTools<InspectHeapTool>()
            .WithTools<QuerySnapshotTool>()
            // ⚠️ Keep this chain in lock-step with PodLocalToolSurfaces.Always — every type
            // listed there must appear above so the SDK actually dispatches to it. The
            // surface-type registries below (scope + deprecation) and the orchestrator
            // proxy allowlist already read from PodLocalToolSurfaces; this chain stays
            // explicit to keep AOT-friendly generic registration.
            .WithPrompts<Prompts.DiagnosticPrompts>()
            .WithResources<Resources.InvestigationGuideResources>()
            .WithResources<Resources.TraceSessionResources>()
            .WithResources<Resources.HeapSnapshotResources>()
            .WithResources<Resources.ThreadSnapshotResources>()
            .WithResources<Resources.JourneyDiffResources>()
            .WithResources<Resources.SignalsResources>();

        if (enableOrchestratorTools)
        {
            builder.WithTools<OrchestratorTools>();
            builder.WithTools<ListOrchestratorTool>();
        }

        if (enableAzureDiscoveryTools)
        {
            // #232 — Azure discovery v1 tool. Surface gated on AzureDiscovery:Enabled so
            // a server with the master switch off looks identical to a pre-#232 build.
            builder.WithTools<DiscoverAzureTool>();
        }

        return builder;
    }

    private static ModelContextProtocol.Server.McpRequestFilter<ListToolsRequestParams, ListToolsResult> BuildScopeListToolsFilter(
        Func<IServiceProvider?>? servicesAccessor,
        bool enableOrchestratorTools,
        bool enableAzureDiscoveryTools)
    {
        Security.ToolScopeRegistry? cachedRegistry = null;
        ModelContextProtocol.Server.McpRequestFilter<ListToolsRequestParams, ListToolsResult>? cachedFilter = null;
        var gate = new object();

        return next =>
        {
            if (cachedFilter is null)
            {
                lock (gate)
                {
                    if (cachedFilter is null)
                    {
                        var surfaceTypes = PodLocalToolSurfaces.GetSurfaceTypes(
                            enableOrchestratorTools,
                            enableAzureDiscoveryTools);
                        cachedRegistry = Security.ToolScopeRegistry.Build(surfaceTypes);
                        cachedFilter = Security.ToolScopeListToolsFilter.Create(
                            cachedRegistry,
                            () => servicesAccessor?.Invoke()?.GetService<Security.IPrincipalAccessor>());
                    }
                }
            }

            return cachedFilter(next);
        };
    }

    private static ModelContextProtocol.Server.McpRequestFilter<CallToolRequestParams, CallToolResult> BuildScopeAuthorizationFilter(
        Func<IServiceProvider?>? servicesAccessor,
        Func<ILoggerFactory?> loggerFactoryAccessor,
        bool enableOrchestratorTools,
        bool enableAzureDiscoveryTools)
    {
        // Build the tool-scope index on first call. The set of scanned types must match
        // the WithTools<>() chain below (DiagnosticTools always; OrchestratorTools when
        // enabled; DiscoverAzureTool when Azure discovery is enabled) so the registry
        // knows about every tool the SDK will dispatch.
        Security.ToolScopeRegistry? cachedRegistry = null;
        ModelContextProtocol.Server.McpRequestFilter<CallToolRequestParams, CallToolResult>? cachedFilter = null;
        var gate = new object();

        return next =>
        {
            if (cachedFilter is null)
            {
                lock (gate)
                {
                    if (cachedFilter is null)
                    {
                        var surfaceTypes = PodLocalToolSurfaces.GetSurfaceTypes(
                            enableOrchestratorTools,
                            enableAzureDiscoveryTools);
                        cachedRegistry = Security.ToolScopeRegistry.Build(surfaceTypes);

                        cachedFilter = Security.ToolScopeAuthorizationFilter.Create(
                            cachedRegistry,
                            () => servicesAccessor?.Invoke()?.GetService<Security.IPrincipalAccessor>(),
                            () => loggerFactoryAccessor()?.CreateLogger(typeof(Security.ToolScopeAuthorizationFilter).FullName!));
                    }
                }
            }
            return cachedFilter(next);
        };
    }

    private static ModelContextProtocol.Server.McpRequestFilter<CallToolRequestParams, CallToolResult> BuildInvestigationProxyFilter(
        Func<IServiceProvider?> servicesAccessor,
        Func<ILoggerFactory?> loggerFactoryAccessor)
    {
        // Wrap the real filter so DI resolution happens lazily on the first call —
        // AddMcpServer's options callback runs before Build(). We resolve once per call
        // since IInvestigationProxyClient is a singleton (no per-request scope needed).
        ModelContextProtocol.Server.McpRequestFilter<CallToolRequestParams, CallToolResult>? cached = null;
        var gate = new object();

        return next =>
        {
            if (cached is null)
            {
                lock (gate)
                {
                    if (cached is null)
                    {
                        var sp = servicesAccessor()
                            ?? throw new InvalidOperationException(
                                "InvestigationProxyCallToolFilter requires a service provider; servicesAccessor returned null.");
                        cached = Tools.InvestigationProxyCallToolFilter.Create(
                            sp.GetRequiredService<Orchestrator.Investigations.IInvestigationSessionBinder>(),
                            sp.GetRequiredService<Orchestrator.Investigations.IInvestigationStore>(),
                            sp.GetRequiredService<Orchestrator.Investigations.IInvestigationProxyClient>(),
                            sp.GetRequiredService<OrchestratorOptions>(),
                            sp.GetRequiredService<Security.IPrincipalAccessor>(),
                            sp.GetRequiredService<Observability.OrchestratorObservability>(),
                            () => loggerFactoryAccessor()?.CreateLogger(typeof(Tools.InvestigationProxyCallToolFilter).FullName!));
                    }
                }
            }
            return cached(next);
        };
    }

    private const string ServerInstructionsText =
        """
        This server attaches to running .NET processes (locally or in a K8s sidecar) to
        collect performance diagnostics on demand. No code changes to the target are
        required.

        Recommended call order for a fresh investigation:

          0. For a vague "the app is slow / high CPU / memory growing / where do I start" symptom,
             begin with `inspect_process(view="triage")` — it collects counters for ~5s, separates
             observed signals from evidence-backed hypotheses, and hands back the next collector to
             run. Treat low CPU plus a small queue as inconclusive, not proof of I/O. For a non-trivial, multi-step
             investigation where you want a decision tree up front, call `start_investigation` instead
             (then execute its first recommended step).
          1. `collect_events(kind="counters")` — cheap first signal: CPU, working set, GC pressure,
             thread pool, requests/sec. When exactly one .NET process is reachable the
             server auto-selects it; `processId` is optional on every live-process tool.
          2. From the symptom narrow down: high CPU → `collect_sample(kind="cpu")`; allocations
             or GC pauses → `collect_events(kind="gc")`; errors → `collect_events(kind="exceptions")`;
             request/span traces → `collect_events(kind="activities")`; framework-specific signals →
             `collect_events(kind="event_source")` with the right provider.
          3. `collect_process_dump` is the heavyweight last resort (Mini < Triage <
             WithHeap < Full). Use only when live collectors are insufficient.

        Use `inspect_process(view="list")` only when auto-resolution fails (zero or multiple
        .NET processes visible — the error response will tell you). Use
        `inspect_process(view="capabilities")` to confirm CoreCLR vs NativeAOT before reaching
        for NativeAOT-incompatible collectors (CPU sampling, gcdump).

        Always prefer the shortest collection window that answers the question
        (`durationSeconds`) and bound result lists (`topN`, `maxRecent`, `maxEvents`)
        to keep responses small. Tools are read-only except `collect_process_dump`,
        which writes a dump file to disk and is marked Destructive.

        This server requests Elicitation only for the destructive `collect_process_dump`
        approval gate (a human approves writing the dump). Every other tool ships with
        sensible defaults for every parameter and never elicits. `processId` is optional —
        omit it to auto-select the lone reachable .NET process, or pass an explicit pid from
        `inspect_process(view="list")` when several are visible. Pick a default and re-run
        with refined arguments if the first attempt is too noisy or too sparse — the
        response `hints` will tell you how.

        For a longer playbook (HTTP latency, exception storms, GC retention,
        NativeAOT caveats), read the `diag://guides/investigation` resource or
        invoke one of the Prompts (`diagnose-high-latency`, `diagnose-memory-growth`,
        `diagnose-5xx-errors`, `diagnose-slow-outbound-http`, `triage-nativeaot`,
        `diagnose-safely-in-prod`).
        """;
}
