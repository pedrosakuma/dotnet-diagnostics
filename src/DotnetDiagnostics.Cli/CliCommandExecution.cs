using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace DotnetDiagnostics.Cli;

internal enum CliExecutionContext
{
    OneShot,
    Session,
}

internal sealed record CliPreparedCommand(CliOptions Options, bool InheritedTarget);

internal sealed record CliImmediateCommandResponse(string Text, bool WriteToStdout, bool IncludeUsage);

internal sealed record CliExecutionOptions(
    CliExecutionContext Context,
    bool AnsiEnabled,
    bool ShowProgress,
    int? LaunchedTargetPid = null);

internal sealed record CliExecutionOutcome(CliOptions Options, CliCommandResult? Result, int ExitCode);

internal static class CliCommandExecution
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool TryPrepareOneShot(
        IReadOnlyList<string> args,
        out CliPreparedCommand? prepared,
        out CliImmediateCommandResponse? response)
        => TryPrepare(args, CliExecutionContext.OneShot, sessionTargetPid: null, out prepared, out response);

    public static bool TryPrepareSession(
        IReadOnlyList<string> args,
        int? sessionTargetPid,
        out CliPreparedCommand? prepared,
        out CliImmediateCommandResponse? response)
        => TryPrepare(args, CliExecutionContext.Session, sessionTargetPid, out prepared, out response);

    public static async Task<CliExecutionOutcome> ExecuteAsync(
        IServiceProvider services,
        CliPreparedCommand prepared,
        TextWriter stdout,
        TextWriter stderr,
        CliExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (!CliHost.TryResolveNamedPid(services, prepared.Options, out var options, out var pidResolutionError))
        {
            await stderr.WriteLineAsync(pidResolutionError).ConfigureAwait(false);
            return new CliExecutionOutcome(
                prepared.Options,
                Result: null,
                executionOptions.Context == CliExecutionContext.OneShot ? 2 : 0);
        }

        if (executionOptions.LaunchedTargetPid is { } launchedTargetPid
            && options.Pid is { } resolvedPid
            && resolvedPid == launchedTargetPid)
        {
            options = options with { LaunchedByCli = true };
        }

        if (prepared.InheritedTarget && !options.Json)
        {
            await stdout.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"  · using bound target pid {options.Pid}")).ConfigureAwait(false);
        }

        var result = await WithProgressAsync(
            stderr,
            executionOptions.ShowProgress,
            options.Command!,
            () => RunCoreAsync(services, options, executionOptions.Context, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        await WriteRenderedResultAsync(result, options, stdout, stderr, executionOptions).ConfigureAwait(false);
        var exitCode = result.Cancelled ? 130 : result.IsError ? 1 : 0;
        return new CliExecutionOutcome(options, result, exitCode);
    }

    public static async Task<int> WriteCompletedResultAsync(
        CliCommandResult result,
        CliOptions options,
        TextWriter stdout,
        TextWriter stderr,
        CliExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        await WriteRenderedResultAsync(result, options, stdout, stderr, executionOptions).ConfigureAwait(false);
        return result.Cancelled ? 130 : result.IsError ? 1 : 0;
    }

    public static async Task WriteImmediateResponseAsync(
        CliImmediateCommandResponse response,
        TextWriter stdout,
        TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var writer = response.WriteToStdout ? stdout : stderr;
        await writer.WriteLineAsync(response.Text).ConfigureAwait(false);
        if (!response.WriteToStdout && response.IncludeUsage)
        {
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync(CliHost.Usage).ConfigureAwait(false);
        }
    }

    private static bool TryPrepare(
        IReadOnlyList<string> args,
        CliExecutionContext context,
        int? sessionTargetPid,
        out CliPreparedCommand? prepared,
        out CliImmediateCommandResponse? response)
    {
        prepared = null;
        response = null;

        var options = CliOptions.Parse(args, out var parseError);
        if (parseError is not null)
        {
            response = Error(parseError, context == CliExecutionContext.OneShot);
            return false;
        }

        if (options!.Help)
        {
            var helpText = options.Command is { } helpCommand
                           && CliCommands.Commands.Contains(helpCommand, StringComparer.Ordinal)
                ? CliHelp.ForCommand(helpCommand)
                : context == CliExecutionContext.Session
                    ? SessionRepl.HelpText
                    : CliHelp.Global;
            response = Success(helpText);
            return false;
        }

        if (options.Command is null)
        {
            response = context == CliExecutionContext.OneShot
                ? Error("No command specified.", includeUsage: true)
                : Error("No command specified. Type 'help' for the command list, or 'exit' to leave.", includeUsage: false);
            return false;
        }

        if (context == CliExecutionContext.Session
            && string.Equals(options.Command, "session", StringComparison.Ordinal))
        {
            response = Error("Already in a session. Type 'exit' to leave.", includeUsage: false);
            return false;
        }

        if (!CliCommands.Commands.Contains(options.Command, StringComparer.Ordinal))
        {
            response = context == CliExecutionContext.OneShot
                ? Error($"Unknown command '{options.Command}'.", includeUsage: true)
                : Error($"Unknown command '{options.Command}'. Type 'help' for the command list.", includeUsage: false);
            return false;
        }

        if (context == CliExecutionContext.Session)
        {
            if (options.Launch || options.LaunchArgs.Count > 0)
            {
                response = Error("--launch is only available at session startup ('session --launch -- <app>'); inside a session the target is already live — use 'target <pid>' to switch.", includeUsage: false);
                return false;
            }

            if (options.WatchIntervalSeconds is not null)
            {
                response = Error("--watch is only available for one-shot commands outside a session. Re-run the command manually, or leave the session and use --watch there.", includeUsage: false);
                return false;
            }
        }
        else if (!CliCommands.TryValidateLaunch(options, out var launchValidationError))
        {
            response = Error(launchValidationError!, includeUsage: true);
            return false;
        }

        var inheritedTarget = false;
        if (context == CliExecutionContext.Session
            && sessionTargetPid is { } boundPid
            && !options.HasPid
            && SessionRepl.ShouldInheritTarget(options))
        {
            var tokens = args.ToList();
            tokens.Add("--pid");
            tokens.Add(boundPid.ToString(CultureInfo.InvariantCulture));
            var reparsed = CliOptions.Parse(tokens, out var reparseError);
            if (reparseError is not null || reparsed is null)
            {
                response = Error($"{options.Command}: internal error applying bound target pid {boundPid}.", includeUsage: false);
                return false;
            }

            options = reparsed;
            inheritedTarget = true;
        }

        if (!CliCommands.TryValidateCommand(options, out var validationError))
        {
            response = Error(validationError!, context == CliExecutionContext.OneShot);
            return false;
        }

        if (string.Equals(options.Command, "completion", StringComparison.Ordinal))
        {
            response = Success(CliCompletionScripts.ForShell(options.CompletionShell!));
            return false;
        }

        prepared = new CliPreparedCommand(options, inheritedTarget);
        return true;
    }

    private static CliImmediateCommandResponse Error(string text, bool includeUsage)
        => new(text, WriteToStdout: false, IncludeUsage: includeUsage);

    private static CliImmediateCommandResponse Success(string text)
        => new(text, WriteToStdout: true, IncludeUsage: false);

    private static async Task<CliCommandResult> RunCoreAsync(
        IServiceProvider services,
        CliOptions options,
        CliExecutionContext context,
        CancellationToken cancellationToken)
        => context == CliExecutionContext.Session && string.Equals(options.Command, "query", StringComparison.Ordinal)
            ? await CliCommands.QuerySession(services, options, cancellationToken).ConfigureAwait(false)
            : await CliCommands.RunAsync(services, options, cancellationToken).ConfigureAwait(false);

    private static async Task WriteRenderedResultAsync(
        CliCommandResult result,
        CliOptions options,
        TextWriter stdout,
        TextWriter stderr,
        CliExecutionOptions executionOptions)
    {
        if (options.Json)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(result.Envelope, result.Envelope.GetType(), JsonOptions))
                .ConfigureAwait(false);
        }
        else
        {
            var human = result.RawHuman ? result.Human : CliAnsi.ColorizeHuman(result.Human, executionOptions.AnsiEnabled);
            await stdout.WriteLineAsync(human).ConfigureAwait(false);
        }

        if (!result.Cancelled)
        {
            return;
        }

        if (executionOptions.Context == CliExecutionContext.Session)
        {
            await stderr.WriteLineAsync("(cancelled mid-window — diagnostic session stopped, temp files cleaned up; payload is partial)")
                .ConfigureAwait(false);
            return;
        }

        await stderr.WriteLineAsync($"dotnet-diagnostics-cli {options.Command}: cancelled mid-window — diagnostic session stopped and temp files cleaned up. Payload is partial.")
            .ConfigureAwait(false);
    }

    private static async Task<T> WithProgressAsync<T>(
        TextWriter stderr,
        bool enabled,
        string command,
        Func<Task<T>> run,
        CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            return await run().ConfigureAwait(false);
        }

        using var tickerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ticker = RunSpinnerAsync(stderr, command, tickerCts.Token);
        try
        {
            return await run().ConfigureAwait(false);
        }
        finally
        {
            await tickerCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await ticker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await stderr.WriteAsync($"\r{new string(' ', 48)}\r").ConfigureAwait(false);
            await stderr.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task RunSpinnerAsync(TextWriter stderr, string command, CancellationToken cancellationToken)
    {
        var frames = new[] { '|', '/', '-', '\\' };
        var stopwatch = Stopwatch.StartNew();
        var i = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await stderr.WriteAsync(
                    string.Create(CultureInfo.InvariantCulture, $"\r{frames[i++ % frames.Length]} {command}… {stopwatch.Elapsed.TotalSeconds:F0}s (Ctrl-C to cancel)"))
                    .ConfigureAwait(false);
                await stderr.FlushAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
