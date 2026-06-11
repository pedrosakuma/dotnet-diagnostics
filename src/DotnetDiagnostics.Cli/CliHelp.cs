using System.Collections.Generic;
using System.Text;

namespace DotnetDiagnostics.Cli;

/// <summary>
/// Composable help text for the CLI. <see cref="Global"/> reproduces the full usage screen
/// (printed for a bare <c>--help</c>, no command, or a usage error); <see cref="ForCommand"/>
/// renders a focused screen for a single subcommand (e.g. <c>collect --help</c>) so the user does
/// not have to scroll the entire reference to find one command's flags (#302). Keeping each
/// command's synopsis, options and examples in one structured table means the global screen and the
/// per-command screens are built from the same source and cannot drift apart.
/// </summary>
internal static class CliHelp
{
    private const string Tagline =
        "dotnet-diagnostics-cli — one-shot diagnostics against a live .NET process (no HTTP, no bearer, no daemon).";

    private const string GlobalOptions =
"""
Options:
  -p, --pid <int>               Target OS process id (auto-resolved when only one is visible).
      --json                    Emit the raw DiagnosticResult envelope as JSON.
      --launch -- <app> [args]  Dev mode: launch <app> as a child of the CLI so live attach
                                (inspect-heap --source live, dump) works under ptrace_scope=1 with
                                no privilege. Supported by capabilities/collect/dump/inspect-heap/
                                get-bytes and 'session'. The child is terminated on exit. Launch the
                                app directly ('dotnet App.dll'), not via 'dotnet run'.
  -h, --help                    Show this help.
""";

    private sealed record CommandHelp(string Name, string Synopsis, string? Options, string Examples);

    private static readonly IReadOnlyList<CommandHelp> Commands = new[]
    {
        new CommandHelp(
            "processes",
            "List attachable .NET processes.",
            null,
"""
  dotnet-diagnostics-cli processes
  dotnet-diagnostics-cli processes --json
"""),
        new CommandHelp(
            "capabilities",
            "Probe a target's diagnostic capability matrix.",
            null,
            "  dotnet-diagnostics-cli capabilities --pid 1234"),
        new CommandHelp(
            "collect",
            "Open an EventPipe session and collect events (--kind required).",
"""
collect options:
      --kind <kind>             Required. One of: counters, exceptions, gc, datas, catalog,
                                event_source, activities, logs, jit, threadpool, contention, db, startup.
  -d, --duration <int>          Collection window in seconds (default: counters 5, datas 15, others 10).
      --depth <level>           Verbosity: summary, detail (default), raw.
      --max-events <int>        Per-kind cap (events / exceptions / activities).
      --interval <int>          Refresh interval in seconds (counters, db). Default 1.
      --provider <name>         counters: EventCounter provider (repeatable);
                                catalog: EventPipe provider (repeatable; replaces broad defaults);
                                event_source: required provider name.
      --meter <name>            counters: Meter name (repeatable).
      --source <name>           activities: ActivitySource filter (repeatable, * / ? globs).
      --category <glob>         logs: ILogger category filter (repeatable).
      --min-level <level>       logs: minimum level (default Information).
      --unsafe-provider         event_source: opt in to a non-allowlisted provider.
      --save <file>             Save a comparable snapshot JSON (supports counters, datas, gc, contention, threadpool).
""",
"""
  dotnet-diagnostics-cli collect --kind counters --pid 1234 --duration 5
  dotnet-diagnostics-cli collect --kind datas --pid 1234 --save ./before.json
  dotnet-diagnostics-cli collect --kind event_source --provider System.Net.Http --pid 1234
"""),
        new CommandHelp(
            "inspect-heap",
            "Walk the managed heap of a live process or a .dmp (--source live|dump).",
"""
inspect-heap options:
      --source <live|dump>      Snapshot source (default: inferred — dump when --dump-file is set, else live).
      --dump-file <path>        --source dump: path to a previously-captured .dmp.
      --top-types <int>         Top-N type count (default 20).
      --include-retention-paths Walk a short GC retention chain for the top types.
      --retention-path-limit <int>  Cap retention-chain depth (default 8).
      --include-static-fields   Rank static reference fields by referenced object size.
      --include-delegate-targets  Group MulticastDelegate invocation lists by (target, method).
      --include-duplicate-strings Rank duplicate strings by aggregate retained bytes.
      --symbol-path <path>      NT_SYMBOL_PATH-style search path (remote servers off by default).
""",
"""
  dotnet-diagnostics-cli inspect-heap --pid 1234 --top-types 30
  dotnet-diagnostics-cli inspect-heap --source dump --dump-file ./app.dmp
  dotnet-diagnostics-cli inspect-heap --launch -- dotnet App.dll   # ptrace_scope=1, no privilege
"""),
        new CommandHelp(
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
            "  dotnet-diagnostics-cli dump --pid 1234 --dump-type WithHeap --out ./dumps --confirm"),
        new CommandHelp(
            "query",
            "Drill-down query (unsupported in the one-shot CLI — see notes).",
"""
query options:
      --handle <id>             Drill-down handle (accepted but not honoured — see note).
      --view <name>             Drill-down view (accepted but not honoured — see note).
      --provider-filter <text>  Session query: event-catalog provider substring filter.
      --changes-only            Session query: DATAS 'tuning' view; show only heap-count changes.
      --root-method-filter <t>  Session query: CPU method filter; event-catalog event-name filter.
  Note: drill-down handles are MCP-session scoped; the one-shot CLI emits its full result
  inline on the originating command (use --depth detail / --json). 'query' always returns a
  NotSupported envelope (exit 1).
""",
            string.Empty),
        new CommandHelp(
            "get-bytes",
            "Materialise a module (PE/PDB) or dump file to disk (--out required).",
"""
get-bytes options:
      --kind <module|dump>      Required. Artifact to materialise.
      --out <file>              Required. Destination file the artifact is written to.
      --mvid <guid>             --kind module: module version id (GUID) to fetch.
      --asset <pe|pdb>          --kind module: artifact within the module (default pe).
      --dump-file <path>        --kind dump: path to the source .dmp to copy out.
""",
"""
  dotnet-diagnostics-cli get-bytes --kind module --pid 1234 --mvid <guid> --out ./app.dll
  dotnet-diagnostics-cli get-bytes --kind dump --dump-file ./app.dmp --out ./copy.dmp
"""),
        new CommandHelp(
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
"""),
        new CommandHelp(
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

  dotnet-diagnostics-cli session --launch -- dotnet App.dll   # binds the launched child for the session
"""),
        new CommandHelp(
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
"""),
    };

    /// <summary>The full usage screen (every command, options and examples).</summary>
    public static string Global { get; } = BuildGlobal();

    /// <summary>
    /// Returns a focused help screen for <paramref name="command"/>, or <see cref="Global"/> when the
    /// command is not a known CLI command.
    /// </summary>
    public static string ForCommand(string command)
    {
        CommandHelp? match = null;
        foreach (var c in Commands)
        {
            if (string.Equals(c.Name, command, System.StringComparison.Ordinal))
            {
                match = c;
                break;
            }
        }

        if (match is null)
        {
            return Global;
        }

        var sb = new StringBuilder();
        sb.Append(Tagline).Append('\n').Append('\n');
        sb.Append("Usage:").Append('\n');
        sb.Append("  dotnet-diagnostics-cli ").Append(match.Name).Append(" [options]").Append('\n').Append('\n');
        sb.Append(match.Name).Append(": ").Append(match.Synopsis).Append('\n').Append('\n');
        sb.Append(GlobalOptions);
        if (!string.IsNullOrEmpty(match.Options))
        {
            sb.Append('\n').Append('\n').Append(match.Options);
        }

        if (!string.IsNullOrEmpty(match.Examples))
        {
            sb.Append('\n').Append('\n').Append("Examples:").Append('\n').Append(match.Examples);
        }

        return sb.ToString();
    }

    private static string BuildGlobal()
    {
        var sb = new StringBuilder();
        sb.Append(Tagline).Append('\n').Append('\n');
        sb.Append("Usage:").Append('\n');
        sb.Append("  dotnet-diagnostics-cli <command> [options]").Append('\n').Append('\n');

        sb.Append("Commands:").Append('\n');
        foreach (var c in Commands)
        {
            sb.Append("  ").Append(c.Name.PadRight(28)).Append("  ").Append(c.Synopsis).Append('\n');
        }

        sb.Append('\n').Append(GlobalOptions).Append('\n');

        foreach (var c in Commands)
        {
            if (!string.IsNullOrEmpty(c.Options))
            {
                sb.Append('\n').Append(c.Options).Append('\n');
            }
        }

        sb.Append('\n').Append("Examples:").Append('\n');
        foreach (var c in Commands)
        {
            if (!string.IsNullOrEmpty(c.Examples))
            {
                sb.Append(c.Examples).Append('\n');
            }
        }

        return sb.ToString().TrimEnd('\n');
    }
}
