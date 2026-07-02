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
using DotnetDiagnostics.Core.GatedCapture;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.Kestrel;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.Preflight;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Startup;
using DotnetDiagnostics.Core.Triage;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Core.Investigation;
using DotnetDiagnostics.Core.Memory;
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
        "doctor",
        "collect",
        "inspect",
        "inspect-heap",
        "dump",
        "query",
        "get-bytes",
        "compare",
        "investigate",
        "export-summary",
        "session",
        "completion",
    };

    /// <summary>Heap-snapshot sources accepted by the <c>inspect-heap</c> command (issue #288 PR3b).</summary>
    public static readonly IReadOnlyList<string> HeapSources = new[] { "live", "dump", "gcdump" };

    /// <summary>Views accepted by the <c>inspect</c> command (issue #486).</summary>
    public static readonly IReadOnlyList<string> InspectViews = new[] { "triage", "runtime-config" };

    /// <summary>Artifact kinds accepted by the <c>get-bytes</c> command (issue #288 PR4).</summary>
    public static readonly IReadOnlyList<string> ByteKinds = new[] { "module", "dump", "trace" };

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
        "inspect",
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
        "crash-guard",
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
        "requests",
        "startup",
        "sweep",
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
            "doctor" => Doctor(services, options),
            "collect" => await CollectAsync(services, options, cancellationToken).ConfigureAwait(false),
            "inspect" => await InspectAsync(services, options, cancellationToken).ConfigureAwait(false),
            "inspect-heap" => await InspectHeapAsync(services, options, cancellationToken).ConfigureAwait(false),
            "dump" => await DumpAsync(services, options, cancellationToken).ConfigureAwait(false),
            "query" => Query(),
            "get-bytes" => await GetBytesAsync(services, options, cancellationToken).ConfigureAwait(false),
            "compare" => await CompareAsync(options, cancellationToken).ConfigureAwait(false),
            "investigate" => await InvestigateAsync(services, options, cancellationToken).ConfigureAwait(false),
            "export-summary" => await ExportSummaryAsync(services, options, cancellationToken).ConfigureAwait(false),
            "completion" => Completion(options),
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
        if (!TryValidateWatch(options, out error))
        {
            return false;
        }

        return options.Command switch
        {
            "collect" => TryValidateCollect(options, out error),
            "inspect" => TryValidateInspect(options, out error),
            "inspect-heap" => TryValidateInspectHeap(options, out error),
            "dump" => TryValidateDump(options, out error),
            "get-bytes" => TryValidateGetBytes(options, out error),
            "compare" => TryValidateCompare(options, out error),
            "investigate" => TryValidateInvestigate(options, out error),
            "export-summary" => TryValidateExportSummary(options, out error),
            "completion" => TryValidateCompletion(options, out error),
            _ => true,
        };
    }

    public static bool TryValidateWatch(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;
        if (options.WatchIntervalSeconds is not { } interval)
        {
            return true;
        }

        if (interval <= 0)
        {
            error = "--watch expects a positive interval in seconds.";
            return false;
        }

        // In threshold-gated capture mode --watch is the metric sample interval (a single bounded
        // run), not the human redraw loop — so the redraw-specific restrictions don't apply.
        if (options.CaptureWhen is not null)
        {
            return true;
        }

        if (options.Json)
        {
            error = "--watch cannot be combined with --json because watch redraws human output.";
            return false;
        }

        if (string.Equals(options.Command, "session", StringComparison.Ordinal)
            || string.Equals(options.Command, "completion", StringComparison.Ordinal))
        {
            error = $"--watch is not supported by '{options.Command}'.";
            return false;
        }

        return true;
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

        // Threshold-gated capture (#419): --capture-when / --capture / --window form one bounded
        // watch and must be supplied together with kind=counters. Deep validation (predicate parse,
        // ranges) happens in the use case so the error surfaces with recovery hints.
        var gated = options.CaptureWhen is not null || options.CaptureKind is not null || options.WindowSeconds is not null;
        if (gated)
        {
            if (options.Kind != "counters")
            {
                error = "Threshold-gated capture (--capture-when) requires --kind counters.";
                return false;
            }

            if (options.CaptureWhen is null)
            {
                error = "--capture requires --capture-when <predicate> (e.g. --capture-when 'cpu>85').";
                return false;
            }

            if (options.CaptureKind is null)
            {
                error = "--capture-when requires --capture <dump|cpu-sample|heap|thread-snapshot>.";
                return false;
            }

            if (options.WindowSeconds is null)
            {
                error = "Threshold-gated capture requires --window <seconds> (the watch is bounded).";
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.NativeAotMapFile))
        {
            if (!string.Equals(options.CaptureKind, "cpu-sample", StringComparison.Ordinal))
            {
                error = options.CaptureKind is null
                    ? "--native-aot-map requires '--capture cpu-sample'."
                    : $"--native-aot-map is not supported by '--capture {options.CaptureKind}'. It is only valid with '--capture cpu-sample'.";
                return false;
            }

            if (!File.Exists(options.NativeAotMapFile))
            {
                error = $"--native-aot-map: file '{options.NativeAotMapFile}' does not exist.";
                return false;
            }
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
            error = "inspect-heap accepts a single --source (live, dump or gcdump).";
            return false;
        }

        if (options.Sources.Count == 1)
        {
            source = options.Sources[0];
            if (!HeapSources.Contains(source, StringComparer.Ordinal))
            {
                error = $"Unknown --source '{source}'. Valid values: live, dump, gcdump.";
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

            if (options.HasPid)
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

            if (options.SuspendStartup)
            {
                error = "--suspend-startup requires --launch (cold-start capture launches the target suspended).";
                return false;
            }

            return true;
        }

        if (options.LaunchArgs.Count == 0)
        {
            error = "--launch requires a program after '--', e.g. --launch -- dotnet App.dll.";
            return false;
        }

        if (options.HasPid)
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

        if (options.SuspendStartup
            && !(options.Command == "collect" && options.Kind == "startup"))
        {
            error = "--suspend-startup applies only to 'collect --kind startup'.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates the <c>inspect</c>-specific options before the host is built. Returns
    /// <c>true</c> when well-formed; otherwise sets <paramref name="error"/>.
    /// </summary>
    public static bool TryValidateInspect(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;

        if (string.IsNullOrWhiteSpace(options.View))
        {
            error = $"The 'inspect' command requires --view <view>. Valid views: {string.Join(", ", InspectViews)}.";
            return false;
        }

        if (!InspectViews.Contains(options.View, StringComparer.Ordinal))
        {
            error = $"Unknown --view '{options.View}'. Valid views: {string.Join(", ", InspectViews)}.";
            return false;
        }

        if (options.View == "triage" && options.DurationSeconds is < 1)
        {
            error = "--duration must be >= 1 for 'inspect --view triage'.";
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
        else if (options.Kind == "trace")
        {
            if (string.IsNullOrWhiteSpace(options.DumpFile))
            {
                error = "get-bytes --kind trace requires --dump-file <path> (the exported .nettrace).";
                return false;
            }

            if (options.HasPid)
            {
                error = "get-bytes --kind trace does not accept --pid (the trace is offline).";
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

            if (options.HasPid)
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

    public static bool TryValidateInvestigate(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;

        if (options.MaxToolCalls is < 1)
        {
            error = "--max-tool-calls must be >= 1.";
            return false;
        }

        // Cold mode (no --hypothesis) has nothing to anchor the plan on without a stated symptom;
        // the planner would silently default to a generic route. Require one so the plan is meaningful.
        if (string.IsNullOrWhiteSpace(options.Hypothesis) && string.IsNullOrWhiteSpace(options.Symptom))
        {
            error = "The 'investigate' command requires --symptom <text> (or --hypothesis <text>) so the plan can be anchored to what you observed.";
            return false;
        }

        return true;
    }

    public static bool TryValidateExportSummary(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;

        if (string.IsNullOrWhiteSpace(options.Handle))
        {
            error = "The 'export-summary' command requires --handle <id> (a CPU-sample handle from 'collect --kind cpu').";
            return false;
        }

        if (options.TopHotspots is < 1)
        {
            error = "--top-hotspots must be >= 1.";
            return false;
        }

        return true;
    }

    public static bool TryValidateCompletion(CliOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = null;

        if (string.IsNullOrWhiteSpace(options.CompletionShell))
        {
            error = $"The 'completion' command requires a shell argument. Valid shells: {string.Join(", ", CliCompletionScripts.Shells)}.";
            return false;
        }

        if (!CliCompletionScripts.Shells.Contains(options.CompletionShell, StringComparer.Ordinal))
        {
            error = $"Unknown completion shell '{options.CompletionShell}'. Valid shells: {string.Join(", ", CliCompletionScripts.Shells)}.";
            return false;
        }

        return true;
    }

    private static CliCommandResult Completion(CliOptions options)
    {
        if (!TryValidateCompletion(options, out var error))
        {
            throw new ArgumentException(error, nameof(options));
        }

        var script = CliCompletionScripts.ForShell(options.CompletionShell!);
        return new CliCommandResult(false, false, new { shell = options.CompletionShell, script }, script);
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

    /// <summary>
    /// Issue #486 — workload classifier (<c>--view triage</c>) and runtime configuration reader
    /// (<c>--view runtime-config</c>). Both views require a live target and use Core services only.
    /// </summary>
    private static async Task<CliCommandResult> InspectAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        return options.View switch
        {
            "triage" => await InspectTriageAsync(services, options, cancellationToken).ConfigureAwait(false),
            "runtime-config" => await InspectRuntimeConfigAsync(services, options, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown inspect view '{options.View}'.", nameof(options)),
        };
    }

    private static async Task<CliCommandResult> InspectTriageAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var collector = services.GetRequiredService<ICounterCollector>();
        var resolver = services.GetRequiredService<IProcessContextResolver>();
        var duration = options.DurationSeconds ?? 5;

        var resolved = await ProcessResolutionHelpers
            .ResolveContextAsync<TriageResult>(resolver, options.Pid, cancellationToken)
            .ConfigureAwait(false);
        if (resolved.Failure is not null)
        {
            return BuildResult(resolved.Failure, static (_, _) => { });
        }

        var pid = resolved.ProcessId;

        var snapshot = await collector.CollectAsync(
            pid,
            TimeSpan.FromSeconds(duration),
            providers: null,
            meters: ["Microsoft.AspNetCore.Hosting"],
            intervalSeconds: 1,
            maxInstrumentTimeSeries: 100,
            cancellationToken).ConfigureAwait(false);

        var requestDuration = HeadlineCounters.FindRequestDuration(snapshot.Meters);
        var requestDurationP95 = requestDuration?.Histogram?.P95;

        var triage = TriageClassifier.Classify(snapshot, requestDurationP95);

        var secondaryText = triage.SecondaryVerdicts?.Count > 0
            ? $" (also: {string.Join(", ", triage.SecondaryVerdicts)})"
            : string.Empty;
        var indicatorsText = triage.TopIndicators?.Count > 0
            ? $" | top: {string.Join(", ", triage.TopIndicators.Take(3).Select(i => $"{i.Name}={i.Value}{i.Unit ?? string.Empty}({i.Level})"))}"
            : string.Empty;
        var summary = $"Triage: {triage.Verdict} ({triage.Severity}){secondaryText}{indicatorsText}";

        var hints = BuildCliTriageHints(triage, pid);
        var ok = ProcessResolutionHelpers.WithContext(DiagnosticResult.Ok(triage, summary, [.. hints]), resolved.Context);
        return BuildResult(ok, static (sb, t) =>
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Verdict   : {t.Verdict}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Severity  : {t.Severity}");
            if (t.SecondaryVerdicts?.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Also      : {string.Join(", ", t.SecondaryVerdicts)}");
            }

            if (t.TopIndicators?.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Indicators:");
                foreach (var ind in t.TopIndicators)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture,
                        $"    {ind.Name,-38} {ind.Value,8:F2} {ind.Unit ?? string.Empty,-12} [{ind.Level}]");
                }
            }
        });
    }

    private static async Task<CliCommandResult> InspectRuntimeConfigAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var inspector = services.GetRequiredService<IRuntimeConfigInspector>();
        var resolver = services.GetRequiredService<IProcessContextResolver>();

        var resolved = await ProcessResolutionHelpers
            .ResolveContextAsync<RuntimeConfigView>(resolver, options.Pid, cancellationToken)
            .ConfigureAwait(false);
        if (resolved.Failure is not null)
        {
            return BuildResult(resolved.Failure, static (_, _) => { });
        }

        var config = await inspector.InspectAsync(resolved.ProcessId, cancellationToken).ConfigureAwait(false);

        var summary = BuildRuntimeConfigSummary(config);
        var hints = BuildCliRuntimeConfigHints(config);
        var ok = ProcessResolutionHelpers.WithContext(DiagnosticResult.Ok(config, summary, [.. hints]), resolved.Context);
        return BuildResult(ok, static (sb, cfg) =>
        {
            sb.AppendLine();
            if (cfg.Gc is { } gc)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  GC server={gc.IsServerGc}  concurrent={gc.IsConcurrent?.ToString() ?? "?"}  background={gc.IsBackground?.ToString() ?? "?"}  heaps={gc.HeapCount}");
                if (gc.LargeObjectHeapCompactionMode is not null)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  LOH compaction={gc.LargeObjectHeapCompactionMode}");
                }
            }

            if (cfg.ThreadPool is { } tp)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  ThreadPool worker={FormatNullableRange(tp.MinWorkerThreads, tp.MaxWorkerThreads)}  iocp={FormatNullableRange(tp.MinIocpThreads, tp.MaxIocpThreads)}  hill-climbing={tp.HillClimbingEnabled?.ToString() ?? "?"}");
            }

            if (cfg.TieredCompilation is { } tc)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  TieredCompilation enabled={tc.Enabled?.ToString() ?? "?"}  quick-jit={tc.QuickJitEnabled?.ToString() ?? "?"}  pgo={tc.DynamicPgoEnabled?.ToString() ?? "?"}");
            }

            if (cfg.AppContextSwitches.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  AppContext switches ({cfg.AppContextSwitches.Count}):");
                foreach (var sw in cfg.AppContextSwitches)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {sw.Name} = {sw.Value ?? "<set>"}");
                }
            }

            if (cfg.EnvVars.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Runtime env vars ({cfg.EnvVars.Count}):");
                foreach (var ev in cfg.EnvVars)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {ev.Name} = {ev.Value}");
                }
            }

            if (cfg.Notes.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Notes:");
                foreach (var note in cfg.Notes)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {note}");
                }
            }
        });
    }

    private static string BuildRuntimeConfigSummary(RuntimeConfigView cfg)
    {
        var parts = new List<string>();
        if (cfg.Gc is { } gc)
        {
            parts.Add($"GC server={gc.IsServerGc}, heaps={gc.HeapCount}");
        }

        if (cfg.ThreadPool is { } tp)
        {
            parts.Add($"ThreadPool worker={FormatNullableRange(tp.MinWorkerThreads, tp.MaxWorkerThreads)}");
        }

        parts.Add($"env={cfg.EnvVars.Count}");
        parts.Add($"switches={cfg.AppContextSwitches.Count}");
        return $"Process {cfg.ProcessId} runtime-config: {string.Join("; ", parts)}.";
    }

    private static string FormatNullableRange(int? min, int? max)
    {
        var minStr = min.HasValue ? min.Value.ToString(CultureInfo.InvariantCulture) : "?";
        var maxStr = max.HasValue ? max.Value.ToString(CultureInfo.InvariantCulture) : "?";
        return $"{minStr}/{maxStr}";
    }

    private static List<NextActionHint> BuildCliTriageHints(TriageResult triage, int pid)    {
        var hints = new List<NextActionHint>();
        switch (triage.Verdict)
        {
            case TriageClassifier.CpuBound:
                hints.Add(new NextActionHint("collect", $"cpu-usage={triage.Evidence.CpuUsage:F1}% — capture a CPU sample to find the hot path: collect --kind counters --pid {pid} --duration 10"));
                break;
            case TriageClassifier.GcPressure:
                hints.Add(new NextActionHint("collect", $"time-in-gc={triage.Evidence.TimeInGc:F1}% — collect GC events: collect --kind gc --pid {pid} --duration 10"));
                hints.Add(new NextActionHint("inspect-heap", $"GC pressure — inspect the heap for allocation patterns: inspect-heap --pid {pid}"));
                break;
            case TriageClassifier.MemoryPressure:
                if ((triage.Evidence.AllocRate ?? 0) >= 50_000_000)
                {
                    hints.Add(new NextActionHint("collect", $"alloc-rate={triage.Evidence.AllocRate / 1_000_000:F0} MB/s — collect counters to track allocation: collect --kind counters --pid {pid} --duration 10"));
                }

                hints.Add(new NextActionHint("inspect-heap", $"Memory pressure (gen-2={triage.Evidence.Gen2GcCount:F0}) — inspect the live heap: inspect-heap --pid {pid}"));
                break;
            case TriageClassifier.ThreadPoolStarvation:
                hints.Add(new NextActionHint("collect", $"threadpool-queue-length={triage.Evidence.ThreadPoolQueueLength:F0} — collect ThreadPool events: collect --kind threadpool --pid {pid} --duration 10"));
                break;
            case TriageClassifier.LockContention:
                hints.Add(new NextActionHint("collect", $"monitor-lock-contention={triage.Evidence.MonitorLockContentionCount:F0} — collect contention events: collect --kind contention --pid {pid} --duration 10"));
                break;
            case TriageClassifier.IoBound:
                hints.Add(new NextActionHint("collect", $"cpu={triage.Evidence.CpuUsage:F1}% but queue={triage.Evidence.ThreadPoolQueueLength:F0} — trace activities to see what is waiting: collect --kind activities --pid {pid} --duration 10"));
                break;
            default:
                hints.Add(new NextActionHint("collect", $"System looks healthy — confirm with GC events if response times are high: collect --kind gc --pid {pid} --duration 10"));
                break;
        }

        return hints;
    }

    private static List<NextActionHint> BuildCliRuntimeConfigHints(RuntimeConfigView cfg)
    {
        if (cfg.ThreadPool is { HillClimbingEnabled: false })
        {
            return
            [
                new NextActionHint(
                    "collect",
                    $"ThreadPool hill-climbing is disabled; capture threadpool events before investigating starvation: collect --kind threadpool --pid {cfg.ProcessId} --duration 6"),
            ];
        }

        return
        [
            new NextActionHint(
                "collect",
                $"Use runtime counters as the next cheap signal after confirming the startup configuration: collect --kind counters --pid {cfg.ProcessId} --duration 5"),
        ];
    }

    /// <summary>
    /// Phase 13 / G1 — environment self-diagnosis. Target-optional: with <c>--pid</c> it validates
    /// readiness against that target (diagnostic-socket UID match); without one it diagnoses the host.
    /// Exits non-zero (via <see cref="CliCommandResult.IsError"/>) when a hard blocker is present, so
    /// CI can gate on it. The diagnostic envelope itself stays a success envelope — the findings are
    /// data, not an error.
    /// </summary>
    private static CliCommandResult Doctor(IServiceProvider services, CliOptions options)
    {
        var inspector = services.GetRequiredService<IPreflightInspector>();
        var result = ProcessInspectionUseCases.Preflight(inspector, options.Pid);
        var report = result.Data!;
        var human = RenderDoctor(result, report);

        // Blocker => non-zero exit for CI gating. Envelope stays Ok (IsError=false on the wire).
        return new CliCommandResult(IsError: report.HasBlocker, Cancelled: false, Envelope: result, Human: human);
    }

    private static string RenderDoctor(DiagnosticResult<PreflightReport> result, PreflightReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.Summary);
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"  OS: {report.Os}   target: {(report.ProcessId is int pid ? pid.ToString(CultureInfo.InvariantCulture) : "<none>")}");
        sb.AppendLine();

        foreach (var check in report.Checks)
        {
            var glyph = check.Status switch
            {
                PreflightStatus.Ok => "OK  ",
                PreflightStatus.Degraded => "WARN",
                PreflightStatus.Blocked => "FAIL",
                _ => "n/a ",
            };
            sb.AppendLine(CultureInfo.InvariantCulture, $"  [{glyph}] {check.Title}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"         {check.Reason}");
            if (!string.IsNullOrWhiteSpace(check.Remediation))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"         fix: {check.Remediation}");
            }

            if (check.AffectedTools is { Count: > 0 } tools)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"         affects: {string.Join(", ", tools)}");
            }
        }

        return sb.ToString().TrimEnd();
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

            _ => throw new ArgumentException($"Unknown collect kind '{options.Kind}'.", nameof(options)),
        };
    }

    private static CliCommandResult Wrap<T>(CliOptions options, DiagnosticResult<T> result) =>
        BuildResultWithComparableSave(options, result, static (_, _) => { });

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

    private static async Task<CliCommandResult> InvestigateAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var planner = services.GetRequiredService<IInvestigationPlanner>();
        var resolver = services.GetRequiredService<IProcessContextResolver>();

        var resolved = await ProcessResolutionHelpers.ResolveContextAsync<InvestigationPlan>(
            resolver, options.Pid, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null)
        {
            return BuildResult(resolved.Failure, static (_, _) => { });
        }

        var constraints = new InvestigationConstraints(
            MaxToolCalls: options.MaxToolCalls ?? 8);

        var request = new InvestigationRequest(
            ProcessId: resolved.ProcessId,
            Symptom: options.Symptom,
            Hypothesis: options.Hypothesis,
            Constraints: constraints);

        var plan = planner.Plan(request);
        var cliPlan = CliInvestigationProjection.Project(plan);
        var summary = $"Mode={cliPlan.Mode}. Next step #{cliPlan.NextStep.StepNumber}: {cliPlan.NextStep.StepId}. " +
                      $"{cliPlan.AllSteps.Count} total step(s), {cliPlan.EarlyStopConditions.Count} early-stop condition(s). " +
                      $"Honor MaxToolCalls={cliPlan.MaxToolCalls}.";
        var result = ProcessResolutionHelpers.WithContext(DiagnosticResult.Ok(cliPlan, summary), resolved.Context);

        return BuildResult(result, static (sb, plan) =>
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"  investigation-id : {plan.InvestigationId}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  mode             : {plan.Mode}");
            if (!string.IsNullOrWhiteSpace(plan.Symptom))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  symptom          : {plan.Symptom}");
            }

            if (!string.IsNullOrWhiteSpace(plan.Hypothesis))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  hypothesis       : {plan.Hypothesis}");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"  max-tool-calls   : {plan.MaxToolCalls}");
            sb.AppendLine();
            sb.AppendLine("  next step:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    #{plan.NextStep.StepNumber} {plan.NextStep.StepId}{FormatStepCommand(plan.NextStep.Command)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    rationale: {plan.NextStep.Rationale}");
            if (plan.AllSteps.Count > 1)
            {
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"  all steps ({plan.AllSteps.Count}):");
                foreach (var step in plan.AllSteps)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    #{step.StepNumber} [{step.Status}] {step.StepId}{FormatStepCommand(step.Command)}");
                }
            }

            if (plan.EarlyStopConditions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("  early-stop conditions:");
                foreach (var cond in plan.EarlyStopConditions)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    - {cond.Description} → {cond.Action}");
                }
            }
        });
    }

    private static string FormatStepCommand(string? command)
        => string.IsNullOrWhiteSpace(command) ? string.Empty : $" (via {command})";

    private static async Task<CliCommandResult> ExportSummaryAsync(
        IServiceProvider services,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        if (!TryValidateExportSummary(options, out var validationError))
        {
            throw new ArgumentException(validationError, nameof(options));
        }

        var exporter = services.GetRequiredService<IInvestigationSummaryExporter>();
        var handles = services.GetRequiredService<IDiagnosticHandleStore>();

        var lookup = handles.TryGetWithKind(options.Handle!);
        if (lookup is null)
        {
            return BuildResult<ExportedInvestigationSummary>(
                DiagnosticResult.Fail<ExportedInvestigationSummary>(
                    $"Handle '{options.Handle}' is unknown or expired. Collect a CPU sample first with 'collect --kind cpu', then re-run export-summary.",
                    new DiagnosticError("HandleExpired",
                        "Drill-down handles live until the session ends or the target process exits.",
                        options.Handle)),
                static (_, _) => { });
        }

        if (lookup.Value.Artifact is not CpuSampleTraceArtifact artifact)
        {
            return BuildResult<ExportedInvestigationSummary>(
                DiagnosticResult.Fail<ExportedInvestigationSummary>(
                    $"Handle '{options.Handle}' is a '{lookup.Value.Kind}' handle, not a CPU sample. " +
                    "export-summary needs a CPU-sample handle; re-run with a handle from 'collect --kind cpu'.",
                    new DiagnosticError("HandleKindMismatch",
                        "export-summary projects CPU-sample hotspots into a portable investigation summary.",
                        options.Handle)),
                static (_, _) => { });
        }

        var topHotspots = options.TopHotspots ?? 10;
        var exported = exporter.Export(new ExportRequest(
            Handle: options.Handle!,
            Artifact: artifact,
            TopHotspots: topHotspots,
            Format: SummaryFormat.Json));

        if (!string.IsNullOrWhiteSpace(options.OutDir))
        {
            try
            {
                var fullPath = Path.GetFullPath(options.OutDir);
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // Atomic write: a failure mid-write must never truncate/clobber a pre-existing summary.
                var tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    await File.WriteAllTextAsync(tempPath, exported.Rendered, cancellationToken).ConfigureAwait(false);
                    File.Move(tempPath, fullPath, overwrite: true);
                }
                catch
                {
                    TryDeleteQuietly(tempPath);
                    throw;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return BuildResult<ExportedInvestigationSummary>(
                    DiagnosticResult.Fail<ExportedInvestigationSummary>(
                        $"export-summary: failed to write '{options.OutDir}'.",
                        new DiagnosticError("OutputWriteFailure", ex.Message)),
                    static (_, _) => { });
            }

            var writtenBytes = exported.Rendered.Length;
            var writeSummary = $"Exported investigation summary {exported.Summary.InvestigationId} ({writtenBytes} chars) to {options.OutDir}.";
            return BuildResult(DiagnosticResult.Ok(exported, writeSummary), (sb, e) =>
            {
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"  written to : {options.OutDir}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  id         : {e.Summary.InvestigationId}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  hotspots   : {e.Summary.Findings.TopHotspots.Count}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  size       : {writtenBytes} chars");
            });
        }

        // stdout mode: emit exactly the portable summary document (verbatim, pipe-able), identical to
        // what --out persists. Both --json and human paths print the same portable JSON — never a
        // decorated human envelope that a consumer would have to strip.
        return RawJsonResult(exported.Rendered);
    }

    private static CliCommandResult RawJsonResult(string json)
    {
        using var document = JsonDocument.Parse(json);
        var element = document.RootElement.Clone();
        return new CliCommandResult(IsError: false, Cancelled: false, Envelope: element, Human: json);
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of the temp file; the original write failure is already surfaced.
        }
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

        if (source == "gcdump")
        {
            var collector = services.GetRequiredService<IGcDumpHeapSnapshotCollector>();
            var gcResult = await HeapInspectionUseCases.InspectGcDump(
                collector, handles, resolver,
                options.Pid, topTypes, timeout: null, options.ExportTrace, cancellationToken).ConfigureAwait(false);

            return BuildResult<LiveHeapInspection>(gcResult, static (sb, data) => RenderTopTypes(sb, data.TopTypesByBytes));
        }

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
    /// All thread-snapshot views available in the session REPL: the nine purely artifact-based
    /// <see cref="ThreadSnapshotQueryDispatcher.SessionViews"/> plus <c>frame-vars</c>, which
    /// re-opens the snapshot origin via ClrMD to walk one thread's local variables and parameters.
    /// </summary>
    private static readonly IReadOnlyList<string> ThreadSnapshotAllSessionViews =
        [.. ThreadSnapshotQueryDispatcher.SessionViews, "frame-vars"];

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
            return ThreadSnapshotAllSessionViews;
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
    public static async Task<CliCommandResult> QuerySession(
        IServiceProvider services, CliOptions options, CancellationToken cancellationToken = default)
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
        // the projection views render from the walked snapshot alone (no ClrMD runtime, no
        // sensitive-value redactor), which is exactly the subset a stateless session can serve. The
        // address-addressed views need a ClrMD runtime: for a dump-origin handle the session re-opens
        // the dump file (Core-only, no live attach) to serve `gcroot`/`object` (#464); `objsize` and the
        // sensitive `duplicate-strings` view, plus all live-origin attaches, stay server-only.
        if (kind == HeapInspectionUseCases.HeapSnapshotKind)
        {
            return await QueryHeapSession(services, options, lookup.Value.Artifact, cancellationToken).ConfigureAwait(false);
        }

        // CPU / allocation / native-alloc sample handles drill down through the host-neutral
        // CpuSampleQueryDispatcher (#300): the merged call-tree renders from the collected trace alone.
        // The `diff` view stays server-only (it correlates a second baseline handle).
        if (CpuSampleSessionKinds.Contains(kind))
        {
            return QueryCpuSampleSession(options, lookup.Value.Artifact);
        }

        // Thread-snapshot handles drill down through the host-neutral ThreadSnapshotQueryDispatcher
        // (#300): most views render from the captured artifact alone — no live ClrMD attach. The
        // frame-vars view re-opens the origin via ClrMD to resolve one thread's local variables
        // (#487) and is therefore async.
        if (kind == ThreadSnapshotSessionKind)
        {
            return await QueryThreadSnapshotSessionAsync(services, options, lookup.Value.Artifact, cancellationToken).ConfigureAwait(false);
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
    /// <c>retention-paths</c>, …) render from the walked snapshot. The address-addressed
    /// <c>gcroot</c>/<c>object</c> views are served for <b>dump-origin</b> handles by re-opening the
    /// dump file through <see cref="IDumpInspector"/> (ClrMD walks GC roots on a dump DataTarget
    /// exactly like a live one — #464); <c>objsize</c>, <c>duplicate-strings</c>, and every
    /// live-origin attach stay server-only and yield a clear <c>NotSupportedInSession</c> envelope.
    /// </summary>
    private static async Task<CliCommandResult> QueryHeapSession(
        IServiceProvider services, CliOptions options, object artifact, CancellationToken cancellationToken)
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

            // #464: gcroot/object over a dump-origin snapshot need no live attach — re-open the dump
            // file with ClrMD (Core-only) and walk roots / read the object against the dump DataTarget.
            if ((normalized == "gcroot" || normalized == "object") && heap.Origin == HeapSnapshotOrigin.Dump)
            {
                return await QueryHeapDumpDrilldown(services, options, heap, normalized, cancellationToken).ConfigureAwait(false);
            }

            var detail = normalized == "duplicate-strings"
                ? "The 'duplicate-strings' view exposes raw string previews behind the server's sensitive-value policy, which the standalone CLI cannot enforce; run the MCP server if you need it."
                : "The 'object', 'gcroot' and 'objsize' views need a ClrMD runtime: 'gcroot'/'object' are served in-session for dump-origin handles, but a live attach (and 'objsize') require the MCP server's query_heap_snapshot tool.";
            return Fail($"query: view '{view}' for a heap snapshot is not available in the session yet.", "NotSupportedInSession", detail);
        }

        // outcome.UnknownView
        return Fail($"query: unknown view '{view}' for a heap snapshot.", "InvalidArgument",
            $"Valid views: {string.Join(", ", HeapSnapshotQueryDispatcher.ProjectionViews)}.");
    }

    /// <summary>
    /// Serves the address-addressed <c>gcroot</c>/<c>object</c> heap views for a <b>dump-origin</b>
    /// snapshot inside the session (#464). The injected <see cref="IDumpInspector"/> re-opens the dump
    /// file recorded on the artifact (no live attach) and walks the GC-root chain or reads the managed
    /// object. The <c>object</c> view never emits raw string/field/array values — the Core-only CLI has
    /// no sensitive-value gate, so previews are replaced with the metadata-only placeholder, matching
    /// the MCP server's redacted default.
    /// </summary>
    private static async Task<CliCommandResult> QueryHeapDumpDrilldown(
        IServiceProvider services, CliOptions options, HeapSnapshotArtifact heap, string view, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Address))
        {
            return Fail($"query: --address <addr> is required for view '{view}'.", "InvalidArgument",
                "Pass the managed object address (decimal or 0x-hex), e.g. query --handle <id> --view gcroot --address 0x1f2a3b40.");
        }

        if (!TryParseAddress(options.Address, out var address))
        {
            return Fail($"query: '--address {options.Address}' is not a valid object address.", "InvalidArgument",
                "Use a non-zero decimal or 0x-prefixed hex address taken from a retention-paths / top-types row.");
        }

        var inspector = services.GetRequiredService<IDumpInspector>();
        DiagnosticResult<HeapSnapshotQueryResult> result;
        try
        {
            result = view == "gcroot"
                ? BuildGcRootResult(heap, options.Handle!, await inspector.InspectGcRootAsync(heap, address, cancellationToken).ConfigureAwait(false))
                : BuildObjectResult(heap, options.Handle!, await inspector.InspectObjectAsync(heap, address, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail($"query: view '{view}' could not be served from the dump.", "InspectionFailed", ex.Message);
        }

        return BuildResult<HeapSnapshotQueryResult>(result, static (sb, qr) =>
        {
            sb.AppendLine();
            sb.AppendLine(JsonSerializer.Serialize(qr, QueryJsonOptions));
        });
    }

    /// <summary>Parses a managed object address (decimal or <c>0x</c>-hex); rejects zero.</summary>
    private static bool TryParseAddress(string value, out ulong result)
    {
        result = 0;
        var s = value.Trim();
        var parsed = (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result)
            : ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out result));
        return parsed && result != 0;
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> BuildGcRootResult(
        HeapSnapshotArtifact heap, string handle, HeapGcRootInspection inspection)
    {
        var origin = heap.Origin.ToString();
        var summary = string.Create(CultureInfo.InvariantCulture,
            $"query: gcroot view origin={origin} pid={heap.ProcessId} address=0x{inspection.Address:x} type={inspection.TypeFullName} frames={inspection.Chain.Count}{(inspection.Truncated ? " (truncated by BFS/depth caps)" : string.Empty)}");
        var result = new HeapSnapshotQueryResult(handle, "gcroot", origin, heap.ProcessId, heap.CapturedAt)
        {
            Address = inspection.Address,
            GcRoot = inspection,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> BuildObjectResult(
        HeapSnapshotArtifact heap, string handle, HeapObjectInspection inspection)
    {
        var origin = heap.Origin.ToString();
        var redacted = RedactObjectPreviews(inspection);
        var summary = string.Create(CultureInfo.InvariantCulture,
            $"query: object view origin={origin} pid={heap.ProcessId} address=0x{redacted.Address:x} type={redacted.TypeFullName} size={redacted.Size} (string/field previews redacted — no session sensitive-value gate)");
        var result = new HeapSnapshotQueryResult(handle, "object", origin, heap.ProcessId, heap.CapturedAt)
        {
            Address = redacted.Address,
            ObjectDetails = redacted,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    /// <summary>
    /// Replaces every string / field / array-element preview with the metadata-only placeholder so the
    /// Core-only session never surfaces raw heap content (it holds no sensitive-value gate). Object
    /// shape — type, size, generation, segment, array length, field names/types — is preserved.
    /// </summary>
    private static HeapObjectInspection RedactObjectPreviews(HeapObjectInspection inspection)
    {
        IReadOnlyList<HeapObjectField>? fields = inspection.Fields;
        if (fields is { Count: > 0 })
        {
            var redacted = new List<HeapObjectField>(fields.Count);
            foreach (var f in fields)
            {
                redacted.Add(new HeapObjectField(f.Name, f.TypeFullName, SensitiveDataRedactor.MetadataOnlyPlaceholder)
                {
                    ObjectAddress = f.ObjectAddress,
                    ReferencedTypeFullName = f.ReferencedTypeFullName,
                });
            }

            fields = redacted;
        }

        IReadOnlyList<HeapArrayElement>? array = inspection.ArraySample;
        if (array is { Count: > 0 })
        {
            var redacted = new List<HeapArrayElement>(array.Count);
            foreach (var a in array)
            {
                redacted.Add(new HeapArrayElement(a.Index, a.TypeFullName, SensitiveDataRedactor.MetadataOnlyPlaceholder)
                {
                    ObjectAddress = a.ObjectAddress,
                    ReferencedTypeFullName = a.ReferencedTypeFullName,
                });
            }

            array = redacted;
        }

        return new HeapObjectInspection(inspection.Address, inspection.TypeFullName, inspection.Size, inspection.SegmentKind, inspection.Generation)
        {
            IsArray = inspection.IsArray,
            ArrayLength = inspection.ArrayLength,
            ArraySample = array,
            IsString = inspection.IsString,
            StringValue = inspection.IsString ? SensitiveDataRedactor.MetadataOnlyPlaceholder : inspection.StringValue,
            StringValueTruncated = inspection.StringValueTruncated,
            Fields = fields,
            Warnings = inspection.Warnings,
        };
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
    /// Renders a thread-snapshot drill-down inside the <c>session</c> REPL. The nine artifact-based
    /// views (<see cref="ThreadSnapshotQueryDispatcher.SessionViews"/>) render purely from the
    /// captured snapshot via the host-neutral <see cref="ThreadSnapshotQueryDispatcher"/>. The
    /// <c>frame-vars</c> view (#487) re-opens the snapshot origin via <see cref="IFrameVariableResolver"/>
    /// (ClrMD, same ptrace/dump-read footprint as the original snapshot) and returns the object-typed
    /// locals/parameters on each managed frame of the specified thread.
    /// </summary>
    private static async Task<CliCommandResult> QueryThreadSnapshotSessionAsync(
        IServiceProvider services, CliOptions options, object artifact, CancellationToken cancellationToken)
    {
        if (artifact is not ThreadSnapshotArtifact snapshot)
        {
            return Fail($"query: handle '{options.Handle}' could not be rendered as a thread snapshot.", "InvalidArgument",
                "The stored artifact type did not match its handle kind; re-run the originating collect command to get a fresh handle.");
        }

        var view = string.IsNullOrWhiteSpace(options.View) ? "top-blocked" : options.View;

        if (string.Equals(view, "frame-vars", StringComparison.OrdinalIgnoreCase))
        {
            return await QueryFrameVarsAsync(services, options, snapshot, cancellationToken).ConfigureAwait(false);
        }

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
    /// Re-opens the snapshot origin via <see cref="IFrameVariableResolver"/> (ClrMD) and renders the
    /// object-typed locals/parameters of the specified managed thread. Requires <c>--thread-id</c>.
    /// </summary>
    private static async Task<CliCommandResult> QueryFrameVarsAsync(
        IServiceProvider services, CliOptions options, ThreadSnapshotArtifact snapshot, CancellationToken cancellationToken)
    {
        if (options.ThreadId is null)
        {
            return Fail(
                "--thread-id (ManagedThreadId) is required for view 'frame-vars'.",
                "InvalidArgument",
                "Obtain the ManagedThreadId from view='threads-summary', then re-run: query --handle <id> --view frame-vars --thread-id <id>.");
        }

        // Guard against PID reuse / drift: the requested thread must have been present in the
        // captured snapshot, otherwise we'd resolve frames from whatever now owns that PID.
        if (!snapshot.Threads.Any(t => t.ManagedThreadId == options.ThreadId.Value))
        {
            return Fail(
                $"Managed thread {options.ThreadId.Value} was not present in the captured snapshot; re-capture before inspecting frame variables.",
                "ThreadNotInSnapshot",
                "Use view='threads-summary' to list the ManagedThreadIds actually captured in this snapshot.");
        }

        var resolver = services.GetRequiredService<IFrameVariableResolver>();
        FrameVariablesResult frameVars;
        try
        {
            frameVars = await resolver.ResolveAsync(
                snapshot, options.ThreadId.Value, includeSensitiveValues: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail($"frame-vars: {ex.Message}", "FrameVarsFailed",
                "Frame variable resolution failed. Ensure the target process is still running (live origin) or the dump file is accessible (dump origin). Value-type locals are not enumerable via ClrMD.");
        }

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"frame-vars: {frameVars.Frames.Count} frame(s) for managed thread {frameVars.ManagedThreadId} (OS tid {frameVars.OSThreadId}).");
        var ok = DiagnosticResult.Ok(frameVars, summary);
        return BuildResult<FrameVariablesResult>(ok, static (sb, r) =>
        {
            sb.AppendLine();
            sb.AppendLine(JsonSerializer.Serialize(r, QueryJsonOptions));
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

        if (options.Kind == "trace")
        {
            var traceSource = services.GetRequiredService<IDumpByteSource>();
            // CliHost pinned the artifact root at the trace file's directory; a relative file name
            // resolves under it (SafeArtifactPath rejects anything outside the root).
            var traceResult = await ByteMaterializationUseCases.MaterializeTraceBytes(
                traceSource, principalAllowsLiteralScope,
                Path.GetFileName(options.DumpFile!), outputPath, maxBytes,
                logger: null, principalName: null, cancellationToken).ConfigureAwait(false);
            return WrapMaterialization(traceResult);
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
