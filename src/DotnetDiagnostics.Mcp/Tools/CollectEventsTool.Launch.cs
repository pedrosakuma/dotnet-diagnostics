using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Launch;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Security;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Mcp.Tools;

public sealed partial class CollectEventsTool
{
    /// <summary>
    /// Launch-and-suspend-then-arm path for <c>collect_events(kind="startup", launch=...)</c>
    /// (issue #665 Part A). Spawns <see cref="CollectEventsDispatchContext.Launch"/> suspended on a
    /// reverse-connect diagnostic port, arms the EventPipe startup session before the target's managed
    /// code runs, resumes, collects, and always terminates the launched process afterward — there is no
    /// detach-without-kill API on <see cref="SuspendedTarget"/> today, so v1 does not attempt one.
    /// </summary>
    private static async Task<DiagnosticResult<CollectEventsEnvelope>> RunStartupLaunchAsync(
        CollectEventsDispatchContext context,
        int effectiveDuration,
        CancellationToken cancellationToken)
    {
        var launch = context.Launch!;

        if (!StdioRootPrincipalAccessor.IsCurrent(context.PrincipalAccessor))
        {
            const string message = "launch is only supported when the server runs under --stdio (a shared HTTP " +
                "deployment cannot let one caller spawn processes on the host). Attach to an already-running " +
                "process with processId instead, or run collect_events over stdio.";
            return DiagnosticResult.Fail<CollectEventsEnvelope>(
                message, new DiagnosticError("NotSupported", message, "launch"));
        }

        if (!context.SecurityOptions.AllowProcessLaunch)
        {
            const string message = "launch is disabled — set 'Diagnostics:AllowProcessLaunch=true' in the server " +
                "configuration to allow collect_events(kind=\"startup\") to spawn target processes.";
            return DiagnosticResult.Fail<CollectEventsEnvelope>(
                message, new DiagnosticError("ProcessLaunchDisabled", message, "launch"));
        }

        if (string.IsNullOrWhiteSpace(launch.FileName))
        {
            const string message = "launch.fileName is required and must not be empty.";
            return DiagnosticResult.Fail<CollectEventsEnvelope>(
                message, new DiagnosticError("InvalidArgument", message, "launch.fileName"));
        }

        if (launch.ConnectTimeoutSeconds is <= 0 or > 3600 || double.IsNaN(launch.ConnectTimeoutSeconds))
        {
            const string message = "launch.connectTimeoutSeconds must be > 0 and <= 3600.";
            return DiagnosticResult.Fail<CollectEventsEnvelope>(
                message, new DiagnosticError("InvalidArgument", message, "launch.connectTimeoutSeconds"));
        }

        var logger = context.LoggerFactory?.CreateLogger("CollectEvents.Launch");
        using var consoleSink = new LoggerTextWriter(logger);

        SuspendedTarget? target = null;
        try
        {
            target = await SuspendedColdStartLauncher.LaunchSuspendedAsync(
                launch.FileName,
                launch.Arguments,
                consoleSink,
                TimeSpan.FromSeconds(launch.ConnectTimeoutSeconds),
                launch.WorkingDirectory,
                launch.EnvironmentVariables,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            var message = $"Timed out waiting for the launched process to connect to the diagnostic port: {ex.Message}";
            return DiagnosticResult.Fail<CollectEventsEnvelope>(
                message, new DiagnosticError("LaunchTimeout", message, "launch"));
        }
        catch (InvalidOperationException ex)
        {
            var message = $"Failed to launch the target process: {ex.Message}";
            return DiagnosticResult.Fail<CollectEventsEnvelope>(
                message, new DiagnosticError("LaunchFailed", message, "launch"));
        }

        try
        {
            var result = await EventCollectionUseCases.CollectStartupColdStart(
                context.StartupCollector,
                context.Handles,
                target,
                effectiveDuration,
                context.Depth,
                cancellationToken).ConfigureAwait(false);

            return Project(result, "startup", (env, data) => env with { Startup = data });
        }
        finally
        {
            await target.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Safe non-null <see cref="ChildProcessLauncher.Launch"/> console sink for the <c>launch</c> path:
    /// forwards the launched process's stdout/stderr lines to the server's own logger at Debug level
    /// instead of letting them leak onto this process's stdout, which under <c>--stdio</c> is reserved
    /// exclusively for JSON-RPC framing.
    /// </summary>
    private sealed class LoggerTextWriter(ILogger? logger) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                logger?.LogDebug("[launch] {Line}", value);
            }
        }

        public override void Write(char value)
        {
            // ChildProcessLauncher only ever calls WriteLine(string?); this override exists solely to
            // satisfy the abstract TextWriter contract without buffering partial lines.
        }
    }
}
