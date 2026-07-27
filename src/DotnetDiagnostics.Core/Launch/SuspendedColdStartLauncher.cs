using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnostics.Core.Launch;

/// <summary>
/// Launches a target .NET application with its runtime <b>suspended</b> on a reverse-connect diagnostic
/// port (cold-start capture, issue #446). The child is spawned with
/// <c>DOTNET_DiagnosticPorts=&lt;path&gt;,suspend</c>; the runtime then connects back to a
/// <see cref="DiagnosticsClientConnector"/> that this process listens on and blocks before any managed
/// code runs. The caller arms an EventPipe session on the returned <see cref="SuspendedTarget.Client"/>
/// and only then calls <see cref="SuspendedTarget.ResumeAsync"/>, so callers can capture EventPipe
/// activity emitted before an ordinary post-start attach. Mirrors dotnet-monitor's reverse-connect.
/// </summary>
/// <remarks>
/// <para>This primitive is used by the CLI and by the explicitly gated stdio MCP
/// <c>collect_events(kind="startup", launch=...)</c> path. It only wraps
/// <see cref="ChildProcessLauncher"/> and the connector's public
/// <see cref="DiagnosticsClientConnector.FromDiagnosticPort"/>, carries no MCP knowledge, and never
/// modifies the target application (only its launch parentage + env vars).</para>
/// </remarks>
public static class SuspendedColdStartLauncher
{
    // sockaddr_un.sun_path is 104 bytes on macOS and 108 bytes on Linux, including the trailing NUL.
    // Use the smaller portable payload limit so a path accepted here works on every supported Unix.
    internal const int MaxUnixSocketPathBytes = 103;
    private const string UnixFallbackDirectory = "/tmp";
    private const string PortFilePrefix = "dotnet-diagnostics-coldstart-";

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
        ValidatePortPathForLaunch(portPath);
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
            catch (Exception ex) when (ex is ArgumentOutOfRangeException
                or IOException
                or UnauthorizedAccessException
                or SocketException)
            {
                throw CreatePortException(portPath, ex);
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
            try
            {
                await target.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                TryDeletePort(portPath);
            }

            throw;
        }
    }

    private static string CreatePortPath() =>
        CreatePortPath(Path.GetTempPath(), UnixFallbackDirectory);

    internal static string CreatePortPath(string preferredDirectory, string unixFallbackDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferredDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(unixFallbackDirectory);

        var preferredPath = BuildPortPath(preferredDirectory);
        if (OperatingSystem.IsWindows() || UnixSocketPathFits(preferredPath))
        {
            return preferredPath;
        }

        var fallbackPath = BuildPortPath(unixFallbackDirectory);
        if (UnixSocketPathFits(fallbackPath))
        {
            return fallbackPath;
        }

        throw CreatePortException(
            fallbackPath,
            new ArgumentOutOfRangeException(
                nameof(unixFallbackDirectory),
                $"Both the configured temporary directory and fallback produce paths longer than {MaxUnixSocketPathBytes} UTF-8 bytes."));
    }

    private static string BuildPortPath(string directory)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(directory, $"{PortFilePrefix}{Guid.NewGuid():N}.sock"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException(
                $"Cannot create a cold-start reverse diagnostic socket path from temporary directory '{directory}'. " +
                $"Set TMPDIR to a valid, shorter writable directory (for example '{UnixFallbackDirectory}') and retry.",
                ex);
        }
    }

    private static bool UnixSocketPathFits(string path) =>
        Encoding.UTF8.GetByteCount(path) <= MaxUnixSocketPathBytes;

    private static void ValidatePortPathForLaunch(string portPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.GetDirectoryName(portPath);
        if (directory is null || !Directory.Exists(directory))
        {
            throw CreatePortException(
                portPath,
                new DirectoryNotFoundException($"Socket directory '{directory ?? portPath}' does not exist."));
        }

        try
        {
            _ = new UnixDomainSocketEndPoint(portPath);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw CreatePortException(portPath, ex);
        }
    }

    private static InvalidOperationException CreatePortException(string portPath, Exception innerException)
    {
        var directory = Path.GetDirectoryName(portPath) ?? portPath;
        if (OperatingSystem.IsWindows())
        {
            return new InvalidOperationException(
                $"Failed to create the cold-start reverse diagnostic port '{portPath}'. " +
                $"Ensure '{directory}' exists and is writable, then retry.",
                innerException);
        }

        var byteCount = Encoding.UTF8.GetByteCount(portPath);
        return new InvalidOperationException(
            $"Failed to create the cold-start reverse diagnostic socket '{portPath}'. " +
            $"Unix-domain socket paths must be at most {MaxUnixSocketPathBytes} UTF-8 bytes (this path is {byteCount}), " +
            $"and the socket directory must exist and be writable. Set TMPDIR to a shorter writable directory " +
            $"(for example '{UnixFallbackDirectory}'), or make '{directory}' writable, then retry.",
            innerException);
    }

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
