using System.Globalization;

namespace DotnetDiagnostics.Cli;

/// <summary>
/// Computes tab-completion candidates for the <c>session</c> REPL (issue #657). Kept independent of
/// PrettyPrompt (pure strings in, strings out) so it can be unit tested without a real terminal;
/// <see cref="SessionReplPromptCallbacks"/> is the thin adapter that wraps this in
/// <c>PrettyPrompt.Completion.CompletionItem</c>.
/// </summary>
/// <remarks>
/// Mirrors (but does not share code with — the two run in very different contexts) the
/// <c>prev</c>-token switch <see cref="CliCompletionScripts"/> emits into the bash/zsh/pwsh
/// completion scripts: given the token immediately before the word being completed, offer that
/// flag's known values (<see cref="CliCommandCatalog.CollectKinds"/>, <c>DumpTypes</c>, ...); with no
/// enum candidates for a recognized value-flag, offer nothing (never fall through to flag names as a
/// flag's value); otherwise offer the command name (first word) or that command's flags.
/// </remarks>
internal static class SessionReplCompletion
{
    /// <summary>REPL-only built-ins that are not one-shot CLI commands (see <c>SessionRepl.SessionHelp</c>).</summary>
    private static readonly string[] ReplBuiltins = ["help", "exit", "quit", "target", "use"];

    /// <summary>
    /// Returns completion candidates for the word being typed. <paramref name="tokensBeforeWord"/> is
    /// every whitespace-separated token before the word under the cursor (so <c>tokensBeforeWord[0]</c>,
    /// when present, is the command name); <paramref name="typedPrefix"/> is what has been typed of the
    /// current word so far (case-insensitive prefix filter). <paramref name="boundPid"/> is the
    /// session's <c>target</c>-bound pid, if any (offered as the sole <c>--pid</c>/<c>-p</c> candidate).
    /// </summary>
    public static IReadOnlyList<string> GetCandidates(
        IReadOnlyList<string> tokensBeforeWord,
        string typedPrefix,
        int? boundPid)
    {
        ArgumentNullException.ThrowIfNull(tokensBeforeWord);
        ArgumentNullException.ThrowIfNull(typedPrefix);

        var candidates = ResolveCandidates(tokensBeforeWord, boundPid);
        if (candidates.Count == 0)
        {
            return [];
        }

        return candidates
            .Where(candidate => candidate.StartsWith(typedPrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static candidate => candidate, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> ResolveCandidates(IReadOnlyList<string> tokensBeforeWord, int? boundPid)
    {
        if (tokensBeforeWord.Count == 0)
        {
            // Completing the very first word: a one-shot command name, or a REPL-only built-in.
            return [.. CliCommandCatalog.CommandNames, .. ReplBuiltins];
        }

        var command = tokensBeforeWord[0];
        var prevToken = tokensBeforeWord[^1];

        return prevToken switch
        {
            "--kind" => string.Equals(command, "get-bytes", StringComparison.Ordinal)
                ? CliCommandCatalog.ByteKinds
                : CliCommandCatalog.CollectKinds,
            "--source" when string.Equals(command, "inspect-heap", StringComparison.Ordinal) => CliCommandCatalog.HeapSources,
            "--dump-type" => CliCommandCatalog.DumpTypes,
            "--asset" => CliCommandCatalog.ByteAssets,
            "--depth" => CliCommandCatalog.DepthValues,
            "--mode" => CliCommandCatalog.CompareModes,
            "--view" when string.Equals(command, "inspect", StringComparison.Ordinal) => CliCommandCatalog.InspectViews,
            "-p" or "--pid" => boundPid is { } pid ? [pid.ToString(CultureInfo.InvariantCulture)] : [],
            _ when CliCommandCatalog.ValueFlags.Contains(prevToken, StringComparer.Ordinal) =>
                // A value-taking flag we have no enum candidates for (e.g. --out, --save, --handle,
                // --symbol-path): never fall through to offering flag names as its value.
                [],
            _ => GetFlagCandidates(command),
        };
    }

    private static IReadOnlyList<string> GetFlagCandidates(string command)
        => CliCommandCatalog.TryGetCommand(command, out var descriptor) && descriptor is not null
            ? [.. CliCommandCatalog.GlobalOptions, .. descriptor.CompletionOptions]
            : CliCommandCatalog.GlobalOptions;

    /// <summary>
    /// Whether a character is treated as part of a CLI token for completion purposes. PrettyPrompt's
    /// own default word-boundary logic only recognizes letters/digits/underscore, which excludes
    /// <c>-</c> — but CLI tokens (<c>--kind</c>, <c>get-bytes</c>) use <c>-</c> routinely, both as a
    /// flag prefix and inside hyphenated command names. Used by <see cref="GetReplacementSpan"/> (and
    /// mirrored by <see cref="SessionReplPromptCallbacks"/>, which is PrettyPrompt-coupled and not
    /// independently unit-testable).
    /// </summary>
    internal static bool IsCliTokenCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';

    /// <summary>
    /// Computes the (start, length) span of the CLI token containing <paramref name="caret"/>, widening
    /// PrettyPrompt's default letters/digits/underscore-only span to also include <c>-</c> (see
    /// <see cref="IsCliTokenCharacter"/>) so replacing it with a full flag/command candidate (which
    /// itself includes any leading dashes) does not duplicate them.
    /// </summary>
    internal static (int Start, int Length) GetReplacementSpan(string text, int caret)
    {
        var start = caret;
        while (start > 0 && IsCliTokenCharacter(text[start - 1]))
        {
            start--;
        }

        var end = caret;
        while (end < text.Length && IsCliTokenCharacter(text[end]))
        {
            end++;
        }

        return (start, end - start);
    }

    /// <summary>
    /// Whether the character just typed at <c>caret - 1</c> starts a new <c>-</c>/<c>--</c> flag token
    /// — either at the very beginning of the line, right after whitespace, or completing the second
    /// dash of a long flag. PrettyPrompt's default auto-open heuristic only triggers on a *letter*
    /// starting a new word, so without this, typing <c>collect -</c> or <c>--k</c> would never
    /// auto-open the completion window, defeating flag-value completion entirely.
    /// </summary>
    internal static bool IsStartingFlagToken(string text, int caret)
        => caret >= 1 && caret <= text.Length && text[caret - 1] == '-'
            && (caret == 1 || char.IsWhiteSpace(text[caret - 2]) || text[caret - 2] == '-');
}
