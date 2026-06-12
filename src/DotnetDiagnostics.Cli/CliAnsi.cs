using System.Text.RegularExpressions;

namespace DotnetDiagnostics.Cli;

internal sealed record CliRuntimeOptions(
    bool? ForceAnsi = null,
    int? MaxWatchIterations = null,
    TimeSpan? WatchDelay = null);

internal static partial class CliAnsi
{
    private const string Reset = "\u001b[0m";
    private const string Bold = "\u001b[1m";
    private const string Red = "\u001b[31m";
    private const string Green = "\u001b[32m";
    private const string Yellow = "\u001b[33m";
    private const string Cyan = "\u001b[36m";

    public static bool IsEnabled(TextWriter stdout, bool? forceAnsi)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null)
        {
            return false;
        }

        if (forceAnsi is { } forced)
        {
            return forced;
        }

        return ReferenceEquals(stdout, Console.Out) && !Console.IsOutputRedirected;
    }

    public static string ColorizeHuman(string text, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!enabled || text.Length == 0)
        {
            return text;
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = ColorizeLine(lines[i], isHeadline: i == 0);
        }

        return string.Join('\n', lines);
    }

    public static string ClearScreen(bool enabled) => enabled ? "\u001b[2J\u001b[H" : string.Empty;

    private static string ColorizeLine(string line, bool isHeadline)
    {
        if (line.StartsWith("ERROR:", StringComparison.Ordinal))
        {
            line = string.Concat(Wrap("ERROR:", Red + Bold), line["ERROR:".Length..]);
        }
        else if (isHeadline)
        {
            line = Wrap(line, Cyan + Bold);
        }

        var trimmed = line.Trim();
        if (trimmed.EndsWith(':') && !isHeadline)
        {
            line = Wrap(line, Cyan);
        }

        line = VerdictRegex().Replace(line, match =>
        {
            var value = match.Groups["value"].Value;
            return string.Concat("verdict=", Wrap(value, ColorForValue(value)));
        });

        line = SeverityRegex().Replace(line, match =>
        {
            var label = match.Groups["label"].Value;
            return Wrap(label, ColorForValue(label));
        });

        return line;
    }

    private static string Wrap(string value, string color) => string.Concat(color, value, Reset);

    private static string ColorForValue(string value)
        => value switch
        {
            var v when v.Equals("high", StringComparison.OrdinalIgnoreCase)
                || v.Equals("error", StringComparison.OrdinalIgnoreCase)
                || v.Equals("critical", StringComparison.OrdinalIgnoreCase)
                || v.Equals("regressed", StringComparison.OrdinalIgnoreCase)
                || v.Equals("fail", StringComparison.OrdinalIgnoreCase) => Red + Bold,
            var v when v.Equals("warn", StringComparison.OrdinalIgnoreCase)
                || v.Equals("warning", StringComparison.OrdinalIgnoreCase)
                || v.Equals("degraded", StringComparison.OrdinalIgnoreCase)
                || v.Equals("mixed", StringComparison.OrdinalIgnoreCase) => Yellow + Bold,
            var v when v.Equals("ok", StringComparison.OrdinalIgnoreCase)
                || v.Equals("healthy", StringComparison.OrdinalIgnoreCase)
                || v.Equals("improved", StringComparison.OrdinalIgnoreCase)
                || v.Equals("pass", StringComparison.OrdinalIgnoreCase) => Green + Bold,
            _ => Cyan + Bold,
        };

    [GeneratedRegex(@"\bverdict=(?<value>[A-Za-z_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex VerdictRegex();

    [GeneratedRegex(@"\b(?<label>high|warn|warning|error|critical)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeverityRegex();
}
