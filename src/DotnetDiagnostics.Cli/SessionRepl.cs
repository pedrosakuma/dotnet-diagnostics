using System.Globalization;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.ProcessDiscovery;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli;

/// <summary>
/// The stateful <c>dotnet-diagnostics session</c> REPL (issue #300). Unlike the one-shot CLI — which
/// builds the Core host, runs one command and exits — the REPL keeps the host (and therefore the
/// singleton <see cref="IDiagnosticHandleStore"/> and all collectors) alive across commands. A human
/// can <c>collect --kind gc</c> once and then run <c>query --handle &lt;id&gt; --view pauseHistogram</c>
/// drill-downs without re-collecting, exactly as an MCP client would over a live session.
/// </summary>
/// <remarks>
/// <para><b>Cancellation.</b> The session runs under a <see cref="CancellationTokenSource"/> linked to
/// the caller's token. In production (when writing to the real console) it installs a
/// <see cref="Console.CancelKeyPress"/> handler with two behaviours, guarded by a lock so the handler
/// and the loop never race on the per-command state: while a command runs, the first Ctrl-C cancels
/// only that command (the session stays alive); a second Ctrl-C for the same command falls through to
/// the default hard termination so a wedged operation can never trap the process. At an idle prompt,
/// Ctrl-C cancels the session for a graceful exit.</para>
/// <para><b>Artifact root.</b> The host is built once with a <see cref="MutableArtifactRootProvider"/>;
/// the REPL re-points it to each command's resolved root (and back to the session default afterwards)
/// so <c>dump --out</c> / <c>get-bytes --dump-file</c> behave exactly as in the one-shot path.</para>
/// <para><b>Dead-PID sweep.</b> A 5s in-process timer runs the shared, host-neutral
/// <see cref="DeadProcessHandleEvictor"/> (the same driver behind the server's
/// <c>HandleEvictionBackgroundService</c>): it drops handles whose target process has exited so the
/// user never drills into a dead trace.</para>
/// </remarks>
internal sealed class SessionRepl
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly CancellationTokenSource _sessionCts;
    private CancellationTokenSource? _currentCommandCts;
    private bool _commandRunning;
    private int _commandCtrlCCount;

    // Session-bound target pid (issue #300, strand C). When set, live-target commands that omit
    // --pid inherit it so the user supplies the pid once per session instead of per command.
    private int? _targetPid;

    // Set when the session itself launched the target (issue #365, `session --launch -- <app>`), so
    // the opening banner can explain the bound pid came from the launched child.
    private readonly bool _launchedTarget;

    private SessionRepl(int? initialTargetPid, CancellationToken externalToken)
    {
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _targetPid = initialTargetPid;
        _launchedTarget = initialTargetPid is not null;
    }

    public static Task<int> RunAsync(
        IServiceProvider services,
        MutableArtifactRootProvider artifactProvider,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        int? initialTargetPid,
        CancellationToken externalToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(artifactProvider);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var repl = new SessionRepl(initialTargetPid, externalToken);
        return repl.LoopAsync(services, artifactProvider, stdin, stdout, stderr);
    }

    private async Task<int> LoopAsync(
        IServiceProvider services,
        MutableArtifactRootProvider artifactProvider,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr)
    {
        var store = services.GetRequiredService<IDiagnosticHandleStore>() as MemoryDiagnosticHandleStore;

        // Only hook the real console's Ctrl-C in production; tests drive the loop with a StringReader
        // and cancel the supplied token directly (idle-cancel path).
        var ownsConsole = ReferenceEquals(stdout, Console.Out);
        ConsoleCancelEventHandler? handler = null;
        if (ownsConsole)
        {
            handler = OnConsoleCancel;
            Console.CancelKeyPress += handler;
        }

        var evictor = store is not null ? new DeadProcessHandleEvictor(store) : null;
        var sweep = evictor is not null
            ? Task.Run(() => evictor.RunAsync(SweepInterval, cancellationToken: _sessionCts.Token))
            : Task.CompletedTask;

        int exitCode;
        try
        {
            await stdout.WriteLineAsync(Banner).ConfigureAwait(false);
            if (_launchedTarget && _targetPid is { } launchedPid)
            {
                await stdout.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Launched target bound to pid {launchedPid} (this process is its ptrace parent, so live attach works without privilege). 'target clear' to unbind.")).ConfigureAwait(false);
            }

            while (!_sessionCts.IsCancellationRequested)
            {
                var prompt = _targetPid is { } targetPid
                    ? string.Create(CultureInfo.InvariantCulture, $"diag(pid {targetPid})> ")
                    : "diag> ";
                await stdout.WriteAsync(prompt).ConfigureAwait(false);
                await stdout.FlushAsync().ConfigureAwait(false);

                var line = await ReadLineAsync(stdin, _sessionCts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break; // EOF, or idle Ctrl-C (which also cancelled _sessionCts)
                }

                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (string.Equals(trimmed, "exit", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed, "quit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (string.Equals(trimmed, "help", StringComparison.OrdinalIgnoreCase))
                {
                    await stdout.WriteLineAsync(SessionHelp).ConfigureAwait(false);
                    continue;
                }

                var lineTokens = Tokenize(trimmed);
                if (lineTokens.Count > 0
                    && (string.Equals(lineTokens[0], "target", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(lineTokens[0], "use", StringComparison.OrdinalIgnoreCase)))
                {
                    await HandleTargetAsync(services, lineTokens, stdout, stderr).ConfigureAwait(false);
                    continue;
                }

                await RunOneAsync(services, artifactProvider, store, trimmed, stdout, stderr).ConfigureAwait(false);
            }

            // Capture the outcome BEFORE the finally cancels the session CTS to stop the sweep:
            // an idle Ctrl-C cancels _sessionCts (exit 130); a clean exit/quit/EOF does not (exit 0).
            exitCode = _sessionCts.IsCancellationRequested ? 130 : 0;
        }
        finally
        {
            if (handler is not null)
            {
                Console.CancelKeyPress -= handler;
            }

            _sessionCts.Cancel();
            try
            {
                await sweep.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _sessionCts.Dispose();
        }

        return exitCode;
    }

    private async Task RunOneAsync(
        IServiceProvider services,
        MutableArtifactRootProvider artifactProvider,
        MemoryDiagnosticHandleStore? store,
        string line,
        TextWriter stdout,
        TextWriter stderr)
    {
        var tokens = Tokenize(line);
        var options = CliOptions.Parse(tokens, out var parseError);
        if (parseError is not null)
        {
            await stderr.WriteLineAsync(parseError).ConfigureAwait(false);
            return;
        }

        if (options!.Help)
        {
            var helpText = options.Command is { } helpCommand
                            && CliCommands.CommandSet.Contains(helpCommand)
                ? CliHelp.ForCommand(helpCommand)
                : SessionHelp;
            await stdout.WriteLineAsync(helpText).ConfigureAwait(false);
            return;
        }

        if (options.Command is null)
        {
            await stderr.WriteLineAsync("No command specified. Type 'help' for the command list, or 'exit' to leave.").ConfigureAwait(false);
            return;
        }

        if (string.Equals(options.Command, "session", StringComparison.Ordinal))
        {
            await stderr.WriteLineAsync("Already in a session. Type 'exit' to leave.").ConfigureAwait(false);
            return;
        }

        if (!CliCommands.CommandSet.Contains(options.Command))
        {
            await stderr.WriteLineAsync($"Unknown command '{options.Command}'. Type 'help' for the command list.").ConfigureAwait(false);
            return;
        }

        if (options.Launch || options.LaunchArgs.Count > 0)
        {
            await stderr.WriteLineAsync("--launch is only available at session startup ('session --launch -- <app>'); inside a session the target is already live — use 'target <pid>' to switch.").ConfigureAwait(false);
            return;
        }

        if (options.WatchIntervalSeconds is not null)
        {
            await stderr.WriteLineAsync("--watch is only available for one-shot commands outside a session. Re-run the command manually, or leave the session and use --watch there.").ConfigureAwait(false);
            return;
        }

        // Apply the session-bound target pid to live-target commands that omitted --pid (strand C).
        // We append the flag to the token list and re-parse rather than clone CliOptions (a class with
        // ~30 init-only properties). Validation then runs against the effective options.
        var inheritedTarget = false;
        if (_targetPid is { } boundPid && !options.HasPid && ShouldInheritTarget(options))
        {
            tokens.Add("--pid");
            tokens.Add(boundPid.ToString(CultureInfo.InvariantCulture));
            var reparsed = CliOptions.Parse(tokens, out var reparseError);
            if (reparseError is not null || reparsed is null)
            {
                await stderr.WriteLineAsync($"{options.Command}: internal error applying bound target pid {boundPid}.").ConfigureAwait(false);
                return;
            }

            options = reparsed;
            inheritedTarget = true;
        }

        if (!CliCommands.TryValidateCommand(options, out var validationError))
        {
            await stderr.WriteLineAsync(validationError).ConfigureAwait(false);
            return;
        }

        if (!CliHost.TryResolveNamedPid(services, options, out var resolvedOptions, out var pidResolutionError))
        {
            await stderr.WriteLineAsync(pidResolutionError).ConfigureAwait(false);
            return;
        }

        options = resolvedOptions;

        // Issue #365: when the session launched the target and this command resolves to that pid, mark
        // the options so the capability note reports descendant-attach availability instead of
        // re-suggesting --launch.
        if (_launchedTarget && options.Pid is { } resolvedPid && resolvedPid == _targetPid)
        {
            options = options with { LaunchedByCli = true };
        }

        if (inheritedTarget && !options.Json)
        {
            await stdout.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"  · using bound target pid {options.Pid}")).ConfigureAwait(false);
        }

        // Re-point the artifact sandbox at this command's resolved root (dump --out / get-bytes
        // --dump-file), or the per-session default temp root when no override applies.
        var commandRoot = CliHost.ResolveArtifactRoot(options);
        if (commandRoot is not null)
        {
            artifactProvider.Set(commandRoot);
        }
        else
        {
            artifactProvider.Reset();
        }

        var commandCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
        BeginCommand(commandCts);
        try
        {
            // `query` is handled by the session-aware path (it can honour --handle against the live
            // shared store); every other command runs the same use case the one-shot CLI does.
            var result = string.Equals(options.Command, "query", StringComparison.Ordinal)
                ? await CliCommands.QuerySession(services, options, commandCts.Token).ConfigureAwait(false)
                : await CliCommands.RunAsync(services, options, commandCts.Token).ConfigureAwait(false);

            await RenderAsync(result, options.Json, stdout, stderr).ConfigureAwait(false);

            if (result.Handle is { } handleId)
            {
                await SurfaceHandleAsync(store, handleId, result.HandleExpiresAt, stdout).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (commandCts.IsCancellationRequested && !_sessionCts.IsCancellationRequested)
        {
            await stderr.WriteLineAsync($"{options.Command}: cancelled — session is still alive, temp files cleaned up.").ConfigureAwait(false);
        }
        finally
        {
            EndCommand();
            commandCts.Dispose();
            artifactProvider.Reset();
        }
    }

    /// <summary>
    /// Handles the <c>target</c> / <c>use</c> REPL built-in (issue #300, strand C). With no argument it
    /// reports the current binding; <c>target &lt;pid&gt;</c> (or <c>target --pid &lt;pid&gt;</c>) binds a
    /// default pid for live-target commands; <c>target clear|none|off|unset</c> unbinds it. Binding is
    /// lazy — the pid is not validated here; the next command surfaces the authoritative attach failure.
    /// </summary>
    private async Task HandleTargetAsync(IServiceProvider services, List<string> tokens, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(services);

        var args = tokens.Skip(1).ToList();
        var pidFlagForm = args.Count >= 1
            && (string.Equals(args[0], "--pid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[0], "-p", StringComparison.OrdinalIgnoreCase));
        if (pidFlagForm)
        {
            args = args.Skip(1).ToList();
        }

        if (!pidFlagForm && args.Count == 0)
        {
            if (_targetPid is { } current)
            {
                await stdout.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Target bound to pid {current}. Live-target commands use it unless you pass --pid; 'target clear' to unbind.")).ConfigureAwait(false);
            }
            else
            {
                await stdout.WriteLineAsync("No target bound. Commands auto-resolve the lone .NET process, or pass --pid <id>; 'target <pid>' to bind one.").ConfigureAwait(false);
            }

            return;
        }

        if (!pidFlagForm
            && args.Count == 1
            && (string.Equals(args[0], "clear", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[0], "none", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[0], "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[0], "unset", StringComparison.OrdinalIgnoreCase)))
        {
            _targetPid = null;
            await stdout.WriteLineAsync("Target cleared.").ConfigureAwait(false);
            return;
        }

        if (args.Count == 1
            && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
        {
            if (pid > 0)
            {
                _targetPid = pid;
                await stdout.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Target bound to pid {pid}. capabilities/collect/inspect-heap/dump/get-bytes now use it unless you pass --pid.")).ConfigureAwait(false);
            }
            else
            {
                await WriteTargetUsageAsync(stderr).ConfigureAwait(false);
            }

            return;
        }

        if (args.Count == 1)
        {
            if (string.IsNullOrWhiteSpace(args[0]))
            {
                await WriteTargetUsageAsync(stderr).ConfigureAwait(false);
                return;
            }

            if (args[0].StartsWith('-'))
            {
                await WriteTargetUsageAsync(stderr).ConfigureAwait(false);
                return;
            }

            var discovery = services.GetService<IProcessDiscovery>();
            if (discovery is null)
            {
                await WriteTargetUsageAsync(stderr).ConfigureAwait(false);
                return;
            }

            if (CliProcessSelector.TryResolveName(args[0], discovery.ListProcesses(), out var resolvedPid, out var error))
            {
                _targetPid = resolvedPid;
                await stdout.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Target bound to pid {resolvedPid}. capabilities/collect/inspect-heap/dump/get-bytes now use it unless you pass --pid.")).ConfigureAwait(false);
                return;
            }

            await stderr.WriteLineAsync(error).ConfigureAwait(false);
            return;
        }

        await WriteTargetUsageAsync(stderr).ConfigureAwait(false);
    }

    private static Task WriteTargetUsageAsync(TextWriter stderr)
        => stderr.WriteLineAsync("Usage: target <pid> | target <name-prefix> | target clear. Example: 'target 1234' or 'target MyApp'.");

    /// <summary>
    /// Decides whether a command should inherit the session-bound target pid when it omitted <c>--pid</c>.
    /// True for the live-target commands (<c>capabilities</c>, <c>collect</c>, <c>dump</c>,
    /// <c>inspect-heap</c> against a live process, and <c>get-bytes --kind module</c>); false for offline
    /// commands (<c>inspect-heap --source dump</c> / a <c>--dump-file</c>, <c>get-bytes --kind dump</c>)
    /// and pid-less commands (<c>processes</c>, <c>query</c>) — injecting <c>--pid</c> there would either
    /// be ignored or fail validation.
    /// </summary>
    internal static bool ShouldInheritTarget(CliOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        switch (options.Command)
        {
            case "capabilities":
            case "collect":
            case "dump":
            case "inspect":
                return true;
            case "inspect-heap":
                return options.DumpFile is null
                    && !options.Sources.Contains("dump", StringComparer.Ordinal);
            case "get-bytes":
                return string.Equals(options.Kind, "module", StringComparison.Ordinal);
            default:
                return false;
        }
    }

    private static async Task RenderAsync(CliCommandResult result, bool json, TextWriter stdout, TextWriter stderr)
    {
        if (json)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(result.Envelope, result.Envelope.GetType(), JsonOptions))
                .ConfigureAwait(false);
        }
        else
        {
            await stdout.WriteLineAsync(CliAnsi.ColorizeHuman(result.Human, CliAnsi.IsEnabled(stdout, forceAnsi: null))).ConfigureAwait(false);
        }

        if (result.Cancelled)
        {
            await stderr.WriteLineAsync("(cancelled mid-window — diagnostic session stopped, temp files cleaned up; payload is partial)")
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Prints a follow-up line after a command that published a drill-down handle, listing the valid
    /// views for that handle's kind so the user can <c>query</c> without re-collecting. For heap / CPU
    /// handles (no session query path yet) it prints a truthful "not yet supported" note instead of an
    /// inviting-but-broken command.
    /// </summary>
    private static async Task SurfaceHandleAsync(
        MemoryDiagnosticHandleStore? store,
        string handleId,
        DateTimeOffset? expiresAt,
        TextWriter stdout)
    {
        var lookup = store?.TryGetWithKind(handleId);
        var expiry = expiresAt is { } e
            ? string.Create(CultureInfo.InvariantCulture, $" (expires {e:HH:mm:ss}Z)")
            : string.Empty;

        if (lookup is { } found)
        {
            var views = CliCommands.SessionViewsFor(found.Kind);
            if (views.Count > 0)
            {
                await stdout.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  → handle {handleId}{expiry} — query --handle {handleId} --view <{string.Join('|', views)}>"))
                    .ConfigureAwait(false);
                return;
            }
        }

        // Either the handle was already evicted, or it is a heap/cpu/thread kind not yet queryable in
        // the session.
        await stdout.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"  → handle {handleId}{expiry} — drill-down for this artifact is not available in the session yet."))
            .ConfigureAwait(false);
    }

    // --- Cancellation state machine -----------------------------------------------------------

    private void BeginCommand(CancellationTokenSource commandCts)
    {
        lock (_gate)
        {
            _currentCommandCts = commandCts;
            _commandRunning = true;
            _commandCtrlCCount = 0;
        }
    }

    private void EndCommand()
    {
        lock (_gate)
        {
            _currentCommandCts = null;
            _commandRunning = false;
            _commandCtrlCCount = 0;
        }
    }

    private void OnConsoleCancel(object? sender, ConsoleCancelEventArgs e)
    {
        lock (_gate)
        {
            if (_commandRunning && _currentCommandCts is { } cmd)
            {
                _commandCtrlCCount++;
                if (_commandCtrlCCount == 1)
                {
                    // First Ctrl-C while a command runs: cancel only that command, keep the session.
                    e.Cancel = true;
                    cmd.Cancel();
                }

                // Second Ctrl-C for the same command: leave e.Cancel false so the default handler
                // hard-terminates the process — a wedged operation can never trap the user.
                return;
            }

            // Idle at the prompt: cancel the session for a graceful exit.
            e.Cancel = true;
            _sessionCts.Cancel();
        }
    }

    // --- Input --------------------------------------------------------------------------------

    /// <summary>
    /// Reads a line, abandoning the read when <paramref name="ct"/> is cancelled. <see cref="Console.In"/>
    /// blocking reads do not reliably honour token cancellation across platforms, so we race the read
    /// against the token and return <c>null</c> (treated as EOF / exit) when the session is cancelled.
    /// </summary>
    private static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return null;
        }

        var readTask = reader.ReadLineAsync(ct).AsTask();
        var cancelSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (ct.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), cancelSignal).ConfigureAwait(false))
        {
            var completed = await Task.WhenAny(readTask, cancelSignal.Task).ConfigureAwait(false);
            if (completed != readTask)
            {
                return null; // cancelled at idle — the abandoned read ends with the process
            }
        }

        try
        {
            return await readTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Splits a REPL line into argv-style tokens, honouring double quotes so paths with spaces work
    /// (e.g. <c>dump --out "C:\my dumps"</c>). Deliberately simple — no escape sequences.
    /// </summary>
    internal static List<string> Tokenize(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    // --- Text ---------------------------------------------------------------------------------

    private const string Banner =
        "dotnet-diagnostics session — stateful diagnostics REPL. Collected handles stay queryable until\n"
        + "they expire or the target exits. Bind a process once with 'target <pid>' so live-target commands\n"
        + "don't each need --pid. Type a command (e.g. 'collect --kind gc --pid 1234'), 'help' for the\n"
        + "command list, or 'exit' to leave.";

    private const string SessionHelp =
        """
        Session commands:
          processes                       List attachable .NET processes.
          target [<pid>|clear]            Bind/show/clear a default --pid for live-target commands.
          capabilities [--pid <id>]       Probe a target's diagnostic capability matrix.
          collect --kind <kind> [...]     Collect events; prints a handle to drill into with 'query'.
          inspect-heap [...]              Walk the managed heap of a live process or a .dmp.
          dump [...] --confirm            Write a process dump to disk.
          get-bytes --kind <k> [...]      Materialise a module or dump file to disk.
          query --handle <id> --view <v>  Re-render a collected handle under a different view.
          compare <a.json> <b.json> [...] Compare saved comparable snapshots.
          <command> --help                Show full options for a command.
          help                            Show this list.
          exit | quit                     Leave the session (Ctrl-D / EOF also exits).
        Event catalog query filters: --provider-filter <text>, --root-method-filter <event-name>.
        A bound target (shown as 'diag(pid <id>)>') is overridden by an explicit --pid on any command.
        Ctrl-C cancels the running command and keeps the session alive; press it again to force-quit.
        """;
}
