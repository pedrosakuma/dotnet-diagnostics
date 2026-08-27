using System.Collections.Generic;
using System.Text;

namespace DotnetDiagnostics.Cli;

/// <summary>
/// Composable help text for the CLI. <see cref="Global"/> is the short orienting screen for a bare
/// <c>--help</c>, no command, or a usage error; <see cref="ForCommand"/> renders the full focused
/// screen for a single subcommand (e.g. <c>collect --help</c>) so the user does not have to scroll
/// unrelated flags to find one command's options (#302, #896). Keeping each command's synopsis,
/// options and examples in one structured table means the compact global screen and the per-command
/// screens are still built from the same source and cannot drift apart.
/// </summary>
internal static class CliHelp
{
    private const string Tagline =
        "dotnet-diagnostics-cli — one-shot diagnostics against a live .NET process (no HTTP, no bearer, no daemon).";
    private const string CompactGlobalOptionsHelpText =
"""
Options:
  -p, --pid <pid|name>          Target OS process id, or visible .NET process name/prefix.
      --json                    Emit the raw DiagnosticResult envelope as JSON.
      --explain-risk            Print the resolved Core safety descriptor without executing.
      --acknowledge-risk <level>
                                Non-interactive acknowledgement for high/critical operations.
      --launch -- <app> [args]  Launch a child app for the commands that support descendant attach.
      --suspend-startup         With 'collect --kind startup', arm EventPipe before managed startup.
  -h, --help                    Show this help or 'dotnet-diagnostics-cli <command> --help'.
""";

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

        sb.Append('\n').Append(CompactGlobalOptionsHelpText).Append('\n');
        sb.Append("Next steps:").Append('\n');
        sb.Append("  Run 'dotnet-diagnostics-cli <command> --help' for command-specific flags and examples.").Append('\n');
        sb.Append("  If something does not work, start with 'dotnet-diagnostics-cli doctor'.");

        return sb.ToString().TrimEnd('\n');
    }
}
