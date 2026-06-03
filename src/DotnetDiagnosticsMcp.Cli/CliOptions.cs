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

    /// <summary>EventPipe collection kind for the <c>collect</c> command (<c>--kind</c>): counters, exceptions, gc, event_source, activities, logs, jit, threadpool, contention, db.</summary>
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

    /// <summary>Maximum events/records (<c>--max-events</c>) — maps to the per-kind cap (maxEvents/maxRecent/maxActivities). Null applies the per-kind default.</summary>
    public int? MaxEvents { get; init; }

    /// <summary>Minimum log level (<c>--min-level</c>) for <c>kind=logs</c>. Null applies the default (Information).</summary>
    public string? MinLevel { get; init; }

    /// <summary>Verbosity (<c>--depth</c>): summary, detail, or raw. Null applies the default (summary).</summary>
    public string? Depth { get; init; }

    /// <summary>Opt-in switch (<c>--unsafe-provider</c>) for non-allowlisted <c>kind=event_source</c> providers.</summary>
    public bool UnsafeProvider { get; init; }

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
        string? kind = null;
        int? durationSeconds = null;
        var providers = new List<string>();
        var meters = new List<string>();
        var sources = new List<string>();
        var categories = new List<string>();
        int? intervalSeconds = null;
        int? maxEvents = null;
        string? minLevel = null;
        string? depth = null;
        var unsafeProvider = false;

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
                case "--unsafe-provider":
                    unsafeProvider = true;
                    break;
                case "--pid":
                case "-p":
                    if (!TryTakeInt(args, ref i, token, out var pidValue, out error))
                    {
                        return null;
                    }

                    pid = pidValue;
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
            Kind = kind,
            DurationSeconds = durationSeconds,
            Providers = providers,
            Meters = meters,
            Sources = sources,
            Categories = categories,
            IntervalSeconds = intervalSeconds,
            MaxEvents = maxEvents,
            MinLevel = minLevel,
            Depth = depth,
            UnsafeProvider = unsafeProvider,
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
