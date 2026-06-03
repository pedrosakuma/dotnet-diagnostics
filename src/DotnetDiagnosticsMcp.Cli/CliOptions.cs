namespace DotnetDiagnosticsMcp.Cli;

/// <summary>
/// Parsed flags for a single <c>dotnet-diagnostics</c> invocation. Hand-rolled (no command-line
/// library dependency yet) and deliberately small — the first slice of the standalone CLI (#288)
/// only ships the read-only <c>processes</c> and <c>capabilities</c> commands; the collection /
/// heap / dump / drilldown flags arrive as those commands are wired in later PRs.
/// </summary>
internal sealed class CliOptions
{
    /// <summary>The sub-command (e.g. <c>processes</c>), or null when none was supplied.</summary>
    public string? Command { get; init; }

    /// <summary>Target OS process id (<c>--pid</c>). Optional — collectors auto-resolve the lone visible .NET process.</summary>
    public int? Pid { get; init; }

    /// <summary>Emit the raw <see cref="DotnetDiagnosticsMcp.Core.DiagnosticResult{T}"/> envelope as JSON (<c>--json</c>).</summary>
    public bool Json { get; init; }

    /// <summary>True when <c>--help</c>/<c>-h</c> was supplied.</summary>
    public bool Help { get; init; }

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
        var json = false;
        var help = false;

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            switch (token)
            {
                case "--help":
                case "-h":
                    help = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--pid":
                case "-p":
                    if (!TryTakeInt(args, ref i, token, out var pidValue, out error))
                    {
                        return null;
                    }

                    pid = pidValue;
                    break;
                default:
                    if (token.StartsWith('-'))
                    {
                        error = $"Unknown option '{token}'.";
                        return null;
                    }

                    if (command is not null)
                    {
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
            Json = json,
            Help = help,
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
}
