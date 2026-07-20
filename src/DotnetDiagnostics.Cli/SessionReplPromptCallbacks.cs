using PrettyPrompt;
using PrettyPrompt.Completion;
using PrettyPrompt.Consoles;
using PrettyPrompt.Documents;

namespace DotnetDiagnostics.Cli;

/// <summary>
/// Wires <see cref="SessionReplCompletion"/> (tab-completion) into a real <see cref="PrettyPrompt.Prompt"/>
/// for the interactive <c>session</c> REPL (issue #657). Only constructed when
/// <see cref="SessionRepl"/> has confirmed both stdin and stdout are a genuine, non-redirected
/// terminal — PrettyPrompt cannot read keys from a redirected/piped stdin (it throws), which is why
/// the plain <see cref="TextReader.ReadLineAsync()"/> path stays the only path for tests and CI.
/// </summary>
/// <remarks>
/// <para><b>History</b> comes for free: PrettyPrompt's default <see cref="KeyBindings"/> already bind
/// Up/Down-arrow (and Ctrl-P/Ctrl-N) to in-memory history navigation across successive
/// <see cref="Prompt.ReadLineAsync()"/> calls on the same instance — nothing to wire here. History is
/// intentionally <b>not</b> persisted to disk (no <c>persistentHistoryFilepath</c>): session commands
/// routinely carry pids, file paths and dump/query handles, and a session-scoped, in-memory-only
/// history is the conservative default until there's an explicit opt-in for persistence.</para>
/// <para><b>Ctrl-D / EOF.</b> PrettyPrompt has no built-in EOF handling for a raw terminal (Ctrl-D is
/// otherwise silently swallowed), unlike the plain reader path where an EOF on <see cref="Console.In"/>
/// ends the session. <see cref="GetKeyPressCallbacks"/> binds it explicitly to submit the same "exit"
/// text the plain path recognizes, so the observable behavior — session ends cleanly, exit code 0 — is
/// identical either way.</para>
/// <para><b>Ctrl-C.</b> PrettyPrompt puts the terminal in raw mode with signal generation (ISIG)
/// disabled while <see cref="Prompt.ReadLineAsync()"/> is awaiting a line, so the OS never delivers
/// SIGINT and <see cref="SessionRepl"/>'s own <see cref="Console.CancelKeyPress"/> hook does not fire —
/// PrettyPrompt handles the keypress itself and returns <c>IsSuccess=false</c>, which the caller maps to
/// the same "idle Ctrl-C cancels the session" outcome. Once a line is submitted and a command starts
/// running, PrettyPrompt is no longer reading, the terminal is back in normal (ISIG-enabled) mode, and
/// <see cref="SessionRepl"/>'s existing two-tier <c>Console.CancelKeyPress</c> handler resumes exactly as
/// it does on the plain reader path — verified empirically (see PR description), not by unit test, since
/// it depends on real raw-terminal semantics no test harness here exercises.</para>
/// <para><b>Completion span/trigger.</b> PrettyPrompt's defaults are C#-identifier-oriented: the word
/// span used for replacement only spans letters/digits/underscore (excluding <c>-</c>), and the
/// auto-open heuristic only triggers on a letter starting a new word. Both are overridden below —
/// CLI tokens (<c>--kind</c>, <c>get-bytes</c>) treat <c>-</c> as an ordinary token character, and
/// flag completion needs to trigger on the leading dash(es) themselves, not just the letters after
/// them.</para>
/// </remarks>
internal sealed class SessionReplPromptCallbacks : PromptCallbacks
{
    /// <summary>Text substituted for Ctrl-D, matching the plain reader path's EOF-exits-cleanly behavior.</summary>
    internal const string EofExitText = "exit";

    private readonly Func<int?> _getBoundPid;

    public SessionReplPromptCallbacks(Func<int?> getBoundPid)
    {
        _getBoundPid = getBoundPid ?? throw new ArgumentNullException(nameof(getBoundPid));
    }

    protected override IEnumerable<(KeyPressPattern Pattern, KeyPressCallbackAsync Callback)> GetKeyPressCallbacks()
    {
        yield return (new KeyPressPattern(ConsoleModifiers.Control, ConsoleKey.D), OnControlD);
    }

    private static Task<KeyPressCallbackResult?> OnControlD(string text, int caret, CancellationToken cancellationToken)
        => Task.FromResult<KeyPressCallbackResult?>(new KeyPressCallbackResult(EofExitText, EofExitText));

    /// <summary>
    /// PrettyPrompt's default word-span (letters/digits/underscore only) excludes <c>-</c>, so completing
    /// a flag like <c>--kind</c> would only span the letters after the dashes and — since our candidates
    /// are full flag tokens including the leading dashes — end up inserting them a second time (e.g.
    /// <c>----kind</c>). CLI tokens (command names like <c>get-bytes</c>, flags like <c>--pid</c>) all use
    /// <c>-</c> as a normal token character, so we widen the word-boundary scan to include it, mirroring
    /// the base implementation otherwise.
    /// </summary>
    protected override Task<TextSpan> GetSpanToReplaceByCompletionAsync(string text, int caret, CancellationToken cancellationToken)
    {
        var (start, length) = SessionReplCompletion.GetReplacementSpan(text, caret);
        return Task.FromResult(new TextSpan(start, length));
    }

    /// <summary>
    /// The default auto-open heuristic (see base <c>ShouldOpenCompletionWindowAsync</c>) only triggers
    /// when the character just typed at the start of a new word is a letter — CLI flags start with
    /// <c>-</c>, so typing <c>collect -</c> or <c>--k</c> would never auto-open the window without this
    /// override, defeating flag-value completion entirely.
    /// </summary>
    protected override Task<bool> ShouldOpenCompletionWindowAsync(string text, int caret, KeyPress keyPress, CancellationToken cancellationToken)
    {
        if (SessionReplCompletion.IsStartingFlagToken(text, caret))
        {
            return Task.FromResult(true);
        }

        return base.ShouldOpenCompletionWindowAsync(text, caret, keyPress, cancellationToken);
    }

    protected override Task<IReadOnlyList<CompletionItem>> GetCompletionItemsAsync(
        string text, int caret, TextSpan spanToBeReplaced, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);

        var typedPrefix = spanToBeReplaced.Length > 0 && spanToBeReplaced.End <= text.Length
            ? text.Substring(spanToBeReplaced.Start, spanToBeReplaced.Length)
            : string.Empty;
        var tokensBeforeWord = text[..Math.Min(spanToBeReplaced.Start, text.Length)]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var candidates = SessionReplCompletion.GetCandidates(tokensBeforeWord, typedPrefix, _getBoundPid());

        IReadOnlyList<CompletionItem> items = candidates
            .Select(static candidate => new CompletionItem(replacementText: candidate))
            .ToArray();
        return Task.FromResult(items);
    }
}
