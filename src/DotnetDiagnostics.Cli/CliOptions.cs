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
    /// Optional and inert for CoreCLR targets. Honoured by <c>collect --kind cpu</c> and by
    /// threshold-gated <c>--capture cpu-sample</c>.
    /// </summary>
    public string? NativeAotMapFile { get; init; }

    /// <summary>
    /// CPU sampling source-line resolution toggle (<c>--resolve-source-lines</c> /
    /// <c>--no-resolve-source-lines</c>). Null applies the default (enabled).
    /// </summary>
    public bool? ResolveSourceLines { get; init; }

    /// <summary>
    /// Opt-in closed-generic resolution for CPU sampling (<c>--resolve-method-instantiations</c>).
    /// Default off.
    /// </summary>
    public bool ResolveMethodInstantiations { get; init; }

    /// <summary>
    /// Native allocation sampler period (<c>--native-alloc-sample-period</c>). Null applies the
    /// default (1000).
    /// </summary>
    public long? NativeAllocSamplePeriod { get; init; }

    /// <summary>
    /// Thread-snapshot frame cap (<c>--max-frames-per-thread</c>). Null applies the default (64).
    /// </summary>
    public int? MaxFramesPerThread { get; init; }

    /// <summary>
    /// Include runtime/internal frames in a thread snapshot (<c>--include-runtime-frames</c>).
    /// Default off.
    /// </summary>
    public bool IncludeRuntimeFrames { get; init; }

    /// <summary>
    /// Include native frames in a thread snapshot (<c>--include-native-frames</c>). Default off.
    /// </summary>
    public bool IncludeNativeFrames { get; init; }

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

    /// <summary>Free-text symptom description for the <c>investigate</c> command (<c>--symptom</c>): e.g. 'high latency on /checkout'. Required for cold mode.</summary>
    public string? Symptom { get; init; }

    /// <summary>Specific hypothesis to test for the <c>investigate</c> command (<c>--hypothesis</c>): e.g. 'lock contention on Cart.Checkout'. Triggers hypothesis mode.</summary>
    public string? Hypothesis { get; init; }

    /// <summary>Hard limit on tool calls before forcing summarization for the <c>investigate</c> command (<c>--max-tool-calls</c>). Null applies the default (8).</summary>
    public int? MaxToolCalls { get; init; }

    /// <summary>Top-N hotspots to include in the <c>export-summary</c> output (<c>--top-hotspots</c>). Null applies the default (10).</summary>
    public int? TopHotspots { get; init; }

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

    private static readonly Dictionary<string, OptionDescriptor> OptionLookup = CreateOptionLookup();

    /// <summary>
    /// Parses <paramref name="args"/>. Returns a populated <see cref="CliOptions"/> on success, or
    /// <c>null</c> with a non-null <paramref name="error"/> describing the first usage problem.
    /// </summary>
    public static CliOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = null;

        var state = new ParseState();
        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];

            // Everything after a bare `--` is the launch argv (program + its args); stop option parsing.
            if (token == "--")
            {
                state.LaunchArgs = new List<string>(args.Count - i - 1);
                for (var j = i + 1; j < args.Count; j++)
                {
                    state.LaunchArgs.Add(args[j]);
                }

                break;
            }

            if (OptionLookup.TryGetValue(token, out var descriptor))
            {
                if (!descriptor.TryApply(args, ref i, state, out error))
                {
                    return null;
                }

                continue;
            }

            if (token.StartsWith('-'))
            {
                error = $"Unknown option '{token}'.";
                return null;
            }

            if (state.Command is not null)
            {
                if (string.Equals(state.Command, "compare", StringComparison.Ordinal))
                {
                    state.ComparePaths.Add(token);
                    continue;
                }

                if (string.Equals(state.Command, "completion", StringComparison.Ordinal)
                    && state.CompletionShell is null)
                {
                    state.CompletionShell = token;
                    continue;
                }

                error = $"Unexpected argument '{token}'. Only one command is accepted.";
                return null;
            }

            state.Command = token;
        }

        return state.Build();
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

    private static bool TryTakeLong(IReadOnlyList<string> args, ref int i, string flag, out long value, out string? error)
    {
        value = 0;
        error = null;
        if (i + 1 >= args.Count)
        {
            error = $"Option '{flag}' requires a value.";
            return false;
        }

        var raw = args[++i];
        if (!long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            error = $"Option '{flag}' expects an integer, got '{raw}'.";
            return false;
        }

        return true;
    }

    private static Dictionary<string, OptionDescriptor> CreateOptionLookup()
    {
        var descriptors = new OptionDescriptor[]
        {
            new FlagOptionDescriptor(state => state.Help = true, "--help", "-h"),
            new FlagOptionDescriptor(state => state.Json = true, "--json"),
            new StringOptionDescriptor((state, value) => state.SavePath = value, "--save"),
            new FlagOptionDescriptor(state => state.UnsafeProvider = true, "--unsafe-provider"),
            new FlagOptionDescriptor(state => state.ExportTrace = true, "--export-trace"),
            new FlagOptionDescriptor(state => state.IncludeRetentionPaths = true, "--include-retention-paths"),
            new FlagOptionDescriptor(state => state.IncludeStaticFields = true, "--include-static-fields"),
            new FlagOptionDescriptor(state => state.IncludeDelegateTargets = true, "--include-delegate-targets"),
            new FlagOptionDescriptor(state => state.IncludeDuplicateStrings = true, "--include-duplicate-strings"),
            new FlagOptionDescriptor(state => state.Confirm = true, "--confirm"),
            new FlagOptionDescriptor(state => state.ChangesOnly = true, "--changes-only"),
            new FlagOptionDescriptor(state => state.Launch = true, "--launch"),
            new FlagOptionDescriptor(state => state.SuspendStartup = true, "--suspend-startup"),
            new PidOptionDescriptor("--pid", "-p"),
            new StringOptionDescriptor((state, value) => state.Kind = value, "--kind"),
            new IntOptionDescriptor((state, value) => state.DurationSeconds = value, "--duration", "-d"),
            new IntOptionDescriptor((state, value) => state.IntervalSeconds = value, "--interval"),
            new IntOptionDescriptor((state, value) => state.MaxEvents = value, "--max-events"),
            new IntOptionDescriptor((state, value) => state.WatchIntervalSeconds = value, "--watch"),
            new StringOptionDescriptor((state, value) => state.CaptureWhen = value, "--capture-when"),
            new StringOptionDescriptor((state, value) => state.CaptureKind = value, "--capture"),
            new IntOptionDescriptor((state, value) => state.MaxCaptures = value, "--max-captures"),
            new IntOptionDescriptor((state, value) => state.WindowSeconds = value, "--window"),
            new StringOptionDescriptor((state, value) => state.Symptom = value, "--symptom"),
            new StringOptionDescriptor((state, value) => state.Hypothesis = value, "--hypothesis"),
            new IntOptionDescriptor((state, value) => state.MaxToolCalls = value, "--max-tool-calls"),
            new IntOptionDescriptor((state, value) => state.TopHotspots = value, "--top-hotspots"),
            new IntOptionDescriptor((state, value) => state.TopTypes = value, "--top-types"),
            new IntOptionDescriptor((state, value) => state.RetentionPathLimit = value, "--retention-path-limit"),
            new StringOptionDescriptor((state, value) => state.Providers.Add(value), "--provider"),
            new StringOptionDescriptor((state, value) => state.Meters.Add(value), "--meter"),
            new StringOptionDescriptor((state, value) => state.Sources.Add(value), "--source"),
            new StringOptionDescriptor((state, value) => state.Categories.Add(value), "--category"),
            new StringOptionDescriptor((state, value) => state.MinLevel = value, "--min-level"),
            new StringOptionDescriptor((state, value) => state.Depth = value, "--depth"),
            new StringOptionDescriptor((state, value) => state.Mode = value, "--mode"),
            new StringOptionDescriptor((state, value) => state.DumpFile = value, "--dump-file"),
            new StringOptionDescriptor((state, value) => state.SymbolPath = value, "--symbol-path"),
            new StringOptionDescriptor((state, value) => state.NativeAotMapFile = value, "--native-aot-map"),
            new FlagOptionDescriptor(state => state.ResolveSourceLines = true, "--resolve-source-lines"),
            new FlagOptionDescriptor(state => state.ResolveSourceLines = false, "--no-resolve-source-lines"),
            new FlagOptionDescriptor(state => state.ResolveMethodInstantiations = true, "--resolve-method-instantiations"),
            new LongOptionDescriptor((state, value) => state.NativeAllocSamplePeriod = value, "--native-alloc-sample-period"),
            new IntOptionDescriptor((state, value) => state.MaxFramesPerThread = value, "--max-frames-per-thread"),
            new FlagOptionDescriptor(state => state.IncludeRuntimeFrames = true, "--include-runtime-frames"),
            new FlagOptionDescriptor(state => state.IncludeNativeFrames = true, "--include-native-frames"),
            new StringOptionDescriptor((state, value) => state.DumpType = value, "--dump-type"),
            new StringOptionDescriptor((state, value) => state.OutDir = value, "--out"),
            new StringOptionDescriptor((state, value) => state.Mvid = value, "--mvid"),
            new StringOptionDescriptor((state, value) => state.Asset = value, "--asset"),
            new StringOptionDescriptor((state, value) => state.Handle = value, "--handle"),
            new StringOptionDescriptor((state, value) => state.View = value, "--view"),
            new StringOptionDescriptor((state, value) => state.RankBy = value, "--rank-by"),
            new StringOptionDescriptor((state, value) => state.TypeFilter = value, "--type-filter"),
            new StringOptionDescriptor((state, value) => state.Address = value, "--address"),
            new StringOptionDescriptor((state, value) => state.RootMethodFilter = value, "--root-method-filter"),
            new StringOptionDescriptor((state, value) => state.ProviderFilter = value, "--provider-filter"),
            new IntOptionDescriptor((state, value) => state.MaxDepth = value, "--max-depth"),
            new IntOptionDescriptor((state, value) => state.MaxNodes = value, "--max-nodes"),
            new IntOptionDescriptor((state, value) => state.Top = value, "--top"),
            new IntOptionDescriptor((state, value) => state.Threshold = value, "--threshold"),
            new IntOptionDescriptor((state, value) => state.ThreadId = value, "--thread-id"),
            new IntOptionDescriptor((state, value) => state.StackRank = value, "--stack-rank"),
            new IntOptionDescriptor((state, value) => state.FramesToHash = value, "--frames-to-hash"),
            new IntOptionDescriptor((state, value) => state.MinCount = value, "--min-count"),
        };

        var lookup = new Dictionary<string, OptionDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            foreach (var alias in descriptor.Aliases)
            {
                lookup.Add(alias, descriptor);
            }
        }

        return lookup;
    }

    private sealed class ParseState
    {
        public string? Command { get; set; }

        public int? Pid { get; set; }

        public string? PidName { get; set; }

        public bool Json { get; set; }

        public string? SavePath { get; set; }

        public List<string> ComparePaths { get; } = new();

        public string? CompletionShell { get; set; }

        public string? Mode { get; set; }

        public bool Help { get; set; }

        public string? Kind { get; set; }

        public int? DurationSeconds { get; set; }

        public List<string> Providers { get; } = new();

        public List<string> Meters { get; } = new();

        public List<string> Sources { get; } = new();

        public List<string> Categories { get; } = new();

        public int? IntervalSeconds { get; set; }

        public int? WatchIntervalSeconds { get; set; }

        public int? MaxEvents { get; set; }

        public string? MinLevel { get; set; }

        public string? Depth { get; set; }

        public bool UnsafeProvider { get; set; }

        public string? DumpFile { get; set; }

        public bool ExportTrace { get; set; }

        public int? TopTypes { get; set; }

        public bool IncludeRetentionPaths { get; set; }

        public int? RetentionPathLimit { get; set; }

        public bool IncludeStaticFields { get; set; }

        public bool IncludeDelegateTargets { get; set; }

        public bool IncludeDuplicateStrings { get; set; }

        public string? SymbolPath { get; set; }

        public string? NativeAotMapFile { get; set; }

        public bool? ResolveSourceLines { get; set; }

        public bool ResolveMethodInstantiations { get; set; }

        public long? NativeAllocSamplePeriod { get; set; }

        public int? MaxFramesPerThread { get; set; }

        public bool IncludeRuntimeFrames { get; set; }

        public bool IncludeNativeFrames { get; set; }

        public string? DumpType { get; set; }

        public string? OutDir { get; set; }

        public bool Confirm { get; set; }

        public string? Mvid { get; set; }

        public string? Asset { get; set; }

        public string? Handle { get; set; }

        public string? View { get; set; }

        public string? RankBy { get; set; }

        public string? TypeFilter { get; set; }

        public string? Address { get; set; }

        public string? RootMethodFilter { get; set; }

        public string? ProviderFilter { get; set; }

        public bool ChangesOnly { get; set; }

        public int? MaxDepth { get; set; }

        public int? MaxNodes { get; set; }

        public int? ThreadId { get; set; }

        public int? StackRank { get; set; }

        public int? FramesToHash { get; set; }

        public int? MinCount { get; set; }

        public int? Top { get; set; }

        public int? Threshold { get; set; }

        public string? CaptureWhen { get; set; }

        public string? CaptureKind { get; set; }

        public int? MaxCaptures { get; set; }

        public int? WindowSeconds { get; set; }

        public string? Symptom { get; set; }

        public string? Hypothesis { get; set; }

        public int? MaxToolCalls { get; set; }

        public int? TopHotspots { get; set; }

        public bool Launch { get; set; }

        public bool SuspendStartup { get; set; }

        public List<string>? LaunchArgs { get; set; }

        public CliOptions Build() =>
            new()
            {
                Command = Command,
                Pid = Pid,
                PidName = PidName,
                Json = Json,
                SavePath = SavePath,
                ComparePaths = ComparePaths,
                CompletionShell = CompletionShell,
                Mode = Mode,
                Help = Help,
                Kind = Kind,
                DurationSeconds = DurationSeconds,
                Providers = Providers,
                Meters = Meters,
                Sources = Sources,
                Categories = Categories,
                IntervalSeconds = IntervalSeconds,
                WatchIntervalSeconds = WatchIntervalSeconds,
                MaxEvents = MaxEvents,
                MinLevel = MinLevel,
                Depth = Depth,
                UnsafeProvider = UnsafeProvider,
                ExportTrace = ExportTrace,
                DumpFile = DumpFile,
                TopTypes = TopTypes,
                IncludeRetentionPaths = IncludeRetentionPaths,
                RetentionPathLimit = RetentionPathLimit,
                IncludeStaticFields = IncludeStaticFields,
                IncludeDelegateTargets = IncludeDelegateTargets,
                IncludeDuplicateStrings = IncludeDuplicateStrings,
                SymbolPath = SymbolPath,
                NativeAotMapFile = NativeAotMapFile,
                ResolveSourceLines = ResolveSourceLines,
                ResolveMethodInstantiations = ResolveMethodInstantiations,
                NativeAllocSamplePeriod = NativeAllocSamplePeriod,
                MaxFramesPerThread = MaxFramesPerThread,
                IncludeRuntimeFrames = IncludeRuntimeFrames,
                IncludeNativeFrames = IncludeNativeFrames,
                DumpType = DumpType,
                OutDir = OutDir,
                Confirm = Confirm,
                Mvid = Mvid,
                Asset = Asset,
                Handle = Handle,
                View = View,
                RankBy = RankBy,
                TypeFilter = TypeFilter,
                Address = Address,
                RootMethodFilter = RootMethodFilter,
                ProviderFilter = ProviderFilter,
                ChangesOnly = ChangesOnly,
                MaxDepth = MaxDepth,
                MaxNodes = MaxNodes,
                ThreadId = ThreadId,
                StackRank = StackRank,
                FramesToHash = FramesToHash,
                MinCount = MinCount,
                Top = Top,
                Threshold = Threshold,
                CaptureWhen = CaptureWhen,
                CaptureKind = CaptureKind,
                MaxCaptures = MaxCaptures,
                WindowSeconds = WindowSeconds,
                Symptom = Symptom,
                Hypothesis = Hypothesis,
                MaxToolCalls = MaxToolCalls,
                TopHotspots = TopHotspots,
                Launch = Launch,
                SuspendStartup = SuspendStartup,
                LaunchArgs = LaunchArgs ?? (IReadOnlyList<string>)Array.Empty<string>(),
            };
    }

    private abstract class OptionDescriptor
    {
        protected OptionDescriptor(params string[] aliases)
        {
            Aliases = aliases;
        }

        public IReadOnlyList<string> Aliases { get; }

        public abstract bool TryApply(IReadOnlyList<string> args, ref int index, ParseState state, out string? error);
    }

    private sealed class FlagOptionDescriptor : OptionDescriptor
    {
        private readonly Action<ParseState> _apply;

        public FlagOptionDescriptor(Action<ParseState> apply, params string[] aliases)
            : base(aliases)
        {
            _apply = apply;
        }

        public override bool TryApply(IReadOnlyList<string> args, ref int index, ParseState state, out string? error)
        {
            _apply(state);
            error = null;
            return true;
        }
    }

    private sealed class StringOptionDescriptor : OptionDescriptor
    {
        private readonly Action<ParseState, string> _apply;

        public StringOptionDescriptor(Action<ParseState, string> apply, params string[] aliases)
            : base(aliases)
        {
            _apply = apply;
        }

        public override bool TryApply(IReadOnlyList<string> args, ref int index, ParseState state, out string? error)
        {
            if (!TryTakeString(args, ref index, args[index], out var value, out error))
            {
                return false;
            }

            _apply(state, value);
            return true;
        }
    }

    private sealed class IntOptionDescriptor : OptionDescriptor
    {
        private readonly Action<ParseState, int> _apply;

        public IntOptionDescriptor(Action<ParseState, int> apply, params string[] aliases)
            : base(aliases)
        {
            _apply = apply;
        }

        public override bool TryApply(IReadOnlyList<string> args, ref int index, ParseState state, out string? error)
        {
            if (!TryTakeInt(args, ref index, args[index], out var value, out error))
            {
                return false;
            }

            _apply(state, value);
            return true;
        }
    }

    private sealed class LongOptionDescriptor : OptionDescriptor
    {
        private readonly Action<ParseState, long> _apply;

        public LongOptionDescriptor(Action<ParseState, long> apply, params string[] aliases)
            : base(aliases)
        {
            _apply = apply;
        }

        public override bool TryApply(IReadOnlyList<string> args, ref int index, ParseState state, out string? error)
        {
            if (!TryTakeLong(args, ref index, args[index], out var value, out error))
            {
                return false;
            }

            _apply(state, value);
            return true;
        }
    }

    private sealed class PidOptionDescriptor : OptionDescriptor
    {
        public PidOptionDescriptor(params string[] aliases)
            : base(aliases)
        {
        }

        public override bool TryApply(IReadOnlyList<string> args, ref int index, ParseState state, out string? error)
        {
            if (!TryTakeString(args, ref index, args[index], out var pidValue, out error))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(pidValue))
            {
                error = $"Option '{args[index - 1]}' requires a non-empty pid or process name.";
                return false;
            }

            if (int.TryParse(pidValue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var numericPid))
            {
                state.Pid = numericPid;
                state.PidName = null;
            }
            else
            {
                state.Pid = null;
                state.PidName = pidValue;
            }

            return true;
        }
    }
}
