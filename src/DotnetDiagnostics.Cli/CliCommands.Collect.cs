using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.GatedCapture;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Kestrel;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.NativeAlloc;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Startup;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli;

internal static partial class CliCommands
{
    private static async Task<CliCommandResult> CollectAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var resolver = services.GetRequiredService<IProcessContextResolver>();
        var handles = services.GetRequiredService<IDiagnosticHandleStore>();
        var pid = options.Pid;
        // Mirror the MCP collect_events per-kind duration defaults (counters: 5; all others: 10).
        var duration = options.DurationSeconds ?? (options.Kind == "counters" ? 5 : options.Kind == "datas" ? 15 : options.Kind == "sweep" ? SweepUseCase.MinimumDurationSeconds : 10);
        // The CLI is a stateless one-shot: the in-memory handle store is disposed when the command
        // returns, so a drilldown handle can never be queried in a follow-up invocation. Default to
        // Detail so the captured records stay inline (and land in --json) instead of being trimmed
        // behind an unreachable handle. An explicit --depth still wins.
        var depth = SamplingDepth.Detail;
        if (options.Depth is not null && TryParseDepth(options.Depth, out var parsedDepth))
        {
            depth = parsedDepth;
        }

        return options.Kind switch
        {
            "counters" when options.CaptureWhen is not null => Wrap(options, await GatedCaptureUseCases.WatchAndCapture(
                services.GetRequiredService<IThresholdGatedCaptureCollector>(), resolver, handles,
                services.GetRequiredService<ICpuSampler>(),
                services.GetRequiredService<IThreadSnapshotInspector>(),
                services.GetRequiredService<IDumpInspector>(),
                services.GetRequiredService<IProcessDumper>(),
                options.CaptureWhen, options.CaptureKind, options.WindowSeconds ?? 0,
                options.MaxCaptures ?? 1, options.WatchIntervalSeconds ?? 2, options.Confirm, pid,
                dumpOutputDirectory: null,
                nativeAotSymbols: string.IsNullOrWhiteSpace(options.NativeAotMapFile)
                    ? null
                    : new NativeAotSymbolResolutionOptions(MapFilePath: options.NativeAotMapFile),
                cancellationToken).ConfigureAwait(false)),

            "counters" => Wrap(options, await EventCollectionUseCases.SnapshotCounters(
                services.GetRequiredService<ICounterCollector>(), resolver, handles,
                pid, duration, NullIfEmptyArray(options.Providers), NullIfEmptyArray(options.Meters),
                options.IntervalSeconds ?? 1, 1000, depth, cancellationToken).ConfigureAwait(false)),

            "exceptions" => Wrap(options, await EventCollectionUseCases.CollectExceptions(
                services.GetRequiredService<IExceptionCollector>(), resolver, handles,
                pid, duration, options.MaxEvents ?? 100, depth, cancellationToken).ConfigureAwait(false)),

            "crash-guard" => Wrap(options, await EventCollectionUseCases.CollectCrashGuard(
                services.GetRequiredService<ICrashGuardCollector>(), resolver, handles,
                pid, duration, options.MaxEvents ?? 100, depth, cancellationToken).ConfigureAwait(false)),

            "gc" => Wrap(options, await EventCollectionUseCases.CollectGcEvents(
                services.GetRequiredService<IGcCollector>(), resolver, handles,
                pid, duration, options.MaxEvents ?? 200, depth, cancellationToken).ConfigureAwait(false)),

            "datas" => Wrap(options, await EventCollectionUseCases.CollectGcDatas(
                services.GetRequiredService<IGcDatasCollector>(), resolver, handles,
                pid, duration, options.MaxEvents ?? 1000, cancellationToken).ConfigureAwait(false)),

            "catalog" => Wrap(options, await EventCollectionUseCases.CollectEventCatalog(
                services.GetRequiredService<IEventCatalogCollector>(), resolver, handles,
                pid, duration, NullIfEmptyList(options.Providers), options.MaxEvents ?? 200, depth, cancellationToken).ConfigureAwait(false)),

            "logs" => Wrap(options, await EventCollectionUseCases.CollectLogs(
                services.GetRequiredService<ILogCollector>(), resolver, handles,
                pid, duration, NullIfEmptyList(options.Categories), options.MinLevel ?? "Information",
                options.MaxEvents ?? 500, 4096, depth, cancellationToken).ConfigureAwait(false)),

            "jit" => Wrap(options, await EventCollectionUseCases.CollectJit(
                services.GetRequiredService<IJitCollector>(), resolver, handles,
                pid, duration, depth, cancellationToken).ConfigureAwait(false)),

            "threadpool" => Wrap(options, CliHintProjection.ProjectThreadPoolNotes(await EventCollectionUseCases.CollectThreadPool(
                services.GetRequiredService<IThreadPoolCollector>(), resolver, handles,
                pid, duration, depth, cancellationToken).ConfigureAwait(false))),

            "contention" => Wrap(options, await EventCollectionUseCases.CollectContention(
                services.GetRequiredService<IContentionCollector>(), resolver, handles,
                pid, duration, depth, cancellationToken).ConfigureAwait(false)),

            "db" => Wrap(options, await EventCollectionUseCases.CollectDb(
                services.GetRequiredService<IDbCollector>(), resolver, handles,
                pid, duration, options.IntervalSeconds ?? 1, depth, cancellationToken).ConfigureAwait(false)),

            "kestrel" => Wrap(options, await EventCollectionUseCases.CollectKestrel(
                services.GetRequiredService<IKestrelCollector>(), resolver, handles,
                pid, duration, options.IntervalSeconds ?? 1, depth, cancellationToken).ConfigureAwait(false)),

            "requests" => Wrap(options, await EventCollectionUseCases.CollectInFlightRequests(
                services.GetRequiredService<DotnetDiagnostics.Core.Requests.IInFlightRequestCollector>(), resolver, handles,
                pid, duration, options.Threshold ?? 1000, options.MaxEvents ?? 100, depth, cancellationToken).ConfigureAwait(false)),

            "startup" => Wrap(options, await EventCollectionUseCases.CollectStartup(
                services.GetRequiredService<IStartupCollector>(), resolver, handles,
                pid, duration, depth, cancellationToken).ConfigureAwait(false)),

            "sweep" => Wrap(options, await SweepUseCase.RunSweep(
                services.GetRequiredService<ICounterCollector>(),
                services.GetRequiredService<IGcCollector>(),
                services.GetRequiredService<IExceptionCollector>(),
                services.GetRequiredService<IThreadPoolCollector>(),
                services.GetRequiredService<IProcessResourcesCollector>(),
                resolver, handles,
                pid, duration, options.MaxEvents ?? 100, options.MaxEvents ?? 200, depth,
                cancellationToken).ConfigureAwait(false)),

            "networking" => Wrap(options, await EventCollectionUseCases.CollectNetworking(
                services.GetRequiredService<INetworkingCollector>(), resolver, handles,
                pid, duration, options.IntervalSeconds ?? 1, depth, cancellationToken).ConfigureAwait(false)),

            "activities" => Wrap(options, await EventCollectionUseCases.CollectActivities(
                services.GetRequiredService<IActivityCollector>(), resolver, handles,
                pid, NullIfEmptyList(options.Sources), duration, options.MaxEvents ?? 200,
                cancellationToken).ConfigureAwait(false)),

            "event_source" => Wrap(options, await EventCollectionUseCases.CollectEventSource(
                services.GetRequiredService<IEventSourceCollector>(), resolver, handles,
                services.GetRequiredService<EventSourceAllowlist>(),
                services.GetRequiredService<SensitiveValueGate>(),
                // The CLI runs as the local operator with no bearer principal; grant the same
                // posture the stdio root accessor gives the MCP server (eventsource-any). Reaching a
                // non-allowlisted provider still requires the explicit --unsafe-provider opt-in.
                principalAllowsEventSourceAny: true,
                options.Providers[0], pid, duration, keywords: -1, eventLevel: 5,
                options.MaxEvents ?? 200, depth, options.UnsafeProvider, deprecation: null,
                cancellationToken).ConfigureAwait(false)),

            "cpu" => await CollectCpuSampleAsync(services, options, cancellationToken).ConfigureAwait(false),

            "allocation" => await CollectAllocationSampleAsync(services, options, cancellationToken).ConfigureAwait(false),

            "off_cpu" or "off-cpu" => await CollectOffCpuSampleAsync(services, options, cancellationToken).ConfigureAwait(false),

            "native-alloc" => await CollectNativeAllocSampleAsync(services, options, cancellationToken).ConfigureAwait(false),

            "thread-snapshot" => await CollectThreadSnapshotAsync(services, options, cancellationToken).ConfigureAwait(false),

            _ => throw new ArgumentException($"Unknown collect kind '{options.Kind}'.", nameof(options)),
        };
    }

    private static CliCommandResult Wrap<T>(CliOptions options, DiagnosticResult<T> result) =>
        BuildResultWithComparableSave(options, result, static (_, _) => { });

    private static async Task<CliCommandResult> CollectCpuSampleAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
        => Wrap(options, await SamplerUseCases.CollectCpuSample(
            services.GetRequiredService<ICpuSampler>(),
            services.GetRequiredService<IDiagnosticHandleStore>(),
            services.GetRequiredService<IProcessContextResolver>(),
            services.GetRequiredService<SymbolServerAllowlist>(),
            principalAllowsSymbolsRemote: false,
            options.Pid,
            options.DurationSeconds ?? 10,
            options.Top ?? 25,
            options.ResolveSourceLines ?? true,
            options.SymbolPath,
            options.ResolveMethodInstantiations,
            options.NativeAotMapFile,
            ParseDepth(options.Depth),
            options.ExportTrace,
            cancellationToken).ConfigureAwait(false));

    private static async Task<CliCommandResult> CollectAllocationSampleAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
        => Wrap(options, await SamplerUseCases.CollectAllocationSample(
            services.GetRequiredService<EventPipeAllocationSampler>(),
            services.GetRequiredService<IDiagnosticHandleStore>(),
            services.GetRequiredService<IProcessContextResolver>(),
            options.Pid,
            options.DurationSeconds ?? 10,
            options.Top ?? 25,
            cancellationToken).ConfigureAwait(false));

    private static async Task<CliCommandResult> CollectOffCpuSampleAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
        => Wrap(options, await SamplerUseCases.CollectOffCpuSample(
            services.GetRequiredService<IOffCpuSampler>(),
            services.GetRequiredService<IDiagnosticHandleStore>(),
            services.GetRequiredService<IProcessContextResolver>(),
            services.GetRequiredService<SymbolServerAllowlist>(),
            principalAllowsSymbolsRemote: false,
            options.Pid,
            options.DurationSeconds ?? 10,
            options.Top ?? 25,
            options.SymbolPath,
            ParseDepth(options.Depth),
            cancellationToken).ConfigureAwait(false));

    private static async Task<CliCommandResult> CollectNativeAllocSampleAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
        => Wrap(options, await SamplerUseCases.CollectNativeAllocSample(
            services.GetRequiredService<INativeAllocSampler>(),
            services.GetRequiredService<IDiagnosticHandleStore>(),
            services.GetRequiredService<IProcessContextResolver>(),
            options.Pid,
            options.DurationSeconds ?? 10,
            options.Top ?? 25,
            options.NativeAllocSamplePeriod ?? 1000,
            cancellationToken).ConfigureAwait(false));

    private static async Task<CliCommandResult> CollectThreadSnapshotAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
        => Wrap(options, await SamplerUseCases.CollectThreadSnapshot(
            services.GetRequiredService<IThreadSnapshotInspector>(),
            services.GetRequiredService<IDiagnosticHandleStore>(),
            services.GetRequiredService<IProcessContextResolver>(),
            services.GetRequiredService<SymbolServerAllowlist>(),
            principalAllowsSymbolsRemote: false,
            options.Pid,
            options.DumpFile,
            options.MaxFramesPerThread ?? 64,
            options.IncludeRuntimeFrames,
            options.IncludeNativeFrames,
            options.SymbolPath,
            ParseDepth(options.Depth),
            cancellationToken).ConfigureAwait(false));

    private static SamplingDepth ParseDepth(string? depth)
        => depth is not null && TryParseDepth(depth, out var parsedDepth)
            ? parsedDepth
            : SamplingDepth.Detail;

    /// <summary>
    /// Cold-start capture entry point (issue #446): collects a startup snapshot on a target launched
    /// suspended on a reverse-connect diagnostic port, arming the session before resume. Mirrors the
    /// rendering of the normal <c>collect --kind startup</c> path so --json / human output is identical.
    /// </summary>
    internal static async Task<CliCommandResult> RunColdStartStartupAsync(
        IServiceProvider services,
        CliOptions options,
        DotnetDiagnostics.Core.Launch.SuspendedTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(target);

        var handles = services.GetRequiredService<IDiagnosticHandleStore>();
        var duration = options.DurationSeconds ?? 10;
        var depth = SamplingDepth.Detail;
        if (options.Depth is not null && TryParseDepth(options.Depth, out var parsedDepth))
        {
            depth = parsedDepth;
        }

        return Wrap(options, await EventCollectionUseCases.CollectStartupColdStart(
            services.GetRequiredService<IStartupCollector>(), handles, target, duration, depth, cancellationToken)
            .ConfigureAwait(false));
    }

}
