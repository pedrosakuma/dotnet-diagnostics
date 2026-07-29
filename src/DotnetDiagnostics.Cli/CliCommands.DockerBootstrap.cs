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
        var targetInspection = await InspectContainerAsync(platform, options.TargetContainer!, cancellationToken).ConfigureAwait(false);
        if (targetInspection.Error is not null)
        {
            return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                $"docker inspect failed for target container '{options.TargetContainer}'.",
                new DiagnosticError("ExternalDependencyFailed", targetInspection.Error)),
                static (_, _) => { });
        }

        var target = targetInspection.Container!;
        if (target.State?.Running != true || target.State.Pid <= 0)
        {
            return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                $"Target container '{target.DisplayName}' is not running.",
                new DiagnosticError("TargetNotRunning", "docker inspect reported a non-running container or an invalid host pid.")),
                static (_, _) => { });
        }

        var profileName = options.ProfileName ?? SanitizeProfileName(target.DisplayName);
        var sidecarName = options.SidecarName ?? string.Create(CultureInfo.InvariantCulture, $"{profileName}-dotnet-diagnostics");
        var networkAlias = BuildNetworkAlias(sidecarName);
        var sidecarImage = DockerBootstrapImageResolver.Resolve(options.SidecarImage);
        var bearerToken = string.IsNullOrWhiteSpace(options.BootstrapBearerToken) ? GenerateSecretHex() : options.BootstrapBearerToken!;
        var delegationKey = string.IsNullOrWhiteSpace(options.BootstrapDelegationKey) ? GenerateSecretHex() : options.BootstrapDelegationKey!;

        DockerInspectContainer? central = null;
        DockerNetworkPlan? networkPlan = null;
        var centralAware = options.CentralContainer is not null;
        var internalProfileOverride = options.ProfileUrl is not null
            && (IsInternalSidecarUrl(options.ProfileUrl, sidecarName)
                || IsInternalSidecarUrl(options.ProfileUrl, networkAlias));
        var useInternalRoute = centralAware && (options.ProfileUrl is null || internalProfileOverride);
        if (centralAware)
        {
            var centralInspection = await InspectContainerAsync(platform, options.CentralContainer!, cancellationToken).ConfigureAwait(false);
            if (centralInspection.Error is not null)
            {
                return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                    $"docker inspect failed for central container '{options.CentralContainer}'.",
                    new DiagnosticError("ExternalDependencyFailed", centralInspection.Error)),
                    static (_, _) => { });
            }

            central = centralInspection.Container!;
            if (central.State?.Running != true)
            {
                return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                    $"Central container '{central.DisplayName}' is not running.",
                    new DiagnosticError("CentralNotRunning", "docker inspect reported a non-running central container.")),
                    static (_, _) => { });
            }

            if (string.Equals(target.Id, central.Id, StringComparison.Ordinal))
            {
                return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                    "The target and central container resolve to the same Docker container.",
                    new DiagnosticError("InvalidArgument", "--central-container must identify a different container from --target-container.")),
                    static (_, _) => { });
            }

            if (!useInternalRoute && options.HostPort is null)
            {
                return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                    "An explicit non-sidecar --profile-url requires an explicit --host-port in central-aware mode.",
                    new DiagnosticError("InvalidArgument", "Bootstrap cannot prove how an alternate URL reaches the sidecar unless its host listener is explicitly published.")),
                    static (_, _) => { });
            }

            if (useInternalRoute)
            {
                var networkResult = await SelectCentralNetworkAsync(platform, target, central, cancellationToken).ConfigureAwait(false);
                if (networkResult.Error is not null)
                {
                    return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                        networkResult.Error,
                        new DiagnosticError("CentralNetworkUnavailable", networkResult.Detail!)),
                        static (_, _) => { });
                }

                networkPlan = networkResult.Plan;
            }
        }

        var hostPort = centralAware ? options.HostPort : options.HostPort ?? 18891;
        var preliminaryProfileUrl = options.ProfileUrl
            ?? (centralAware
                ? string.Create(CultureInfo.InvariantCulture, $"http://{networkAlias}:8080/mcp")
                : string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{hostPort}/mcp"));

        if (centralAware
            && central?.HostConfig?.NetworkMode != "host"
            && IsLoopbackHost(new Uri(preliminaryProfileUrl, UriKind.Absolute).Host))
        {
            return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                $"Profile URL '{preliminaryProfileUrl}' uses loopback from a non-host-network central container.",
                new DiagnosticError("InvalidArgument", "127.0.0.1/localhost would address the central container itself, not the sidecar.")),
                static (_, _) => { });
        }

        IReadOnlyList<string>? preliminaryAllowedCidrs = null;
        if (networkPlan is null)
        {
            preliminaryAllowedCidrs = ResolveAllowedCidrs(preliminaryProfileUrl, options.AllowedCidrs);
            if (preliminaryAllowedCidrs is null)
            {
                return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                    $"Could not derive AllowedCidrs for profile URL '{preliminaryProfileUrl}'. Re-run with --allow-cidr <cidr>.",
                    new DiagnosticError("InvalidArgument", "Pass one or more --allow-cidr values when --profile-url uses a non-IP host name."),
                    new NextActionHint("docker-bootstrap", "Re-run docker-bootstrap with --allow-cidr <cidr> matching the central's resolved route to that host.")),
                    static (_, _) => { });
            }
        }

        if (centralAware)
        {
            var collisionCommand = new DockerCliInvocation(
                "docker",
                ["ps", "-a", "--filter", string.Create(CultureInfo.InvariantCulture, $"name=^/{sidecarName}$"), "--format", "{{.ID}}"]);
            var collisionResult = await platform.RunAsync(collisionCommand, cancellationToken).ConfigureAwait(false);
            if (collisionResult.ExitCode != 0)
            {
                return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                    $"Could not check whether sidecar name '{sidecarName}' is available.",
                    new DiagnosticError("ExternalDependencyFailed", BuildProcessError(collisionCommand, collisionResult))),
                    static (_, _) => { });
            }

            if (!string.IsNullOrWhiteSpace(collisionResult.Stdout))
            {
                return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                    $"Docker container name '{sidecarName}' is already in use.",
                    new DiagnosticError("NameCollision", "Choose a different --sidecar-name or remove the existing container explicitly; bootstrap never replaces it.")),
                    static (_, _) => { });
            }
        }

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
        var removeCommand = new DockerCliInvocation("docker", ["rm", "-f", sidecarName]);
        var disconnectCommand = networkPlan is null
            ? null
            : new DockerCliInvocation("docker", ["network", "disconnect", "--force", networkPlan.Name, sidecarName]);
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
            targetTmpPath,
            dockerNetwork: networkPlan?.Name,
            networkAlias);

        var runResult = await platform.RunAsync(runCommand, cancellationToken).ConfigureAwait(false);
        if (runResult.ExitCode != 0)
        {
            return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                $"docker run failed for sidecar '{sidecarName}'.",
                new DiagnosticError("ExternalDependencyFailed", BuildProcessError(runCommand, runResult))),
                static (_, _) => { });
        }

        var networkConnected = networkPlan is not null;

        var waitSeconds = options.WaitSeconds ?? 90;
        string? health;
        try
        {
            health = await WaitForContainerHealthyAsync(platform, sidecarName, TimeSpan.FromSeconds(waitSeconds), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupBootstrapResourcesBestEffortAsync(platform, disconnectCommand, removeCommand, networkConnected).ConfigureAwait(false);
            throw;
        }

        if (health is not null)
        {
            var cleanup = await CleanupBootstrapResourcesBestEffortAsync(platform, disconnectCommand, removeCommand, networkConnected).ConfigureAwait(false);
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
                            $"{health} Cleanup failure: {cleanup.Message} Run '{removeCommand.ToDisplayString()}' manually."))),
                static (_, _) => { });
        }

        string profileUrl = preliminaryProfileUrl;
        IReadOnlyList<string>? allowedCidrs;
        string? sidecarAddress = null;
        if (networkPlan is not null)
        {
            var sidecarInspection = await InspectContainerAsync(platform, sidecarName, cancellationToken).ConfigureAwait(false);
            if (sidecarInspection.Error is not null
                || !TryGetNetworkAddress(sidecarInspection.Container, networkPlan.Name, out sidecarAddress))
            {
                var cleanup = await CleanupBootstrapResourcesBestEffortAsync(platform, disconnectCommand, removeCommand, networkConnected).ConfigureAwait(false);
                var failure = cleanup.Succeeded
                    ? DiagnosticResult.Fail<DockerBootstrapReport>(
                        $"Could not resolve sidecar '{sidecarName}' on Docker network '{networkPlan.Name}'; bootstrap cleaned up its resources.",
                        new DiagnosticError(
                            "ExternalDependencyFailed",
                            sidecarInspection.Error ?? "docker inspect did not report an IPv4 or IPv6 address on the selected network."))
                    : DiagnosticResult.Fail<DockerBootstrapReport>(
                        $"Could not resolve sidecar '{sidecarName}' on Docker network '{networkPlan.Name}', and cleanup also failed.",
                        new DiagnosticError(
                            "ExternalDependencyFailed",
                            sidecarInspection.Error ?? "docker inspect did not report an IPv4 or IPv6 address on the selected network."),
                        new NextActionHint("docker-bootstrap", $"Run '{removeCommand.ToDisplayString()}' manually."));
                return BuildResult(failure, static (_, _) => { });
            }

            allowedCidrs = options.AllowedCidrs.Count > 0
                ? [.. options.AllowedCidrs]
                : [IpToSingleHostCidr(IPAddress.Parse(sidecarAddress))];
        }
        else
        {
            allowedCidrs = preliminaryAllowedCidrs;
        }
        var resolvedAllowedCidrs = allowedCidrs
            ?? throw new InvalidOperationException("Allowed CIDRs must be resolved before the bootstrap report is built.");

        if (central is not null)
        {
            var centralRecheck = await InspectContainerAsync(platform, central.DisplayName, cancellationToken).ConfigureAwait(false);
            var centralStable = centralRecheck.Error is null
                && centralRecheck.Container?.State?.Running == true
                && string.Equals(centralRecheck.Container.Id, central.Id, StringComparison.Ordinal)
                && (networkPlan is null || centralRecheck.Container.NetworkSettings?.Networks.ContainsKey(networkPlan.Name) == true);
            if (!centralStable)
            {
                await CleanupBootstrapResourcesBestEffortAsync(platform, disconnectCommand, removeCommand, networkConnected).ConfigureAwait(false);
                return BuildResult(DiagnosticResult.Fail<DockerBootstrapReport>(
                    $"Central container '{central.DisplayName}' restarted, was recreated, stopped, or left the selected network during bootstrap.",
                    new DiagnosticError("CentralChanged", "Re-run docker-bootstrap against the current central container instance and network attachment.")),
                    static (_, _) => { });
            }
        }

        var profileUri = new Uri(profileUrl, UriKind.Absolute);
        var cleanupCommands = new List<string>();
        if (disconnectCommand is not null)
        {
            cleanupCommands.Add(disconnectCommand.ToDisplayString());
        }
        cleanupCommands.Add(removeCommand.ToDisplayString());

        var report = new DockerBootstrapReport(
            TargetContainer: target.DisplayName,
            CentralContainer: central?.DisplayName,
            CentralContainerId: central?.Id,
            SidecarContainer: sidecarName,
            SidecarImage: runCommand.Arguments[^1],
            TargetPid: target.State.Pid,
            TargetNamespacePid: targetNamespacePid,
            TargetUid: uid,
            TargetGid: gid,
            TargetTmpPath: targetTmpPath,
            Route: networkPlan is not null ? "docker-network" : centralAware ? "explicit" : "host-loopback",
            DockerNetwork: networkPlan?.Name,
            DockerNetworkId: networkPlan?.Id,
            DockerNetworkAlias: networkPlan is null ? null : networkAlias,
            SidecarNetworkAddress: sidecarAddress,
            HostPort: hostPort,
            HostPortPublished: hostPort is not null,
            SysPtraceEnabled: !options.NoSysPtrace,
            ProfileName: profileName,
            ProfileUrl: profileUrl,
            AllowedCidrs: resolvedAllowedCidrs,
            AllowedPorts: [profileUri.Port],
            BearerToken: bearerToken,
            DelegationKey: delegationKey,
            ContainerId: runResult.Stdout.Trim(),
            DockerRunCommand: runCommand.ToDisplayString(),
            NetworkConnectedByBootstrap: networkConnected,
            CleanupCommands: cleanupCommands,
            CentralEnvLines: BuildCentralEnvLines(profileName, profileUrl, resolvedAllowedCidrs, profileUri.Port, bearerToken, delegationKey),
            CentralJson: BuildCentralJson(profileName, profileUrl, resolvedAllowedCidrs, profileUri.Port, bearerToken, delegationKey));

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"Started sidecar '{report.SidecarContainer}' for target '{report.TargetContainer}' using route '{report.Route}' and emitted Orchestrator:ExternalMcpProfiles:{report.ProfileName} config.");

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
                sb.AppendLine(CultureInfo.InvariantCulture, $"  central       : {data.CentralContainer ?? "(host process / not inspected)"}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  image         : {data.SidecarImage}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  target tmp    : {data.TargetTmpPath} (via TMPDIR)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  route         : {data.Route}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  docker network: {data.DockerNetwork ?? "(none selected)"}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  network alias : {data.DockerNetworkAlias ?? "(none)"}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  sidecar addr  : {data.SidecarNetworkAddress ?? "(not inspected)"}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  host publish  : {(data.HostPortPublished ? $"127.0.0.1:{data.HostPort}:8080" : "none")}");
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
                foreach (var command in data.CleanupCommands)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {command}");
                }
                if (data.NetworkConnectedByBootstrap)
                {
                    sb.AppendLine("    The disconnect is bootstrap-owned; ignore 'not connected'/'no such container' when repeating cleanup.");
                }
                sb.AppendLine("  note:");
                sb.AppendLine("    This command does not register the profile dynamically. The current central tool surface lists and attaches existing external profiles only, so add the config and restart the central MCP first.");
            });
    }

    private static async Task<DockerContainerInspection> InspectContainerAsync(
        IDockerBootstrapPlatform platform,
        string container,
        CancellationToken cancellationToken)
    {
        var command = new DockerCliInvocation("docker", ["inspect", "--type", "container", container]);
        var result = await platform.RunAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return new DockerContainerInspection(null, BuildProcessError(command, result));
        }

        try
        {
            return new DockerContainerInspection(ParseInspect(result.Stdout), null);
        }
        catch (Exception ex)
        {
            return new DockerContainerInspection(null, ex.Message);
        }
    }

    private static async Task<DockerNetworkSelection> SelectCentralNetworkAsync(
        IDockerBootstrapPlatform platform,
        DockerInspectContainer target,
        DockerInspectContainer central,
        CancellationToken cancellationToken)
    {
        if (string.Equals(central.HostConfig?.NetworkMode, "host", StringComparison.OrdinalIgnoreCase))
        {
            return new DockerNetworkSelection(
                null,
                $"Central container '{central.DisplayName}' uses network=host, so an internal container-DNS route cannot be derived.",
                "Use an explicit --profile-url, --allow-cidr, and --host-port for this topology, or attach the central to a user-defined bridge network.");
        }

        var centralNetworks = central.NetworkSettings?.Networks.Keys
            .Where(static name => !string.Equals(name, "bridge", StringComparison.Ordinal)
                && !string.Equals(name, "host", StringComparison.Ordinal)
                && !string.Equals(name, "none", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (centralNetworks.Length == 0)
        {
            return new DockerNetworkSelection(
                null,
                $"Central container '{central.DisplayName}' has no user-defined Docker network suitable for container DNS.",
                "Attach the central to a user-defined local bridge network, then re-run docker-bootstrap.");
        }

        var command = new DockerCliInvocation("docker", ["network", "inspect", .. centralNetworks]);
        var result = await platform.RunAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return new DockerNetworkSelection(
                null,
                $"Could not inspect the central container's Docker networks.",
                BuildProcessError(command, result));
        }

        List<DockerInspectNetwork> inspected;
        try
        {
            inspected = JsonSerializer.Deserialize<List<DockerInspectNetwork>>(result.Stdout, InspectJsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            return new DockerNetworkSelection(null, "Could not parse docker network inspect output.", ex.Message);
        }

        var targetNetworks = target.NetworkSettings?.Networks.Keys.ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var candidate = inspected
            .Where(static network => string.Equals(network.Driver, "bridge", StringComparison.OrdinalIgnoreCase)
                && string.Equals(network.Scope, "local", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(network => targetNetworks.Contains(network.Name))
            .ThenBy(static network => network.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (candidate is null)
        {
            return new DockerNetworkSelection(
                null,
                $"Central container '{central.DisplayName}' has no supported local bridge network.",
                "Only user-defined local bridge networks are selected automatically; overlay, macvlan, and default bridge routes require explicit options.");
        }

        return new DockerNetworkSelection(
            new DockerNetworkPlan(candidate.Name, candidate.Id, targetNetworks.Contains(candidate.Name)),
            null,
            null);
    }

    private static bool IsInternalSidecarUrl(string profileUrl, string sidecarName)
    {
        var uri = new Uri(profileUrl, UriKind.Absolute);
        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            && string.Equals(uri.Host, sidecarName, StringComparison.OrdinalIgnoreCase)
            && uri.Port == 8080;
    }

    private static bool IsLoopbackHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    private static bool TryGetNetworkAddress(
        DockerInspectContainer? container,
        string networkName,
        out string address)
    {
        address = string.Empty;
        if (container?.NetworkSettings?.Networks.TryGetValue(networkName, out var endpoint) != true)
        {
            return false;
        }

        address = !string.IsNullOrWhiteSpace(endpoint!.IPAddress)
            ? endpoint.IPAddress
            : endpoint.GlobalIPv6Address;
        return IPAddress.TryParse(address, out _);
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
        int? hostPort,
        string bearerToken,
        string delegationKey,
        bool addSysPtrace,
        string targetTmpPath,
        string? dockerNetwork,
        string networkAlias)
    {
        var args = new List<string>
        {
            "run",
            "-d",
            "--name", sidecarName,
            "--pid", string.Create(CultureInfo.InvariantCulture, $"container:{targetContainer}"),
            "--user", string.Create(CultureInfo.InvariantCulture, $"{uid}:{gid}"),
        };

        if (dockerNetwork is not null)
        {
            args.AddRange(["--network", dockerNetwork, "--network-alias", networkAlias]);
        }

        if (hostPort is not null)
        {
            args.AddRange(["--publish", string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{hostPort}:8080")]);
        }

        args.AddRange(
        [
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
        ]);

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

    private static async Task<DockerCleanupResult> CleanupBootstrapResourcesBestEffortAsync(
        IDockerBootstrapPlatform platform,
        DockerCliInvocation? disconnectCommand,
        DockerCliInvocation removeCommand,
        bool networkConnected)
    {
        var failures = new List<string>();
        if (networkConnected && disconnectCommand is not null)
        {
            var disconnect = await CleanupSidecarBestEffortAsync(platform, disconnectCommand).ConfigureAwait(false);
            if (!disconnect.Succeeded)
            {
                failures.Add(disconnect.Message!);
            }
        }

        var remove = await CleanupSidecarBestEffortAsync(platform, removeCommand).ConfigureAwait(false);
        if (!remove.Succeeded)
        {
            failures.Add(remove.Message!);
        }

        return failures.Count == 0
            ? DockerCleanupResult.Success
            : new DockerCleanupResult(false, string.Join(" ", failures));
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

    private static string BuildNetworkAlias(string sidecarName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sidecarName));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"ddmcp-{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}");
    }

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
        string? CentralContainer,
        string? CentralContainerId,
        string SidecarContainer,
        string SidecarImage,
        int TargetPid,
        int TargetNamespacePid,
        int TargetUid,
        int TargetGid,
        string TargetTmpPath,
        string Route,
        string? DockerNetwork,
        string? DockerNetworkId,
        string? DockerNetworkAlias,
        string? SidecarNetworkAddress,
        int? HostPort,
        bool HostPortPublished,
        bool SysPtraceEnabled,
        string ProfileName,
        string ProfileUrl,
        IReadOnlyList<string> AllowedCidrs,
        IReadOnlyList<int> AllowedPorts,
        string BearerToken,
        string DelegationKey,
        string ContainerId,
        string DockerRunCommand,
        bool NetworkConnectedByBootstrap,
        IReadOnlyList<string> CleanupCommands,
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

    private sealed record DockerContainerInspection(DockerInspectContainer? Container, string? Error);

    private sealed record DockerNetworkSelection(DockerNetworkPlan? Plan, string? Error, string? Detail);

    private sealed record DockerNetworkPlan(string Name, string Id, bool SharedWithTarget);

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

        public DockerInspectHostConfig? HostConfig { get; set; }

        public DockerInspectNetworkSettings? NetworkSettings { get; set; }

        public string DisplayName
            => string.IsNullOrEmpty(Name)
                ? Id
                : Name.TrimStart('/');
    }

    private sealed class DockerInspectHostConfig
    {
        public string? NetworkMode { get; set; }
    }

    private sealed class DockerInspectNetworkSettings
    {
        public Dictionary<string, DockerInspectEndpoint> Networks { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class DockerInspectEndpoint
    {
        public string IPAddress { get; set; } = string.Empty;

        public string GlobalIPv6Address { get; set; } = string.Empty;
    }

    private sealed class DockerInspectNetwork
    {
        public string Name { get; set; } = string.Empty;

        public string Id { get; set; } = string.Empty;

        public string Driver { get; set; } = string.Empty;

        public string Scope { get; set; } = string.Empty;
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
