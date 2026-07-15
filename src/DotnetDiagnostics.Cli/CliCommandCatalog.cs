namespace DotnetDiagnostics.Cli;

internal sealed record CliCommandDescriptor(
    string Name,
    string Synopsis,
    string? OptionsHelpText,
    string Examples,
    IReadOnlyList<string> CompletionOptions);

internal static class CliCommandCatalog
{
    public static readonly IReadOnlyList<string> Shells = ["bash", "zsh", "pwsh"];

    public static readonly IReadOnlyList<string> GlobalOptions =
    [
        "-p",
        "--pid",
        "--json",
        "--launch",
        "-h",
        "--help",
    ];

    public static readonly IReadOnlyList<string> ValueFlags =
    [
        "-p", "--pid", "--kind", "-d", "--duration", "--depth", "--max-events", "--interval",
        "--provider", "--meter", "--source", "--category", "--min-level", "--save", "--dump-file",
        "--top-types", "--retention-path-limit", "--symbol-path", "--native-aot-map", "--dump-type", "--out", "--mvid",
        "--asset", "--handle", "--view", "--provider-filter", "--root-method-filter", "--rank-by",
        "--type-filter", "--address", "--max-depth", "--max-nodes", "--thread-id",
        "--native-alloc-sample-period", "--max-frames-per-thread", "--watch",
        "--frames-to-hash", "--min-count", "--top", "--threshold", "--mode", "--stack-rank",
        "--symptom", "--hypothesis", "--max-tool-calls", "--top-hotspots",
        "--capture-when", "--capture", "--window", "--max-captures",
    ];

    public static readonly IReadOnlyList<string> DepthValues = ["summary", "detail", "raw"];
    public static readonly IReadOnlyList<string> CompareModes = ["trend", "dispersion"];

    public static readonly IReadOnlyList<string> HeapSources = ["live", "dump", "gcdump"];
    public static readonly IReadOnlyList<string> InspectViews = ["triage", "runtime-config", "container"];
    public static readonly IReadOnlyList<string> ByteKinds = ["module", "dump", "trace"];
    public static readonly IReadOnlyList<string> ByteAssets = ["pe", "pdb"];
    public static readonly IReadOnlyList<string> DumpTypes = ["Mini", "Triage", "WithHeap", "Full"];

    public static readonly IReadOnlyList<string> LaunchableCommands =
    [
        "session",
        "capabilities",
        "collect",
        "inspect",
        "dump",
        "inspect-heap",
        "get-bytes",
    ];

    public static readonly IReadOnlyList<string> CollectKinds =
    [
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
    ];

    public const string GlobalOptionsHelpText =
"""
Options:
  -p, --pid <pid|name>          Target OS process id, or visible .NET process name/prefix
                                (auto-resolved when only one is visible).
      --json                    Emit the raw DiagnosticResult envelope as JSON.
      --launch -- <app> [args]  Dev mode: launch <app> as a child of the CLI so live attach
                                (inspect-heap --source live, dump) works under ptrace_scope=1 with
                                no privilege. Supported by capabilities/collect/dump/inspect-heap/
                                get-bytes and 'session'. The child is terminated on exit. Launch the
                                app directly ('dotnet App.dll'), not via 'dotnet run'.
      --suspend-startup         Cold-start capture for 'collect --kind startup' (with --launch):
                                launch the target suspended on a reverse-connect DOTNET_DiagnosticPorts
                                port, arm EventPipe before any managed code runs, then resume — capturing
                                static ctors, DI build, module-init exceptions and startup timings the
                                post-attach path misses. Default OFF.
  -h, --help                    Show this help.
""";

    public static readonly IReadOnlyList<CliCommandDescriptor> CommandDescriptors =
    [
        new(
            "processes",
            "List attachable .NET processes.",
            null,
"""
  dotnet-diagnostics-cli processes
  dotnet-diagnostics-cli processes --json
""",
            []),
        new(
            "capabilities",
            "Probe a target's diagnostic capability matrix.",
            null,
            "  dotnet-diagnostics-cli capabilities --pid 1234",
            []),
        new(
            "doctor",
            "Diagnose the environment (UID, ptrace, perf) and print the exact fix. Target-optional; exits non-zero on a hard blocker.",
            null,
"""
  dotnet-diagnostics-cli doctor                # host-only readiness check
  dotnet-diagnostics-cli doctor --pid 1234     # also verify socket UID vs target
  dotnet-diagnostics-cli doctor --json
""",
            []),
        new(
            "collect",
            "Open an EventPipe session and collect events (--kind required).",
"""
collect options:
      --kind <kind>             Required. One of: counters, exceptions, crash-guard, gc, datas,
                                catalog, event_source, activities, logs, jit, threadpool,
                                contention, db, kestrel, networking, requests, startup, sweep,
                                cpu, allocation, off_cpu (alias off-cpu), native-alloc,
                                thread-snapshot.
  -d, --duration <int>          Collection window in seconds (default: counters 5, datas 15, sweep 6, others 10).
      --depth <level>           Verbosity: summary, detail (default), raw.
      --top <int>               Top-N rows / hotspots for cpu, allocation, off_cpu, native-alloc.
      --max-events <int>        Per-kind cap (events / exceptions / activities).
      --interval <int>          Refresh interval in seconds (counters, db, kestrel, networking). Default 1.
      --symbol-path <path>      NT_SYMBOL_PATH-style search path for cpu, off_cpu and
                                thread-snapshot symbol resolution.
      --export-trace            cpu: persist the raw .nettrace under the artifact root.
      --resolve-source-lines    cpu: resolve top hotspots to source file:line (default on).
      --no-resolve-source-lines cpu: disable source file:line resolution.
      --resolve-method-instantiations
                                cpu: opt in to ClrMD generic-instantiation enrichment.
      --native-alloc-sample-period <int>
                                native-alloc: perf sample period (default 1000).
      --dump-file <path>        thread-snapshot: inspect a previously-captured dump instead of a live pid.
      --max-frames-per-thread <int>
                                thread-snapshot: cap frames captured per thread (default 64).
      --include-runtime-frames  thread-snapshot: include CLR/runtime helper frames.
      --include-native-frames   thread-snapshot: include native frames.
      --watch <seconds>         Re-run and redraw every N seconds until Ctrl-C.
                                With --capture-when: metric sample interval (default 2) for the
                                bounded gated-capture watch (no redraw loop).
      --capture-when <pred>     Threshold-gated capture: arm a bounded watch and capture when the
                                metric trips, e.g. 'cpu>85', 'gcHeapMb>=500', 'rssMb>1024',
                                'threadCount>200', 'activeTimerCount>1000'. Requires --kind counters.
      --capture <kind>          What to capture on trip: dump, cpu-sample, heap, thread-snapshot.
      --window <seconds>        Required with --capture-when. Bounds the watch (max 300s).
      --max-captures <int>      Stop after N captures (default 1, max 10).
      --native-aot-map <path>   NativeAOT only (Linux / perf path). Path to the ILC *.map.xml
                                produced by publishing with <IlcGenerateMapFile>true</IlcGenerateMapFile>.
                                When supplied with --capture cpu-sample, the AOT sampler emits
                                name-based MethodIdentity for managed frames instead of raw
                                addresses. Optional and inert for CoreCLR targets. Requires
                                --capture cpu-sample; silently ignored on Windows (ETW path).
      --provider <name>         counters: EventCounter provider (repeatable);
                                catalog: EventPipe provider (repeatable; replaces broad defaults);
                                event_source: required provider name.
      --meter <name>            counters: Meter name (repeatable).
      --source <name>           activities: ActivitySource filter (repeatable, * / ? globs).
      --category <glob>         logs: ILogger category filter (repeatable).
      --min-level <level>       logs: minimum level (default Information).
      --threshold <ms>          requests: long-running flag threshold in ms (default 1000).
      --max-events <int>        requests: also caps the in-flight requests returned (default 100).
      --unsafe-provider         event_source: opt in to a non-allowlisted provider.
      --save <file>             Save a comparable snapshot JSON (supports counters, datas, gc, contention, threadpool).
""",
"""
  dotnet-diagnostics-cli collect --kind counters --pid 1234 --duration 5
  dotnet-diagnostics-cli collect --kind counters --pid MyApp --watch 2
  dotnet-diagnostics-cli collect --kind counters --pid MyApp --capture-when 'cpu>85' --capture cpu-sample --window 60
  dotnet-diagnostics-cli collect --kind cpu --pid 1234 --top 20 --export-trace
  dotnet-diagnostics-cli collect --kind off_cpu --pid 1234 --top 10 --symbol-path /symbols
  dotnet-diagnostics-cli collect --kind allocation --pid 1234 --top 15
  dotnet-diagnostics-cli collect --kind native-alloc --pid 1234 --native-alloc-sample-period 500
  dotnet-diagnostics-cli collect --kind thread-snapshot --pid 1234 --max-frames-per-thread 128
  dotnet-diagnostics-cli collect --kind datas --pid 1234 --save ./before.json
  dotnet-diagnostics-cli collect --kind event_source --provider System.Net.Http --pid 1234
  dotnet-diagnostics-cli collect --kind requests --pid MyApp --duration 5 --threshold 2000  # in-flight requests
  dotnet-diagnostics-cli collect --kind startup --suspend-startup --launch -- dotnet App.dll  # cold start
""",
            [
                "--kind",
                "-d",
                "--duration",
                "--depth",
                "--top",
                "--max-events",
                "--interval",
                "--symbol-path",
                "--export-trace",
                "--resolve-source-lines",
                "--no-resolve-source-lines",
                "--resolve-method-instantiations",
                "--native-alloc-sample-period",
                "--dump-file",
                "--max-frames-per-thread",
                "--include-runtime-frames",
                "--include-native-frames",
                "--watch",
                "--suspend-startup",
                "--provider",
                "--meter",
                "--source",
                "--category",
                "--min-level",
                "--unsafe-provider",
                "--save",
                "--capture-when",
                "--capture",
                "--window",
                "--max-captures",
                "--native-aot-map",
            ]),
        new(
            "inspect",
            "One-shot process inspector: evidence-backed workload triage or runtime configuration (--view required).",
"""
inspect options:
      --view <view>             Required. One of: triage, runtime-config, container.
  -d, --duration <int>          triage: counter collection window in seconds (default 5).
inspect views:
  triage          Collect counters for <duration>s, separate observed signals from evidence-backed
                  hypotheses, and return neutral next steps. Each hypothesis includes confidence and
                  supporting/contradicting evidence. Legacy verdict fields are deprecated until v1.0.
  runtime-config  Read the process's effective runtime configuration: GC mode, ThreadPool bounds,
                  tiered-compilation flags, runtime env vars and AppContext switches.
  container       Read cgroup/container CPU, memory, PSI, pid-limit and OOM signals for the target.
""",
"""
  dotnet-diagnostics-cli inspect --view triage --pid 1234
  dotnet-diagnostics-cli inspect --view triage --pid 1234 --duration 10
  dotnet-diagnostics-cli inspect --view runtime-config --pid 1234
  dotnet-diagnostics-cli inspect --view container --pid 1234
  dotnet-diagnostics-cli inspect --view triage --json
""",
            ["--view", "-d", "--duration"]),
        new(
            "inspect-heap",
            "Walk the managed heap of a live process or a .dmp (--source live|dump|gcdump).",
"""
inspect-heap options:
      --source <live|dump|gcdump>  Snapshot source (default: inferred — dump when --dump-file is set, else live).
      --dump-file <path>        --source dump: path to a previously-captured .dmp.
      --top-types <int>         Top-N type count (default 20).
      --include-retention-paths Walk a short GC retention chain for the top types.
      --retention-path-limit <int>  Cap retention-chain depth (default 8).
      --include-static-fields   Rank static reference fields by referenced object size.
      --include-delegate-targets  Group MulticastDelegate invocation lists by (target, method).
      --include-duplicate-strings Rank duplicate strings by aggregate retained bytes.
      --symbol-path <path>      NT_SYMBOL_PATH-style search path (remote servers off by default).
      --export-trace            --source gcdump: persist the raw .nettrace under the artifact root
                                (fetch later with get-bytes --kind trace).
""",
"""
  dotnet-diagnostics-cli inspect-heap --pid 1234 --top-types 30
  dotnet-diagnostics-cli inspect-heap --source dump --dump-file ./app.dmp
  dotnet-diagnostics-cli inspect-heap --source gcdump --pid 1234   # EventPipe, no ptrace, prod-safe
  dotnet-diagnostics-cli inspect-heap --launch -- dotnet App.dll   # ptrace_scope=1, no privilege
""",
            [
                "--source",
                "--dump-file",
                "--top-types",
                "--include-retention-paths",
                "--retention-path-limit",
                "--include-static-fields",
                "--include-delegate-targets",
                "--include-duplicate-strings",
                "--symbol-path",
                "--export-trace",
            ]),
        new(
            "dump",
            "Write a process dump to disk (requires --confirm).",
"""
dump options:
      --dump-type <type>        Mini (default), Triage, WithHeap or Full.
      --out <dir>               Directory to write the dump into (default: temp artifact root).
      --confirm                 Required to actually write; without it a preview is returned.
  Scripting: a preview (run without --confirm) is a success and exits 0. To tell a preview apart
  from a written dump, parse --json: data.kind == "confirmation_required" (preview) versus
  data.kind == "dump_written" (a dump was written to disk).
""",
            "  dotnet-diagnostics-cli dump --pid 1234 --dump-type WithHeap --out ./dumps --confirm",
            ["--dump-type", "--out", "--confirm"]),
        new(
            "query",
            "Drill-down query (unsupported in the one-shot CLI — see notes).",
"""
query options:
      --handle <id>             Drill-down handle (accepted but not honoured — see note).
      --view <name>             Drill-down view (accepted but not honoured — see note).
      --provider-filter <text>  Session query: event-catalog provider substring filter.
      --changes-only            Session query: DATAS 'tuning' view; show only heap-count changes.
      --root-method-filter <t>  Session query: CPU method filter; event-catalog event-name filter.
      --thread-id <int>         Session query: ManagedThreadId; required for thread-snapshot 'frame-vars' view.
      --stack-rank <int>        Session query: 1-based rank for the off-CPU 'stack' view.
  Note: drill-down handles are MCP-session scoped; the one-shot CLI emits its full result
  inline on the originating command (use --depth detail / --json). 'query' always returns a
  NotSupported envelope (exit 1).

  Thread-snapshot views (session only):
    threads-summary  List all threads with state and top frame.
    stack            Full managed stack for --thread-id <ManagedThreadId>.
    lock-graph       Monitor lock ownership graph.
    deadlocks        Deadlock cycles.
    top-blocked      Threads most likely blocked (default).
    unique-stacks    Deduplicated stacks by hash.
    async-stalls     Async continuation stalls.
    wait-chains      Thread wait-chain analysis.
    threadpool       Thread-pool queue/worker statistics.
    frame-vars       Object-typed locals/parameters on each frame via ClrMD re-open; requires --thread-id.
""",
            string.Empty,
            [
                "--handle",
                "--view",
                "--provider-filter",
                "--changes-only",
                "--root-method-filter",
                "--rank-by",
                "--type-filter",
                "--address",
                "--max-depth",
                "--max-nodes",
                "--thread-id",
                "--frames-to-hash",
                "--min-count",
                "--top",
                "--threshold",
                "--stack-rank",
            ]),
        new(
            "get-bytes",
            "Materialise a module (PE/PDB) or dump file to disk (--out required).",
"""
get-bytes options:
      --kind <module|dump|trace>  Required. Artifact to materialise.
      --out <file>              Required. Destination file the artifact is written to.
      --mvid <guid>             --kind module: module version id (GUID) to fetch.
      --asset <pe|pdb>          --kind module: artifact within the module (default pe).
      --dump-file <path>        --kind dump|trace: path to the source .dmp / .nettrace to copy out.
""",
"""
  dotnet-diagnostics-cli get-bytes --kind module --pid 1234 --mvid <guid> --out ./app.dll
  dotnet-diagnostics-cli get-bytes --kind dump --dump-file ./app.dmp --out ./copy.dmp
  dotnet-diagnostics-cli get-bytes --kind trace --dump-file ./cpu.nettrace --out ./cpu.copy.nettrace
""",
            ["--kind", "--out", "--mvid", "--asset", "--dump-file"]),
        new(
            "compare",
            "Compare two or more comparable snapshot JSON files.",
"""
compare options:
      --json                    Emit the full SnapshotJourneyDiff JSON.
      --save <file>             Write the full journey diff JSON to disk.
      --mode <mode>             Journey mode: trend (default) or dispersion.
""",
"""
  dotnet-diagnostics-cli compare ./before.json ./after.json
  dotnet-diagnostics-cli compare ./a.json ./b.json ./c.json --mode dispersion --save ./matrix.json
""",
            ["--json", "--save", "--mode"]),
        new(
            "investigate",
            "Plan a .NET performance investigation and get the recommended first step.",
"""
investigate options:
      --pid <id>                Target process id (auto-resolved when only one .NET process is visible).
      --symptom <text>          Plain-language symptom, e.g. 'high latency on /checkout since v2'. Required for cold mode.
      --hypothesis <text>       Specific hypothesis to test, e.g. 'lock contention on Cart.Checkout'. Triggers hypothesis mode.
      --max-tool-calls <int>    Hard limit on recommended tool calls before forcing summarization. Default 8.
""",
"""
  dotnet-diagnostics-cli investigate --pid 1234 --symptom 'high CPU after deploy'
  dotnet-diagnostics-cli investigate --pid 1234 --hypothesis 'lock contention in Cart.Checkout'
  dotnet-diagnostics-cli investigate --json
""",
            ["--symptom", "--hypothesis", "--max-tool-calls"]),
        new(
            "export-summary",
            "Export a portable investigation summary JSON from a prior CPU-sample handle (session only).",
"""
export-summary options:
      --handle <id>             Required. CPU-sample handle from a prior 'collect --kind cpu' command.
      --out <file>              Write the summary to a file (default: stdout).
      --top-hotspots <int>      Max hotspots to include in the summary. Default 10.

  Note: export-summary requires a CPU-sample handle, which is only available in a 'session' REPL
  after running 'collect --kind cpu'. The JSON is portable: paste it into a PR or ADR and
  supply it to a future investigation to compare baselines (server is stateless — you own persistence).
""",
"""
  diag> collect --kind cpu --pid 1234            # obtain a handle
  diag> export-summary --handle <id>             # print JSON to stdout
  diag> export-summary --handle <id> --out ./inv.json
""",
            ["--handle", "--out", "--top-hotspots"]),
        new(
            "session",
            "Start a stateful REPL that keeps collected handles queryable across commands.",
"""
session notes:
  Builds the diagnostic host once and reads commands from stdin until 'exit'/'quit'/EOF. Handles
  published by 'collect' stay alive (until they expire or the target exits), so you can drill in with
  'query --handle <id> --view <view>' without re-collecting. Ctrl-C cancels the running command and
  keeps the session alive; press it again to force-quit. An idle Ctrl-C leaves the session.
  Start with '--launch -- <app> [args]' to spawn the target as a child and bind it for the whole
  session (so live attach works under ptrace_scope=1 with no privilege); the child is killed on exit.
""",
"""
  dotnet-diagnostics-cli session
  diag> collect --kind gc --pid 1234
  diag> query --handle <id> --view pauseHistogram
  diag> exit

  # Thread-snapshot frame-vars drilldown (capture a thread-snapshot handle first):
  diag> collect --kind counters --pid 1234 --capture-when 'cpu>0' --capture thread-snapshot --window 30
  diag> query --handle <id> --view threads-summary
  diag> query --handle <id> --view frame-vars --thread-id 7
  diag> exit

  dotnet-diagnostics-cli session --launch -- dotnet App.dll   # binds the launched child for the session
""",
            []),
        new(
            "completion",
            "Emit a shell-completion script for bash, zsh or PowerShell.",
"""
completion options:
  No flags. Pass the target shell as a positional argument: bash, zsh or pwsh.
""",
"""
  dotnet-diagnostics-cli completion bash
  dotnet-diagnostics-cli completion zsh
  dotnet-diagnostics-cli completion pwsh
""",
            []),
    ];

    public static IReadOnlyList<string> CommandNames { get; } =
        CommandDescriptors.Select(static descriptor => descriptor.Name).ToArray();

    public static bool TryGetCommand(string name, out CliCommandDescriptor? descriptor)
    {
        foreach (var candidate in CommandDescriptors)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                descriptor = candidate;
                return true;
            }
        }

        descriptor = null;
        return false;
    }
}
