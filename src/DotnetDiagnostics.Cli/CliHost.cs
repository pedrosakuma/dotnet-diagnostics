using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Hosting;
using DotnetDiagnostics.Core.Launch;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Symbols;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace DotnetDiagnostics.Cli;

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

        // The stateful `session` REPL owns its own Ctrl-C semantics (first Ctrl-C cancels only the
        // running command and keeps the session alive; an idle Ctrl-C exits the session), so we must
        // NOT install the one-shot global handler below for it. Peek the parsed command and route to
        // the REPL path, which manages its own session CancellationTokenSource via SessionRepl.
        var peek = CliOptions.Parse(args, out var peekError);
        if (peekError is null && peek!.Command == "session" && !peek.Help)
        {
            return await RunAsync(args, Console.In, Console.Out, Console.Error, CancellationToken.None).ConfigureAwait(false);
        }

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
    /// Backwards-compatible overload used by the existing one-shot tests: no stdin (the one-shot
    /// commands never read it). Delegates to the canonical overload with <see cref="TextReader.Null"/>.
    /// </summary>
    internal static Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
        => RunAsync(args, TextReader.Null, stdout, stderr, cancellationToken);

    /// <summary>
    /// Testable core: parses <paramref name="args"/>, builds the Core host, dispatches the command and
    /// renders to the supplied writers. The <c>session</c> command branches into the stateful
    /// <see cref="SessionRepl"/> (issue #300), which reads commands from <paramref name="stdin"/>.
    /// Exit codes: <c>0</c> success · <c>1</c> error envelope · <c>2</c> usage error · <c>130</c> cancelled.
    /// </summary>
    internal static async Task<int> RunAsync(
        string[] args,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        // Human summaries/hints are built in Core with `{x:F1}`/`{x:N0}` interpolation, which honours
        // the ambient culture (e.g. pt-BR renders `cpu-usage=0,0%`). Pin the invariant culture so the
        // CLI's textual output is locale-independent and reproducible, matching the --json path which
        // already forces invariant (#301 #2). Idempotent — assigning the same invariant every run.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

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
            var helpText = options.Command is { } helpCommand
                            && CliCommands.Commands.Contains(helpCommand, StringComparer.Ordinal)
                ? CliHelp.ForCommand(helpCommand)
                : CliHelp.Global;
            await stdout.WriteLineAsync(helpText).ConfigureAwait(false);
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

        if (!CliCommands.TryValidateLaunch(options, out var launchValidationError))
        {
            await stderr.WriteLineAsync(launchValidationError).ConfigureAwait(false);
            await stderr.WriteLineAsync().ConfigureAwait(false);
            await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        // The stateful session REPL builds the host ONCE (shared singletons — the handle store that
        // makes drill-down possible must outlive every command) and reads commands from stdin until
        // exit/EOF. It owns a per-command artifact root via a MutableArtifactRootProvider.
        if (options.Command == "session")
        {
            LaunchedTarget? sessionTarget = null;
            int? initialTargetPid = null;
            if (options.Launch)
            {
                var (target, pid, launchError, cancelled) = await LaunchAndWaitAsync(options, stdout, stderr, cancellationToken).ConfigureAwait(false);
                if (cancelled)
                {
                    await stderr.WriteLineAsync($"dotnet-diagnostics-cli {options.Command}: cancelled before the launched target was ready — child terminated.")
                        .ConfigureAwait(false);
                    return 130;
                }

                if (launchError is not null)
                {
                    if (target is not null)
                    {
                        await target.DisposeAsync().ConfigureAwait(false);
                    }

                    await stderr.WriteLineAsync(launchError).ConfigureAwait(false);
                    return 1;
                }

                sessionTarget = target;
                initialTargetPid = pid;
            }

            var sessionRoot = Path.Combine(Path.GetTempPath(), $"dotnet-diagnostics-session-{Guid.NewGuid():N}");
            var artifactProvider = new MutableArtifactRootProvider(sessionRoot);
            using var sessionHost = BuildHost(artifactProvider);
            try
            {
                return await SessionRepl.RunAsync(
                    sessionHost.Services, artifactProvider, stdin, stdout, stderr, initialTargetPid, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (sessionTarget is not null)
                {
                    await sessionTarget.DisposeAsync().ConfigureAwait(false);
                }

                TryDeleteDirectory(sessionRoot);
            }
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

        if (options.Command == "get-bytes" && !CliCommands.TryValidateGetBytes(options, out var getBytesError))
        {
            await stderr.WriteLineAsync(getBytesError).ConfigureAwait(false);
            await stderr.WriteLineAsync().ConfigureAwait(false);
            await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        if (options.Command == "compare" && !CliCommands.TryValidateCompare(options, out var compareError))
        {
            await stderr.WriteLineAsync(compareError).ConfigureAwait(false);
            await stderr.WriteLineAsync().ConfigureAwait(false);
            await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        // Opt-in `--launch` dev mode (issue #365): spawn the target as a child of this process so the
        // ClrMD live-attach commands are permitted under Yama ptrace_scope=1 without privilege. The
        // launched pid becomes the effective --pid for this one-shot command; the child is terminated
        // when the command returns.
        LaunchedTarget? launchedTarget = null;
        if (options.Launch)
        {
            var (target, pid, launchError, cancelled) = await LaunchAndWaitAsync(options, stdout, stderr, cancellationToken).ConfigureAwait(false);
            if (cancelled)
            {
                await stderr.WriteLineAsync($"dotnet-diagnostics-cli {options.Command}: cancelled before the launched target was ready — child terminated.")
                    .ConfigureAwait(false);
                return 130;
            }

            if (launchError is not null)
            {
                if (target is not null)
                {
                    await target.DisposeAsync().ConfigureAwait(false);
                }

                await stderr.WriteLineAsync(launchError).ConfigureAwait(false);
                return 1;
            }

            launchedTarget = target;
            options = options with { Pid = pid, Launch = false, LaunchedByCli = true };
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
                await stderr.WriteLineAsync($"dotnet-diagnostics-cli {options.Command}: cancelled mid-window — diagnostic session stopped and temp files cleaned up. Payload is partial.")
                    .ConfigureAwait(false);
                return 130;
            }

            return result.IsError ? 1 : 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await stderr.WriteLineAsync($"dotnet-diagnostics-cli {options.Command}: cancelled — diagnostic session stopped and temp files cleaned up.")
                .ConfigureAwait(false);
            return 130;
        }
        finally
        {
            if (launchedTarget is not null)
            {
                await launchedTarget.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Maximum time to wait for a launched child's diagnostic endpoint to come up.</summary>
    private static readonly TimeSpan LaunchReadinessTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Launches the target named in <see cref="CliOptions.LaunchArgs"/> as a child of this process and
    /// waits for its diagnostic endpoint. Returns the owned <see cref="LaunchedTarget"/> (so the caller
    /// can dispose it even on the error paths) plus the child pid, or a non-null error string. The
    /// launch banner is written to <paramref name="stdout"/> so it never contaminates a <c>--json</c>
    /// payload (which goes to the result, written later).
    /// </summary>
    private static async Task<(LaunchedTarget? Target, int Pid, string? Error, bool Cancelled)> LaunchAndWaitAsync(
        CliOptions options,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var program = options.LaunchArgs[0];
        var argv = new string[options.LaunchArgs.Count - 1];
        for (var i = 1; i < options.LaunchArgs.Count; i++)
        {
            argv[i - 1] = options.LaunchArgs[i];
        }

        LaunchedTarget target;
        try
        {
            // In --json mode pump the child's console to stderr so stdout carries only the envelope;
            // otherwise let the child inherit the console for real-time, interactive output.
            var consoleSink = options.Json ? stderr : null;
            target = ChildProcessLauncher.Launch(program, argv, consoleSink);
        }
        catch (InvalidOperationException ex)
        {
            return (null, 0, ex.Message, false);
        }

        await stderr.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Launched '{program}' as child pid {target.ProcessId}; waiting up to {LaunchReadinessTimeout.TotalSeconds:N0}s for its diagnostic endpoint…"))
            .ConfigureAwait(false);

        // The session path passes CancellationToken.None (the REPL only owns Ctrl-C AFTER startup), so
        // the launch-wait would otherwise be uninterruptible — a Ctrl-C here would hard-terminate the
        // CLI and orphan the freshly-launched child (AppDomain.ProcessExit does NOT run on SIGINT for a
        // plain console app). Install a temporary cooperative Ctrl-C handler scoped to the wait so
        // cancellation always disposes the child. Only hook the real console (tests inject writers).
        using var launchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ownsConsole = ReferenceEquals(stdout, Console.Out) || ReferenceEquals(stderr, Console.Error);
        ConsoleCancelEventHandler? cancelHandler = null;
        if (ownsConsole)
        {
            cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                // ReSharper disable once AccessToDisposedClosure — removed in the finally below, before launchCts is disposed.
                launchCts.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
        }

        bool ready;
        try
        {
            ready = await ChildProcessLauncher
                .WaitForDiagnosticEndpointAsync(target.ProcessId, LaunchReadinessTimeout, launchCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl-C (or a cancelled caller token) while waiting for the child to come up: terminate the
            // freshly-launched child so it never outlives the cancelled invocation.
            await target.DisposeAsync().ConfigureAwait(false);
            return (null, 0, null, true);
        }
        finally
        {
            if (cancelHandler is not null)
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }

        if (!ready)
        {
            var reason = target.HasExited
                ? $"Launched target (pid {target.ProcessId}) exited before exposing a diagnostic endpoint. Launch the app directly (e.g. 'dotnet App.dll' or a published apphost), not via 'dotnet run'."
                : string.Create(CultureInfo.InvariantCulture, $"Launched target (pid {target.ProcessId}) did not expose a diagnostic endpoint within {LaunchReadinessTimeout.TotalSeconds:N0}s.");
            return (target, target.ProcessId, reason, false);
        }

        return (target, target.ProcessId, null, false);
    }

    private static IHost BuildHost(CliOptions options)
    {
        // The one-shot path derives a command-specific artifact root from the options (dump --out,
        // get-bytes --dump-file) and pins it via a FixedArtifactRootProvider, or keeps the default
        // (temp / MCP_ARTIFACT_ROOT) provider when no override applies.
        var artifactRoot = ResolveArtifactRoot(options);
        IArtifactRootProvider? provider = artifactRoot is not null ? new FixedArtifactRootProvider(artifactRoot) : null;
        return BuildHost(provider);
    }

    /// <summary>
    /// Builds the Core service graph host. When <paramref name="artifactProvider"/> is non-null it is
    /// registered as the <see cref="IArtifactRootProvider"/> (the LAST registration wins for
    /// <c>GetRequiredService</c>), overriding the default <c>EnvironmentArtifactRootProvider</c>. The
    /// <c>session</c> REPL passes a <see cref="MutableArtifactRootProvider"/> so it can re-point the
    /// sandbox per command without rebuilding the host.
    /// </summary>
    private static IHost BuildHost(IArtifactRootProvider? artifactProvider)
    {
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

        // Point the artifact sandbox at a command-specific directory by overriding the default
        // EnvironmentArtifactRootProvider with an explicit one (the LAST IArtifactRootProvider
        // registration wins for GetRequiredService). This replaces mutating the process-global
        // MCP_ARTIFACT_ROOT env var, which would leak across commands sharing the process (e.g. tests).
        if (artifactProvider is not null)
        {
            builder.Services.AddSingleton(artifactProvider);
        }

        return builder.Build();
    }

    /// <summary>Best-effort recursive delete of the per-session artifact root on REPL exit.</summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Resolves the command-specific artifact-sandbox root, or <c>null</c> to keep the default
    /// (temp-dir / <c>MCP_ARTIFACT_ROOT</c>) provider.
    /// <list type="bullet">
    ///   <item><c>dump --out &lt;dir&gt;</c>: the dump lands directly in the root, so the root IS
    ///   <c>--out</c> (resolved absolute; the handler passes a null sub-path).</item>
    ///   <item><c>get-bytes --kind dump --dump-file &lt;path&gt;</c>: the source dump must resolve
    ///   under the root, so the root is the dump file's directory (the handler passes its file name).</item>
    /// </list>
    /// <c>get-bytes --kind module</c> writes its <c>--out</c> file directly (no sandbox), so it needs
    /// no override.
    /// </summary>
    internal static string? ResolveArtifactRoot(CliOptions options)
    {
        if (options.Command == "dump" && !string.IsNullOrWhiteSpace(options.OutDir))
        {
            return Path.GetFullPath(options.OutDir);
        }

        if (options.Command == "get-bytes"
            && options.Kind == "dump"
            && !string.IsNullOrWhiteSpace(options.DumpFile))
        {
            var fullDumpPath = Path.GetFullPath(options.DumpFile);
            return Path.GetDirectoryName(fullDumpPath) ?? Directory.GetCurrentDirectory();
        }

        return null;
    }

    internal static string Usage => CliHelp.Global;
}
