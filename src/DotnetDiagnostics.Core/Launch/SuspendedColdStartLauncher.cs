using System.Diagnostics;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnostics.Core.Launch;

/// <summary>
/// Launches a target .NET application with its runtime <b>suspended</b> on a reverse-connect diagnostic
/// port (cold-start capture, issue #446). The child is spawned with
/// <c>DOTNET_DiagnosticPorts=&lt;path&gt;,suspend</c>; the runtime then connects back to a
/// <see cref="DiagnosticsClientConnector"/> that this process listens on and blocks before any managed
/// code runs. The caller arms an EventPipe session on the returned <see cref="SuspendedTarget.Client"/>
/// and only then calls <see cref="SuspendedTarget.ResumeAsync"/>, so static constructors, DI container
/// build, module-init exceptions and startup timings are captured — events the post-attach path
/// (CLI <c>--launch</c> / MCP attach) always misses. Mirrors dotnet-monitor's reverse-connect.
/// </summary>
/// <remarks>
/// <para>CLI-only by design: the MCP server attaches to already-running pids and cannot influence the
/// target's launch environment. This primitive only wraps <see cref="ChildProcessLauncher"/> and the
/// connector's public <see cref="DiagnosticsClientConnector.FromDiagnosticPort"/>, carries no MCP
/// knowledge, and never modifies the target application (only its launch parentage + env vars).</para>
/// </remarks>
public static class SuspendedColdStartLauncher
{
    /// <summary>
    /// Spawns <paramref name="fileName"/> with <paramref name="arguments"/> suspended on a fresh
    /// reverse-connect diagnostic port, waits up to <paramref name="connectTimeout"/> for the runtime to
    /// connect, and returns a <see cref="SuspendedTarget"/> that owns the child + the listening server.
    /// The runtime stays suspended until the caller invokes <see cref="SuspendedTarget.ResumeAsync"/>.
    /// <paramref name="workingDirectory"/> and <paramref name="additionalEnvironment"/> are forwarded to
    /// <see cref="ChildProcessLauncher.Launch"/> as-is; <c>DOTNET_DiagnosticPorts</c> always wins over any
    /// caller-supplied entry of the same name so the cold-start wiring can never be overridden.
    /// </summary>
    public static async Task<SuspendedTarget> LaunchSuspendedAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TextWriter? consoleSink,
        TimeSpan connectTimeout,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? additionalEnvironment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var portPath = CreatePortPath();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (additionalEnvironment is not null)
        {
            foreach (var pair in additionalEnvironment)
            {
                env[pair.Key] = pair.Value;
            }
        }

        // Always wins over a same-named caller entry — the cold-start reverse-connect wiring must
        // never be silently overridable via additionalEnvironment.
        env["DOTNET_DiagnosticPorts"] = string.Create(CultureInfo.InvariantCulture, $"{portPath},suspend");

        var target = ChildProcessLauncher.Launch(fileName, arguments, consoleSink, env, workingDirectory);
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(connectTimeout);

            DiagnosticsClientConnector? connector;
            try
            {
                connector = await DiagnosticsClientConnector
                    .FromDiagnosticPort(portPath, connectCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (connectCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Launched target (pid {target.ProcessId}) did not reverse-connect to the cold-start diagnostic port within {connectTimeout.TotalSeconds:0}s. Launch the app directly (e.g. 'dotnet App.dll' or a published apphost), not via 'dotnet run'.");
            }

            // FromDiagnosticPort returns null on a cancelled wait rather than throwing; surface the
            // same timeout/cancel signal instead of letting a null connector NRE in SuspendedTarget.
            if (connector is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    $"Launched target (pid {target.ProcessId}) did not reverse-connect to the cold-start diagnostic port within {connectTimeout.TotalSeconds:0}s. Launch the app directly (e.g. 'dotnet App.dll' or a published apphost), not via 'dotnet run'.");
            }

            return new SuspendedTarget(connector, target, target.ProcessId, portPath);
        }
        catch
        {
            await target.DisposeAsync().ConfigureAwait(false);
            TryDeletePort(portPath);
            throw;
        }
    }

    private static string CreatePortPath() =>
        Path.Combine(Path.GetTempPath(), $"dotnet-diagnostics-coldstart-{Guid.NewGuid():N}.sock");

    internal static void TryDeletePort(string portPath)
    {
        try
        {
            if (File.Exists(portPath))
            {
                File.Delete(portPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of the reverse-connect socket.
        }
    }
}
