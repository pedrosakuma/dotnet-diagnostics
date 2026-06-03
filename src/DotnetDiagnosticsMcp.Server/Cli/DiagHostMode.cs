using System.Text.Json;
using DotnetDiagnosticsMcp.Core.Symbols;
using DotnetDiagnosticsMcp.Server.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace DotnetDiagnosticsMcp.Server.Cli;

/// <summary>
/// The <c>diag</c> host-mode shell (issue #287, Phase-0 spike of the standalone CLI in #283). Runs
/// one-shot diagnostic commands against the shared Core engine and exits — <b>no HTTP listener, no
/// bearer token, no daemon</b>. It proves the host-neutral seam (the Core use-case layer extracted
/// in #285) end-to-end at the lowest possible packaging cost, before the separate
/// <c>DotnetDiagnosticsMcp.Cli</c> project (#288) is stood up.
/// </summary>
/// <remarks>
/// <para>Lifecycle: the shell builds a <see cref="Microsoft.Extensions.Hosting.IHost"/> only to
/// resolve the Core service graph — it never calls <c>RunAsync</c>/<c>StartAsync</c>, so no
/// <c>IHostedService</c> ever starts. The host is disposed (which disposes singleton collectors)
/// before the process exits.</para>
/// <para>Cancellation: the shell owns the <see cref="CancellationTokenSource"/> wired to the first
/// Ctrl-C and passes the token into the collectors, whose <c>finally</c> blocks stop the EventPipe
/// session and delete the <c>.nettrace</c> temp. This is best-effort — a forced second interrupt or
/// SIGKILL can still bypass cleanup.</para>
/// </remarks>
public static class DiagHostMode
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Production entry point — wired from <c>Program.cs</c> when <c>args[0] == "diag"</c>. Owns the
    /// Ctrl-C handler and writes to the real console. Returns the process exit code.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        using var cts = new CancellationTokenSource();
        var cancelRequested = 0;
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            // First Ctrl-C: cancel cooperatively so the collectors' finally blocks stop the session
            // and delete the .nettrace temp. A second Ctrl-C falls through to the default (hard)
            // termination so a wedged collector can never trap the process.
            if (Interlocked.Exchange(ref cancelRequested, 1) == 0)
            {
                e.Cancel = true;
                // ReSharper disable once AccessToDisposedClosure — the using scope outlives every await below.
                cts.Cancel();
            }
        };

        Console.CancelKeyPress += handler;
        try
        {
            return await RunAsync(args, Console.Out, Console.Error, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    /// <summary>
    /// Testable core: parses <paramref name="args"/> (including the leading <c>diag</c> token),
    /// builds the Core host, dispatches the command and renders to the supplied writers. Exit codes:
    /// <c>0</c> success · <c>1</c> error envelope · <c>2</c> usage error · <c>130</c> cancelled.
    /// </summary>
    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        // Strip the leading "diag" token (Program.cs matched it to route here).
        var commandArgs = args.Length > 0 && string.Equals(args[0], "diag", StringComparison.Ordinal)
            ? args[1..]
            : args;

        var options = DiagOptions.Parse(commandArgs, out var parseError);
        if (parseError is not null)
        {
            await stderr.WriteLineAsync(parseError).ConfigureAwait(false);
            await stderr.WriteLineAsync().ConfigureAwait(false);
            await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        if (options!.Help)
        {
            await stdout.WriteLineAsync(Usage).ConfigureAwait(false);
            return 0;
        }

        if (options.Command is null)
        {
            await stderr.WriteLineAsync("No command specified.").ConfigureAwait(false);
            await stderr.WriteLineAsync().ConfigureAwait(false);
            await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        if (!DiagCommands.Commands.Contains(options.Command, StringComparer.Ordinal))
        {
            await stderr.WriteLineAsync($"Unknown command '{options.Command}'.").ConfigureAwait(false);
            await stderr.WriteLineAsync().ConfigureAwait(false);
            await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        using var host = BuildHost();

        try
        {
            var result = await DiagCommands.RunAsync(host.Services, options, cancellationToken).ConfigureAwait(false);

            if (options.Json)
            {
                await stdout.WriteLineAsync(JsonSerializer.Serialize(result.Envelope, result.Envelope.GetType(), JsonOptions))
                    .ConfigureAwait(false);
            }
            else
            {
                await stdout.WriteLineAsync(result.Human).ConfigureAwait(false);
            }

            // A collector that swallows OperationCanceledException returns a partial envelope flagged
            // Cancelled (no Error) — surface it as the cancelled exit code, not success.
            if (result.Cancelled)
            {
                await stderr.WriteLineAsync($"diag {options.Command}: cancelled mid-window — diagnostic session stopped and temp files cleaned up. Payload is partial.")
                    .ConfigureAwait(false);
                return 130;
            }

            return result.IsError ? 1 : 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await stderr.WriteLineAsync($"diag {options.Command}: cancelled — diagnostic session stopped and temp files cleaned up.")
                .ConfigureAwait(false);
            return 130;
        }
    }

    private static IHost BuildHost()
    {
        // Pass NO command-line args to the host builder: the diag command/flags are not configuration
        // and the default command-line config provider rejects bare positionals like "processes".
        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());

        // stdout carries the table / JSON; route every log to stderr (mirrors --stdio mode) and keep
        // it quiet by default so machine-readable stdout stays clean.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.IncludeScopes = false;
            o.SingleLine = true;
        });
        builder.Logging.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var configuredSymbolPath = Environment.GetEnvironmentVariable(SymbolPathBuilder.McpSymbolPathEnvironmentVariable);
        builder.Services.AddDiagnosticCoreServices(configuredSymbolPath, builder.Configuration);

        return builder.Build();
    }

    internal const string Usage =
        """
        dotnet-diagnostics-mcp diag — one-shot diagnostics against a live .NET process (no HTTP, no bearer).

        Usage:
          dotnet-diagnostics-mcp diag <command> [options]

        Commands:
          processes                     List attachable .NET processes.
          capabilities                  Probe a target's diagnostic capability matrix.
          collect                       Run an EventPipe collector (counters, gc, exceptions, …).
          inspect-heap                  Walk a managed heap (live process or dump file).

        Options:
          -p, --pid <int>               Target OS process id (auto-resolved when only one is visible).
          -d, --duration <int>          collect: collection window in seconds.
          -k, --kind <name>             collect: counters|exceptions|gc|event_source|activities|logs|jit|threadpool|contention|db. Default: counters.
              --provider <name>         collect --kind event_source: EventSource provider name.
              --source <live|dump>      inspect-heap: backend. Default: live.
              --dump-path <path>        inspect-heap --source dump: dump file path.
              --top <int>               inspect-heap: number of top types to return. Default: 20.
              --json                    Emit the raw DiagnosticResult envelope as JSON.
          -h, --help                    Show this help.

        Examples:
          dotnet-diagnostics-mcp diag processes
          dotnet-diagnostics-mcp diag capabilities --pid 1234
          dotnet-diagnostics-mcp diag collect --kind counters --duration 5 --pid 1234 --json
          dotnet-diagnostics-mcp diag inspect-heap --source dump --dump-path ./app.dmp --json
        """;
}
