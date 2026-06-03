namespace DotnetDiagnosticsMcp.Server.Cli;

/// <summary>
/// Parsed flags for a single <c>diag</c> host-mode invocation (issue #287). Deliberately tiny —
/// the Phase-0 spike (#283) only needs enough surface to exercise the host-neutral seam, not a
/// full CLI parser. The real <c>diag</c> CLI project (#288) will replace this with a proper
/// command-line library.
/// </summary>
internal sealed class DiagOptions
{
    /// <summary>The sub-command (e.g. <c>processes</c>), or null when none was supplied.</summary>
    public string? Command { get; init; }

    /// <summary>Target OS process id (<c>--pid</c>). Optional — collectors auto-resolve the lone visible .NET process.</summary>
    public int? Pid { get; init; }

    /// <summary>Emit the raw <see cref="DotnetDiagnosticsMcp.Core.DiagnosticResult{T}"/> envelope as JSON (<c>--json</c>).</summary>
    public bool Json { get; init; }

    /// <summary>Collection window in seconds (<c>--duration</c>); applies to <c>collect</c>.</summary>
    public int? DurationSeconds { get; init; }

    /// <summary>EventPipe collector kind (<c>--kind</c>) for <c>collect</c>. Defaults to <c>counters</c>.</summary>
    public string? Kind { get; init; }

    /// <summary>EventSource provider name (<c>--provider</c>) for <c>collect --kind event_source</c>.</summary>
    public string? Provider { get; init; }

    /// <summary>Heap backend (<c>--source</c>) for <c>inspect-heap</c>. Defaults to <c>live</c>.</summary>
    public string? Source { get; init; }

    /// <summary>Dump file path (<c>--dump-path</c>) for <c>inspect-heap --source dump</c>.</summary>
    public string? DumpPath { get; init; }

    /// <summary>Number of top types to return (<c>--top</c>) for <c>inspect-heap</c>.</summary>
    public int? Top { get; init; }

    /// <summary>True when <c>--help</c>/<c>-h</c> was supplied.</summary>
    public bool Help { get; init; }

    /// <summary>
    /// Parses <paramref name="args"/> (already stripped of the leading <c>diag</c> token). Returns a
    /// populated <see cref="DiagOptions"/> on success, or a non-null <paramref name="error"/> string
    /// describing the first usage problem encountered.
    /// </summary>
    public static DiagOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = null;

        string? command = null;
        int? pid = null;
        var json = false;
        int? duration = null;
        string? kind = null;
        string? provider = null;
        string? source = null;
        string? dumpPath = null;
        int? top = null;
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
                    if (!TryTakeInt(args, ref i, token, out var pidValue, out error)) return null;
                    pid = pidValue;
                    break;
                case "--duration":
                case "-d":
                    if (!TryTakeInt(args, ref i, token, out var durationValue, out error)) return null;
                    duration = durationValue;
                    break;
                case "--top":
                    if (!TryTakeInt(args, ref i, token, out var topValue, out error)) return null;
                    top = topValue;
                    break;
                case "--kind":
                case "-k":
                    if (!TryTakeString(args, ref i, token, out kind, out error)) return null;
                    break;
                case "--provider":
                    if (!TryTakeString(args, ref i, token, out provider, out error)) return null;
                    break;
                case "--source":
                    if (!TryTakeString(args, ref i, token, out source, out error)) return null;
                    break;
                case "--dump-path":
                    if (!TryTakeString(args, ref i, token, out dumpPath, out error)) return null;
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

        return new DiagOptions
        {
            Command = command,
            Pid = pid,
            Json = json,
            DurationSeconds = duration,
            Kind = kind,
            Provider = provider,
            Source = source,
            DumpPath = dumpPath,
            Top = top,
            Help = help,
        };
    }

    private static bool TryTakeString(IReadOnlyList<string> args, ref int i, string flag, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (i + 1 >= args.Count)
        {
            error = $"Option '{flag}' requires a value.";
            return false;
        }

        value = args[++i];
        return true;
    }

    private static bool TryTakeInt(IReadOnlyList<string> args, ref int i, string flag, out int value, out string? error)
    {
        value = 0;
        if (!TryTakeString(args, ref i, flag, out var raw, out error))
        {
            return false;
        }

        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            error = $"Option '{flag}' expects an integer, got '{raw}'.";
            return false;
        }

        return true;
    }
}
