using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core;

namespace DotnetDiagnostics.Cli;

internal static partial class CliCommands
{
    internal static IDisposable PushDockerBootstrapPlatformForCurrentAsyncFlow(IDockerBootstrapPlatform platform)
        => DockerBootstrapExecutionContext.Push(platform);

    internal static async Task<CliCommandResult> DockerBootstrapAsync(
        CliOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!TryValidateDockerBootstrap(options, out var validationError))
        {
            throw new ArgumentException(validationError, nameof(options));
        }

        var platform = DockerBootstrapExecutionContext.Current;
        var inspectCommand = new DockerCliInvocation("docker", ["inspect", "--type", "container", options.TargetContainer!]);
        var inspectResult = await platform.RunAsync(inspectCommand, cancellationToken).ConfigureAwait(false);
        if (inspectResult.ExitCode != 0)
        {
            return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                $"docker inspect failed for target container '{options.TargetContainer}'.",
                new DiagnosticError("ExternalDependencyFailed", BuildProcessError(inspectCommand, inspectResult))),
                static (_, _) => { });
        }

        DockerInspectContainer target;
        try
        {
            target = ParseInspect(inspectResult.Stdout);
        }
        catch (Exception ex)
        {
            return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                $"Could not parse docker inspect output for '{options.TargetContainer}'.",
                new DiagnosticError("ExternalDependencyFailed", ex.Message)),
                static (_, _) => { });
        }

        if (target.State?.Running != true || target.State.Pid <= 0)
        {
            return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                $"Target container '{target.DisplayName}' is not running.",
                new DiagnosticError("TargetNotRunning", "docker inspect reported a non-running container or an invalid host pid.")),
                static (_, _) => { });
        }

        var hostPort = options.HostPort ?? 18891;
        var profileName = options.ProfileName ?? SanitizeProfileName(target.DisplayName);
        var sidecarName = options.SidecarName ?? string.Create(CultureInfo.InvariantCulture, $"{profileName}-dotnet-diagnostics");
        var sidecarImage = DockerBootstrapImageResolver.Resolve(options.SidecarImage);
        var profileUrl = options.ProfileUrl ?? string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{hostPort}/mcp");
        var bearerToken = string.IsNullOrWhiteSpace(options.BootstrapBearerToken) ? GenerateSecretHex() : options.BootstrapBearerToken!;
        var delegationKey = string.IsNullOrWhiteSpace(options.BootstrapDelegationKey) ? GenerateSecretHex() : options.BootstrapDelegationKey!;
        var allowedCidrs = ResolveAllowedCidrs(profileUrl, options.AllowedCidrs);
        if (allowedCidrs is null)
        {
            return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                $"Could not derive AllowedCidrs for profile URL '{profileUrl}'. Re-run with --allow-cidr <cidr>.",
                new DiagnosticError("InvalidArgument", "Pass one or more --allow-cidr values when --profile-url uses a non-IP host name."),
                new NextActionHint("docker-bootstrap", "Re-run docker-bootstrap with --allow-cidr <cidr> matching the central's resolved route to that host.")),
                static (_, _) => { });
        }

        var profileUri = new Uri(profileUrl, UriKind.Absolute);
        var procStatusCommand = BuildProcStatusProbeCommand(target.State.Pid, sidecarImage);
        var procStatusResult = await platform.RunAsync(procStatusCommand, cancellationToken).ConfigureAwait(false);
        if (procStatusResult.ExitCode != 0)
        {
            return await BuildProcStatusProbeFailureAsync(
                platform,
                target,
                sidecarImage,
                procStatusCommand,
                procStatusResult,
                cancellationToken).ConfigureAwait(false);
        }

        if (!TryParseProcStatus(procStatusResult.Stdout, out var uid, out var gid, out var targetNamespacePid))
        {
            return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                $"Could not determine the target UID/GID and namespace PID for container '{target.DisplayName}'.",
                new DiagnosticError(
                    "ExternalDependencyFailed",
                    $"The Docker host-PID probe did not return effective Uid/Gid and NSpid lines. {BuildProcessError(procStatusCommand, procStatusResult)}")),
                static (_, _) => { });
        }

        var targetTmpPath = string.Create(CultureInfo.InvariantCulture, $"/proc/{targetNamespacePid}/root/tmp");
        var cleanupCommand = new DockerCliInvocation("docker", ["rm", "-f", sidecarName]);
        var runCommand = BuildDockerRunCommand(
            targetContainer: target.DisplayName,
            sidecarName,
            sidecarImage,
            uid,
            gid,
            hostPort,
            bearerToken,
            delegationKey,
            addSysPtrace: !options.NoSysPtrace,
            targetTmpPath);

        var runResult = await platform.RunAsync(runCommand, cancellationToken).ConfigureAwait(false);
        if (runResult.ExitCode != 0)
        {
            return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                $"docker run failed for sidecar '{sidecarName}'.",
                new DiagnosticError("ExternalDependencyFailed", BuildProcessError(runCommand, runResult))),
                static (_, _) => { });
        }

        var waitSeconds = options.WaitSeconds ?? 90;
        string? health;
        try
        {
            health = await WaitForContainerHealthyAsync(platform, sidecarName, TimeSpan.FromSeconds(waitSeconds), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupSidecarBestEffortAsync(platform, cleanupCommand).ConfigureAwait(false);
            throw;
        }

        if (health is not null)
        {
            var cleanup = await CleanupSidecarBestEffortAsync(platform, cleanupCommand).ConfigureAwait(false);
            return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                cleanup.Succeeded
                    ? $"Sidecar container '{sidecarName}' did not become healthy; the bootstrap removed it automatically."
                    : $"Sidecar container '{sidecarName}' did not become healthy, and automatic cleanup also failed.",
                new DiagnosticError(
                    "Timeout",
                    cleanup.Succeeded
                        ? health
                        : string.Create(
                            CultureInfo.InvariantCulture,
                            $"{health} Cleanup failure: {cleanup.Message} Run '{cleanupCommand.ToDisplayString()}' manually."))),
                static (_, _) => { });
        }

        var report = new DockerBootstrapReport(
            TargetContainer: target.DisplayName,
            SidecarContainer: sidecarName,
            SidecarImage: runCommand.Arguments[^1],
            TargetPid: target.State.Pid,
            TargetNamespacePid: targetNamespacePid,
            TargetUid: uid,
            TargetGid: gid,
            TargetTmpPath: targetTmpPath,
            HostPort: hostPort,
            SysPtraceEnabled: !options.NoSysPtrace,
            ProfileName: profileName,
            ProfileUrl: profileUrl,
            AllowedCidrs: allowedCidrs,
            AllowedPorts: [profileUri.Port],
            BearerToken: bearerToken,
            DelegationKey: delegationKey,
            ContainerId: runResult.Stdout.Trim(),
            DockerRunCommand: runCommand.ToDisplayString(),
            CleanupCommand: cleanupCommand.ToDisplayString(),
            CentralEnvLines: BuildCentralEnvLines(profileName, profileUrl, allowedCidrs, profileUri.Port, bearerToken, delegationKey),
            CentralJson: BuildCentralJson(profileName, profileUrl, allowedCidrs, profileUri.Port, bearerToken, delegationKey));

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"Started sidecar '{report.SidecarContainer}' for target '{report.TargetContainer}' on host port {report.HostPort} and emitted Orchestrator:ExternalMcpProfiles:{report.ProfileName} config.");

        return BuildResult(DiagnosticResult.Ok(
            report,
            summary,
            new NextActionHint(
                "docker-bootstrap",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Add the emitted Orchestrator__ExternalMcpProfiles__{profileName} keys (or JSON block) to the central MCP, restart it, then verify the profile with list_orchestrator(kind='external-profiles') before attach_to_pod(profileName='{profileName}')."))),
            static (sb, data) =>
            {
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"  target        : {data.TargetContainer} (host pid {data.TargetPid}, namespace pid {data.TargetNamespacePid}, uid:gid {data.TargetUid}:{data.TargetGid})");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  sidecar       : {data.SidecarContainer}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  image         : {data.SidecarImage}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  target tmp    : {data.TargetTmpPath} (via TMPDIR)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  profile       : {data.ProfileName}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  profile url   : {data.ProfileUrl}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  allowed cidrs : {string.Join(", ", data.AllowedCidrs)}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  allowed ports : {string.Join(", ", data.AllowedPorts)}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  SYS_PTRACE    : {(data.SysPtraceEnabled ? "enabled" : "disabled")}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  bearer token  : {data.BearerToken}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  delegation key: {data.DelegationKey}");
                sb.AppendLine("  docker run:");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {data.DockerRunCommand}");
                sb.AppendLine("  central env:");
                foreach (var line in data.CentralEnvLines)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {line}");
                }

                sb.AppendLine("  central appsettings.json:");
                foreach (var line in data.CentralJson.Split(Environment.NewLine, StringSplitOptions.None))
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {line}");
                }

                sb.AppendLine("  cleanup:");
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {data.CleanupCommand}");
                sb.AppendLine("  note:");
                sb.AppendLine("    This command does not register the profile dynamically. The current central tool surface lists and attaches existing external profiles only, so add the config and restart the central MCP first.");
            });
    }

    private static DockerInspectContainer ParseInspect(string stdout)
    {
        var containers = JsonSerializer.Deserialize<List<DockerInspectContainer>>(stdout, InspectJsonOptions) ?? [];
        if (containers.Count == 0)
        {
            throw new InvalidOperationException("docker inspect returned no container objects.");
        }

        return containers[0];
    }

    private static DockerCliInvocation BuildDockerRunCommand(
        string targetContainer,
        string sidecarName,
        string sidecarImage,
        int uid,
        int gid,
        int hostPort,
        string bearerToken,
        string delegationKey,
        bool addSysPtrace,
        string targetTmpPath)
    {
        var args = new List<string>
        {
            "run",
            "-d",
            "--name", sidecarName,
            "--pid", string.Create(CultureInfo.InvariantCulture, $"container:{targetContainer}"),
            "--user", string.Create(CultureInfo.InvariantCulture, $"{uid}:{gid}"),
            "--publish", string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{hostPort}:8080"),
            "--health-cmd", "dotnet DotnetDiagnostics.Mcp.dll --health-check --urls http://127.0.0.1:8080",
            "--health-interval", "2s",
            "--health-timeout", "2s",
            "--health-start-period", "10s",
            "--health-retries", "30",
            "--label", "io.github.pedrosakuma.dotnet-diagnostics.bootstrap=external-investigation",
            "--label", string.Create(CultureInfo.InvariantCulture, $"io.github.pedrosakuma.dotnet-diagnostics.target={targetContainer}"),
            "--env", "ASPNETCORE_URLS=http://0.0.0.0:8080",
            "--env", "DOTNET_EnableDiagnostics=0",
            "--env", "DOTNET_NOLOGO=1",
            "--env", string.Create(CultureInfo.InvariantCulture, $"TMPDIR={targetTmpPath}"),
            "--env", string.Create(CultureInfo.InvariantCulture, $"MCP_BEARER_TOKEN={bearerToken}"),
            "--env", string.Create(CultureInfo.InvariantCulture, $"MCP_INTERNAL_SCOPE_DELEGATION_KEY={delegationKey}"),
        };

        if (addSysPtrace)
        {
            args.Add("--cap-add");
            args.Add("SYS_PTRACE");
        }

        args.Add(sidecarImage);
        return new DockerCliInvocation("docker", args);
    }

    private static DockerCliInvocation BuildProcStatusProbeCommand(int targetHostPid, string sidecarImage)
        => new(
            "docker",
            [
                "run",
                "--rm",
                "--network", "none",
                "--read-only",
                "--cap-drop", "ALL",
                "--security-opt", "no-new-privileges",
                "--pid", "host",
                "--entrypoint", "/bin/cat",
                sidecarImage,
                string.Create(CultureInfo.InvariantCulture, $"/proc/{targetHostPid}/status"),
            ]);

    private static readonly string[] ImageResolutionFailureMarkers =
    [
        "unable to find image",
        "no such image",
        "manifest unknown",
        "pull access denied",
        "repository does not exist",
    ];

    private static async Task<CliCommandResult> BuildProcStatusProbeFailureAsync(
        IDockerBootstrapPlatform platform,
        DockerInspectContainer initialTarget,
        string sidecarImage,
        DockerCliInvocation probeCommand,
        DockerCliResult probeResult,
        CancellationToken cancellationToken)
    {
        foreach (var marker in ImageResolutionFailureMarkers)
        {
            if (probeResult.Stderr.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                    $"The selected sidecar image '{sidecarImage}' could not be resolved.",
                    new DiagnosticError("ExternalDependencyFailed", BuildProcessError(probeCommand, probeResult))),
                    static (_, _) => { });
            }
        }

        var recheckCommand = new DockerCliInvocation("docker", ["inspect", "--type", "container", initialTarget.DisplayName]);
        var recheckResult = await platform.RunAsync(recheckCommand, cancellationToken).ConfigureAwait(false);
        if (recheckResult.ExitCode != 0)
        {
            return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                $"Target container '{initialTarget.DisplayName}' stopped responding to docker inspect after the PID-namespace probe failed.",
                new DiagnosticError("TargetNotRunning", BuildProcessError(recheckCommand, recheckResult))),
                static (_, _) => { });
        }

        DockerInspectContainer recheckedTarget;
        try
        {
            recheckedTarget = ParseInspect(recheckResult.Stdout);
        }
        catch (Exception ex)
        {
            return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                $"Could not re-parse docker inspect output for '{initialTarget.DisplayName}' while verifying the failed PID-namespace probe.",
                new DiagnosticError("ExternalDependencyFailed", ex.Message)),
                static (_, _) => { });
        }

        if (recheckedTarget.State?.Running != true || recheckedTarget.State.Pid <= 0)
        {
            return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                $"Target container '{recheckedTarget.DisplayName}' is no longer running.",
                new DiagnosticError("TargetNotRunning", "docker inspect no longer reports a running container with a valid host pid.")),
                static (_, _) => { });
        }

        if (recheckedTarget.State.Pid != initialTarget.State!.Pid)
        {
            return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                $"Target container '{recheckedTarget.DisplayName}' restarted while docker-bootstrap was probing its PID namespace.",
                new DiagnosticError(
                    "TargetNotRunning",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"docker inspect first reported pid {initialTarget.State.Pid}, then pid {recheckedTarget.State.Pid}. Re-run docker-bootstrap against the current container instance."))),
                static (_, _) => { });
        }

        return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
            $"Target container '{recheckedTarget.DisplayName}' is still running, but Docker could not inspect its PID namespace.",
            new DiagnosticError(
                "HostProcNotAccessible",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"docker inspect still reports Running=true and Pid={recheckedTarget.State.Pid}, but the transient PID-namespace probe failed. {BuildProcessError(probeCommand, probeResult)}")),
            new NextActionHint(
                "docker-bootstrap",
                "Confirm the sidecar image contains /bin/cat and can join the target container's PID namespace, or use the manual compose/shared-volume recipe from docs/external-investigation-docker.md.")),
            static (_, _) => { });
    }

    private static async Task<string?> WaitForContainerHealthyAsync(
        IDockerBootstrapPlatform platform,
        string sidecarName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var inspect = new DockerCliInvocation("docker", ["inspect", "--type", "container", sidecarName]);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await platform.RunAsync(inspect, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return BuildProcessError(inspect, result);
            }

            DockerInspectContainer container;
            try
            {
                container = ParseInspect(result.Stdout);
            }
            catch (Exception ex)
            {
                return ex.Message;
            }

            var healthStatus = container.State?.Health?.Status;
            if (string.Equals(healthStatus, "healthy", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (string.Equals(container.State?.Status, "exited", StringComparison.OrdinalIgnoreCase)
                || string.Equals(container.State?.Status, "dead", StringComparison.OrdinalIgnoreCase))
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"Container state became {container.State?.Status}. Run 'docker logs {sidecarName}' for details.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Timed out after {timeout.TotalSeconds:F0}s waiting for 'docker inspect {sidecarName}' to report State.Health.Status=healthy. Run 'docker inspect {sidecarName}' and 'docker logs {sidecarName}' for details.");
    }

    private static async Task<DockerCleanupResult> CleanupSidecarBestEffortAsync(
        IDockerBootstrapPlatform platform,
        DockerCliInvocation cleanupCommand)
    {
        try
        {
            var cleanupResult = await platform.RunAsync(cleanupCommand, CancellationToken.None).ConfigureAwait(false);
            return cleanupResult.ExitCode == 0
                ? DockerCleanupResult.Success
                : new DockerCleanupResult(
                    false,
                    BuildProcessError(cleanupCommand, cleanupResult));
        }
        catch (Exception ex)
        {
            return new DockerCleanupResult(false, ex.Message);
        }
    }

    private static IReadOnlyList<string>? ResolveAllowedCidrs(string profileUrl, IReadOnlyList<string> explicitCidrs)
    {
        if (explicitCidrs.Count > 0)
        {
            return [.. explicitCidrs];
        }

        var uri = new Uri(profileUrl, UriKind.Absolute);
        if (IPAddress.TryParse(uri.Host, out var ip))
        {
            return [IpToSingleHostCidr(ip)];
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return ["127.0.0.1/32", "::1/128"];
        }

        return null;
    }

    private static string IpToSingleHostCidr(IPAddress ip)
        => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? string.Create(CultureInfo.InvariantCulture, $"{ip}/128")
            : string.Create(CultureInfo.InvariantCulture, $"{ip}/32");

    private static bool TryParseProcStatus(string procStatus, out int uid, out int gid, out int namespacePid)
    {
        uid = 0;
        gid = 0;
        namespacePid = 0;
        var foundUid = false;
        var foundGid = false;
        var foundNamespacePid = false;
        foreach (var line in procStatus.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Uid:", StringComparison.Ordinal))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    foundUid = int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out uid);
                }
            }
            else if (line.StartsWith("Gid:", StringComparison.Ordinal))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    foundGid = int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out gid);
                }
            }
            else if (line.StartsWith("NSpid:", StringComparison.Ordinal))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    foundNamespacePid = int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out namespacePid);
                }
            }
        }

        return foundUid && foundGid && foundNamespacePid && uid >= 0 && gid >= 0 && namespacePid > 0;
    }

    private static string GenerateSecretHex()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string SanitizeProfileName(string targetContainer)
    {
        var sb = new StringBuilder(targetContainer.Length);
        foreach (var ch in targetContainer)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        if (sb.Length == 0 || !char.IsLetterOrDigit(sb[0]))
        {
            sb.Insert(0, 'p');
        }

        return sb.ToString();
    }

    private static List<string> BuildCentralEnvLines(
        string profileName,
        string profileUrl,
        IReadOnlyList<string> allowedCidrs,
        int port,
        string bearerToken,
        string delegationKey)
    {
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Orchestrator__ExternalMcpProfiles__{profileName}__Url={profileUrl}"),
            string.Create(CultureInfo.InvariantCulture, $"Orchestrator__ExternalMcpProfiles__{profileName}__BearerToken={bearerToken}"),
            string.Create(CultureInfo.InvariantCulture, $"Orchestrator__ExternalMcpProfiles__{profileName}__DelegationKey={delegationKey}"),
            string.Create(CultureInfo.InvariantCulture, $"Orchestrator__ExternalMcpProfiles__{profileName}__AllowedPorts__0={port}"),
        };

        for (var i = 0; i < allowedCidrs.Count; i++)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Orchestrator__ExternalMcpProfiles__{profileName}__AllowedCidrs__{i}={allowedCidrs[i]}"));
        }

        return lines;
    }

    private static string BuildCentralJson(
        string profileName,
        string profileUrl,
        IReadOnlyList<string> allowedCidrs,
        int port,
        string bearerToken,
        string delegationKey)
    {
        var payload = new
        {
            Orchestrator = new
            {
                ExternalMcpProfiles = new Dictionary<string, object?>
                {
                    [profileName] = new
                    {
                        Url = profileUrl,
                        BearerToken = bearerToken,
                        DelegationKey = delegationKey,
                        AllowedCidrs = allowedCidrs,
                        AllowedPorts = new[] { port },
                    },
                },
            },
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static string BuildProcessError(DockerCliInvocation command, DockerCliResult result)
    {
        var stderr = result.Stderr.Trim();
        var stdout = result.Stdout.Trim();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Command '{command.ToDisplayString()}' exited {result.ExitCode}. {(stderr.Length > 0 ? $"stderr: {stderr}" : stdout.Length > 0 ? $"stdout: {stdout}" : "No output captured.")}");
    }

    private static readonly JsonSerializerOptions InspectJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    internal sealed record DockerBootstrapReport(
        string TargetContainer,
        string SidecarContainer,
        string SidecarImage,
        int TargetPid,
        int TargetNamespacePid,
        int TargetUid,
        int TargetGid,
        string TargetTmpPath,
        int HostPort,
        bool SysPtraceEnabled,
        string ProfileName,
        string ProfileUrl,
        IReadOnlyList<string> AllowedCidrs,
        IReadOnlyList<int> AllowedPorts,
        string BearerToken,
        string DelegationKey,
        string ContainerId,
        string DockerRunCommand,
        string CleanupCommand,
        IReadOnlyList<string> CentralEnvLines,
        string CentralJson);

    internal sealed record DockerCliInvocation(string FileName, IReadOnlyList<string> Arguments)
    {
        public string ToDisplayString()
            => string.Join(" ", [FileName, .. Arguments.Select(Quote)]);

        private static string Quote(string arg)
        {
            if (arg.Length == 0)
            {
                return "''";
            }

            return arg.Any(static ch => char.IsWhiteSpace(ch) || ch is '\'' or '"' or ':' or '=' or ',' or '/')
                ? string.Concat("'", arg.Replace("'", "'\"'\"'", StringComparison.Ordinal), "'")
                : arg;
        }
    }

    internal sealed record DockerCliResult(int ExitCode, string Stdout, string Stderr);

    private sealed record DockerCleanupResult(bool Succeeded, string? Message)
    {
        public static DockerCleanupResult Success { get; } = new(true, null);
    }

    internal interface IDockerBootstrapPlatform
    {
        Task<DockerCliResult> RunAsync(DockerCliInvocation invocation, CancellationToken cancellationToken);
    }

    private static class DockerBootstrapExecutionContext
    {
        private static readonly AsyncLocal<IDockerBootstrapPlatform?> CurrentPlatformSlot = new();

        public static IDockerBootstrapPlatform Current => CurrentPlatformSlot.Value ?? DefaultDockerBootstrapPlatform.Instance;

        public static IDisposable Push(IDockerBootstrapPlatform platform)
        {
            ArgumentNullException.ThrowIfNull(platform);
            var previous = CurrentPlatformSlot.Value;
            CurrentPlatformSlot.Value = platform;
            return new RestoreScope(previous);
        }

        private sealed class RestoreScope(IDockerBootstrapPlatform? previous) : IDisposable
        {
            public void Dispose() => CurrentPlatformSlot.Value = previous;
        }
    }

    private sealed class DefaultDockerBootstrapPlatform : IDockerBootstrapPlatform
    {
        public static DefaultDockerBootstrapPlatform Instance { get; } = new();

        public async Task<DockerCliResult> RunAsync(DockerCliInvocation invocation, CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo(invocation.FileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var arg in invocation.Arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = psi };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new DockerCliResult(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }

    }

    private sealed class DockerInspectContainer
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public DockerInspectState? State { get; set; }

        public string DisplayName
            => string.IsNullOrEmpty(Name)
                ? Id
                : Name.TrimStart('/');
    }

    private sealed class DockerInspectState
    {
        public bool Running { get; set; }

        public int Pid { get; set; }

        public string? Status { get; set; }

        public DockerInspectHealth? Health { get; set; }
    }

    private sealed class DockerInspectHealth
    {
        public string? Status { get; set; }
    }
}
