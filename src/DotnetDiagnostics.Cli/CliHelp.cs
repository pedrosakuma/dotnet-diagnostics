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

    /// <summary>The full usage screen (every command, options and examples).</summary>
    public static string Global { get; } = BuildGlobal();

    /// <summary>
    /// Returns a focused help screen for <paramref name="command"/>, or <see cref="Global"/> when the
    /// command is not a known CLI command.
    /// </summary>
    public static string ForCommand(string command)
    {
        if (!CliCommandCatalog.TryGetCommand(command, out var match))
        {
            return Global;
        }

        var sb = new StringBuilder();
        sb.Append(Tagline).Append('\n').Append('\n');
        sb.Append("Usage:").Append('\n');
        sb.Append("  dotnet-diagnostics-cli ").Append(match!.Name).Append(" [options]").Append('\n').Append('\n');
        sb.Append(match.Name).Append(": ").Append(match.Synopsis).Append('\n').Append('\n');
        sb.Append(CliCommandCatalog.GlobalOptionsHelpText);
        if (!string.IsNullOrEmpty(match.OptionsHelpText))
        {
            sb.Append('\n').Append('\n').Append(match.OptionsHelpText);
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
        foreach (var c in CliCommandCatalog.CommandDescriptors)
        {
            sb.Append("  ").Append(c.Name.PadRight(28)).Append("  ").Append(c.Synopsis).Append('\n');
        }

        sb.Append('\n').Append(CliCommandCatalog.GlobalOptionsHelpText).Append('\n');

        foreach (var c in CliCommandCatalog.CommandDescriptors)
        {
            if (!string.IsNullOrEmpty(c.OptionsHelpText))
            {
                sb.Append('\n').Append(c.OptionsHelpText).Append('\n');
            }
        }

        sb.Append('\n').Append("Examples:").Append('\n');
        foreach (var c in CliCommandCatalog.CommandDescriptors)
        {
            if (!string.IsNullOrEmpty(c.Examples))
            {
                sb.Append(c.Examples).Append('\n');
            }
        }

        return sb.ToString().TrimEnd('\n');
    }
}
