using System.Globalization;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.ThreadPool;

namespace DotnetDiagnostics.Cli;

/// <summary>
/// Projects the MCP-audience <see cref="NextActionHint"/>s authored in Core into vocabulary the
/// standalone CLI actually owns (issue #301). The shared Core engine names <b>MCP tools</b>
/// (<c>collect_events</c>, <c>inspect_heap</c>, <c>query_snapshot</c>, …) and embeds MCP call syntax
/// (<c>confirm=true</c>, <c>dumpType='WithHeap'</c>, the <c>dotnet-assembly-mcp</c> handoff) in hint
/// reasons — none of which exist in the one-shot CLI. Rendering those verbatim points a human at
/// commands that are absent (CPU sampling / thread snapshots) or <c>NotSupported</c> (<c>query</c>).
/// </summary>
/// <remarks>
/// <para>The projection runs once on the <see cref="DiagnosticResult{T}"/> before <b>both</b> the
/// human table and the <c>--json</c> envelope are produced, so neither leaks. For each hint it:</para>
/// <list type="number">
///   <item>translates <see cref="NextActionHint.NextTool"/> MCP→CLI via <see cref="ToolToCommand"/>,
///   dropping any hint whose tool has no CLI equivalent;</item>
///   <item>drops <see cref="NextActionHint.SuggestedArguments"/> entirely (it carries MCP argument
///   names like <c>processId</c>/<c>dumpType</c>/<c>handle</c> that would leak through JSON);</item>
///   <item>rewrites the few <see cref="NextActionHint.Reason"/> fragments that embed MCP call syntax;
///   and</item>
///   <item><b>fails closed</b>: if a projected hint still contains a known MCP token it is dropped
///   rather than rendered, so a future Core hint can never silently leak.</item>
/// </list>
/// </remarks>
internal static class CliHintProjection
{
    /// <summary>
    /// MCP tool name → CLI command. A hint whose <see cref="NextActionHint.NextTool"/> is absent from
    /// this map has no CLI equivalent (e.g. <c>collect_sample</c>, <c>collect_thread_snapshot</c>,
    /// <c>query_snapshot</c>, <c>query_heap_snapshot</c>, <c>dotnet-assembly-mcp.get_method</c>) and is
    /// dropped.
    /// </summary>
    private static readonly Dictionary<string, string> ToolToCommand = new(StringComparer.Ordinal)
    {
        ["collect_events"] = "collect",
        ["inspect_process"] = "processes",
        ["list_dotnet_processes"] = "processes",
        ["inspect_heap"] = "inspect-heap",
        ["inspect_live_heap"] = "inspect-heap",
        ["inspect_dump"] = "inspect-heap",
        ["collect_process_dump"] = "dump",
        // Hints authored on the CLI side already use these verbs verbatim — keep them stable.
        ["collect"] = "collect",
        ["inspect-heap"] = "inspect-heap",
        ["dump"] = "dump",
        ["processes"] = "processes",
        ["capabilities"] = "capabilities",
        ["get-bytes"] = "get-bytes",
        ["query"] = "query",
        ["session"] = "session",
    };

    /// <summary>
    /// Ordered (specific → general) rewrites for the handful of Core hint reasons that embed MCP call
    /// syntax. Applied sequentially; the <see cref="LeakTokens"/> backstop drops anything still leaking
    /// afterwards.
    /// </summary>
    private static readonly (string From, string To)[] ReasonRewrites =
    {
        ("inspect_heap(source=\"dump\")", "inspect-heap --source dump"),
        ("inspect_heap/inspect_dump", "inspect-heap"),
        ("collect_process_dump", "dump"),
        ("inspect_live_heap", "inspect-heap"),
        ("inspect_dump", "inspect-heap"),
        ("inspect_heap", "inspect-heap"),
        ("dumpType='WithHeap'", "--dump-type WithHeap"),
        ("confirm=true", "--confirm"),
        (" + handoff payload to dotnet-assembly-mcp", ""),
        // Threadpool collector note: keep the MinThreads/MaxThreads fact, drop the MCP-only
        // collect_thread_snapshot follow-up the one-shot CLI cannot act on (#302).
        (" Use collect_thread_snapshot(view=\"threadpool\") when a ptrace-backed snapshot is acceptable.", ""),
        ("outputDirectory", "--out"),
        ("processId", "--pid"),
    };

    /// <summary>
    /// MCP-only tokens that must never survive into CLI hint output. Doubles as the fail-closed
    /// backstop for <see cref="TryProjectHint"/> and as the denylist a guard test scans the rendered
    /// human + JSON output for. Uses the underscore/MCP spellings so the hyphenated CLI verbs
    /// (<c>inspect-heap</c>) and underscore <i>argument values</i> (<c>event_source</c>) are not
    /// false-positives.
    /// </summary>
    internal static readonly IReadOnlyList<string> LeakTokens = new[]
    {
        "collect_events",
        "collect_sample",
        "collect_thread_snapshot",
        "collect_off_cpu_sample",
        "collect_process_dump",
        "inspect_process",
        "inspect_live_heap",
        "inspect_dump",
        "inspect_heap",
        "list_dotnet_processes",
        "processId",
        "collect_cpu_sample",
        "query_snapshot",
        "query_heap_snapshot",
        "dotnet-assembly-mcp",
        "confirm=true",
        "dumpType=",
        "(handle=",
        "view=\"",
    };

    /// <summary>
    /// Returns a copy of <paramref name="result"/> whose <see cref="DiagnosticResult{T}.Hints"/> have
    /// been projected into CLI vocabulary (dropping any without a CLI equivalent) and whose
    /// <see cref="DiagnosticResult{T}.Summary"/> has had MCP argument vocabulary (e.g. <c>processId</c>)
    /// rewritten to CLI flags. The rest of the envelope is untouched.
    /// </summary>
    public static DiagnosticResult<T> Project<T>(DiagnosticResult<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var summary = SanitizeReason(result.Summary);
        if (result.Hints.Count == 0)
        {
            return string.Equals(summary, result.Summary, StringComparison.Ordinal)
                ? result
                : result with { Summary = summary };
        }

        var projected = new List<NextActionHint>(result.Hints.Count);
        foreach (var hint in result.Hints)
        {
            if (TryProjectHint(hint, out var cliHint))
            {
                projected.Add(cliHint);
            }
        }

        return result with { Hints = projected, Summary = summary };
    }

    /// <summary>
    /// Projects a single hint. Returns <see langword="false"/> when the hint has no CLI equivalent or
    /// would still leak MCP vocabulary after rewriting (fail-closed).
    /// </summary>
    internal static bool TryProjectHint(NextActionHint hint, out NextActionHint projected)
    {
        ArgumentNullException.ThrowIfNull(hint);
        projected = hint;

        if (!ToolToCommand.TryGetValue(hint.NextTool, out var cliCommand))
        {
            return false;
        }

        var reason = SanitizeReason(hint.Reason);
        if (ContainsLeak(cliCommand) || ContainsLeak(reason))
        {
            return false;
        }

        // Drop SuggestedArguments: it carries MCP argument names that would leak through --json.
        projected = new NextActionHint(cliCommand, reason);
        return true;
    }

    private static string SanitizeReason(string reason)
    {
        foreach (var (from, to) in ReasonRewrites)
        {
            if (reason.Contains(from, StringComparison.Ordinal))
            {
                reason = reason.Replace(from, to, StringComparison.Ordinal);
            }
        }

        return reason;
    }

    private static bool ContainsLeak(string value)
    {
        foreach (var token in LeakTokens)
        {
            if (value.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rewrites the <c>dump</c> confirmation-required preview envelope (whose Core <see cref="string"/>
    /// summary and <c>Message</c> name <c>collect_process_dump</c> / <c>confirm=true</c>) into
    /// CLI-facing text. Non-preview dump results pass through unchanged. Kept here next to the hint
    /// projection so all CLI-vocabulary rewriting lives in one place.
    /// </summary>
    public static DiagnosticResult<Core.Dump.DumpToolResult> RewriteDumpPreview(DiagnosticResult<Core.Dump.DumpToolResult> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Data is not { Kind: Core.Dump.DumpToolResultKinds.ConfirmationRequired } preview)
        {
            return result;
        }

        var target = preview.TargetPid is { } pid
            ? string.Create(CultureInfo.InvariantCulture, $"pid {pid}")
            : "the auto-resolved .NET process";
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"preview only — no dump written. Re-run with --confirm to write a {preview.DumpType} dump for {target}.");
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"Re-run with --confirm to write the {preview.DumpType} dump to disk.");

        return result with { Summary = summary, Data = preview with { Message = message } };
    }

    /// <summary>
    /// Replaces the MCP-audience capability narrative (Core's <see cref="DiagnosticCapabilities.Notes"/>,
    /// which names MCP-only tools such as <c>collect_off_cpu_sample</c>, <c>collect_thread_snapshot</c>,
    /// <c>inspect_process(view=resources)</c>) with a concise CLI-authored note built from the typed
    /// capability fields (#302). The note covers only what the one-shot CLI actually exposes — the
    /// live-attach host gate that decides whether <c>inspect-heap --source live</c> and <c>dump</c>
    /// can attach — so it can never leak MCP vocabulary. The full structured capability matrix
    /// (off-CPU / perf / ptrace booleans) is still emitted verbatim in the <c>--json</c> envelope.
    /// </summary>
    public static DiagnosticResult<DiagnosticCapabilities> ProjectCapabilities(DiagnosticResult<DiagnosticCapabilities> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Data is not { } caps)
        {
            return result;
        }

        var cliNotes = BuildCliCapabilityNotes(caps);

        // Core's capability summary embeds the original MCP-vocabulary Notes verbatim
        // (ProcessInspectionUseCases: "Runtime: … gcdump: …. {Notes}"). Swap that same substring for
        // the CLI-authored note so the human summary line stops leaking MCP tool names too.
        var summary = result.Summary;
        if (!string.IsNullOrEmpty(caps.Notes) && summary.Contains(caps.Notes, StringComparison.Ordinal))
        {
            summary = summary.Replace(caps.Notes, cliNotes, StringComparison.Ordinal);
        }

        return result with { Summary = summary, Data = caps with { Notes = cliNotes } };
    }

    internal static string BuildCliCapabilityNotes(DiagnosticCapabilities caps)
    {
        ArgumentNullException.ThrowIfNull(caps);

        var parts = new List<string>(2);
        if (caps.Runtime == RuntimeFlavor.Unknown)
        {
            parts.Add("Runtime flavor could not be classified (the EventPipe probe may have failed); inspect the capability flags in --json.");
        }

        if (caps.CanAttachClrMD)
        {
            parts.Add("Live attach for `inspect-heap --source live` and `dump` is available.");
        }
        else
        {
            // AttachClrMdReason is Core prose that may name MCP tools (e.g. "use the dump-based
            // workflow (collect_process_dump + inspect_dump)"); sanitize it and fail closed to a
            // generic message if anything still leaks.
            var reason = SanitizeReason(caps.AttachClrMdReason ?? string.Empty);
            parts.Add(!string.IsNullOrWhiteSpace(reason) && !ContainsLeak(reason)
                ? $"Live attach for `inspect-heap --source live` and `dump` is unavailable: {reason}"
                : "Live attach for `inspect-heap --source live` and `dump` is unavailable.");
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Sanitizes the free-text <see cref="ThreadPoolEventSnapshot.Notes"/> (surfaced in the
    /// <c>collect --kind threadpool --json</c> envelope), where Core points the LLM at the MCP-only
    /// <c>collect_thread_snapshot</c> tool. Each note is rewritten to CLI vocabulary; any note that
    /// still names an MCP tool after rewriting is dropped (fail-closed), matching the hint projection
    /// boundary (#302).
    /// </summary>
    public static DiagnosticResult<ThreadPoolEventSnapshot> ProjectThreadPoolNotes(DiagnosticResult<ThreadPoolEventSnapshot> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Data is not { } snapshot || snapshot.Notes.Count == 0)
        {
            return result;
        }

        var notes = new List<string>(snapshot.Notes.Count);
        foreach (var note in snapshot.Notes)
        {
            var sanitized = SanitizeReason(note);
            if (!ContainsLeak(sanitized))
            {
                notes.Add(sanitized);
            }
        }

        return result with { Data = snapshot with { Notes = notes } };
    }
}
