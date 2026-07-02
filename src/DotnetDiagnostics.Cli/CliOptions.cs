namespace DotnetDiagnostics.Cli;

/// <summary>
/// Parsed flags for a single <c>dotnet-diagnostics</c> invocation. Hand-rolled (no command-line
/// library dependency yet) and deliberately small — the first slice of the standalone CLI (#288)
/// only ships the read-only <c>processes</c> and <c>capabilities</c> commands; the collection /
/// heap / dump / drilldown flags arrive as those commands are wired in later PRs.
/// </summary>
internal sealed record CliOptions
{
    /// <summary>The sub-command (e.g. <c>processes</c>), or null when none was supplied.</summary>
    public string? Command { get; init; }

    /// <summary>Target OS process id (<c>--pid</c>). Optional — collectors auto-resolve the lone visible .NET process.</summary>
    public int? Pid { get; init; }

    /// <summary>Target process name/prefix selector from <c>--pid &lt;name&gt;</c>. Resolved by the CLI before dispatch.</summary>
    public string? PidName { get; init; }

    /// <summary>True when the caller supplied either a numeric pid or a name/prefix selector.</summary>
    public bool HasPid => Pid is not null || PidName is not null;

    /// <summary>Emit the raw <see cref="DotnetDiagnostics.Core.DiagnosticResult{T}"/> envelope as JSON (<c>--json</c>).</summary>
    public bool Json { get; init; }

    /// <summary>Comparable snapshot/diff destination path (<c>--save</c>) for <c>collect</c> / <c>compare</c>.</summary>
    public string? SavePath { get; init; }

    /// <summary>Comparable snapshot JSON paths for the <c>compare</c> command.</summary>
    public IReadOnlyList<string> ComparePaths { get; init; } = Array.Empty<string>();

    /// <summary>Shell name for the <c>completion</c> command (<c>bash</c>, <c>zsh</c>, or <c>pwsh</c>).</summary>
    public string? CompletionShell { get; init; }

    /// <summary>Journey interpretation for the <c>compare</c> command (<c>--mode</c>): trend or dispersion.</summary>
    public string? Mode { get; init; }

    /// <summary>True when <c>--help</c>/<c>-h</c> was supplied.</summary>
    public bool Help { get; init; }

    /// <summary>EventPipe collection kind for the <c>collect</c> command (<c>--kind</c>): counters, exceptions, crash-guard, gc, catalog, event_source, activities, logs, jit, threadpool, contention, db.</summary>
    public string? Kind { get; init; }

    /// <summary>Collection window in seconds (<c>--duration</c>/<c>-d</c>). Null applies the per-kind default (counters: 5; others: 10).</summary>
    public int? DurationSeconds { get; init; }

    /// <summary>EventCounter provider names (<c>--provider</c>, repeatable) for <c>kind=counters</c>; the first value is the required <c>kind=event_source</c> provider name.</summary>
    public IReadOnlyList<string> Providers { get; init; } = Array.Empty<string>();

    /// <summary>Meter names (<c>--meter</c>, repeatable) for <c>kind=counters</c>.</summary>
    public IReadOnlyList<string> Meters { get; init; } = Array.Empty<string>();

    /// <summary>ActivitySource name filters (<c>--source</c>, repeatable) for <c>kind=activities</c>.</summary>
    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();

    /// <summary>ILogger category glob filters (<c>--category</c>, repeatable) for <c>kind=logs</c>.</summary>
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    /// <summary>Refresh interval in seconds (<c>--interval</c>) for <c>kind=counters</c>/<c>kind=db</c>. Null applies the default (1).</summary>
    public int? IntervalSeconds { get; init; }

    /// <summary>Re-run the one-shot command every N seconds (<c>--watch</c>). Null runs once.</summary>
    public int? WatchIntervalSeconds { get; init; }

    /// <summary>Maximum events/records (<c>--max-events</c>) — maps to the per-kind cap (maxEvents/maxRecent/maxActivities). Null applies the per-kind default.</summary>
    public int? MaxEvents { get; init; }

    /// <summary>Minimum log level (<c>--min-level</c>) for <c>kind=logs</c>. Null applies the default (Information).</summary>
    public string? MinLevel { get; init; }

    /// <summary>Verbosity (<c>--depth</c>): summary, detail, or raw. Null applies the default (summary).</summary>
    public string? Depth { get; init; }

    /// <summary>Opt-in switch (<c>--unsafe-provider</c>) for non-allowlisted <c>kind=event_source</c> providers.</summary>
    public bool UnsafeProvider { get; init; }

    /// <summary>Absolute path to a previously-captured .dmp file (<c>--dump-file</c>) for <c>inspect-heap --source dump</c>.</summary>
    public string? DumpFile { get; init; }

    /// <summary>When set (<c>--export-trace</c>), persists the raw .nettrace under the artifact root for offline analysis (<c>inspect-heap --source gcdump</c>). Surfaces the path so it can be fetched with <c>get-bytes --kind trace</c>.</summary>
    public bool ExportTrace { get; init; }

    /// <summary>Top-N type count (<c>--top-types</c>) for <c>inspect-heap</c>. Null applies the default (20).</summary>
    public int? TopTypes { get; init; }

    /// <summary>Walk a short GC retention chain for the top retained types (<c>--include-retention-paths</c>).</summary>
    public bool IncludeRetentionPaths { get; init; }

    /// <summary>Cap on retention-chain depth (<c>--retention-path-limit</c>). Null applies the default (8).</summary>
    public int? RetentionPathLimit { get; init; }

    /// <summary>Enumerate static reference fields ranked by referenced object size (<c>--include-static-fields</c>).</summary>
    public bool IncludeStaticFields { get; init; }

    /// <summary>Group MulticastDelegate invocation lists by (target type, method) (<c>--include-delegate-targets</c>).</summary>
    public bool IncludeDelegateTargets { get; init; }

    /// <summary>Rank duplicate System.String instances by aggregate retained bytes (<c>--include-duplicate-strings</c>).</summary>
    public bool IncludeDuplicateStrings { get; init; }

    /// <summary>NT_SYMBOL_PATH-style search path (<c>--symbol-path</c>) for symbol-resolving heap drilldowns.</summary>
    public string? SymbolPath { get; init; }

    /// <summary>
    /// Path to the ILC <c>*.map.xml</c> map file for NativeAOT CPU sampling (<c>--native-aot-map</c>).
    /// When set, the perf-based AOT sampler emits a name-based <c>MethodIdentity</c> for managed frames.
    /// Optional and inert for CoreCLR targets. Only honoured with <c>--capture cpu-sample</c>.
    /// </summary>
    public string? NativeAotMapFile { get; init; }

    /// <summary>Dump type for the <c>dump</c> command (<c>--dump-type</c>): Mini, Triage, WithHeap or Full. Null applies the default (Mini).</summary>
    public string? DumpType { get; init; }

    /// <summary>Output directory for the <c>dump</c> command (<c>--out</c>). Sets the artifact root for this invocation; absolute or relative.</summary>
    public string? OutDir { get; init; }

    /// <summary>Defense-in-depth confirmation flag (<c>--confirm</c>) required to actually write a dump file.</summary>
    public bool Confirm { get; init; }

    /// <summary>Module MVID (GUID 'D', <c>--mvid</c>) for <c>get-bytes --kind module</c>.</summary>
    public string? Mvid { get; init; }

    /// <summary>Module artifact (<c>--asset</c>): <c>pe</c> (default) or <c>pdb</c>, for <c>get-bytes --kind module</c>.</summary>
    public string? Asset { get; init; }

    /// <summary>Drill-down handle (<c>--handle</c>) for the <c>query</c> command (parsed for forward-compat; the one-shot CLI cannot honour it — see #286).</summary>
    public string? Handle { get; init; }

    /// <summary>Drill-down view name (<c>--view</c>) for the <c>query</c> command (parsed for forward-compat; the one-shot CLI cannot honour it — see #286).</summary>
    public string? View { get; init; }

    /// <summary>Ranking for the heap <c>top-types</c> view (<c>--rank-by</c>): <c>bytes</c> (default) or <c>instances</c>. Honoured only by the stateful <c>session</c> <c>query</c> path.</summary>
    public string? RankBy { get; init; }

    /// <summary>Case-insensitive type substring for the heap <c>retention-paths</c> view (<c>--type-filter</c>). Honoured only by the stateful <c>session</c> <c>query</c> path.</summary>
    public string? TypeFilter { get; init; }

    /// <summary>Managed object address (decimal or <c>0x</c>-hex) for the heap <c>object</c> / <c>gcroot</c> views (<c>--address</c>). Honoured only by the stateful <c>session</c> <c>query</c> path, and only for dump-origin handles.</summary>
    public string? Address { get; init; }

    /// <summary>Case-insensitive method substring to re-root the CPU <c>call-tree</c> view (<c>--root-method-filter</c>). Event-catalog query reuses it as an event-name filter. Honoured only by the stateful <c>session</c> <c>query</c> path.</summary>
    public string? RootMethodFilter { get; init; }

    /// <summary>Case-insensitive provider substring for event-catalog query views (<c>--provider-filter</c>). Honoured only by the stateful <c>session</c> <c>query</c> path.</summary>
    public string? ProviderFilter { get; init; }

    /// <summary>DATAS <c>tuning</c> query view only: emit only rows where the heap-count decision changed versus the previous GC (<c>--changes-only</c>). Honoured only by the stateful <c>session</c> <c>query</c> path.</summary>
    public bool ChangesOnly { get; init; }

    /// <summary>Maximum call-tree depth for the CPU <c>call-tree</c> view (<c>--max-depth</c>, default 8). Honoured only by the stateful <c>session</c> <c>query</c> path.</summary>
    public int? MaxDepth { get; init; }

    /// <summary>Approximate cap on call-tree nodes for the CPU <c>call-tree</c> view (<c>--max-nodes</c>, default 200). Honoured only by the stateful <c>session</c> <c>query</c> path.</summary>
    public int? MaxNodes { get; init; }

    /// <summary>Thread id for the thread-snapshot <c>stack</c> view (<c>--thread-id</c>): ManagedThreadId for CoreCLR snapshots, OS TID for linux-native-stack. Honoured only by the stateful <c>session</c> <c>query</c> path.</summary>
    public int? ThreadId { get; init; }

    /// <summary>1-based stack rank for the off-CPU <c>stack</c> view (<c>--stack-rank</c>). Honoured only by the stateful <c>session</c> <c>query</c> path.</summary>
    public int? StackRank { get; init; }

    /// <summary>Top frames folded into the signature hash for the thread-snapshot <c>unique-stacks</c> view (<c>--frames-to-hash</c>, default 20). Honoured only by the stateful <c>session</c> <c>query</c> path.</summary>
    public int? FramesToHash { get; init; }

    /// <summary>Minimum threads per group for the thread-snapshot <c>unique-stacks</c> view (<c>--min-count</c>, default 1). Honoured only by the stateful <c>session</c> <c>query</c> path.</summary>
    public int? MinCount { get; init; }

    /// <summary>Row cap for the CPU <c>top-methods</c> / <c>by-module</c> / <c>by-namespace</c> / <c>caller-callee</c> views (<c>--top</c>, default 20). Honoured only by the stateful <c>session</c> <c>query</c> path.</summary>
    public int? Top { get; init; }

    /// <summary>Hot-path threshold percent for the CPU <c>hot-path</c> view (<c>--threshold</c>, default 50): a child must carry at least this % of its parent's inclusive samples to extend the chain. Honoured only by the stateful <c>session</c> <c>query</c> path.</summary>
    public int? Threshold { get; init; }

    /// <summary>Threshold-gated capture predicate (<c>--capture-when</c>) for <c>collect --kind counters</c>: <c>&lt;metric&gt;&lt;op&gt;&lt;value&gt;</c> e.g. <c>cpu&gt;85</c>. Arms a bounded watch (#419).</summary>
    public string? CaptureWhen { get; init; }

    /// <summary>What to capture when <see cref="CaptureWhen"/> trips (<c>--capture</c>): <c>dump</c>, <c>cpu-sample</c>, <c>heap</c>, or <c>thread-snapshot</c>.</summary>
    public string? CaptureKind { get; init; }

    /// <summary>Hard cap on fired captures (<c>--max-captures</c>) for threshold-gated capture. Null applies the default (1).</summary>
    public int? MaxCaptures { get; init; }

    /// <summary>Hard upper bound (seconds) on how long the threshold-gated watch is armed (<c>--window</c>). Required in gated mode.</summary>
    public int? WindowSeconds { get; init; }

    /// <summary>
    /// Opt-in <c>--launch</c> dev mode (issue #365): re-launch the target as a child of the CLI so
    /// ClrMD live attach is permitted under Yama <c>ptrace_scope=1</c> with zero privilege. The program
    /// and its arguments are everything after the <c>--</c> separator (see <see cref="LaunchArgs"/>).
    /// </summary>
    public bool Launch { get; init; }

    /// <summary>
    /// The launch argv captured after <c>--</c> (<see cref="Launch"/> mode). The first element is the
    /// program to spawn (e.g. <c>dotnet</c>); the rest are its arguments (e.g. <c>App.dll</c>). Empty
    /// when <c>--launch</c> was not supplied.
    /// </summary>
    public IReadOnlyList<string> LaunchArgs { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Set by the CLI (not by argument parsing) once it has launched the target as a child and rebound
    /// <see cref="Pid"/> to it. Signals downstream projection (e.g. the capability note) that this
    /// process is the target's ptrace parent, so descendant attach is available under
    /// <c>ptrace_scope=1</c> — preventing a misleading "live attach unavailable" note that re-suggests
    /// <c>--launch</c> while already running under it (issue #365).
    /// </summary>
    public bool LaunchedByCli { get; init; }

    /// <summary>
    /// Cold-start capture opt-in (<c>--suspend-startup</c>, issue #446). With <c>--launch</c>, spawns the
    /// target suspended on a reverse-connect <c>DOTNET_DiagnosticPorts</c> port, arms the EventPipe
    /// session before any managed code runs, then resumes — capturing static ctors, DI build,
    /// module-init exceptions and startup timings the post-attach path misses. CLI-only; default OFF.
    /// Applies to <c>collect --kind startup</c> and <c>collect --kind cpu</c>.
    /// </summary>
    public bool SuspendStartup { get; init; }

    /// <summary>
    /// Parses <paramref name="args"/>. Returns a populated <see cref="CliOptions"/> on success, or
    /// <c>null</c> with a non-null <paramref name="error"/> describing the first usage problem.
    /// </summary>
    public static CliOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = null;

        string? command = null;
        int? pid = null;
        string? pidName = null;
        var json = false;
        var help = false;
        string? savePath = null;
        var comparePaths = new List<string>();
        string? completionShell = null;
        string? mode = null;
        string? kind = null;
        int? durationSeconds = null;
        var providers = new List<string>();
        var meters = new List<string>();
        var sources = new List<string>();
        var categories = new List<string>();
        int? intervalSeconds = null;
        int? maxEvents = null;
        int? watchIntervalSeconds = null;
        string? minLevel = null;
        string? depth = null;
        var unsafeProvider = false;
        var exportTrace = false;
        string? dumpFile = null;
        int? topTypes = null;
        var includeRetentionPaths = false;
        int? retentionPathLimit = null;
        var includeStaticFields = false;
        var includeDelegateTargets = false;
        var includeDuplicateStrings = false;
        string? symbolPath = null;
        string? nativeAotMapFile = null;
        string? dumpType = null;
        string? outDir = null;
        var confirm = false;
        var changesOnly = false;
        string? mvid = null;
        string? asset = null;
        string? handle = null;
        string? view = null;
        string? rankBy = null;
        string? typeFilter = null;
        string? address = null;
        string? rootMethodFilter = null;
        string? providerFilter = null;
        int? maxDepth = null;
        int? maxNodes = null;
        int? threadId = null;
        int? stackRank = null;
        int? framesToHash = null;
        int? minCount = null;
        int? top = null;
        int? threshold = null;
        string? captureWhen = null;
        string? captureKind = null;
        int? maxCaptures = null;
        int? windowSeconds = null;
        var launch = false;
        var suspendStartup = false;
        List<string>? launchArgs = null;

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];

            // Everything after a bare `--` is the launch argv (program + its args); stop option parsing.
            if (token == "--")
            {
                launchArgs = new List<string>(args.Count - i - 1);
                for (var j = i + 1; j < args.Count; j++)
                {
                    launchArgs.Add(args[j]);
                }

                break;
            }

            switch (token)
            {
                case "--help":
                case "-h":
                    help = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--save":
                    if (!TryTakeString(args, ref i, token, out var saveValue, out error))
                    {
                        return null;
                    }

                    savePath = saveValue;
                    break;
                case "--unsafe-provider":
                    unsafeProvider = true;
                    break;
                case "--export-trace":
                    exportTrace = true;
                    break;
                case "--include-retention-paths":
                    includeRetentionPaths = true;
                    break;
                case "--include-static-fields":
                    includeStaticFields = true;
                    break;
                case "--include-delegate-targets":
                    includeDelegateTargets = true;
                    break;
                case "--include-duplicate-strings":
                    includeDuplicateStrings = true;
                    break;
                case "--confirm":
                    confirm = true;
                    break;
                case "--changes-only":
                    changesOnly = true;
                    break;
                case "--launch":
                    launch = true;
                    break;
                case "--suspend-startup":
                    suspendStartup = true;
                    break;
                case "--pid":
                case "-p":
                    if (!TryTakeString(args, ref i, token, out var pidValue, out error))
                    {
                        return null;
                    }

                    if (string.IsNullOrWhiteSpace(pidValue))
                    {
                        error = $"Option '{token}' requires a non-empty pid or process name.";
                        return null;
                    }

                    if (int.TryParse(pidValue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var numericPid))
                    {
                        pid = numericPid;
                        pidName = null;
                    }
                    else
                    {
                        pid = null;
                        pidName = pidValue;
                    }

                    break;
                case "--kind":
                    if (!TryTakeString(args, ref i, token, out var kindValue, out error))
                    {
                        return null;
                    }

                    kind = kindValue;
                    break;
                case "--duration":
                case "-d":
                    if (!TryTakeInt(args, ref i, token, out var durationValue, out error))
                    {
                        return null;
                    }

                    durationSeconds = durationValue;
                    break;
                case "--interval":
                    if (!TryTakeInt(args, ref i, token, out var intervalValue, out error))
                    {
                        return null;
                    }

                    intervalSeconds = intervalValue;
                    break;
                case "--max-events":
                    if (!TryTakeInt(args, ref i, token, out var maxEventsValue, out error))
                    {
                        return null;
                    }

                    maxEvents = maxEventsValue;
                    break;
                case "--watch":
                    if (!TryTakeInt(args, ref i, token, out var watchValue, out error))
                    {
                        return null;
                    }

                    watchIntervalSeconds = watchValue;
                    break;
                case "--capture-when":
                    if (!TryTakeString(args, ref i, token, out var captureWhenValue, out error))
                    {
                        return null;
                    }

                    captureWhen = captureWhenValue;
                    break;
                case "--capture":
                    if (!TryTakeString(args, ref i, token, out var captureKindValue, out error))
                    {
                        return null;
                    }

                    captureKind = captureKindValue;
                    break;
                case "--max-captures":
                    if (!TryTakeInt(args, ref i, token, out var maxCapturesValue, out error))
                    {
                        return null;
                    }

                    maxCaptures = maxCapturesValue;
                    break;
                case "--window":
                    if (!TryTakeInt(args, ref i, token, out var windowValue, out error))
                    {
                        return null;
                    }

                    windowSeconds = windowValue;
                    break;
                case "--top-types":
                    if (!TryTakeInt(args, ref i, token, out var topTypesValue, out error))
                    {
                        return null;
                    }

                    topTypes = topTypesValue;
                    break;
                case "--retention-path-limit":
                    if (!TryTakeInt(args, ref i, token, out var retentionLimitValue, out error))
                    {
                        return null;
                    }

                    retentionPathLimit = retentionLimitValue;
                    break;
                case "--provider":
                    if (!TryTakeString(args, ref i, token, out var providerValue, out error))
                    {
                        return null;
                    }

                    providers.Add(providerValue);
                    break;
                case "--meter":
                    if (!TryTakeString(args, ref i, token, out var meterValue, out error))
                    {
                        return null;
                    }

                    meters.Add(meterValue);
                    break;
                case "--source":
                    if (!TryTakeString(args, ref i, token, out var sourceValue, out error))
                    {
                        return null;
                    }

                    sources.Add(sourceValue);
                    break;
                case "--category":
                    if (!TryTakeString(args, ref i, token, out var categoryValue, out error))
                    {
                        return null;
                    }

                    categories.Add(categoryValue);
                    break;
                case "--min-level":
                    if (!TryTakeString(args, ref i, token, out var minLevelValue, out error))
                    {
                        return null;
                    }

                    minLevel = minLevelValue;
                    break;
                case "--depth":
                    if (!TryTakeString(args, ref i, token, out var depthValue, out error))
                    {
                        return null;
                    }

                    depth = depthValue;
                    break;
                case "--mode":
                    if (!TryTakeString(args, ref i, token, out var modeValue, out error))
                    {
                        return null;
                    }

                    mode = modeValue;
                    break;
                case "--dump-file":
                    if (!TryTakeString(args, ref i, token, out var dumpFileValue, out error))
                    {
                        return null;
                    }

                    dumpFile = dumpFileValue;
                    break;
                case "--symbol-path":
                    if (!TryTakeString(args, ref i, token, out var symbolPathValue, out error))
                    {
                        return null;
                    }

                    symbolPath = symbolPathValue;
                    break;
                case "--native-aot-map":
                    if (!TryTakeString(args, ref i, token, out var nativeAotMapValue, out error))
                    {
                        return null;
                    }

                    nativeAotMapFile = nativeAotMapValue;
                    break;
                case "--dump-type":
                    if (!TryTakeString(args, ref i, token, out var dumpTypeValue, out error))
                    {
                        return null;
                    }

                    dumpType = dumpTypeValue;
                    break;
                case "--out":
                    if (!TryTakeString(args, ref i, token, out var outValue, out error))
                    {
                        return null;
                    }

                    outDir = outValue;
                    break;
                case "--mvid":
                    if (!TryTakeString(args, ref i, token, out var mvidValue, out error))
                    {
                        return null;
                    }

                    mvid = mvidValue;
                    break;
                case "--asset":
                    if (!TryTakeString(args, ref i, token, out var assetValue, out error))
                    {
                        return null;
                    }

                    asset = assetValue;
                    break;
                case "--handle":
                    if (!TryTakeString(args, ref i, token, out var handleValue, out error))
                    {
                        return null;
                    }

                    handle = handleValue;
                    break;
                case "--view":
                    if (!TryTakeString(args, ref i, token, out var viewValue, out error))
                    {
                        return null;
                    }

                    view = viewValue;
                    break;
                case "--rank-by":
                    if (!TryTakeString(args, ref i, token, out var rankByValue, out error))
                    {
                        return null;
                    }

                    rankBy = rankByValue;
                    break;
                case "--type-filter":
                    if (!TryTakeString(args, ref i, token, out var typeFilterValue, out error))
                    {
                        return null;
                    }

                    typeFilter = typeFilterValue;
                    break;
                case "--address":
                    if (!TryTakeString(args, ref i, token, out var addressValue, out error))
                    {
                        return null;
                    }

                    address = addressValue;
                    break;
                case "--root-method-filter":
                    if (!TryTakeString(args, ref i, token, out var rootMethodFilterValue, out error))
                    {
                        return null;
                    }

                    rootMethodFilter = rootMethodFilterValue;
                    break;
                case "--provider-filter":
                    if (!TryTakeString(args, ref i, token, out var providerFilterValue, out error))
                    {
                        return null;
                    }

                    providerFilter = providerFilterValue;
                    break;
                case "--max-depth":
                    if (!TryTakeInt(args, ref i, token, out var maxDepthValue, out error))
                    {
                        return null;
                    }

                    maxDepth = maxDepthValue;
                    break;
                case "--max-nodes":
                    if (!TryTakeInt(args, ref i, token, out var maxNodesValue, out error))
                    {
                        return null;
                    }

                    maxNodes = maxNodesValue;
                    break;
                case "--top":
                    if (!TryTakeInt(args, ref i, token, out var topValue, out error))
                    {
                        return null;
                    }

                    top = topValue;
                    break;
                case "--threshold":
                    if (!TryTakeInt(args, ref i, token, out var thresholdValue, out error))
                    {
                        return null;
                    }

                    threshold = thresholdValue;
                    break;
                case "--thread-id":
                    if (!TryTakeInt(args, ref i, token, out var threadIdValue, out error))
                    {
                        return null;
                    }

                    threadId = threadIdValue;
                    break;
                case "--stack-rank":
                    if (!TryTakeInt(args, ref i, token, out var stackRankValue, out error))
                    {
                        return null;
                    }

                    stackRank = stackRankValue;
                    break;
                case "--frames-to-hash":
                    if (!TryTakeInt(args, ref i, token, out var framesToHashValue, out error))
                    {
                        return null;
                    }

                    framesToHash = framesToHashValue;
                    break;
                case "--min-count":
                    if (!TryTakeInt(args, ref i, token, out var minCountValue, out error))
                    {
                        return null;
                    }

                    minCount = minCountValue;
                    break;
                default:
                    if (token.StartsWith('-'))
                    {
                        error = $"Unknown option '{token}'.";
                        return null;
                    }

                    if (command is not null)
                    {
                        if (string.Equals(command, "compare", StringComparison.Ordinal))
                        {
                            comparePaths.Add(token);
                            break;
                        }

                        if (string.Equals(command, "completion", StringComparison.Ordinal)
                            && completionShell is null)
                        {
                            completionShell = token;
                            break;
                        }

                        error = $"Unexpected argument '{token}'. Only one command is accepted.";
                        return null;
                    }

                    command = token;
                    break;
            }
        }

        return new CliOptions
        {
            Command = command,
            Pid = pid,
            PidName = pidName,
            Json = json,
            SavePath = savePath,
            ComparePaths = comparePaths,
            CompletionShell = completionShell,
            Mode = mode,
            Help = help,
            Kind = kind,
            DurationSeconds = durationSeconds,
            Providers = providers,
            Meters = meters,
            Sources = sources,
            Categories = categories,
            IntervalSeconds = intervalSeconds,
            WatchIntervalSeconds = watchIntervalSeconds,
            MaxEvents = maxEvents,
            MinLevel = minLevel,
            Depth = depth,
            UnsafeProvider = unsafeProvider,
            ExportTrace = exportTrace,
            DumpFile = dumpFile,
            TopTypes = topTypes,
            IncludeRetentionPaths = includeRetentionPaths,
            RetentionPathLimit = retentionPathLimit,
            IncludeStaticFields = includeStaticFields,
            IncludeDelegateTargets = includeDelegateTargets,
            IncludeDuplicateStrings = includeDuplicateStrings,
            SymbolPath = symbolPath,
            NativeAotMapFile = nativeAotMapFile,
            DumpType = dumpType,
            OutDir = outDir,
            Confirm = confirm,
            Mvid = mvid,
            Asset = asset,
            Handle = handle,
            View = view,
            RankBy = rankBy,
            TypeFilter = typeFilter,
            Address = address,
            RootMethodFilter = rootMethodFilter,
            ProviderFilter = providerFilter,
            ChangesOnly = changesOnly,
            MaxDepth = maxDepth,
            MaxNodes = maxNodes,
            ThreadId = threadId,
            StackRank = stackRank,
            FramesToHash = framesToHash,
            MinCount = minCount,
            Top = top,
            Threshold = threshold,
            CaptureWhen = captureWhen,
            CaptureKind = captureKind,
            MaxCaptures = maxCaptures,
            WindowSeconds = windowSeconds,
            Launch = launch,
            SuspendStartup = suspendStartup,
            LaunchArgs = launchArgs ?? (IReadOnlyList<string>)Array.Empty<string>(),
        };
    }

    private static bool TryTakeInt(IReadOnlyList<string> args, ref int i, string flag, out int value, out string? error)
    {
        value = 0;
        error = null;
        if (i + 1 >= args.Count)
        {
            error = $"Option '{flag}' requires a value.";
            return false;
        }

        var raw = args[++i];
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            error = $"Option '{flag}' expects an integer, got '{raw}'.";
            return false;
        }

        return true;
    }

    private static bool TryTakeString(IReadOnlyList<string> args, ref int i, string flag, out string value, out string? error)
    {
        value = string.Empty;
        error = null;
        if (i + 1 >= args.Count)
        {
            error = $"Option '{flag}' requires a value.";
            return false;
        }

        value = args[++i];
        return true;
    }
}
