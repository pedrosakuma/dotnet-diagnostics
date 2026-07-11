using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Bytes;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Container;
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
using DotnetDiagnostics.Core.NativeAlloc;
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

    /// <summary>When <see langword="true"/>, <see cref="Human"/> is emitted to stdout verbatim — no
    /// ANSI colorization — because it is a machine-readable payload (e.g. <c>export-summary</c>'s
    /// portable JSON document) that a consumer pipes or persists.</summary>
    public bool RawHuman { get; init; }
}

/// <summary>
/// The standalone CLI sub-command handlers (issue #288). Each handler runs one host-neutral use
/// case from <see cref="ProcessInspectionUseCases"/> against the shared Core engine and returns a
/// <see cref="CliCommandResult"/>. Handlers never call <c>Environment.Exit</c> and never own the
/// process lifecycle — <see cref="CliHost"/> owns exit codes and Ctrl-C cancellation.
/// </summary>
internal static partial class CliCommands
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


    internal static readonly FrozenSet<string> CommandSet = Commands.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Heap-snapshot sources accepted by the <c>inspect-heap</c> command (issue #288 PR3b).</summary>
    public static readonly IReadOnlyList<string> HeapSources = new[] { "live", "dump", "gcdump" };
    internal static readonly FrozenSet<string> HeapSourceSet = HeapSources.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Views accepted by the <c>inspect</c> command (issue #486).</summary>
    public static readonly IReadOnlyList<string> InspectViews = new[] { "triage", "runtime-config", "container" };
    internal static readonly FrozenSet<string> InspectViewSet = InspectViews.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Artifact kinds accepted by the <c>get-bytes</c> command (issue #288 PR4).</summary>
    public static readonly IReadOnlyList<string> ByteKinds = new[] { "module", "dump", "trace" };
    internal static readonly FrozenSet<string> ByteKindSet = ByteKinds.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Module assets accepted by <c>get-bytes --kind module</c> (issue #288 PR4).</summary>
    public static readonly IReadOnlyList<string> ByteAssets = new[] { "pe", "pdb" };
    internal static readonly FrozenSet<string> ByteAssetSet = ByteAssets.ToFrozenSet(StringComparer.Ordinal);

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
    internal static readonly FrozenSet<string> LaunchableCommandSet = LaunchableCommands.ToFrozenSet(StringComparer.Ordinal);


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
        "cpu",
        "off_cpu",
        "off-cpu",
        "allocation",
        "native-alloc",
        "thread-snapshot",
    };

    internal static readonly FrozenSet<string> CollectKindSet = CollectKinds.ToFrozenSet(StringComparer.Ordinal);

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
}
