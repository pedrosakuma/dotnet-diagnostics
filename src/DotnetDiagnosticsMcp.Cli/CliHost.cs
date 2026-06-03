using System.Text.Json;
using DotnetDiagnosticsMcp.Core.Hosting;
using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Core.Symbols;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace DotnetDiagnosticsMcp.Cli;

/// <summary>
/// The standalone <c>dotnet-diagnostics</c> CLI shell (issue #288). Runs one-shot diagnostic
/// commands against the shared Core engine and exits — <b>no HTTP listener, no bearer token, no
/// daemon, and (critically for the #283 seam) no reference to the MCP server assembly</b>. It
/// composes the host-neutral Core service graph (<see cref="DiagnosticCoreServiceRegistration"/>,
/// made public in #284) directly, binding <see cref="SecurityOptions"/> from configuration exactly
/// as the server does so the two front-ends share one security posture.
/// </summary>
/// <remarks>
/// <para>Lifecycle: the shell builds an <see cref="IHost"/> only to resolve the Core service graph —
/// it never calls <c>RunAsync</c>/<c>StartAsync</c>, so no <c>IHostedService</c> ever starts. The
/// host is disposed (which disposes singleton collectors) before the process exits.</para>
/// <para>Cancellation: the shell owns the <see cref="CancellationTokenSource"/> wired to the first
/// Ctrl-C and passes the token into the use cases. A second Ctrl-C falls through to the default
/// (hard) termination so a wedged operation can never trap the process.</para>
/// </remarks>
internal static class CliHost
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Production entry point (from <see cref="Program"/>). Owns the Ctrl-C handler and writes to the
    /// real console. Returns the process exit code.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        using var cts = new CancellationTokenSource();
        var cancelRequested = 0;
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            // First Ctrl-C: cancel cooperatively so the use cases stop any session and clean up temp
            // files. A second Ctrl-C falls through to the default (hard) termination so a wedged
            // operation can never trap the process.
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
    /// Testable core: parses <paramref name="args"/>, builds the Core host, dispatches the command and
    /// renders to the supplied writers. Exit codes: <c>0</c> success · <c>1</c> error envelope ·
    /// <c>2</c> usage error · <c>130</c> cancelled.
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

        var options = CliOptions.Parse(args, out var parseError);
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

        if (!CliCommands.Commands.Contains(options.Command, StringComparer.Ordinal))
        {
            await stderr.WriteLineAsync($"Unknown command '{options.Command}'.").ConfigureAwait(false);
            await stderr.WriteLineAsync().ConfigureAwait(false);
            await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        if (options.Command == "collect" && !CliCommands.TryValidateCollect(options, out var collectError))
        {
            await stderr.WriteLineAsync(collectError).ConfigureAwait(false);
            await stderr.WriteLineAsync().ConfigureAwait(false);
            await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        if (options.Command == "inspect-heap" && !CliCommands.TryValidateInspectHeap(options, out var heapError))
        {
            await stderr.WriteLineAsync(heapError).ConfigureAwait(false);
            await stderr.WriteLineAsync().ConfigureAwait(false);
            await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        if (options.Command == "dump" && !CliCommands.TryValidateDump(options, out var dumpError))
        {
            await stderr.WriteLineAsync(dumpError).ConfigureAwait(false);
            await stderr.WriteLineAsync().ConfigureAwait(false);
            await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        using var host = BuildHost(options);

        try
        {
            var result = await CliCommands.RunAsync(host.Services, options, cancellationToken).ConfigureAwait(false);

            if (options.Json)
            {
                await stdout.WriteLineAsync(JsonSerializer.Serialize(result.Envelope, result.Envelope.GetType(), JsonOptions))
                    .ConfigureAwait(false);
            }
            else
            {
                await stdout.WriteLineAsync(result.Human).ConfigureAwait(false);
            }

            // A use case that swallows OperationCanceledException returns a partial envelope flagged
            // Cancelled (no Error) — surface it as the cancelled exit code, not success.
            if (result.Cancelled)
            {
                await stderr.WriteLineAsync($"dotnet-diagnostics {options.Command}: cancelled mid-window — diagnostic session stopped and temp files cleaned up. Payload is partial.")
                    .ConfigureAwait(false);
                return 130;
            }

            return result.IsError ? 1 : 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await stderr.WriteLineAsync($"dotnet-diagnostics {options.Command}: cancelled — diagnostic session stopped and temp files cleaned up.")
                .ConfigureAwait(false);
            return 130;
        }
    }

    private static IHost BuildHost(CliOptions options)
    {
        // `dump --out <dir>` selects where the dump file lands. The Core dumper treats its
        // outputDirectory argument as a *relative* sub-path under the artifact root and rejects
        // absolute paths, so the CLI maps --out onto the artifact root itself (resolved absolute) and
        // passes a null sub-path. EnvironmentArtifactRootProvider reads this env var once on
        // construction; setting it here (before AddDiagnosticCoreServices resolves the singleton) is
        // safe for a one-shot process.
        if (!string.IsNullOrWhiteSpace(options.OutDir))
        {
            Environment.SetEnvironmentVariable(
                DotnetDiagnosticsMcp.Core.Artifacts.EnvironmentArtifactRootProvider.EnvironmentVariableName,
                Path.GetFullPath(options.OutDir));
        }

        // Pass NO command-line args to the host builder: the CLI command/flags are not configuration
        // and the default command-line config provider rejects bare positionals like "processes".
        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());

        // stdout carries the table / JSON; route every log to stderr and keep it quiet by default so
        // machine-readable stdout stays clean.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.IncludeScopes = false;
            o.SingleLine = true;
        });
        builder.Logging.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Bind the B4 security gates from the `Diagnostics` configuration section here (the CLI is an
        // app, so it may depend on Microsoft.Extensions.Configuration; Core stays config-free). This
        // mirrors the server's binding so both front-ends honour the same allowlists / sensitivity
        // gates. Host.CreateApplicationBuilder already layers appsettings.json + environment variables.
        var securityOptions = new SecurityOptions();
        builder.Configuration.GetSection(SecurityOptions.SectionName).Bind(securityOptions);

        var configuredSymbolPath = Environment.GetEnvironmentVariable(SymbolPathBuilder.McpSymbolPathEnvironmentVariable);
        builder.Services.AddDiagnosticCoreServices(securityOptions, configuredSymbolPath);

        return builder.Build();
    }

    internal const string Usage =
        """
        dotnet-diagnostics — one-shot diagnostics against a live .NET process (no HTTP, no bearer, no daemon).

        Usage:
          dotnet-diagnostics <command> [options]

        Commands:
          processes                     List attachable .NET processes.
          capabilities                  Probe a target's diagnostic capability matrix.
          collect                       Open an EventPipe session and collect events (--kind required).
          inspect-heap                  Walk the managed heap of a live process or a .dmp (--source live|dump).
          dump                          Write a process dump to disk (requires --confirm).

        Options:
          -p, --pid <int>               Target OS process id (auto-resolved when only one is visible).
              --json                    Emit the raw DiagnosticResult envelope as JSON.
          -h, --help                    Show this help.

        collect options:
              --kind <kind>             Required. One of: counters, exceptions, gc, event_source,
                                        activities, logs, jit, threadpool, contention, db.
          -d, --duration <int>          Collection window in seconds (default: counters 5, others 10).
              --depth <level>           Verbosity: summary, detail (default), raw.
              --max-events <int>        Per-kind cap (events / exceptions / activities).
              --interval <int>          Refresh interval in seconds (counters, db). Default 1.
              --provider <name>         counters: EventCounter provider (repeatable);
                                        event_source: required provider name.
              --meter <name>            counters: Meter name (repeatable).
              --source <name>           activities: ActivitySource filter (repeatable, * / ? globs).
              --category <glob>         logs: ILogger category filter (repeatable).
              --min-level <level>       logs: minimum level (default Information).
              --unsafe-provider         event_source: opt in to a non-allowlisted provider.

        inspect-heap options:
              --source <live|dump>      Snapshot source (default: inferred — dump when --dump-file is set, else live).
              --dump-file <path>        --source dump: path to a previously-captured .dmp.
              --top-types <int>         Top-N type count (default 20).
              --include-retention-paths Walk a short GC retention chain for the top types.
              --retention-path-limit <int>  Cap retention-chain depth (default 8).
              --include-static-fields   Rank static reference fields by referenced object size.
              --include-delegate-targets  Group MulticastDelegate invocation lists by (target, method).
              --include-duplicate-strings Rank duplicate strings by aggregate retained bytes.
              --symbol-path <path>      NT_SYMBOL_PATH-style search path (remote servers off by default).

        dump options:
              --dump-type <type>        Mini (default), Triage, WithHeap or Full.
              --out <dir>               Directory to write the dump into (default: temp artifact root).
              --confirm                 Required to actually write; without it a preview is returned.

        Examples:
          dotnet-diagnostics processes
          dotnet-diagnostics capabilities --pid 1234
          dotnet-diagnostics processes --json
          dotnet-diagnostics collect --kind counters --pid 1234 --duration 5
          dotnet-diagnostics collect --kind gc --pid 1234 --json
          dotnet-diagnostics collect --kind event_source --provider System.Net.Http --pid 1234
          dotnet-diagnostics inspect-heap --pid 1234 --top-types 30
          dotnet-diagnostics inspect-heap --source dump --dump-file ./app.dmp
          dotnet-diagnostics dump --pid 1234 --dump-type WithHeap --out ./dumps --confirm
        """;
}
