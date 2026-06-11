using System.Globalization;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Bytes;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Kestrel;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli;

/// <summary>
/// Outcome of a single CLI command handler: the raw
/// <see cref="DotnetDiagnostics.Core.DiagnosticResult{T}"/> envelope (boxed for JSON) plus a
/// pre-rendered human table, and whether the envelope is an error.
/// </summary>
internal sealed record CliCommandResult(bool IsError, bool Cancelled, object Envelope, string Human)
{
    /// <summary>Drill-down handle published by the originating command (e.g. <c>collect</c>), or
    /// <c>null</c>. Surfaced by the <c>session</c> REPL so the user can <c>query --handle &lt;id&gt;</c>
    /// without re-collecting; meaningless (and unused) in the one-shot path where the process exits.</summary>
    public string? Handle { get; init; }

    /// <summary>UTC moment <see cref="Handle"/> expires, or <c>null</c>.</summary>
    public DateTimeOffset? HandleExpiresAt { get; init; }
}

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
        "query",
        "get-bytes",
        "compare",
        "session",
    };

    /// <summary>Heap-snapshot sources accepted by the <c>inspect-heap</c> command (issue #288 PR3b).</summary>
    public static readonly IReadOnlyList<string> HeapSources = new[] { "live", "dump" };

    /// <summary>Artifact kinds accepted by the <c>get-bytes</c> command (issue #288 PR4).</summary>
    public static readonly IReadOnlyList<string> ByteKinds = new[] { "module", "dump" };

    /// <summary>Module assets accepted by <c>get-bytes --kind module</c> (issue #288 PR4).</summary>
    public static readonly IReadOnlyList<string> ByteAssets = new[] { "pe", "pdb" };

    /// <summary>Dump types accepted by the <c>dump</c> command (mirrors <see cref="ProcessDumpType"/>).</summary>
    public static readonly IReadOnlyList<string> DumpTypes = new[] { "Mini", "Triage", "WithHeap", "Full" };

    /// <summary>
    /// Commands the opt-in <c>--launch</c> dev mode (issue #365) supports: the live-target commands
    /// whose attach is unblocked by the CLI becoming the target's ptrace parent, plus <c>session</c>
    /// (which launches once and binds the child for the whole REPL). <c>inspect-heap --source dump</c>,
    /// <c>get-bytes --kind dump</c>, <c>processes</c>, <c>query</c> and <c>compare</c> are offline /
    /// pid-less and reject <c>--launch</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> LaunchableCommands = new[]
    {
        "session",
        "capabilities",
        "collect",
        "dump",
        "inspect-heap",
        "get-bytes",
    };

    /// <summary>
    /// EventPipe collection kinds accepted by the <c>collect</c> command (issue #288 PR2). Mirrors
    /// the MCP <c>collect_events</c> discriminator set so both front-ends accept the same kinds.
    /// </summary>
    public static readonly IReadOnlyList<string> CollectKinds = new[]
    {
        "counters",
        "exceptions",
        "gc",
        "datas",
        "catalog",
        "event_source",
        "activities",
        "logs",
        "jit",
        "threadpool",
        "contention",
        "db",
        "kestrel",
        "networking",
    };

    private static readonly IComparableProjector[] ComparableProjectors =
    {
        new GcDatasComparableProjector(),
        new CountersComparableProjector(),
        new GcEventsComparableProjector(),
        new ContentionComparableProjector(),
        new ThreadPoolComparableProjector(),
    };

    private static readonly string SupportedComparableKinds = string.Join(", ", ComparableProjectors.Select(p => p.Kind));

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
            "query" => Query(),
            "get-bytes" => await GetBytesAsync(services, options, cancellationToken).ConfigureAwait(false),
            "compare" => await CompareAsync(options, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown command '{options.Command}'.", nameof(options)),
        };
    }

    /// <summary>
    /// Aggregates the per-command <c>TryValidate*</c> checks into one entry point for the stateful
    /// <c>session</c> REPL (issue #300), which validates a parsed line before dispatching it against
    /// the shared host. Commands without dedicated validation (<c>processes</c>, <c>capabilities</c>,
    /// <c>query</c>) return <c>true</c>. The one-shot <see cref="CliHost"/> path keeps its own
    /// per-command validation blocks (which additionally print the full usage screen on failure).
    /// </summary>
    public static bool TryValidateCommand(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;
        return options.Command switch
        {
            "collect" => TryValidateCollect(options, out error),
            "inspect-heap" => TryValidateInspectHeap(options, out error),
            "dump" => TryValidateDump(options, out error),
            "get-bytes" => TryValidateGetBytes(options, out error),
            "compare" => TryValidateCompare(options, out error),
            _ => true,
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
    /// Validates the opt-in <c>--launch</c> dev mode (issue #365), independent of the per-command
    /// option checks. Enforces that the <c>--</c> launch argv and the <c>--launch</c> flag are used
    /// together, that <c>--launch</c> is mutually exclusive with <c>--pid</c> (the CLI supplies the
    /// child's pid), and that the command actually targets a live process the child relationship can
    /// unblock. Commands that pass no <c>--launch</c> and no <c>--</c> argv are accepted unchanged.
    /// </summary>
    public static bool TryValidateLaunch(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;

        if (!options.Launch)
        {
            if (options.LaunchArgs.Count > 0)
            {
                error = "Launch arguments after '--' require --launch (e.g. --launch -- dotnet App.dll).";
                return false;
            }

            return true;
        }

        if (options.LaunchArgs.Count == 0)
        {
            error = "--launch requires a program after '--', e.g. --launch -- dotnet App.dll.";
            return false;
        }

        if (options.Pid is not null)
        {
            error = "--launch cannot be combined with --pid: the CLI launches the target and binds its pid.";
            return false;
        }

        if (!LaunchableCommands.Contains(options.Command, StringComparer.Ordinal))
        {
            error = $"--launch is not supported by '{options.Command}'. Supported: {string.Join(", ", LaunchableCommands)}.";
            return false;
        }

        if (options.Command == "inspect-heap"
            && (options.DumpFile is not null || options.Sources.Contains("dump", StringComparer.Ordinal)))
        {
            error = "--launch applies to a live target; it cannot be combined with inspect-heap --source dump.";
            return false;
        }

        if (options.Command == "get-bytes" && string.Equals(options.Kind, "dump", StringComparison.Ordinal))
        {
            error = "--launch applies to a live target; it cannot be combined with get-bytes --kind dump.";
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

    /// <summary>
    /// Validates the <c>get-bytes</c>-specific options before the host is built. Returns <c>true</c>
    /// when well-formed; otherwise sets <paramref name="error"/>. <c>get-bytes</c> always materialises
    /// the artifact to a file, so <c>--out &lt;file&gt;</c> is mandatory.
    /// </summary>
    public static bool TryValidateGetBytes(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;

        if (string.IsNullOrWhiteSpace(options.Kind))
        {
            error = "The 'get-bytes' command requires --kind <module|dump>.";
            return false;
        }

        if (!ByteKinds.Contains(options.Kind, StringComparer.Ordinal))
        {
            error = $"Unknown get-bytes kind '{options.Kind}'. Valid kinds: {string.Join(", ", ByteKinds)}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.OutDir))
        {
            error = "The 'get-bytes' command requires --out <file> (the destination the artifact is written to).";
            return false;
        }

        if (options.Kind == "module")
        {
            if (string.IsNullOrWhiteSpace(options.Mvid))
            {
                error = "get-bytes --kind module requires --mvid <module-version-id>.";
                return false;
            }

            if (!Guid.TryParse(options.Mvid, out _))
            {
                error = $"--mvid '{options.Mvid}' is not a valid GUID.";
                return false;
            }

            if (options.Asset is not null && !ByteAssets.Contains(options.Asset, StringComparer.Ordinal))
            {
                error = $"Unknown --asset '{options.Asset}'. Valid values: {string.Join(", ", ByteAssets)}.";
                return false;
            }

            if (options.DumpFile is not null)
            {
                error = "get-bytes --kind module does not accept --dump-file (that is for --kind dump).";
                return false;
            }
        }
        else
        {
            // kind == dump
            if (string.IsNullOrWhiteSpace(options.DumpFile))
            {
                error = "get-bytes --kind dump requires --dump-file <path>.";
                return false;
            }

            if (options.Pid is not null)
            {
                error = "get-bytes --kind dump does not accept --pid (the dump is offline).";
                return false;
            }
        }

        return true;
    }

    public static bool TryValidateCompare(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;

        if (options.ComparePaths.Count < 2)
        {
            error = "The 'compare' command requires at least two snapshot JSON paths.";
            return false;
        }

        if (!JourneyModeParser.TryParse(options.Mode, out _))
        {
            error = $"Unknown --mode '{options.Mode}'. Valid values: trend, dispersion.";
            return false;
        }

        return true;
    }

    private static CliCommandResult Processes(IServiceProvider services)
    {
        var discovery = services.GetRequiredService<IProcessDiscovery>();
        var result = ProcessInspectionUseCases.ListProcesses(discovery);

        return BuildResult(result, static (sb, processes) =>
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

        // Swap Core's MCP-audience capability narrative for a CLI-authored note before rendering, so
        // neither the human table nor the --json envelope leaks MCP tool names (#302).
        result = CliHintProjection.ProjectCapabilities(result, options.LaunchedByCli);

        return BuildResult(result, static (sb, caps) =>
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
        var duration = options.DurationSeconds ?? (options.Kind == "counters" ? 5 : options.Kind == "datas" ? 15 : 10);
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
            "counters" => Wrap(options, await EventCollectionUseCases.SnapshotCounters(
                services.GetRequiredService<ICounterCollector>(), resolver, handles,
                pid, duration, NullIfEmptyArray(options.Providers), NullIfEmptyArray(options.Meters),
                options.IntervalSeconds ?? 1, 1000, depth, cancellationToken).ConfigureAwait(false)),

            "exceptions" => Wrap(options, await EventCollectionUseCases.CollectExceptions(
                services.GetRequiredService<IExceptionCollector>(), resolver, handles,
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

            _ => throw new ArgumentException($"Unknown collect kind '{options.Kind}'.", nameof(options)),
        };
    }

    private static CliCommandResult Wrap<T>(CliOptions options, DiagnosticResult<T> result) =>
        BuildResultWithComparableSave(options, result, static (_, _) => { });

    private static async Task<CliCommandResult> CompareAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var snapshots = new List<ComparableSnapshot>(options.ComparePaths.Count);
        foreach (var path in options.ComparePaths)
        {
            ComparableSnapshot? snapshot;
            try
            {
                await using var stream = File.OpenRead(path);
                snapshot = await JsonSerializer.DeserializeAsync(
                    stream,
                    ComparableSnapshotJsonContext.Default.ComparableSnapshot,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                return BuildResult<object>(DiagnosticResult.Fail<object>(
                    $"compare: failed to read '{path}'.",
                    new DiagnosticError("InvalidSnapshot", ex.Message)), static (_, _) => { });
            }

            if (snapshot is null || !string.Equals(snapshot.Schema, ComparableSnapshot.SchemaV1, StringComparison.Ordinal))
            {
                return BuildResult<object>(DiagnosticResult.Fail<object>(
                    $"compare: '{path}' is not a comparable snapshot v1 JSON file.",
                    new DiagnosticError("InvalidSnapshot", $"Expected schema '{ComparableSnapshot.SchemaV1}'.")), static (_, _) => { });
            }

            snapshots.Add(snapshot);
        }

        if (!JourneyModeParser.TryParse(options.Mode, out var mode))
        {
            return BuildResult<object>(DiagnosticResult.Fail<object>(
                $"compare: unknown --mode '{options.Mode}'.",
                new DiagnosticError("InvalidArgument", "Valid values: trend, dispersion.", nameof(options.Mode))), static (_, _) => { });
        }

        var diff = SnapshotDiffer.Compare(snapshots, mode);
        if (!string.IsNullOrWhiteSpace(options.SavePath))
        {
            try
            {
                var fullPath = Path.GetFullPath(options.SavePath);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await using var output = File.Create(fullPath);
                await JsonSerializer.SerializeAsync(
                    output,
                    diff,
                    ComparableSnapshotJsonContext.Default.SnapshotJourneyDiff,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return BuildResult<object>(DiagnosticResult.Fail<object>(
                    $"compare: failed to write '{options.SavePath}'.",
                    new DiagnosticError("OutputWriteFailure", ex.Message)), static (_, _) => { });
            }
        }

        return new CliCommandResult(IsError: false, Cancelled: false, diff, RenderJourneyDiff(diff));
    }

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

            return BuildResult<DumpInspection>(dumpResult, static (sb, data) => RenderTopTypes(sb, data.TopTypesByBytes));
        }

        var resolver = services.GetRequiredService<IProcessContextResolver>();
        var liveResult = await HeapInspectionUseCases.InspectLiveHeap(
            inspector, handles, resolver, allowlist,
            principalAllowsSymbolsRemote: true,
            options.Pid, topTypes, options.IncludeRetentionPaths, retentionLimit,
            options.IncludeStaticFields, options.IncludeDelegateTargets, options.IncludeDuplicateStrings,
            NullIfEmpty(options.SymbolPath), deprecation: null, cancellationToken).ConfigureAwait(false);

        return BuildResult<LiveHeapInspection>(liveResult, static (sb, data) => RenderTopTypes(sb, data.TopTypesByBytes));
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

        // The Core confirmation-required preview names the MCP tool (collect_process_dump) and
        // confirm=true in its summary/message; rewrite it to CLI vocabulary before rendering (#301).
        result = CliHintProjection.RewriteDumpPreview(result);

        // #387: disclose the resolved artifact directory the dump WOULD be written to *before* it is
        // written, so the operator sees the destination on the --confirm preview (not only in the
        // success envelope). The root is the CLI's sandbox (dump --out, or the temp / MCP_ARTIFACT_ROOT
        // default).
        var artifactRoot = services.GetRequiredService<DotnetDiagnostics.Core.Artifacts.IArtifactRootProvider>().Root;

        return BuildResult<DumpToolResult>(result, (sb, data) =>
        {
            if (data.Dump is { } dump)
            {
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"  file  : {dump.FilePath}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  size  : {dump.FileSizeBytes:N0} bytes");
            }
            else if (string.Equals(data.Kind, DumpToolResultKinds.ConfirmationRequired, StringComparison.Ordinal))
            {
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"  would write to : {artifactRoot}");
                sb.AppendLine("  re-run with --confirm to write the dump.");
            }
        });
    }

    /// <summary>
    /// The <c>query</c> drill-down command in the one-shot CLI. Drill-down handles are MCP-session
    /// scoped and the one-shot CLI is stateless (per the #286 persistence decision: cheap inline
    /// summaries only, no handle store survives the process), so there is nothing to query in a
    /// follow-up invocation. Returns a structured <c>NotSupported</c> envelope (exit 1) that redirects
    /// the operator to the <c>session</c> REPL — the one place where a collected handle lives long
    /// enough to <c>query --handle &lt;id&gt; --view &lt;view&gt;</c>.
    /// </summary>
    private static CliCommandResult Query()
    {
        var result = DiagnosticResult.Fail<object>(
            "The 'query' drill-down command needs a live session. Start one with 'dotnet-diagnostics session'.",
            new DiagnosticError(
                "NotSupported",
                "Drill-down handles are scoped to a live session. The one-shot CLI is stateless, so a handle "
                + "from a previous command no longer exists. Start the interactive REPL with 'dotnet-diagnostics "
                + "session': there, a 'collect' (or 'inspect-heap' / 'dump') issues a handle you can drill into "
                + "with 'query --handle <id> --view <view>' in the same session. For a one-shot answer instead, "
                + "re-run the originating command with --depth detail (or --json) to get the full result inline.",
                "one-shot-cli"),
            new NextActionHint("session", "Start the interactive session REPL, then collect and query --handle <id> --view <view> there."),
            new NextActionHint("collect", "Or re-run the originating command with --depth detail (or --json) to get the full result inline."));

        return BuildResult<object>(result, static (_, _) => { });
    }

    /// <summary>
    /// Dispatcher views the <c>session</c> <c>query</c> path cannot render yet because they correlate a
    /// second collected artifact the session has no way to supply (currently only the activities
    /// <c>gc-overlay</c>, which needs a GC handle). They are hidden from the advertised view list and
    /// rejected with a clear <c>NotSupportedInSession</c> rather than the dispatcher's confusing
    /// "missing correlate" <c>InvalidArgument</c>.
    /// </summary>
    private static readonly HashSet<string> SessionExcludedViews =
        new(StringComparer.OrdinalIgnoreCase) { "gc-overlay" };

    /// <summary>
    /// Handle kinds backing a <see cref="CpuSampleTraceArtifact"/> (directly, or wrapped in an
    /// <see cref="AllocationSampleArtifact"/>) whose session drill-down is the host-neutral
    /// <c>call-tree</c> view. Keep in sync with the server's <c>cpu-sample</c> /
    /// <c>allocation-sample</c> / <c>native-alloc-sample</c> handle registrations.
    /// </summary>
    private static readonly HashSet<string> CpuSampleSessionKinds =
        new(StringComparer.Ordinal) { "cpu-sample", "allocation-sample", "native-alloc-sample" };

    /// <summary>
    /// Handle kind backing a <see cref="ThreadSnapshotArtifact"/> whose session drill-down is served by
    /// the host-neutral <see cref="ThreadSnapshotQueryDispatcher"/>. Keep in sync with the server's
    /// <c>collect_thread_snapshot</c> handle registration (<c>thread-snapshot</c>).
    /// </summary>
    private const string ThreadSnapshotSessionKind = "thread-snapshot";

    /// <summary>
    /// Handle kind backing an <see cref="OffCpuSnapshotArtifact"/> whose session drill-down is served by
    /// the host-neutral <see cref="OffCpuQueryDispatcher"/>. Keep in sync with the server's
    /// <c>collect_off_cpu_sample</c> handle registration (<c>off-cpu-snapshot</c>).
    /// </summary>
    private const string OffCpuSessionKind = "off-cpu-snapshot";

    /// <summary>
    /// The subset of <see cref="CollectionQueryDispatcher.ViewsFor(string)"/> that the session
    /// <c>query</c> path can actually render for <paramref name="kind"/> — i.e. minus
    /// <see cref="SessionExcludedViews"/>. Used both to advertise valid views after a collect and to
    /// list them in the unknown-view error, so the two never drift.
    /// </summary>
    public static IReadOnlyList<string> SessionViewsFor(string kind)
    {
        if (kind == HeapInspectionUseCases.HeapSnapshotKind)
        {
            return HeapSnapshotQueryDispatcher.ProjectionViews;
        }

        if (CpuSampleSessionKinds.Contains(kind))
        {
            return CpuSampleQueryDispatcher.SessionViews;
        }

        if (kind == ThreadSnapshotSessionKind)
        {
            return ThreadSnapshotQueryDispatcher.SessionViews;
        }

        if (kind == OffCpuSessionKind)
        {
            return OffCpuQueryDispatcher.SessionViews;
        }

        if (kind == CollectionHandleKinds.EventCatalog)
        {
            return EventCatalogQueryDispatcher.SessionViews;
        }

        if (kind == CollectionHandleKinds.GcDatas)
        {
            return GcDatasQueryDispatcher.SessionViews;
        }

        var all = CollectionQueryDispatcher.ViewsFor(kind);
        var result = new List<string>(all.Count);
        foreach (var view in all)
        {
            if (!SessionExcludedViews.Contains(view))
            {
                result.Add(view);
            }
        }

        return result;
    }

    /// <summary>
    /// JSON used to pretty-print a <see cref="CollectionQueryResult.Payload"/> in the <c>session</c>
    /// REPL's human render so the user sees the drill-down data without re-typing <c>--json</c>.
    /// </summary>
    private static readonly JsonSerializerOptions QueryJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The <c>query</c> drill-down command <b>inside the stateful <c>session</c> REPL</b> (issue #300).
    /// Unlike the one-shot <see cref="Query()"/> (which returns <c>NotSupported</c> because no handle
    /// store survives the process), the REPL keeps the shared <see cref="IDiagnosticHandleStore"/>
    /// alive, so a handle published by an earlier <c>collect</c> can be re-rendered under a different
    /// view via <see cref="CollectionQueryDispatcher"/> — with no re-collection. Only the 10 collection
    /// kinds are supported here; heap/cpu/thread drill-down routing still lives in the MCP server
    /// (deferred to a follow-up PR) and yields a clear <c>NotSupportedInSession</c> envelope.
    /// </summary>
    public static CliCommandResult QuerySession(IServiceProvider services, CliOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Handle))
        {
            return Fail("query: --handle <id> is required.", "InvalidArgument",
                "Pass the handle printed after a collect command, e.g. query --handle <id> --view <view>.");
        }

        var store = services.GetRequiredService<IDiagnosticHandleStore>();
        var lookup = store.TryGetWithKind(options.Handle);
        if (lookup is null)
        {
            return Fail($"query: handle '{options.Handle}' is unknown or expired.", "NotFound",
                "Handles are evicted when they expire or when the target process exits. Re-run the originating collect command to get a fresh handle.");
        }

        var kind = lookup.Value.Kind;

        // Heap snapshot handles drill down through the host-neutral HeapSnapshotQueryDispatcher (#300):
        // the projection views render from the walked snapshot alone (no live ClrMD attach, no
        // sensitive-value redactor), which is exactly the subset a stateless session can serve. The
        // four capability-bound views (object/gcroot/objsize, duplicate-strings) stay server-only.
        if (kind == HeapInspectionUseCases.HeapSnapshotKind)
        {
            return QueryHeapSession(options, lookup.Value.Artifact);
        }

        // CPU / allocation / native-alloc sample handles drill down through the host-neutral
        // CpuSampleQueryDispatcher (#300): the merged call-tree renders from the collected trace alone.
        // The `diff` view stays server-only (it correlates a second baseline handle).
        if (CpuSampleSessionKinds.Contains(kind))
        {
            return QueryCpuSampleSession(options, lookup.Value.Artifact);
        }

        // Thread-snapshot handles drill down through the host-neutral ThreadSnapshotQueryDispatcher
        // (#300): every view (threads-summary, stack, lock-graph, deadlocks, top-blocked, unique-stacks,
        // async-stalls, threadpool) renders from the captured artifact alone — no live ClrMD attach.
        if (kind == ThreadSnapshotSessionKind)
        {
            return QueryThreadSnapshotSession(options, lookup.Value.Artifact);
        }

        // Off-CPU handles drill down through the host-neutral OffCpuQueryDispatcher (#300): topStacks,
        // byThread and stack all re-project the captured artifact — no perf re-run.
        if (kind == OffCpuSessionKind)
        {
            return QueryOffCpuSession(options, lookup.Value.Artifact);
        }

        if (kind == CollectionHandleKinds.EventCatalog)
        {
            return QueryEventCatalogSession(options, lookup.Value.Artifact);
        }

        // DATAS handles drill down through the host-neutral GcDatasQueryDispatcher (#315): overview,
        // tuning, samples and gen2 all re-project the captured snapshot — no EventPipe re-run.
        if (kind == CollectionHandleKinds.GcDatas)
        {
            return QueryGcDatasSession(options, lookup.Value.Artifact);
        }

        var allowedViews = CollectionQueryDispatcher.ViewsFor(kind);
        if (allowedViews.Count == 0)
        {
            return Fail($"query: drill-down for '{kind}' handles is not available in the session yet.", "NotSupportedInSession",
                "Heap / CPU / thread drill-down routing still lives in the MCP server; re-run the originating command (e.g. inspect-heap) with the inline flags you need.");
        }

        // Some dispatcher views correlate a second collected artifact (e.g. activities gc-overlay needs
        // a GC handle) that the session can't supply yet — reject them with a clear message instead of
        // letting the dispatcher fail with a confusing "missing correlate" InvalidArgument.
        if (!string.IsNullOrWhiteSpace(options.View) && SessionExcludedViews.Contains(options.View))
        {
            return Fail($"query: view '{options.View}' for a '{kind}' handle is not available in the session yet.", "NotSupportedInSession",
                "This view correlates two collected artifacts, which the session cannot supply yet; re-run the originating command with the inline flags you need.");
        }

        var topN = options.TopTypes ?? 50;
        var outcome = CollectionQueryDispatcher.Dispatch(kind, options.View, lookup.Value.Artifact, topN);

        if (outcome.Result is { } queryResult)
        {
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"query: {queryResult.Kind} view={queryResult.View} pid={queryResult.ProcessId}");
            var ok = DiagnosticResult.Ok(queryResult, summary);
            return BuildResult<CollectionQueryResult>(ok, static (sb, qr) =>
            {
                sb.AppendLine();
                sb.AppendLine(JsonSerializer.Serialize(qr.Payload, qr.Payload.GetType(), QueryJsonOptions));
            });
        }

        if (outcome.UnknownView is { } badView)
        {
            var sessionViews = SessionViewsFor(kind);
            var views = sessionViews.Count > 0 ? string.Join(", ", sessionViews) : string.Join(", ", allowedViews);
            return Fail($"query: unknown view '{badView}' for a '{kind}' handle.", "InvalidArgument",
                $"Valid views: {views}.");
        }

        if (outcome.InvalidArgument is { } invalid)
        {
            return Fail($"query: {invalid}.", "InvalidArgument",
                "Adjust the argument and retry, e.g. --top-types 20.");
        }

        // UnknownKind here means the stored artifact's runtime type did not match the handle kind.
        return Fail($"query: handle '{options.Handle}' could not be rendered as '{kind}'.", "InvalidArgument",
            "The stored artifact type did not match its handle kind; re-run the originating collect command.");
    }

    /// <summary>
    /// Renders a heap-snapshot drill-down inside the <c>session</c> REPL via the host-neutral
    /// <see cref="HeapSnapshotQueryDispatcher"/>. Projection views (<c>top-types</c>,
    /// <c>retention-paths</c>, …) render from the walked snapshot; the four server-only views
    /// (<c>object</c>/<c>gcroot</c>/<c>objsize</c> need a live attach, <c>duplicate-strings</c> needs
    /// the server's sensitive-value gate) yield a clear <c>NotSupportedInSession</c> envelope.
    /// </summary>
    private static CliCommandResult QueryHeapSession(CliOptions options, object artifact)
    {
        if (artifact is not HeapSnapshotArtifact heap)
        {
            return Fail($"query: handle '{options.Handle}' could not be rendered as a heap snapshot.", "InvalidArgument",
                "The stored artifact type did not match its handle kind; re-run inspect-heap to get a fresh handle.");
        }

        var view = string.IsNullOrWhiteSpace(options.View) ? "top-types" : options.View;
        var topN = options.TopTypes ?? 50;
        var outcome = HeapSnapshotQueryDispatcher.Dispatch(heap, options.Handle!, view, topN, options.RankBy, options.TypeFilter);

        if (outcome.Result is { } heapResult)
        {
            return BuildResult<HeapSnapshotQueryResult>(heapResult, static (sb, qr) =>
            {
                sb.AppendLine();
                sb.AppendLine(JsonSerializer.Serialize(qr, QueryJsonOptions));
            });
        }

        if (outcome.ServerOnlyView)
        {
            var normalized = view.Trim().ToLowerInvariant();
            var detail = normalized == "duplicate-strings"
                ? "The 'duplicate-strings' view exposes raw string previews behind the server's sensitive-value policy, which the standalone CLI cannot enforce; run the MCP server if you need it."
                : "The 'object', 'gcroot' and 'objsize' views require a live ClrMD attach the session does not hold; run the MCP server's query_heap_snapshot tool with an address if you need them.";
            return Fail($"query: view '{view}' for a heap snapshot is not available in the session yet.", "NotSupportedInSession", detail);
        }

        // outcome.UnknownView
        return Fail($"query: unknown view '{view}' for a heap snapshot.", "InvalidArgument",
            $"Valid views: {string.Join(", ", HeapSnapshotQueryDispatcher.ProjectionViews)}.");
    }

    /// <summary>
    /// Renders a CPU / allocation / native-alloc sample drill-down inside the <c>session</c> REPL via the
    /// host-neutral <see cref="CpuSampleQueryDispatcher"/>. Only the <c>call-tree</c> view is served; the
    /// <c>diff</c> view stays server-only (it correlates a baseline handle) and yields a clear
    /// <c>NotSupportedInSession</c> envelope.
    /// </summary>
    private static CliCommandResult QueryCpuSampleSession(CliOptions options, object artifact)
    {
        var trace = CpuSampleQueryDispatcher.ResolveTrace(artifact);
        if (trace is null)
        {
            return Fail($"query: handle '{options.Handle}' could not be rendered as a CPU/allocation sample.", "InvalidArgument",
                "The stored artifact type did not match its handle kind; re-run the originating collect command to get a fresh handle.");
        }

        var view = string.IsNullOrWhiteSpace(options.View) ? CpuSampleQueryDispatcher.CallTreeView : options.View;
        var normalized = view.Trim().ToLowerInvariant();

        if (normalized == "diff")
        {
            return Fail($"query: view '{view}' for a CPU sample handle is not available in the session yet.", "NotSupportedInSession",
                "The 'diff' view correlates a baseline handle the session cannot supply; run the MCP server's query_snapshot(view='diff') with a baselineHandle.");
        }

        var handle = options.Handle!;
        var topN = options.Top ?? CpuSampleQueryDispatcher.DefaultTopN;

        switch (normalized)
        {
            case CpuSampleQueryDispatcher.TopMethodsView:
                return BuildResult(CpuSampleQueryDispatcher.RenderTopMethods(trace, handle, options.RankBy, topN), SerializeQuery);
            case CpuSampleQueryDispatcher.ByModuleView:
                return BuildResult(CpuSampleQueryDispatcher.RenderByModule(trace, handle, topN), SerializeQuery);
            case CpuSampleQueryDispatcher.ByNamespaceView:
                return BuildResult(CpuSampleQueryDispatcher.RenderByNamespace(trace, handle, topN), SerializeQuery);
            case CpuSampleQueryDispatcher.HotPathView:
                return BuildResult(CpuSampleQueryDispatcher.RenderHotPath(trace, handle, options.Threshold ?? CpuSampleQueryDispatcher.DefaultHotPathThresholdPercent), SerializeQuery);
            case CpuSampleQueryDispatcher.CallerCalleeView:
                return BuildResult(CpuSampleQueryDispatcher.RenderCallerCallee(trace, handle, options.RootMethodFilter, topN), SerializeQuery);
            case CpuSampleQueryDispatcher.CallTreeView:
                break;
            default:
                return Fail($"query: unknown view '{view}' for a CPU sample handle.", "InvalidArgument",
                    $"Valid views: {string.Join(", ", CpuSampleQueryDispatcher.SessionViews)}.");
        }

        var maxDepth = options.MaxDepth ?? 8;
        var maxNodes = options.MaxNodes ?? 200;
        var result = CpuSampleQueryDispatcher.RenderCallTree(trace, handle, options.RootMethodFilter, maxDepth, maxNodes);

        return BuildResult<CallTreeView>(result, SerializeQuery);
    }

    private static void SerializeQuery<T>(StringBuilder sb, T payload)
    {
        sb.AppendLine();
        sb.AppendLine(JsonSerializer.Serialize(payload, QueryJsonOptions));
    }

    /// <summary>
    /// Renders an event-catalog drill-down inside the <c>session</c> REPL via the host-neutral
    /// <see cref="EventCatalogQueryDispatcher"/>. Occurrence samples are metadata-only; payload values
    /// are never captured by the catalog collector.
    /// </summary>
    private static CliCommandResult QueryEventCatalogSession(CliOptions options, object artifact)
    {
        if (artifact is not EventCatalogSnapshot snapshot)
        {
            return Fail($"query: handle '{options.Handle}' could not be rendered as an event catalog.", "InvalidArgument",
                "The stored artifact type did not match its handle kind; re-run collect --kind catalog to get a fresh handle.");
        }

        var view = string.IsNullOrWhiteSpace(options.View) ? EventCatalogQueryDispatcher.CatalogView : options.View;
        if (!EventCatalogQueryDispatcher.IsKnownView(view))
        {
            return Fail($"query: unknown view '{view}' for an event-catalog handle.", "InvalidArgument",
                $"Valid views: {string.Join(", ", EventCatalogQueryDispatcher.SessionViews)}.");
        }

        var topN = options.Top ?? options.TopTypes ?? EventCatalogQueryDispatcher.DefaultTopN;
        var result = EventCatalogQueryDispatcher.Render(
            snapshot,
            options.Handle!,
            view,
            topN,
            options.ProviderFilter,
            options.RootMethodFilter);

        return BuildResult<object>(result, SerializeQuery);
    }

    /// <summary>
    /// Renders a DATAS drill-down inside the <c>session</c> REPL via the host-neutral
    /// <see cref="GcDatasQueryDispatcher"/>. The overview, tuning, samples and gen2 views all render
    /// from the captured snapshot alone — no EventPipe re-run. The <c>tuning</c> view honours
    /// <c>--changes-only</c>.
    /// </summary>
    private static CliCommandResult QueryGcDatasSession(CliOptions options, object artifact)
    {
        if (artifact is not GcDatasSnapshot snapshot)
        {
            return Fail($"query: handle '{options.Handle}' could not be rendered as a DATAS snapshot.", "InvalidArgument",
                "The stored artifact type did not match its handle kind; re-run collect --kind datas to get a fresh handle.");
        }

        var view = string.IsNullOrWhiteSpace(options.View) ? GcDatasQueryDispatcher.OverviewView : options.View;
        if (!GcDatasQueryDispatcher.IsKnownView(view))
        {
            return Fail($"query: unknown view '{view}' for a DATAS handle.", "InvalidArgument",
                $"Valid views: {string.Join(", ", GcDatasQueryDispatcher.SessionViews)}.");
        }

        var topN = options.Top ?? options.TopTypes ?? GcDatasQueryDispatcher.DefaultTopN;
        var result = GcDatasQueryDispatcher.Render(
            snapshot,
            options.Handle!,
            view,
            topN,
            options.ChangesOnly);

        return BuildResult<object>(result, SerializeQuery);
    }

    /// <summary>
    /// Renders a thread-snapshot drill-down inside the <c>session</c> REPL via the host-neutral
    /// <see cref="ThreadSnapshotQueryDispatcher"/>. All eight views render from the captured artifact —
    /// the dispatcher returns a clear <c>InvalidArgument</c> (listing valid views) for an unknown view,
    /// which this helper surfaces directly.
    /// </summary>
    private static CliCommandResult QueryThreadSnapshotSession(CliOptions options, object artifact)
    {
        if (artifact is not ThreadSnapshotArtifact snapshot)
        {
            return Fail($"query: handle '{options.Handle}' could not be rendered as a thread snapshot.", "InvalidArgument",
                "The stored artifact type did not match its handle kind; re-run the originating collect command to get a fresh handle.");
        }

        var view = string.IsNullOrWhiteSpace(options.View) ? "top-blocked" : options.View;
        var topN = options.TopTypes ?? 50;
        var framesToHash = options.FramesToHash ?? 20;
        var minCount = options.MinCount ?? 1;
        var result = ThreadSnapshotQueryDispatcher.Dispatch(
            snapshot, options.Handle!, view, options.ThreadId, topN, framesToHash, minCount);

        return BuildResult<ThreadSnapshotQueryResult>(result, static (sb, qr) =>
        {
            sb.AppendLine();
            sb.AppendLine(JsonSerializer.Serialize(qr, QueryJsonOptions));
        });
    }

    /// <summary>
    /// Renders an off-CPU drill-down inside the <c>session</c> REPL via the host-neutral
    /// <see cref="OffCpuQueryDispatcher"/>. Because the server's original switch silently treats an
    /// unknown view as <c>topStacks</c>, this helper validates the view name up front and returns a
    /// clear <c>InvalidArgument</c> for the CLI operator (a host-specific UX choice; the shared
    /// dispatcher keeps the server's fall-through behavior).
    /// </summary>
    private static CliCommandResult QueryOffCpuSession(CliOptions options, object artifact)
    {
        if (artifact is not OffCpuSnapshotArtifact snapshot)
        {
            return Fail($"query: handle '{options.Handle}' could not be rendered as an off-CPU snapshot.", "InvalidArgument",
                "The stored artifact type did not match its handle kind; re-run the originating collect command to get a fresh handle.");
        }

        var view = string.IsNullOrWhiteSpace(options.View) ? "topStacks" : options.View;
        var normalized = view.Trim().ToLowerInvariant();
        if (normalized is not ("topstacks" or "bythread" or "stack"))
        {
            return Fail($"query: unknown view '{view}' for an off-CPU snapshot.", "InvalidArgument",
                $"Valid views: {string.Join(", ", OffCpuQueryDispatcher.SessionViews)}.");
        }

        var topN = options.TopTypes ?? 25;
        var result = OffCpuQueryDispatcher.Dispatch(snapshot, view, topN, options.StackRank);

        return BuildResult<OffCpuQueryView>(result, static (sb, qr) =>
        {
            sb.AppendLine();
            sb.AppendLine(JsonSerializer.Serialize(qr, QueryJsonOptions));
        });
    }

    private static CliCommandResult Fail(string summary, string errorKind, string detail)
    {
        var result = DiagnosticResult.Fail<object>(
            summary,
            new DiagnosticError(errorKind, summary, detail));
        return BuildResult<object>(result, static (_, _) => { });
    }

    private static async Task<CliCommandResult> GetBytesAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        // Validated before the host was built; --out is guaranteed present and is the destination file.
        var outputPath = options.OutDir!;
        // The CLI runs as the local operator and carries no bearer principal; grant the literal
        // 'module-bytes-read' scope the same way the stdio root accessor does for the MCP server.
        const bool principalAllowsLiteralScope = true;
        // Use the largest chunk the readers permit so a big artifact takes as few re-attaches as possible.
        var maxBytes = FileChunkReader.MaxChunkBytes;

        if (options.Kind == "dump")
        {
            var dumpSource = services.GetRequiredService<IDumpByteSource>();
            // CliHost pointed the artifact root at the dump file's directory, so a relative file name
            // resolves under it (SafeArtifactPath rejects anything outside the root).
            var dumpResult = await ByteMaterializationUseCases.MaterializeDumpBytes(
                dumpSource, principalAllowsLiteralScope,
                Path.GetFileName(options.DumpFile!), outputPath, maxBytes,
                logger: null, principalName: null, cancellationToken).ConfigureAwait(false);
            return WrapMaterialization(dumpResult);
        }

        var moduleSource = services.GetRequiredService<IModuleByteSource>();
        var resolver = services.GetRequiredService<IProcessContextResolver>();
        var moduleResult = await ByteMaterializationUseCases.MaterializeModuleBytes(
            moduleSource, resolver, principalAllowsLiteralScope,
            options.Mvid!, options.Asset ?? "pe", options.Pid, outputPath, maxBytes,
            logger: null, principalName: null, cancellationToken).ConfigureAwait(false);
        return WrapMaterialization(moduleResult);
    }

    private static CliCommandResult WrapMaterialization(DiagnosticResult<ByteMaterialization> result)
    {
        return BuildResult<ByteMaterialization>(result, static (sb, data) =>
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"  asset  : {data.Asset}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  source : {data.SourcePath}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  output : {data.OutputPath}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  size   : {data.TotalBytes:N0} bytes");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  sha256 : {data.Sha256}");
            if (!string.IsNullOrWhiteSpace(data.CompanionPdbPath))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  pdb    : {data.CompanionPdbPath}");
            }
        });
    }

    internal static string RenderJourneyDiff(SnapshotJourneyDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var sb = new StringBuilder();
        var first = diff.Labels.Count > 0 ? diff.Labels[0] : "first";
        var last = diff.Labels.Count > 0 ? diff.Labels[^1] : "last";
        sb.AppendLine(CultureInfo.InvariantCulture, $"compare: {diff.Kind} {diff.Mode} {first}→{last} verdict={diff.Verdict}");
        if (diff.Pairwise?.Headline is { } headline)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  headline: {headline.Relation} {headline.Verdict}");
        }

        AppendMetricDeltas(sb, diff.MetricSeries, diff.Mode, diff.Labels);
        AppendKeyDeltas(sb, diff.KeyMatrix, diff.Mode, diff.Labels);

        if (diff.Notes.Count > 0)
        {
            sb.AppendLine("  notes:");
            foreach (var note in diff.Notes.Take(3))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    - {note}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendMetricDeltas(StringBuilder sb, IReadOnlyList<MetricSeries> series, JourneyMode mode, IReadOnlyList<string> labels)
    {
        var rows = mode == JourneyMode.Dispersion
            ? series
                .Where(s => s.Dispersion is not null)
                .OrderByDescending(s => s.Dispersion!.CoefficientOfVariation)
                .ThenBy(s => s.Definition.Name, StringComparer.Ordinal)
                .Take(5)
                .ToArray()
            : series
                .Where(s => s.DeltaAbs.HasValue || s.DeltaPct.HasValue)
                .OrderByDescending(s => Math.Abs(s.DeltaPct ?? 0))
                .ThenBy(s => s.Definition.Name, StringComparer.Ordinal)
                .Take(5)
                .ToArray();
        if (rows.Length == 0)
        {
            return;
        }

        sb.AppendLine("  metrics:");
        foreach (var row in rows)
        {
            if (mode == JourneyMode.Dispersion && row.Dispersion is { } dispersion)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"    - {row.Definition.Name}: cv {FormatNumber(dispersion.CoefficientOfVariation)} outlier {LabelAt(labels, dispersion.OutlierIndex)} values [{FormatValues(row.Values)}]");
                continue;
            }

            var first = FirstValue(row.Values);
            var last = LastValue(row.Values);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"    - {row.Definition.Name}: {FormatNumber(first)} → {FormatNumber(last)} (Δ {FormatSigned(row.DeltaAbs)}, {FormatSignedPercent(row.DeltaPct)}, {row.Direction}, trend {row.Trend})");
        }
    }

    private static void AppendKeyDeltas(StringBuilder sb, IReadOnlyList<KeyMatrixRow> rows, JourneyMode mode, IReadOnlyList<string> labels)
    {
        var top = mode == JourneyMode.Dispersion
            ? rows
                .Where(r => r.Dispersion is not null)
                .OrderByDescending(r => r.Dispersion!.CoefficientOfVariation)
                .ThenBy(r => r.DisplayName, StringComparer.Ordinal)
                .Take(5)
                .Select(r => (Row: r, Stats: (Cv: r.Dispersion!.CoefficientOfVariation, r.Dispersion.OutlierIndex)))
                .ToArray()
            : rows
                .Where(r => r.DeltaAbs.HasValue || r.DeltaPct.HasValue)
                .OrderByDescending(r => Math.Abs(r.DeltaPct ?? 0))
                .ThenBy(r => r.DisplayName, StringComparer.Ordinal)
                .Take(5)
                .Select(r => (Row: r, Stats: (Cv: -1.0, OutlierIndex: -1)))
                .ToArray();
        if (top.Length == 0)
        {
            return;
        }

        sb.AppendLine("  keys:");
        foreach (var item in top)
        {
            var row = item.Row;
            if (mode == JourneyMode.Dispersion)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"    - {row.DisplayName}: cv {FormatNumber(item.Stats.Cv)} outlier {LabelAt(labels, item.Stats.OutlierIndex)} values [{FormatValues(row.Values)}]");
                continue;
            }

            sb.AppendLine(CultureInfo.InvariantCulture,
                $"    - {row.DisplayName}: {FormatNumber(FirstValue(row.Values))} → {FormatNumber(LastValue(row.Values))} (Δ {FormatSigned(row.DeltaAbs)}, {FormatSignedPercent(row.DeltaPct)}, {row.Direction})");
        }
    }

    private static string FormatValues(IReadOnlyList<double?> values)
        => string.Join(", ", values.Select(FormatNumber));

    private static string LabelAt(IReadOnlyList<string> labels, int index)
        => index < 0 ? "none" : index < labels.Count ? labels[index] : index.ToString(CultureInfo.InvariantCulture);

    private static double? FirstValue(IReadOnlyList<double?> values) => values.Count == 0 ? null : values[0];

    private static double? LastValue(IReadOnlyList<double?> values) => values.Count == 0 ? null : values[^1];

    private static string FormatNumber(double? value) => value?.ToString("G4", CultureInfo.InvariantCulture) ?? "n/a";

    private static string FormatSigned(double? value) => value.HasValue
        ? value.Value.ToString("+0.####;-0.####;0", CultureInfo.InvariantCulture)
        : "n/a";

    private static string FormatSignedPercent(double? value) => value.HasValue
        ? string.Concat(value.Value.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture), "%")
        : "n/a";

    internal static void RenderTopTypes(StringBuilder sb, IReadOnlyList<TypeStat> topByBytes)
    {
        if (topByBytes.Count == 0)
        {
            return;
        }

        // Types whose identity (mvid + metadata token) is known get a short numeric handle in the ID
        // column and a line in the identities block, so a human can copy the GUID straight into
        // `get-bytes --kind module --mvid <guid>` without dropping to --json (#301 #3).
        var identities = new List<(int Id, Guid Mvid, int? Token)>();
        var nextId = 1;

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"  {"BYTES%",-8} {"INSTANCES",-14} {"ID",-4} TYPE");
        foreach (var t in topByBytes)
        {
            var idColumn = string.Empty;
            if (t.Identity is { ModuleVersionId: { } mvid })
            {
                var id = nextId++;
                idColumn = id.ToString(CultureInfo.InvariantCulture);
                identities.Add((id, mvid, t.Identity.MetadataToken));
            }

            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {t.TotalBytesPercent,-8} {t.InstanceCount,-14:N0} {idColumn,-4} {t.TypeFullName}");
        }

        if (identities.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  identities (for get-bytes --kind module --mvid <guid>):");
            foreach (var (id, mvid, token) in identities)
            {
                var tokenText = token is { } tk ? string.Create(CultureInfo.InvariantCulture, $"0x{tk:X8}") : "(none)";
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {id}: mvid={mvid} token={tokenText}");
            }
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

    internal static bool TrySaveComparableSnapshot(object artifact, string savePath, out ComparableSnapshot? snapshot, out string? error)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(savePath);

        snapshot = null;
        error = null;
        var projector = ComparableProjectors.FirstOrDefault(p => p.CanProject(artifact));
        if (projector is null)
        {
            error = $"kind '{InferComparableKind(artifact)}' is not yet comparable (--save supports: {SupportedComparableKinds})";
            return false;
        }

        var label = Path.GetFileNameWithoutExtension(savePath);
        if (string.IsNullOrWhiteSpace(label))
        {
            label = "capture";
        }

        snapshot = projector.Project(artifact, label);
        try
        {
            var fullPath = Path.GetFullPath(savePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = File.Create(fullPath);
            JsonSerializer.Serialize(stream, snapshot, ComparableSnapshotJsonContext.Default.ComparableSnapshot);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            error = $"failed to write comparable snapshot to '{savePath}': {ex.Message}";
            snapshot = null;
            return false;
        }
    }

    private static string InferComparableKind(object artifact) => artifact switch
    {
        CounterSnapshot => CollectionHandleKinds.Counters,
        GcDatasSnapshot => CollectionHandleKinds.GcDatas,
        ExceptionSnapshot => CollectionHandleKinds.ExceptionSnapshot,
        GcSummary => CollectionHandleKinds.GcEvents,
        EventSourceCapture => CollectionHandleKinds.EventSource,
        EventCatalogSnapshot => CollectionHandleKinds.EventCatalog,
        ActivityCapture => CollectionHandleKinds.Activities,
        LogSnapshot => CollectionHandleKinds.LogSnapshot,
        JitSnapshot => CollectionHandleKinds.JitSnapshot,
        ThreadPoolEventSnapshot => CollectionHandleKinds.ThreadPoolSnapshot,
        ContentionSnapshot => CollectionHandleKinds.ContentionSnapshot,
        DbSnapshot => CollectionHandleKinds.DbSnapshot,
        _ => artifact.GetType().Name,
    };

    private static CliCommandResult BuildResultWithComparableSave<T>(
        CliOptions options,
        DiagnosticResult<T> result,
        Action<StringBuilder, T> renderData)
    {
        if (result is { IsError: false, Data: { } data } && !string.IsNullOrWhiteSpace(options.SavePath))
        {
            if (!TrySaveComparableSnapshot(data, options.SavePath, out var saved, out var error))
            {
                var failure = DiagnosticResult.Fail<object>(
                    error!,
                    new DiagnosticError("NotSupported", "Choose a comparable collection kind and re-run collect with --save."));
                return BuildResult<object>(failure, static (_, _) => { });
            }

            var built = BuildResult(result, renderData);
            return built with
            {
                Human = string.Concat(
                    built.Human,
                    Environment.NewLine,
                    string.Create(CultureInfo.InvariantCulture, $"  saved comparable snapshot: {saved!.Label} -> {options.SavePath}")),
            };
        }

        return BuildResult(result, renderData);
    }

    /// <summary>
    /// Renders the host-neutral parts of any <see cref="DiagnosticResult{T}"/> (summary, error,
    /// resolved-process digest, next-action hints) plus a command-specific data block supplied by
    /// <paramref name="renderData"/> (skipped on error / null payload).
    /// </summary>
    private static CliCommandResult BuildResult<T>(DiagnosticResult<T> result, Action<StringBuilder, T> renderData)
    {
        // Project Core's MCP-audience hints into CLI vocabulary ONCE, before both the human table and
        // the --json envelope are produced, so neither leaks MCP tool names / call syntax (#301).
        var projected = CliHintProjection.Project(result);
        var human = RenderEnvelope(projected, renderData);
        return new CliCommandResult(projected.IsError, projected.Cancelled, projected, human)
        {
            Handle = projected.Handle,
            HandleExpiresAt = projected.HandleExpiresAt,
        };
    }

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
