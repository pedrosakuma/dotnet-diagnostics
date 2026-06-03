using System.Globalization;
using System.Text;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Activities;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Collection;
using DotnetDiagnosticsMcp.Core.Contention;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.Db;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Exceptions;
using DotnetDiagnosticsMcp.Core.Gc;
using DotnetDiagnosticsMcp.Core.Jit;
using DotnetDiagnosticsMcp.Core.Logs;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Core.ThreadPool;
using DotnetDiagnosticsMcp.Core.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnosticsMcp.Cli;

/// <summary>
/// Outcome of a single CLI command handler: the raw
/// <see cref="DotnetDiagnosticsMcp.Core.DiagnosticResult{T}"/> envelope (boxed for JSON) plus a
/// pre-rendered human table, and whether the envelope is an error.
/// </summary>
internal sealed record CliCommandResult(bool IsError, bool Cancelled, object Envelope, string Human);

/// <summary>
/// The standalone CLI sub-command handlers (issue #288). Each handler runs one host-neutral use
/// case from <see cref="ProcessInspectionUseCases"/> against the shared Core engine and returns a
/// <see cref="CliCommandResult"/>. Handlers never call <c>Environment.Exit</c> and never own the
/// process lifecycle — <see cref="CliHost"/> owns exit codes and Ctrl-C cancellation.
/// </summary>
internal static class CliCommands
{
    /// <summary>The commands wired in this slice (#288 PR1), in help-listing order.</summary>
    public static readonly IReadOnlyList<string> Commands = new[]
    {
        "processes",
        "capabilities",
        "collect",
        "inspect-heap",
        "dump",
    };

    /// <summary>Heap-snapshot sources accepted by the <c>inspect-heap</c> command (issue #288 PR3b).</summary>
    public static readonly IReadOnlyList<string> HeapSources = new[] { "live", "dump" };

    /// <summary>Dump types accepted by the <c>dump</c> command (mirrors <see cref="ProcessDumpType"/>).</summary>
    public static readonly IReadOnlyList<string> DumpTypes = new[] { "Mini", "Triage", "WithHeap", "Full" };

    /// <summary>
    /// EventPipe collection kinds accepted by the <c>collect</c> command (issue #288 PR2). Mirrors
    /// the MCP <c>collect_events</c> discriminator set so both front-ends accept the same kinds.
    /// </summary>
    public static readonly IReadOnlyList<string> CollectKinds = new[]
    {
        "counters",
        "exceptions",
        "gc",
        "event_source",
        "activities",
        "logs",
        "jit",
        "threadpool",
        "contention",
        "db",
    };

    public static async Task<CliCommandResult> RunAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        return options.Command switch
        {
            "processes" => Processes(services),
            "capabilities" => await CapabilitiesAsync(services, options, cancellationToken).ConfigureAwait(false),
            "collect" => await CollectAsync(services, options, cancellationToken).ConfigureAwait(false),
            "inspect-heap" => await InspectHeapAsync(services, options, cancellationToken).ConfigureAwait(false),
            "dump" => await DumpAsync(services, options, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown command '{options.Command}'.", nameof(options)),
        };
    }

    /// <summary>
    /// Validates the <c>collect</c>-specific options before the host is built so usage problems
    /// surface as exit code 2 (not a thrown exception or a runtime error envelope). Returns
    /// <c>true</c> when the options are well-formed; otherwise sets <paramref name="error"/>.
    /// </summary>
    public static bool TryValidateCollect(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;

        if (string.IsNullOrWhiteSpace(options.Kind))
        {
            error = "The 'collect' command requires --kind <kind>.";
            return false;
        }

        if (!CollectKinds.Contains(options.Kind, StringComparer.Ordinal))
        {
            error = $"Unknown collect kind '{options.Kind}'. Valid kinds: {string.Join(", ", CollectKinds)}.";
            return false;
        }

        if (options.Kind == "event_source" && options.Providers.Count == 0)
        {
            error = "kind=event_source requires --provider <EventSource name>.";
            return false;
        }

        if (options.Depth is not null && !TryParseDepth(options.Depth, out _))
        {
            error = $"Unknown --depth '{options.Depth}'. Valid values: summary, detail, raw.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves the effective <c>inspect-heap</c> source: an explicit <c>--source live|dump</c> wins;
    /// otherwise it is inferred (presence of <c>--dump-file</c> ⇒ dump, else live). Also enforces the
    /// live/dump mutual-exclusion rules. Returns <c>true</c> with <paramref name="source"/> set, or
    /// <c>false</c> with <paramref name="error"/>.
    /// </summary>
    public static bool TryResolveHeapSource(CliOptions options, out string source, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        source = string.Empty;
        error = null;

        if (options.Sources.Count > 1)
        {
            error = "inspect-heap accepts a single --source (live or dump).";
            return false;
        }

        if (options.Sources.Count == 1)
        {
            source = options.Sources[0];
            if (!HeapSources.Contains(source, StringComparer.Ordinal))
            {
                error = $"Unknown --source '{source}'. Valid values: live, dump.";
                return false;
            }
        }
        else
        {
            // Infer from the presence of a dump file so the common cases need no --source.
            source = options.DumpFile is not null ? "dump" : "live";
        }

        if (source == "dump")
        {
            if (string.IsNullOrWhiteSpace(options.DumpFile))
            {
                error = "inspect-heap --source dump requires --dump-file <path>.";
                return false;
            }

            if (options.Pid is not null)
            {
                error = "inspect-heap --source dump does not accept --pid (the dump is offline).";
                return false;
            }
        }
        else if (options.DumpFile is not null)
        {
            error = "inspect-heap --source live does not accept --dump-file (use --source dump).";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates the <c>inspect-heap</c>-specific options before the host is built. Returns
    /// <c>true</c> when well-formed; otherwise sets <paramref name="error"/>.
    /// </summary>
    public static bool TryValidateInspectHeap(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        return TryResolveHeapSource(options, out _, out error);
    }

    /// <summary>
    /// Validates the <c>dump</c>-specific options before the host is built. Returns <c>true</c> when
    /// well-formed; otherwise sets <paramref name="error"/>.
    /// </summary>
    public static bool TryValidateDump(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;

        if (options.DumpType is not null && !TryParseDumpType(options.DumpType, out _))
        {
            error = $"Unknown --dump-type '{options.DumpType}'. Valid values: {string.Join(", ", DumpTypes)}.";
            return false;
        }

        return true;
    }

    private static CliCommandResult Processes(IServiceProvider services)
    {
        var discovery = services.GetRequiredService<IProcessDiscovery>();
        var result = ProcessInspectionUseCases.ListProcesses(discovery);

        var human = RenderEnvelope(result, static (sb, processes) =>
        {
            if (processes.Count == 0)
            {
                return;
            }

            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"{"PID",-8} {"RUNTIME",-16} {"OS/ARCH",-16} ENTRYPOINT");
            foreach (var p in processes)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"{p.ProcessId,-8} {Trunc(p.RuntimeVersion, 16),-16} {Trunc($"{p.OperatingSystem}/{p.ProcessArchitecture}", 16),-16} {p.ManagedEntrypointAssemblyName ?? "<unknown>"}");
            }
        });

        return new CliCommandResult(result.IsError, result.Cancelled, result, human);
    }

    private static async Task<CliCommandResult> CapabilitiesAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var detector = services.GetRequiredService<ICapabilityDetector>();
        var resolver = services.GetRequiredService<IProcessContextResolver>();
        var result = await ProcessInspectionUseCases
            .GetCapabilitiesAsync(detector, resolver, options.Pid, cancellationToken)
            .ConfigureAwait(false);

        var human = RenderEnvelope(result, static (sb, caps) =>
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Runtime           : {caps.Runtime} {caps.RuntimeVersion}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  CPU sampling      : {caps.CanSampleCpu}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  GC dump           : {caps.CanCollectGcDump}");
            if (!string.IsNullOrWhiteSpace(caps.Notes))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Notes             : {caps.Notes}");
            }
        });

        return new CliCommandResult(result.IsError, result.Cancelled, result, human);
    }

    private static async Task<CliCommandResult> CollectAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var resolver = services.GetRequiredService<IProcessContextResolver>();
        var handles = services.GetRequiredService<IDiagnosticHandleStore>();
        var pid = options.Pid;
        // Mirror the MCP collect_events per-kind duration defaults (counters: 5; all others: 10).
        var duration = options.DurationSeconds ?? (options.Kind == "counters" ? 5 : 10);
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
            "counters" => Wrap(await EventCollectionUseCases.SnapshotCounters(
                services.GetRequiredService<ICounterCollector>(), resolver, handles,
                pid, duration, NullIfEmptyArray(options.Providers), NullIfEmptyArray(options.Meters),
                options.IntervalSeconds ?? 1, 1000, depth, cancellationToken).ConfigureAwait(false)),

            "exceptions" => Wrap(await EventCollectionUseCases.CollectExceptions(
                services.GetRequiredService<IExceptionCollector>(), resolver, handles,
                pid, duration, options.MaxEvents ?? 100, depth, cancellationToken).ConfigureAwait(false)),

            "gc" => Wrap(await EventCollectionUseCases.CollectGcEvents(
                services.GetRequiredService<IGcCollector>(), resolver, handles,
                pid, duration, options.MaxEvents ?? 200, depth, cancellationToken).ConfigureAwait(false)),

            "logs" => Wrap(await EventCollectionUseCases.CollectLogs(
                services.GetRequiredService<ILogCollector>(), resolver, handles,
                pid, duration, NullIfEmptyList(options.Categories), options.MinLevel ?? "Information",
                options.MaxEvents ?? 500, 4096, depth, cancellationToken).ConfigureAwait(false)),

            "jit" => Wrap(await EventCollectionUseCases.CollectJit(
                services.GetRequiredService<IJitCollector>(), resolver, handles,
                pid, duration, depth, cancellationToken).ConfigureAwait(false)),

            "threadpool" => Wrap(await EventCollectionUseCases.CollectThreadPool(
                services.GetRequiredService<IThreadPoolCollector>(), resolver, handles,
                pid, duration, depth, cancellationToken).ConfigureAwait(false)),

            "contention" => Wrap(await EventCollectionUseCases.CollectContention(
                services.GetRequiredService<IContentionCollector>(), resolver, handles,
                pid, duration, depth, cancellationToken).ConfigureAwait(false)),

            "db" => Wrap(await EventCollectionUseCases.CollectDb(
                services.GetRequiredService<IDbCollector>(), resolver, handles,
                pid, duration, options.IntervalSeconds ?? 1, depth, cancellationToken).ConfigureAwait(false)),

            "activities" => Wrap(await EventCollectionUseCases.CollectActivities(
                services.GetRequiredService<IActivityCollector>(), resolver, handles,
                pid, NullIfEmptyList(options.Sources), duration, options.MaxEvents ?? 200,
                cancellationToken).ConfigureAwait(false)),

            "event_source" => Wrap(await EventCollectionUseCases.CollectEventSource(
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

            _ => throw new ArgumentException($"Unknown collect kind '{options.Kind}'.", nameof(options)),
        };
    }

    private static CliCommandResult Wrap<T>(DiagnosticResult<T> result) =>
        new(result.IsError, result.Cancelled, result, RenderEnvelope<T>(result, static (_, _) => { }));

    private static async Task<CliCommandResult> InspectHeapAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        // Source was already validated before the host was built; re-resolve to dispatch.
        TryResolveHeapSource(options, out var source, out _);

        var inspector = services.GetRequiredService<IDumpInspector>();
        var handles = services.GetRequiredService<IDiagnosticHandleStore>();
        var allowlist = services.GetRequiredService<SymbolServerAllowlist>();
        var topTypes = options.TopTypes ?? 20;
        var retentionLimit = options.RetentionPathLimit ?? 8;

        if (source == "dump")
        {
            var dumpResult = await HeapInspectionUseCases.InspectDump(
                inspector, handles, allowlist,
                // The CLI runs as the local operator: it owns any remote symbol fetch, so it gets the
                // same posture the stdio root accessor gives the MCP server (symbols-remote granted).
                principalAllowsSymbolsRemote: true,
                options.DumpFile!, topTypes, options.IncludeRetentionPaths, retentionLimit,
                options.IncludeStaticFields, options.IncludeDelegateTargets, options.IncludeDuplicateStrings,
                NullIfEmpty(options.SymbolPath), deprecation: null, cancellationToken).ConfigureAwait(false);

            var dumpHuman = RenderEnvelope<DumpInspection>(dumpResult, static (sb, data) => RenderTopTypes(sb, data.TopTypesByBytes));
            return new CliCommandResult(dumpResult.IsError, dumpResult.Cancelled, dumpResult, dumpHuman);
        }

        var resolver = services.GetRequiredService<IProcessContextResolver>();
        var liveResult = await HeapInspectionUseCases.InspectLiveHeap(
            inspector, handles, resolver, allowlist,
            principalAllowsSymbolsRemote: true,
            options.Pid, topTypes, options.IncludeRetentionPaths, retentionLimit,
            options.IncludeStaticFields, options.IncludeDelegateTargets, options.IncludeDuplicateStrings,
            NullIfEmpty(options.SymbolPath), deprecation: null, cancellationToken).ConfigureAwait(false);

        var liveHuman = RenderEnvelope<LiveHeapInspection>(liveResult, static (sb, data) => RenderTopTypes(sb, data.TopTypesByBytes));
        return new CliCommandResult(liveResult.IsError, liveResult.Cancelled, liveResult, liveHuman);
    }

    private static async Task<CliCommandResult> DumpAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var dumper = services.GetRequiredService<IProcessDumper>();
        var resolver = services.GetRequiredService<IProcessContextResolver>();
        var dumpType = ProcessDumpType.Mini;
        if (options.DumpType is not null && TryParseDumpType(options.DumpType, out var parsedDumpType))
        {
            dumpType = parsedDumpType;
        }

        // --out is wired in as the artifact root for this invocation (CliHost), so the dump lands
        // directly there; pass a null sub-path. The CLI is a local operator and carries no bearer
        // principal, so audit-log fields are empty.
        var result = await ProcessDumpUseCases.CollectProcessDump(
            dumper, resolver, logger: null, principalName: null,
            options.Pid, dumpType, outputDirectory: null, options.Confirm, cancellationToken).ConfigureAwait(false);

        var human = RenderEnvelope<DumpToolResult>(result, static (sb, data) =>
        {
            if (data.Dump is { } dump)
            {
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"  file  : {dump.FilePath}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  size  : {dump.FileSizeBytes:N0} bytes");
            }
        });
        return new CliCommandResult(result.IsError, result.Cancelled, result, human);
    }

    private static void RenderTopTypes(StringBuilder sb, IReadOnlyList<TypeStat> topByBytes)
    {
        if (topByBytes.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"  {"BYTES%",-8} {"INSTANCES",-14} TYPE");
        foreach (var t in topByBytes)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {t.TotalBytesPercent,-8} {t.InstanceCount,-14:N0} {t.TypeFullName}");
        }
    }

    private static bool TryParseDumpType(string value, out ProcessDumpType dumpType) =>
        Enum.TryParse(value, ignoreCase: true, out dumpType) && Enum.IsDefined(dumpType);

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool TryParseDepth(string? value, out SamplingDepth depth)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            depth = SamplingDepth.Summary;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out depth) && Enum.IsDefined(depth);
    }

    private static string[]? NullIfEmptyArray(IReadOnlyList<string> values) =>
        values.Count == 0 ? null : values.ToArray();

    private static IReadOnlyList<string>? NullIfEmptyList(IReadOnlyList<string> values) =>
        values.Count == 0 ? null : values;

    /// <summary>
    /// Renders the host-neutral parts of any <see cref="DiagnosticResult{T}"/> (summary, error,
    /// resolved-process digest, next-action hints) plus a command-specific data block supplied by
    /// <paramref name="renderData"/> (skipped on error / null payload).
    /// </summary>
    private static string RenderEnvelope<T>(DiagnosticResult<T> result, Action<StringBuilder, T> renderData)
    {
        var sb = new StringBuilder();
        sb.Append(result.IsError ? "ERROR: " : string.Empty);
        sb.AppendLine(result.Summary);

        if (result.Error is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  kind   : {result.Error.Kind}");
            if (!string.IsNullOrWhiteSpace(result.Error.Detail))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  detail : {result.Error.Detail}");
            }
        }

        if (result.ResolvedProcess is { } ctx)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  target : pid {ctx.ProcessId}{(ctx.AutoResolved ? " (auto-resolved)" : string.Empty)}");
        }

        if (!result.IsError && result.Data is not null)
        {
            renderData(sb, result.Data);
        }

        if (result.Hints.Count > 0)
        {
            sb.AppendLine("  next:");
            foreach (var hint in result.Hints)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    - {hint.NextTool}: {hint.Reason}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string Trunc(string? value, int max)
    {
        value ??= string.Empty;
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }
}
