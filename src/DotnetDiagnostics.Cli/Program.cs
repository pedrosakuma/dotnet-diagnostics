namespace DotnetDiagnostics.Cli;

/// <summary>
/// Entry point for the standalone <c>dotnet-diagnostics</c> CLI (issue #288, the packaging step of
/// the #283 CLI roadmap). Delegates straight to <see cref="CliHost"/> so the lifecycle, Ctrl-C
/// handling and exit-code policy live in one testable place. This project references <b>Core only</b>
/// — it has no knowledge of the MCP server, HTTP transport or bearer auth.
/// </summary>
internal static class Program
{
    private static Task<int> Main(string[] args) => CliHost.RunAsync(args);
}
