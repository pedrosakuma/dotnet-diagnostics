using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Internal;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.MethodParameters;

public sealed class MethodParameterCaptureCollector : IMethodParameterCaptureCollector
{
    private const string ProviderName = "Microsoft.Diagnostics.Monitoring.ParameterCapturing";
    private const string SharedPathEnvVar = "DotnetMonitor_Profiler_SharedPath";
    private const string RuntimeInstanceEnvVar = "DotnetMonitor_Profiler_RuntimeInstanceId";
    private const string ParameterCaptureEnvVar = "DotnetMonitor_InProcessFeatures_ParameterCapturing_Enable";
    private const string NotifyProfilerModulePathEnvVar = "DotnetMonitor_MonitorProfiler_ModulePath";
    private const string MutatingProfilerModulePathEnvVar = "DotnetMonitor_MutatingMonitorProfiler_ModulePath";
    private const string StartupHookAvailableEnvVar = "DotnetMonitor_InProcessFeatures_AvailableInfrastructure_StartupHook";
    private const string ManagedMessagingAvailableEnvVar = "DotnetMonitor_InProcessFeatures_AvailableInfrastructure_ManagedMessaging";
    private const string HotReloadEnvVar = "DOTNET_MODIFIABLE_ASSEMBLIES";
    private static readonly Guid NotifyOnlyProfilerClsid = new("6A494330-5848-4A23-9D87-0E57BBF6DE79");
    private static readonly Guid MutatingProfilerClsid = new("38759DC4-0685-4771-AD09-A7627CE1B3B4");
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(10);
    private const UnixFileMode SecureSharedDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private readonly ManagedMethodFilterResolver _resolver;
    private readonly SensitiveDataRedactor _redactor;
    private readonly ILogger<MethodParameterCaptureCollector> _logger;

    public MethodParameterCaptureCollector(MvidReader mvidReader, SensitiveDataRedactor redactor, ILogger<MethodParameterCaptureCollector>? logger = null)
    {
        _resolver = new ManagedMethodFilterResolver(mvidReader);
        _redactor = redactor;
        _logger = logger ?? NullLogger<MethodParameterCaptureCollector>.Instance;
    }

    public async Task<DiagnosticResult<MethodParameterCaptureArtifact>> CollectAsync(
        int processId,
        MethodParameterCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        var runtimeVersion = request.RuntimeVersion;
        if (!TryParseMajor(runtimeVersion, out var majorVersion) || majorVersion < 8)
        {
            return DiagnosticResult.Fail<MethodParameterCaptureArtifact>(
                "`collect_sample(kind=\"method-params\")` requires a .NET 8+ CoreCLR target because runtime startup-hook injection depends on `DiagnosticsClient.ApplyStartupHookAsync(...)` on .NET 8+.",
                new DiagnosticError(
                    "NotSupported",
                    "`collect_sample(kind=\"method-params\")` requires a .NET 8+ CoreCLR target because runtime startup-hook injection depends on `DiagnosticsClient.ApplyStartupHookAsync(...)` on .NET 8+.",
                    runtimeVersion),
                new NextActionHint("inspect_process", "Confirm the target runtime version before retrying.", new Dictionary<string, object?> { ["view"] = "capabilities", ["processId"] = processId }));
        }

        if (request.ProcessContext?.Runtime == Capabilities.RuntimeFlavor.NativeAot)
        {
            return DiagnosticResult.Fail<MethodParameterCaptureArtifact>(
                "`collect_sample(kind=\"method-params\")` is unsupported for NativeAOT targets. V1 requires CoreCLR profiler attach + ReJIT instrumentation.",
                new DiagnosticError(
                    "NotSupported",
                    "`collect_sample(kind=\"method-params\")` is unsupported for NativeAOT targets. V1 requires CoreCLR profiler attach + ReJIT instrumentation.",
                    request.ProcessContext.Runtime.ToString()),
                new NextActionHint("collect_sample", "Use cpu or native-alloc sampling instead of parameter capture for NativeAOT targets.", new Dictionary<string, object?> { ["processId"] = processId, ["kind"] = "cpu" }));
        }

        var isX64 = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64;
        if (!isX64 || !(OperatingSystem.IsLinux() || OperatingSystem.IsWindows()))
        {
            return DiagnosticResult.Fail<MethodParameterCaptureArtifact>(
                "`collect_sample(kind=\"method-params\")` is currently shipped only with linux-x64 and win-x64 profiler payloads. This build cannot stage the required dotnet-monitor native assets for the current RID.",
                new DiagnosticError(
                    "NotSupported",
                    "`collect_sample(kind=\"method-params\")` is currently shipped only with linux-x64 and win-x64 profiler payloads. This build cannot stage the required dotnet-monitor native assets for the current RID.",
                    System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier));
        }

        var assetRoot = GetAssetRoot();
        var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        var notifyProfilerFileName = OperatingSystem.IsWindows() ? "MonitorProfiler.dll" : "libMonitorProfiler.so";
        var mutatingProfilerFileName = OperatingSystem.IsWindows() ? "MutatingMonitorProfiler.dll" : "libMutatingMonitorProfiler.so";
        var notifyProfilerPath = Path.Combine(assetRoot, rid, "native", notifyProfilerFileName);
        var mutatingProfilerPath = Path.Combine(assetRoot, rid, "native", mutatingProfilerFileName);
        var startupHookPath = Path.Combine(assetRoot, "shared", "any", "net6.0", "Microsoft.Diagnostics.Monitoring.StartupHook.dll");
        if (!File.Exists(notifyProfilerPath) || !File.Exists(mutatingProfilerPath) || !File.Exists(startupHookPath))
        {
            return DiagnosticResult.Fail<MethodParameterCaptureArtifact>(
                "`collect_sample(kind=\"method-params\")` could not locate the vendored dotnet-monitor profiler payloads in the published package.",
                new DiagnosticError(
                    "NotSupported",
                    "Method-parameter capture assets are missing from the published output.",
                    assetRoot));
        }

        IReadOnlyList<ResolvedMethodBinding> resolvedMethods;
        try
        {
            resolvedMethods = _resolver.Resolve(processId, request.MethodFilters);
        }
        catch (ArgumentException ex)
        {
            return DiagnosticResult.Fail<MethodParameterCaptureArtifact>(
                ex.Message,
                new DiagnosticError("InvalidArgument", ex.Message, nameof(request.MethodFilters)));
        }

        var uniqueResolvedMethods = resolvedMethods.Select(binding => binding.Identity).Distinct().ToArray();

        var client = new DiagnosticsClient(processId);
        var environment = await DiagnosticsClientReflection.GetProcessEnvironmentAsync(client, cancellationToken).ConfigureAwait(false);
        if (environment.TryGetValue(HotReloadEnvVar, out var hotReloadState) && !string.IsNullOrWhiteSpace(hotReloadState) && !string.Equals(hotReloadState, "0", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticResult.Fail<MethodParameterCaptureArtifact>(
                "`collect_sample(kind=\"method-params\")` cannot run while Hot Reload is active for the target process. Stop the Hot Reload session and retry.",
                new DiagnosticError(
                    "Conflict",
                    "`collect_sample(kind=\"method-params\")` cannot run while Hot Reload is active for the target process. Stop the Hot Reload session and retry.",
                    hotReloadState),
                new NextActionHint("inspect_process", "Retry after stopping dotnet watch / Hot Reload for the target process.", new Dictionary<string, object?> { ["view"] = "info", ["processId"] = processId }));
        }

        var configuredProfiler = FirstNonEmpty(environment, "CORECLR_PROFILER", "COR_PROFILER");
        if (!string.IsNullOrWhiteSpace(configuredProfiler) &&
            !GuidEquals(configuredProfiler, NotifyOnlyProfilerClsid) &&
            !GuidEquals(configuredProfiler, MutatingProfilerClsid))
        {
            return DiagnosticResult.Fail<MethodParameterCaptureArtifact>(
                "`collect_sample(kind=\"method-params\")` cannot attach because the target already has a non-notify-only profiler attached. Remove/restart that profiler and retry.",
                new DiagnosticError(
                    "Conflict",
                    "`collect_sample(kind=\"method-params\")` cannot attach because the target already has a non-notify-only profiler attached. Remove/restart that profiler and retry.",
                    configuredProfiler),
                new NextActionHint("inspect_process", "Retry on a clean target process or use the existing profiler owner's tooling.", new Dictionary<string, object?> { ["view"] = "info", ["processId"] = processId }));
        }

        var runtimeInstanceId = TryGetRuntimeInstanceId(environment) ?? Guid.NewGuid();
        var sharedPath = CreateSecureSharedDirectory();
        try
        {
            var socketPath = Path.Combine(sharedPath, runtimeInstanceId.ToString("D") + ".sock");

            await DiagnosticsClientReflection.SetEnvironmentVariableAsync(client, SharedPathEnvVar, sharedPath, cancellationToken).ConfigureAwait(false);
            await DiagnosticsClientReflection.SetEnvironmentVariableAsync(client, RuntimeInstanceEnvVar, runtimeInstanceId.ToString("D"), cancellationToken).ConfigureAwait(false);
            await DiagnosticsClientReflection.SetEnvironmentVariableAsync(client, ParameterCaptureEnvVar, "1", cancellationToken).ConfigureAwait(false);
            await DiagnosticsClientReflection.SetEnvironmentVariableAsync(client, NotifyProfilerModulePathEnvVar, notifyProfilerPath, cancellationToken).ConfigureAwait(false);
            await DiagnosticsClientReflection.SetEnvironmentVariableAsync(client, MutatingProfilerModulePathEnvVar, mutatingProfilerPath, cancellationToken).ConfigureAwait(false);

            var infrastructureReady = IsInfrastructureReady(environment) && File.Exists(socketPath);
            if (!infrastructureReady)
            {
                try
                {
                    var attachFailure = await EnsureInfrastructureAsync(processId, client, notifyProfilerPath, mutatingProfilerPath, startupHookPath, environment, socketPath, cancellationToken).ConfigureAwait(false);
                    if (attachFailure is not null)
                    {
                        return attachFailure;
                    }
                }
                catch (TimeoutException ex)
                {
                    return DiagnosticResult.Fail<MethodParameterCaptureArtifact>(
                        $"collect_sample(kind=\"method-params\") failed for pid {processId}: {ex.Message}",
                        new DiagnosticError("Internal", ex.Message, ex.GetType().FullName));
                }
                catch (InvalidOperationException ex)
                {
                    return DiagnosticResult.Fail<MethodParameterCaptureArtifact>(
                        $"collect_sample(kind=\"method-params\") failed for pid {processId}: {ex.Message}",
                        new DiagnosticError("Internal", ex.Message, ex.GetType().FullName));
                }
            }

            var requestId = Guid.NewGuid();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var session = client.StartEventPipeSession(
                new[] { new EventPipeProvider(ProviderName, EventLevel.Informational, (long)EventKeywords.All) },
                requestRundown: false,
                circularBufferMB: 256);
            using var source = new EventPipeEventSource(session.EventStream);
            var observer = new ParameterCaptureObserver(_redactor, uniqueResolvedMethods);
            var processingTask = Task.Run(() =>
            {
                source.Dynamic.All += traceEvent =>
                {
                    if (string.Equals(traceEvent.ProviderName, ProviderName, StringComparison.Ordinal))
                    {
                        observer.OnEvent(traceEvent);
                    }
                };
                try
                {
                    source.Process();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Method-parameter EventPipe source terminated for pid {Pid}.", processId);
                }
            }, CancellationToken.None);

            try
            {
                var payload = new StartCapturePayload
                {
                    RequestId = requestId,
                    Duration = request.Duration,
                    Configuration = new CaptureParametersConfiguration
                    {
                        Methods = resolvedMethods.Select(binding => binding.PayloadDescription).DistinctBy(description => $"{description.ModuleName}|{description.TypeName}|{description.MethodName}").ToArray(),
                        UseDebuggerDisplayAttribute = false,
                        CaptureLimit = request.MaxEvents,
                    },
                };

                await SendProfilerMessageAsync(socketPath, commandSet: 2, command: 0, JsonSerializer.SerializeToUtf8Bytes(payload), cancellationToken).ConfigureAwait(false);
                await observer.WaitForStartedAsync(requestId, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                await observer.WaitForCompletionAsync(requestId, request.Duration + TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                observer.MarkCancelled();
            }
            catch (TimeoutException ex)
            {
                return DiagnosticResult.Fail<MethodParameterCaptureArtifact>(
                    $"collect_sample(kind=\"method-params\") failed for pid {processId}: {ex.Message}",
                    new DiagnosticError("Internal", ex.Message, ex.GetType().FullName),
                    new NextActionHint("inspect_process", "Confirm the target remains healthy and reachable before retrying method-parameter capture.", new Dictionary<string, object?> { ["processId"] = processId }));
            }
            finally
            {
                try
                {
                    await SendProfilerMessageAsync(socketPath, commandSet: 2, command: 1, JsonSerializer.SerializeToUtf8Bytes(new StopCapturePayload { RequestId = requestId }), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Best-effort stop for method-parameter capture failed for pid {Pid}.", processId);
                }

                try
                {
                    await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }

                try
                {
                    await processingTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            if (observer.TryGetFailure(requestId, out var failureReason))
            {
                return DiagnosticResult.Fail<MethodParameterCaptureArtifact>(
                    $"collect_sample(kind=\"method-params\") failed for pid {processId}: {failureReason}",
                    new DiagnosticError("Internal", failureReason, nameof(requestId)));
            }

            var artifact = observer.BuildArtifact(
                processId,
                DateTimeOffset.UtcNow,
                request.Duration,
                runtimeVersion,
                request.MethodFilters,
                uniqueResolvedMethods,
                request.MaxEvents,
                request.PreviewCount);
            return DiagnosticResult.Ok(artifact, $"Captured {artifact.CaptureCount} method invocation(s) from pid {processId}.") with { Cancelled = observer.Cancelled };
        }
        finally
        {
            CleanupSharedPath(sharedPath);
        }
    }

    private async Task<DiagnosticResult<MethodParameterCaptureArtifact>?> EnsureInfrastructureAsync(
        int processId,
        DiagnosticsClient client,
        string notifyProfilerPath,
        string mutatingProfilerPath,
        string startupHookPath,
        IReadOnlyDictionary<string, string> environment,
        string socketPath,
        CancellationToken cancellationToken)
    {
        var infraReady = IsInfrastructureReady(environment) && File.Exists(socketPath);
        if (!infraReady)
        {
            try
            {
                await DiagnosticsClientReflection.AttachProfilerAsync(client, AttachTimeout, NotifyOnlyProfilerClsid, notifyProfilerPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsProfilerConflict(ex))
            {
                return DiagnosticResult.Fail<MethodParameterCaptureArtifact>(
                    "`collect_sample(kind=\"method-params\")` cannot attach because the target already has a non-notify-only profiler attached. Remove/restart that profiler and retry.",
                    new DiagnosticError("Conflict", "`collect_sample(kind=\"method-params\")` cannot attach because the target already has a non-notify-only profiler attached. Remove/restart that profiler and retry.", ex.Message));
            }

            try
            {
                await DiagnosticsClientReflection.AttachProfilerAsync(client, AttachTimeout, MutatingProfilerClsid, mutatingProfilerPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsProfilerConflict(ex))
            {
                return DiagnosticResult.Fail<MethodParameterCaptureArtifact>(
                    "`collect_sample(kind=\"method-params\")` cannot attach because the target already has a non-notify-only profiler attached. Remove/restart that profiler and retry.",
                    new DiagnosticError("Conflict", "`collect_sample(kind=\"method-params\")` cannot attach because the target already has a non-notify-only profiler attached. Remove/restart that profiler and retry.", ex.Message));
            }

            try
            {
                await DiagnosticsClientReflection.ApplyStartupHookAsync(client, startupHookPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ApplyStartupHookAsync failed for pid {Pid}; proceeding to availability check.", processId);
            }
        }

        var readyEnvironment = await WaitForInfrastructureAsync(client, cancellationToken).ConfigureAwait(false);
        if (!IsInfrastructureReady(readyEnvironment))
        {
            return DiagnosticResult.Fail<MethodParameterCaptureArtifact>(
                "collect_sample(kind=\"method-params\") could not confirm that the dotnet-monitor startup-hook messaging infrastructure became available in the target process.",
                new DiagnosticError("Internal", "Startup hook infrastructure did not report itself as ready.", nameof(ManagedMessagingAvailableEnvVar)));
        }

        await WaitForSocketAsync(socketPath, processId, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private static async Task<IReadOnlyDictionary<string, string>> WaitForInfrastructureAsync(DiagnosticsClient client, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var environment = await DiagnosticsClientReflection.GetProcessEnvironmentAsync(client, cancellationToken).ConfigureAwait(false);
            if (IsInfrastructureReady(environment))
            {
                return environment;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Startup hook infrastructure did not report itself as ready.");
    }

    private static bool IsInfrastructureReady(IReadOnlyDictionary<string, string> environment)
        => string.Equals(environment.GetValueOrDefault(StartupHookAvailableEnvVar), "1", StringComparison.OrdinalIgnoreCase)
           && string.Equals(environment.GetValueOrDefault(ManagedMessagingAvailableEnvVar), "1", StringComparison.OrdinalIgnoreCase);

    private static Guid? TryGetRuntimeInstanceId(IReadOnlyDictionary<string, string> environment)
        => Guid.TryParse(environment.GetValueOrDefault(RuntimeInstanceEnvVar), out var value) ? value : null;

    private static bool TryParseMajor(string? version, out int major)
    {
        major = 0;
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var token = version.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        if (Version.TryParse(token, out var parsed))
        {
            major = parsed.Major;
            return true;
        }

        return false;
    }

    private static bool GuidEquals(string candidate, Guid expected)
        => Guid.TryParse(candidate, out var parsed) && parsed == expected;

    private static string? FirstNonEmpty(IReadOnlyDictionary<string, string> environment, params string[] names)
    {
        foreach (var name in names)
        {
            if (environment.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string GetAssetRoot()
        => Path.Combine(AppContext.BaseDirectory, "Vendor", "dotnet-monitor", "10.0.2");

    private static string CreateSecureSharedDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateSecureSharedDirectoryWindows();
        }

        if (OperatingSystem.IsLinux())
        {
            return CreateSecureSharedDirectoryLinux();
        }

        throw new PlatformNotSupportedException("Method-parameter capture only supports linux-x64 and win-x64 hosts.");
    }

    [SupportedOSPlatform("linux")]
    private static string CreateSecureSharedDirectoryLinux()
    {
        var tempRoot = Path.GetTempPath();
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Path.Combine(tempRoot, $"ddmp-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}");
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                continue;
            }

            Directory.CreateDirectory(candidate, SecureSharedDirectoryMode);
            File.SetUnixFileMode(candidate, SecureSharedDirectoryMode);
            ValidateSharedDirectoryLinux(candidate);
            return candidate;
        }

        throw new InvalidOperationException("Could not allocate a secure short-path directory for method-parameter capture.");
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateSharedDirectoryLinux(string path)
    {
        var info = new DirectoryInfo(path);
        info.Refresh();
        if (info.LinkTarget is not null)
        {
            throw new InvalidOperationException($"Refusing to use symlinked shared directory '{path}' for method-parameter capture.");
        }

        var mode = File.GetUnixFileMode(path);
        if (mode != SecureSharedDirectoryMode)
        {
            throw new InvalidOperationException($"Shared directory '{path}' must have Unix mode 0700; found '{mode}'.");
        }
    }

    // Windows AF_UNIX sockets (afunix.sys, Windows 10 1803+) are used by dotnet-monitor's
    // profiler for the exact same "<sharedPath>/<runtimeInstanceId>.sock" transport as Linux
    // (see IpcCommServer::Bind, which builds a sockaddr_un regardless of TARGET_WINDOWS/TARGET_UNIX).
    // .NET's System.Net.Sockets.UnixDomainSocketEndPoint works unmodified against that transport
    // on Windows, so only directory hardening needs a platform-specific implementation here:
    // Unix 0700 permission bits have no Windows equivalent, so we harden the shared directory with
    // an explicit, non-inherited ACL granting FullControl to the current user only (owner-only),
    // mirroring the Linux 0700 guarantee.
    [SupportedOSPlatform("windows")]
    private static string CreateSecureSharedDirectoryWindows()
    {
        var tempRoot = Path.GetTempPath();
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Path.Combine(tempRoot, $"ddmp-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}");
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                continue;
            }

            var directoryInfo = Directory.CreateDirectory(candidate);
            ApplyOwnerOnlyAcl(directoryInfo);
            ValidateSharedDirectoryWindows(candidate);
            return candidate;
        }

        throw new InvalidOperationException("Could not allocate a secure short-path directory for method-parameter capture.");
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyOwnerOnlyAcl(DirectoryInfo directoryInfo)
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not resolve the current Windows user SID to secure the shared directory.");

        var security = new DirectorySecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directoryInfo.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateSharedDirectoryWindows(string path)
    {
        var directoryInfo = new DirectoryInfo(path);
        directoryInfo.Refresh();
        if (directoryInfo.LinkTarget is not null)
        {
            throw new InvalidOperationException($"Refusing to use symlinked/reparse-point shared directory '{path}' for method-parameter capture.");
        }

        var security = directoryInfo.GetAccessControl();
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not resolve the current Windows user SID to validate the shared directory.");
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && !rule.IdentityReference.Equals(currentUser))
            {
                throw new InvalidOperationException($"Shared directory '{path}' grants access to a principal other than the current user; refusing to use it for method-parameter capture.");
            }
        }
    }

    private static void CleanupSharedPath(string sharedPath)
    {
        try
        {
            if (Directory.Exists(sharedPath))
            {
                Directory.Delete(sharedPath, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static async Task WaitForSocketAsync(string socketPath, int processId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(socketPath))
            {
                return;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"Target process {processId} exited before the profiler socket appeared.");
                }
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException($"Target process {processId} exited before the profiler socket appeared.");
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Profiler socket did not appear: {socketPath}");
    }

    private static async Task SendProfilerMessageAsync(string socketPath, ushort commandSet, ushort command, byte[] payload, CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);

        Span<byte> header = stackalloc byte[sizeof(ushort) + sizeof(ushort) + sizeof(int)];
        BitConverter.GetBytes(commandSet).CopyTo(header[..sizeof(ushort)]);
        BitConverter.GetBytes(command).CopyTo(header.Slice(sizeof(ushort), sizeof(ushort)));
        BitConverter.GetBytes(payload.Length).CopyTo(header.Slice(sizeof(ushort) + sizeof(ushort), sizeof(int)));
        await socket.SendAsync(header.ToArray(), SocketFlags.None, cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await socket.SendAsync(payload, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        }

        var responseHeader = new byte[sizeof(ushort) + sizeof(ushort) + sizeof(int)];
        await ReceiveExactAsync(socket, responseHeader, cancellationToken).ConfigureAwait(false);
        var responseCommandSet = BitConverter.ToUInt16(responseHeader, 0);
        var responseCommand = BitConverter.ToUInt16(responseHeader, sizeof(ushort));
        var responsePayloadLength = BitConverter.ToInt32(responseHeader, sizeof(ushort) + sizeof(ushort));
        if (responseCommandSet != 0 || responseCommand != 0 || responsePayloadLength != sizeof(int))
        {
            throw new InvalidOperationException($"Unexpected profiler response header: set={responseCommandSet}, command={responseCommand}, payload={responsePayloadLength}.");
        }

        var responsePayload = new byte[sizeof(int)];
        await ReceiveExactAsync(socket, responsePayload, cancellationToken).ConfigureAwait(false);
        var hr = BitConverter.ToInt32(responsePayload, 0);
        if (hr != 0)
        {
            throw new InvalidOperationException($"Profiler channel returned HRESULT 0x{hr:X8}.");
        }
    }

    private static async Task ReceiveExactAsync(Socket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidOperationException("Unexpected EOF from profiler socket.");
            }

            offset += read;
        }
    }

    private static bool IsProfilerConflict(Exception ex)
        => ex.Message.Contains("profiler", StringComparison.OrdinalIgnoreCase)
           && (ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("loaded", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("attached", StringComparison.OrdinalIgnoreCase));

    private sealed class ParameterCaptureObserver(SensitiveDataRedactor redactor, IReadOnlyList<ResolvedMethodIdentity> resolvedMethods)
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, string> _failureByRequestId = new();
        private readonly HashSet<Guid> _started = new();
        private readonly HashSet<Guid> _stopped = new();
        private readonly List<MethodParameterInvocation> _events = new();
        private readonly Dictionary<int, PendingInvocation> _pendingByThread = new();
        private int _sequence;
        private int _truncatedCount;
        private int _redactedCount;

        public bool Cancelled { get; private set; }

        public void MarkCancelled() => Cancelled = true;

        public void OnEvent(TraceEvent traceEvent)
        {
            lock (_gate)
            {
                switch (traceEvent.EventName)
                {
                    case "Capturing/Start":
                        if (TryGetGuid(traceEvent, "RequestId", 0, out var started))
                        {
                            _started.Add(started);
                        }
                        break;
                    case "Capturing/Stop":
                        FlushPending(traceEvent.ThreadID);
                        if (TryGetGuid(traceEvent, "RequestId", 0, out var stopped))
                        {
                            _stopped.Add(stopped);
                        }
                        break;
                    case "FailedToCapture":
                        if (TryGetGuid(traceEvent, "RequestId", 0, out var failed))
                        {
                            _failureByRequestId[failed] = $"{Payload(traceEvent, "reason", 1)}: {Payload(traceEvent, "details", 2)}";
                        }
                        break;
                    case "CapturedParameter/Start":
                        FlushPending(traceEvent.ThreadID);
                        _pendingByThread[traceEvent.ThreadID] = new PendingInvocation(
                            Interlocked.Increment(ref _sequence),
                            DateTimeOffset.UtcNow,
                            ResolveMethodIdentity(traceEvent),
                            new List<CapturedParameterValue>());
                        break;
                    case "CapturedParameter":
                        if (_pendingByThread.TryGetValue(traceEvent.ThreadID, out var pending))
                        {
                            var parameter = RenderParameter(traceEvent);
                            if (parameter.Redacted)
                            {
                                _redactedCount++;
                            }
                            if (parameter.Truncated)
                            {
                                _truncatedCount++;
                            }
                            pending.Parameters.Add(parameter);
                        }
                        break;
                }
            }
        }

        public bool TryGetFailure(Guid requestId, out string reason)
        {
            lock (_gate)
            {
                return _failureByRequestId.TryGetValue(requestId, out reason!);
            }
        }

        public async Task WaitForStartedAsync(Guid requestId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            await WaitUntilAsync(() =>
            {
                lock (_gate)
                {
                    return _started.Contains(requestId) || _failureByRequestId.ContainsKey(requestId);
                }
            }, timeout, "parameter capture start", cancellationToken).ConfigureAwait(false);
        }

        public async Task WaitForCompletionAsync(Guid requestId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            await WaitUntilAsync(() =>
            {
                lock (_gate)
                {
                    return _stopped.Contains(requestId) || _failureByRequestId.ContainsKey(requestId) || Cancelled;
                }
            }, timeout, "parameter capture stop", cancellationToken).ConfigureAwait(false);
        }

        public MethodParameterCaptureArtifact BuildArtifact(
            int processId,
            DateTimeOffset capturedAtUtc,
            TimeSpan requestedDuration,
            string runtimeVersion,
            IReadOnlyList<MethodFilter> methodFilters,
            IReadOnlyList<ResolvedMethodIdentity> resolvedIdentities,
            int maxEvents,
            int previewCount)
        {
            lock (_gate)
            {
                foreach (var threadId in _pendingByThread.Keys.ToArray())
                {
                    FlushPending(threadId);
                }

                var stopReason = Cancelled
                    ? "cancelled"
                    : _events.Count >= maxEvents
                        ? "max_events_reached"
                        : "duration_elapsed";
                return new MethodParameterCaptureArtifact(
                    processId,
                    capturedAtUtc,
                    requestedDuration,
                    "CoreClr",
                    runtimeVersion,
                    methodFilters,
                    resolvedIdentities,
                    maxEvents,
                    previewCount,
                    _events.Count,
                    0,
                    _truncatedCount,
                    _redactedCount,
                    _truncatedCount > 0,
                    _redactedCount > 0,
                    stopReason,
                    _events.ToArray());
            }
        }

        private void FlushPending(int threadId)
        {
            if (_pendingByThread.Remove(threadId, out var pending))
            {
                _events.Add(new MethodParameterInvocation(pending.Sequence, pending.TimestampUtc, pending.Method, pending.Parameters.ToArray()));
            }
        }

        private ResolvedMethodIdentity ResolveMethodIdentity(TraceEvent traceEvent)
        {
            var moduleName = Payload(traceEvent, "methodModuleName", 4);
            var typeName = Payload(traceEvent, "methodDeclaringTypeName", 5);
            var methodName = Payload(traceEvent, "methodName", 3);
            var matched = resolvedMethods.FirstOrDefault(identity =>
                string.Equals(identity.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(identity.TypeName, typeName, StringComparison.Ordinal) &&
                string.Equals(identity.MethodName, methodName, StringComparison.Ordinal));
            return matched ?? new ResolvedMethodIdentity(moduleName, Guid.Empty.ToString("D"), typeName, methodName, 0, 0, Array.Empty<string>());
        }

        private CapturedParameterValue RenderParameter(TraceEvent traceEvent)
        {
            var name = Payload(traceEvent, "parameterName", 2);
            var typeName = Payload(traceEvent, "parameterType", 3);
            if (string.IsNullOrWhiteSpace(typeName))
            {
                typeName = "System.Object";
            }

            var rawValue = Payload(traceEvent, "parameterValue", 5);
            var notes = new List<string>();
            var truncated = false;
            var bytes = Encoding.UTF8.GetByteCount(rawValue);
            if (bytes > 4096)
            {
                rawValue = TrimUtf8(rawValue, 4096);
                truncated = true;
                notes.Add("value-cap");
            }

            var redactedValue = redactor.Redact(rawValue) ?? rawValue;
            var redacted = !string.Equals(rawValue, redactedValue, StringComparison.Ordinal);
            return new CapturedParameterValue(name, typeName, redactedValue, redacted, truncated) { Notes = notes };
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string description, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (predicate())
                {
                    return;
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException($"Timed out waiting for {description}.");
        }

        private static string TrimUtf8(string value, int maxBytes)
        {
            if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            {
                return value;
            }

            var builder = new StringBuilder(value.Length);
            var usedBytes = 0;
            foreach (var rune in value.EnumerateRunes())
            {
                var runeBytes = Encoding.UTF8.GetByteCount(rune.ToString());
                if (usedBytes + runeBytes > maxBytes)
                {
                    break;
                }

                builder.Append(rune.ToString());
                usedBytes += runeBytes;
            }

            return builder.ToString();
        }

        private static bool TryGetGuid(TraceEvent traceEvent, string payloadName, int fallbackIndex, out Guid value)
        {
            var payload = PayloadObject(traceEvent, payloadName, fallbackIndex);
            if (payload is Guid guid)
            {
                value = guid;
                return true;
            }
            if (payload is string text && Guid.TryParse(text, out var parsed))
            {
                value = parsed;
                return true;
            }

            value = Guid.Empty;
            return false;
        }

        private static string Payload(TraceEvent traceEvent, string payloadName, int fallbackIndex)
            => PayloadObject(traceEvent, payloadName, fallbackIndex) switch
            {
                null => string.Empty,
                Array array => string.Join(", ", array.Cast<object?>().Select(item => item?.ToString() ?? string.Empty)),
                _ => Convert.ToString(PayloadObject(traceEvent, payloadName, fallbackIndex), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            };

        private static object? PayloadObject(TraceEvent traceEvent, string payloadName, int fallbackIndex)
        {
            for (var i = 0; i < traceEvent.PayloadNames.Length; i++)
            {
                if (string.Equals(traceEvent.PayloadNames[i], payloadName, StringComparison.OrdinalIgnoreCase))
                {
                    return traceEvent.PayloadValue(i);
                }
            }

            return fallbackIndex < traceEvent.PayloadNames.Length ? traceEvent.PayloadValue(fallbackIndex) : null;
        }

        private sealed record PendingInvocation(int Sequence, DateTimeOffset TimestampUtc, ResolvedMethodIdentity Method, List<CapturedParameterValue> Parameters);
    }

    private static class DiagnosticsClientReflection
    {
        private static readonly MethodInfo? SetEnvironmentVariableAsyncMethod = typeof(DiagnosticsClient).GetMethod(
            "SetEnvironmentVariableAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(string), typeof(string), typeof(CancellationToken) },
            modifiers: null);
        private static readonly MethodInfo? AttachProfilerAsyncMethod = typeof(DiagnosticsClient).GetMethod(
            "AttachProfilerAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(TimeSpan), typeof(Guid), typeof(string), typeof(byte[]), typeof(CancellationToken) },
            modifiers: null);
        private static readonly MethodInfo? ApplyStartupHookAsyncMethod = typeof(DiagnosticsClient).GetMethod(
            "ApplyStartupHookAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(string), typeof(CancellationToken) },
            modifiers: null);
        private static readonly MethodInfo? GetProcessEnvironmentAsyncMethod = typeof(DiagnosticsClient).GetMethod(
            "GetProcessEnvironmentAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(CancellationToken) },
            modifiers: null);

        public static Task SetEnvironmentVariableAsync(DiagnosticsClient client, string name, string value, CancellationToken cancellationToken)
            => InvokeTaskAsync(client, SetEnvironmentVariableAsyncMethod, name, value, cancellationToken);

        public static Task AttachProfilerAsync(DiagnosticsClient client, TimeSpan timeout, Guid clsid, string profilerPath, CancellationToken cancellationToken)
            => InvokeTaskAsync(client, AttachProfilerAsyncMethod, timeout, clsid, profilerPath, Array.Empty<byte>(), cancellationToken);

        public static Task ApplyStartupHookAsync(DiagnosticsClient client, string startupHookPath, CancellationToken cancellationToken)
            => InvokeTaskAsync(client, ApplyStartupHookAsyncMethod, startupHookPath, cancellationToken);

        public static async Task<IReadOnlyDictionary<string, string>> GetProcessEnvironmentAsync(DiagnosticsClient client, CancellationToken cancellationToken)
        {
            var result = await InvokeTaskAsync<IDictionary<string, string>>(client, GetProcessEnvironmentAsyncMethod, cancellationToken).ConfigureAwait(false);
            return new Dictionary<string, string>(result, StringComparer.Ordinal);
        }

        private static async Task InvokeTaskAsync(object instance, MethodInfo? method, params object?[] arguments)
        {
            _ = await InvokeTaskAsync<object>(instance, method, arguments).ConfigureAwait(false);
        }

        private static async Task<T> InvokeTaskAsync<T>(object instance, MethodInfo? method, params object?[] arguments)
        {
            if (method is null)
            {
                throw new MissingMethodException(instance.GetType().FullName, nameof(method));
            }

            var result = method.Invoke(instance, arguments) ?? throw new InvalidOperationException($"{method.Name} returned null.");
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                var taskType = task.GetType();
                if (!taskType.IsGenericType)
                {
                    return default!;
                }

                return (T)(taskType.GetProperty("Result")?.GetValue(task)
                    ?? throw new InvalidOperationException($"{method.Name} completed without a result."));
            }

            return (T)result;
        }
    }
}
