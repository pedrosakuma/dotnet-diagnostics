using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;

const string ProviderName = "Microsoft.Diagnostics.Monitoring.ParameterCapturing";
const string SharedPathEnvVar = "DotnetMonitor_Profiler_SharedPath";
const string RuntimeInstanceEnvVar = "DotnetMonitor_Profiler_RuntimeInstanceId";
const string ParameterCaptureEnvVar = "DotnetMonitor_InProcessFeatures_ParameterCapturing_Enable";
const string NotifyProfilerModulePathEnvVar = "DotnetMonitor_MonitorProfiler_ModulePath";
const string MutatingProfilerModulePathEnvVar = "DotnetMonitor_MutatingMonitorProfiler_ModulePath";
const string StartupHookAvailableEnvVar = "DotnetMonitor_InProcessFeatures_AvailableInfrastructure_StartupHook";
const string ManagedMessagingAvailableEnvVar = "DotnetMonitor_InProcessFeatures_AvailableInfrastructure_ManagedMessaging";

var repoRoot = Directory.GetCurrentDirectory();
var sampleDllPath = Path.Combine(repoRoot, "samples", "CoreClrSample", "bin", "Release", "net10.0", "CoreClrSample.dll");
var notifyProfilerPath = Path.Combine(repoRoot, "spike", "method-parameter-capture", "vendor", "linux-x64", "native", "libMonitorProfiler.so");
var mutatingProfilerPath = Path.Combine(repoRoot, "spike", "method-parameter-capture", "vendor", "linux-x64", "native", "libMutatingMonitorProfiler.so");
var startupHookPath = Path.Combine(repoRoot, "spike", "method-parameter-capture", "vendor", "shared", "any", "net6.0", "Microsoft.Diagnostics.Monitoring.StartupHook.dll");

EnsureExists(sampleDllPath);
EnsureExists(notifyProfilerPath);
EnsureExists(mutatingProfilerPath);
EnsureExists(startupHookPath);

var method = DiscoverMethod(sampleDllPath);
var port = 5181;
var baseAddress = new Uri($"http://127.0.0.1:{port}/");
var runtimeInstanceId = Guid.NewGuid();
var sharedPath = Path.Combine(repoRoot, ".dm");
var socketPath = Path.Combine(sharedPath, $"{runtimeInstanceId:D}.sock");
Directory.CreateDirectory(sharedPath);

Console.WriteLine($"Repo root: {repoRoot}");
Console.WriteLine($"Target method: {method.ModuleName}!{method.TypeName}.{method.MethodName}");
Console.WriteLine($"Shared path: {sharedPath}");
Console.WriteLine($"Profiler socket path: {socketPath} ({socketPath.Length} chars)");

using var process = StartSample(sampleDllPath, baseAddress);
var stdoutPump = PumpAsync(process.StandardOutput, "sample:stdout");
var stderrPump = PumpAsync(process.StandardError, "sample:stderr");
var eventObserver = new ParameterCaptureObserver();

try
{
    using var httpClient = CreateHttpClient();
    await WaitForSampleAsync(httpClient, baseAddress, process);

    var client = new DiagnosticsClient(process.Id);
    var processInfo = await InvokeTaskAsync(client, "GetProcessInfoAsync", CancellationToken.None);
    var notifyAttachSucceeded = false;
    var mutatingAttachSucceeded = false;
    var startupHookSucceeded = false;

    Console.WriteLine($"Diagnostics client assembly: {client.GetType().Assembly.Location}");
    Console.WriteLine($"Target PID: {process.Id}");
    Console.WriteLine($"Target runtime: {ReadProperty(processInfo, "RuntimeVersion")} ({ReadProperty(processInfo, "PortableRuntimeIdentifier")})");

    await InvokeTaskAsync(client, "SetEnvironmentVariableAsync", SharedPathEnvVar, sharedPath, CancellationToken.None);
    await InvokeTaskAsync(client, "SetEnvironmentVariableAsync", RuntimeInstanceEnvVar, runtimeInstanceId.ToString("D"), CancellationToken.None);
    await InvokeTaskAsync(client, "SetEnvironmentVariableAsync", ParameterCaptureEnvVar, "1", CancellationToken.None);
    await InvokeTaskAsync(client, "SetEnvironmentVariableAsync", NotifyProfilerModulePathEnvVar, notifyProfilerPath, CancellationToken.None);
    await InvokeTaskAsync(client, "SetEnvironmentVariableAsync", MutatingProfilerModulePathEnvVar, mutatingProfilerPath, CancellationToken.None);

    Console.WriteLine("Attaching notify-only profiler...");
    try
    {
        await InvokeTaskAsync(client, "AttachProfilerAsync", TimeSpan.FromSeconds(10), new Guid("6A494330-5848-4A23-9D87-0E57BBF6DE79"), notifyProfilerPath, Array.Empty<byte>(), CancellationToken.None);
        notifyAttachSucceeded = true;
        Console.WriteLine("Notify-only profiler attached.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Notify-only profiler attach failed: {ex.GetType().Name}: {ex.Message}");
    }

    Console.WriteLine("Attaching mutating profiler...");
    try
    {
        await InvokeTaskAsync(client, "AttachProfilerAsync", TimeSpan.FromSeconds(10), new Guid("38759DC4-0685-4771-AD09-A7627CE1B3B4"), mutatingProfilerPath, Array.Empty<byte>(), CancellationToken.None);
        mutatingAttachSucceeded = true;
        Console.WriteLine("Mutating profiler attached.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Mutating profiler attach failed: {ex.GetType().Name}: {ex.Message}");
    }

    Console.WriteLine("Applying startup hook...");
    try
    {
        await InvokeTaskAsync(client, "ApplyStartupHookAsync", startupHookPath, CancellationToken.None);
        startupHookSucceeded = true;
        Console.WriteLine("Startup hook applied.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Startup hook apply failed: {ex.GetType().Name}: {ex.Message}");
    }

    Console.WriteLine();
    Console.WriteLine("=== ATTACH SUMMARY ===");
    Console.WriteLine($"Notify profiler attach: {notifyAttachSucceeded}");
    Console.WriteLine($"Mutating profiler attach: {mutatingAttachSucceeded}");
    Console.WriteLine($"Startup hook apply: {startupHookSucceeded}");

    Dictionary<string, string>? attachSnapshot = null;
    if (startupHookSucceeded)
    {
        await Task.Delay(1000);
        attachSnapshot = await GetEnvironmentSnapshotAsync(client);
        Console.WriteLine($"Startup hook env: {attachSnapshot.GetValueOrDefault(StartupHookAvailableEnvVar, "<missing>")}");
        Console.WriteLine($"Managed messaging env: {attachSnapshot.GetValueOrDefault(ManagedMessagingAvailableEnvVar, "<missing>")}");
    }

    var infrastructureReady = attachSnapshot is not null &&
        string.Equals(attachSnapshot.GetValueOrDefault(StartupHookAvailableEnvVar), "1", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(attachSnapshot.GetValueOrDefault(ManagedMessagingAvailableEnvVar), "1", StringComparison.OrdinalIgnoreCase);

    if (!mutatingAttachSucceeded || !startupHookSucceeded || !infrastructureReady)
    {
        return;
    }

    var environment = attachSnapshot ?? await WaitForInfrastructureAsync(client);
    Console.WriteLine($"Managed messaging env: {environment.GetValueOrDefault(ManagedMessagingAvailableEnvVar, "<missing>")}");
    Console.WriteLine($"Startup hook env: {environment.GetValueOrDefault(StartupHookAvailableEnvVar, "<missing>")}");

    try
    {
        await WaitForSocketAsync(socketPath, process);
        Console.WriteLine($"Profiler socket: {socketPath}");

        using var session = client.StartEventPipeSession(
            new[] { new EventPipeProvider(ProviderName, EventLevel.Verbose, 0) },
            requestRundown: false,
            circularBufferMB: 256);
        using var source = new EventPipeEventSource(session.EventStream);
        using var sourceReady = new ManualResetEventSlim();

        source.Dynamic.All += traceEvent =>
        {
            if (!string.Equals(traceEvent.ProviderName, ProviderName, StringComparison.Ordinal))
            {
                return;
            }

            sourceReady.Set();
            eventObserver.OnEvent(traceEvent);
        };

        var processingTask = Task.Run(() => source.Process());
        sourceReady.Wait(TimeSpan.FromSeconds(2));

        var requestId = Guid.NewGuid();
        var payload = new StartCapturePayload
        {
            RequestId = requestId,
            Duration = TimeSpan.FromSeconds(15),
            Configuration = new CaptureParametersConfiguration
            {
                Methods = new[] { method },
                CaptureLimit = 4,
                UseDebuggerDisplayAttribute = false,
            },
        };

        Console.WriteLine($"Sending StartCapturingParameters request {requestId:D}...");
        await SendProfilerMessageAsync(socketPath, commandSet: 2, command: 0, JsonSerializer.SerializeToUtf8Bytes(payload), CancellationToken.None);

        await WaitUntilAsync(
            () => eventObserver.StartedRequestIds.Contains(requestId) || eventObserver.FailureByRequestId.ContainsKey(requestId),
            TimeSpan.FromSeconds(10),
            "parameter capture start");

        using var response = await httpClient.GetAsync(new Uri(baseAddress, "cpu-burn?ms=123"), HttpCompletionOption.ResponseHeadersRead);
        Console.WriteLine($"Triggered /cpu-burn?ms=123 => {(int)response.StatusCode} {response.StatusCode}");

        await WaitUntilAsync(
            () => eventObserver.ParameterValues.Count > 0 || eventObserver.FailureByRequestId.ContainsKey(requestId) || eventObserver.StoppedRequestIds.Contains(requestId),
            TimeSpan.FromSeconds(15),
            "parameter capture result");

        Console.WriteLine();
        Console.WriteLine("=== OBSERVED EVENTS ===");
        foreach (var line in eventObserver.LogLines)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine();
        Console.WriteLine("=== SUMMARY ===");
        Console.WriteLine($"Started: {eventObserver.StartedRequestIds.Contains(requestId)}");
        Console.WriteLine($"Stopped: {eventObserver.StoppedRequestIds.Contains(requestId)}");

        if (eventObserver.FailureByRequestId.TryGetValue(requestId, out var failure))
        {
            Console.WriteLine($"Failure: {failure}");
        }
        else if (eventObserver.ParameterValues.Count > 0)
        {
            Console.WriteLine("Captured parameters:");
            foreach (var value in eventObserver.ParameterValues)
            {
                Console.WriteLine($"  {value}");
            }
        }
        else
        {
            Console.WriteLine("No parameter values captured before timeout.");
        }

        Console.WriteLine("Sending StopCapturingParameters...");
        await SendProfilerMessageAsync(
            socketPath,
            commandSet: 2,
            command: 1,
            JsonSerializer.SerializeToUtf8Bytes(new StopCapturePayload { RequestId = requestId }),
            CancellationToken.None);

        try
        {
            session.Stop();
        }
        catch
        {
        }

        await processingTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Capture stage failed: {ex.GetType().Name}: {ex.Message}");
    }
}
finally
{
    if (!process.HasExited)
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }

    await Task.WhenAll(stdoutPump, stderrPump);
}

static HttpClient CreateHttpClient()
{
    return new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
    });
}

static void EnsureExists(string path)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException("Required file is missing.", path);
    }
}

static MethodDescription DiscoverMethod(string sampleDllPath)
{
    var loadContext = new ProbeLoadContext(sampleDllPath);

    try
    {
        var assembly = loadContext.LoadFromAssemblyPath(sampleDllPath);
        var candidateTypes = new[]
        {
            assembly.GetType("Program", throwOnError: false),
            assembly.GetType("<Program>$", throwOnError: false),
        }.Where(type => type is not null).Cast<Type>().ToArray();

        var method = candidateTypes
            .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate =>
                    candidate.Name.Contains("BurnCpu", StringComparison.Ordinal) &&
                    candidate.GetParameters().Length == 1 &&
                    candidate.GetParameters()[0].ParameterType == typeof(int)))
            .Single();

        return new MethodDescription
        {
            ModuleName = method.Module.Name,
            TypeName = method.DeclaringType?.FullName ?? throw new InvalidOperationException("Method has no declaring type."),
            MethodName = method.Name,
        };
    }
    finally
    {
        loadContext.Unload();
    }
}

static Process StartSample(string sampleDllPath, Uri baseAddress)
{
    var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList =
            {
                sampleDllPath,
                "--urls",
                baseAddress.ToString(),
            },
            WorkingDirectory = Path.GetDirectoryName(sampleDllPath) ?? throw new InvalidOperationException("Sample directory missing."),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        },
    };

    process.Start();
    return process;
}

static async Task PumpAsync(StreamReader reader, string prefix)
{
    string? line;
    while ((line = await reader.ReadLineAsync()) is not null)
    {
        Console.WriteLine($"{prefix}: {line}");
    }
}

static async Task WaitForSampleAsync(HttpClient httpClient, Uri baseAddress, Process process)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

    while (DateTime.UtcNow < deadline)
    {
        if (process.HasExited)
        {
            throw new InvalidOperationException($"CoreClrSample exited early with code {process.ExitCode}.");
        }

        try
        {
            using var response = await httpClient.GetAsync(new Uri(baseAddress, "weatherforecast"), HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                Console.WriteLine($"Sample responded with {(int)response.StatusCode} {response.StatusCode}.");
                return;
            }
        }
        catch
        {
        }

        await Task.Delay(500);
    }

    throw new TimeoutException("CoreClrSample did not become ready.");
}

static async Task<Dictionary<string, string>> WaitForInfrastructureAsync(DiagnosticsClient client)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

    while (DateTime.UtcNow < deadline)
    {
        var environment = await GetEnvironmentSnapshotAsync(client);
        if (environment.TryGetValue(StartupHookAvailableEnvVar, out var startupHookLoaded) &&
            string.Equals(startupHookLoaded, "1", StringComparison.OrdinalIgnoreCase) &&
            environment.TryGetValue(ManagedMessagingAvailableEnvVar, out var managedMessagingLoaded) &&
            string.Equals(managedMessagingLoaded, "1", StringComparison.OrdinalIgnoreCase))
        {
            return environment;
        }

        await Task.Delay(500);
    }

    throw new TimeoutException("Startup hook infrastructure did not report itself as ready.");
}

static async Task<Dictionary<string, string>> GetEnvironmentSnapshotAsync(DiagnosticsClient client)
{
    return new Dictionary<string, string>(
        (IDictionary<string, string>)(await InvokeTaskAsync(client, "GetProcessEnvironmentAsync", CancellationToken.None)),
        StringComparer.Ordinal);
}

static string ReadProperty(object instance, string propertyName)
{
    return instance.GetType().GetProperty(propertyName)?.GetValue(instance)?.ToString() ?? "<null>";
}

static async Task<object> InvokeTaskAsync(object instance, string methodName, params object?[] arguments)
{
    var candidates = instance.GetType()
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal))
        .ToArray();

    var method = candidates.SingleOrDefault(candidate =>
    {
        var parameters = candidate.GetParameters();
        if (parameters.Length != arguments.Length)
        {
            return false;
        }

        for (var index = 0; index < parameters.Length; index++)
        {
            var argument = arguments[index];
            if (argument is null)
            {
                continue;
            }

            var parameterType = parameters[index].ParameterType;
            var argumentType = argument.GetType();

            if (parameterType != argumentType &&
                !parameterType.IsInstanceOfType(argument) &&
                !parameterType.IsAssignableFrom(argumentType))
            {
                return false;
            }
        }

        return true;
    });

    if (method is null)
    {
        var available = string.Join(
            "; ",
            candidates.Select(candidate => $"{candidate.Name}({string.Join(", ", candidate.GetParameters().Select(parameter => parameter.ParameterType.FullName))})"));
        throw new InvalidOperationException($"Could not bind {methodName} on {instance.GetType().Assembly.Location}. Candidates: {available}");
    }

    var result = method.Invoke(instance, arguments) ?? throw new InvalidOperationException($"Method {methodName} returned null.");
    if (result is not Task task)
    {
        return result;
    }

    await task.ConfigureAwait(false);

    var taskType = task.GetType();
    if (!taskType.IsGenericType)
    {
        return new object();
    }

    return taskType.GetProperty("Result")?.GetValue(task) ?? throw new InvalidOperationException($"Method {methodName} completed without a result.");
}

static async Task WaitForSocketAsync(string socketPath, Process process)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

    while (DateTime.UtcNow < deadline)
    {
        if (process.HasExited)
        {
            throw new InvalidOperationException($"CoreClrSample exited early with code {process.ExitCode}.");
        }

        if (File.Exists(socketPath))
        {
            return;
        }

        await Task.Delay(250);
    }

    throw new TimeoutException($"Profiler socket did not appear: {socketPath}");
}

static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string description)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (predicate())
        {
            return;
        }

        await Task.Delay(250);
    }

    throw new TimeoutException($"Timed out waiting for {description}.");
}

static async Task SendProfilerMessageAsync(string socketPath, ushort commandSet, ushort command, byte[] payload, CancellationToken cancellationToken)
{
    using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);

    var header = new byte[sizeof(ushort) + sizeof(ushort) + sizeof(int)];
    Buffer.BlockCopy(BitConverter.GetBytes(commandSet), 0, header, 0, sizeof(ushort));
    Buffer.BlockCopy(BitConverter.GetBytes(command), 0, header, sizeof(ushort), sizeof(ushort));
    Buffer.BlockCopy(BitConverter.GetBytes(payload.Length), 0, header, sizeof(ushort) + sizeof(ushort), sizeof(int));

    await socket.SendAsync(header, SocketFlags.None, cancellationToken);
    if (payload.Length > 0)
    {
        await socket.SendAsync(payload, SocketFlags.None, cancellationToken);
    }

    var responseHeader = new byte[sizeof(ushort) + sizeof(ushort) + sizeof(int)];
    await ReceiveExactAsync(socket, responseHeader, cancellationToken);

    var responseCommandSet = BitConverter.ToUInt16(responseHeader, 0);
    var responseCommand = BitConverter.ToUInt16(responseHeader, sizeof(ushort));
    var responsePayloadLength = BitConverter.ToInt32(responseHeader, sizeof(ushort) + sizeof(ushort));

    if (responseCommandSet != 0 || responseCommand != 0 || responsePayloadLength != sizeof(int))
    {
        throw new InvalidOperationException($"Unexpected profiler response header: set={responseCommandSet}, command={responseCommand}, payload={responsePayloadLength}");
    }

    var responsePayload = new byte[sizeof(int)];
    await ReceiveExactAsync(socket, responsePayload, cancellationToken);
    var hr = BitConverter.ToInt32(responsePayload, 0);
    if (hr != 0)
    {
        throw new InvalidOperationException($"Profiler channel returned HRESULT 0x{hr:X8}.");
    }
}

static async Task ReceiveExactAsync(Socket socket, byte[] buffer, CancellationToken cancellationToken)
{
    var read = 0;
    while (read < buffer.Length)
    {
        var received = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, cancellationToken);
        if (received == 0)
        {
            throw new InvalidOperationException("Unexpected EOF from profiler socket.");
        }

        read += received;
    }
}

sealed class ParameterCaptureObserver
{
    private readonly object _gate = new();
    public List<string> LogLines { get; } = new();
    public HashSet<Guid> StartedRequestIds { get; } = new();
    public HashSet<Guid> StoppedRequestIds { get; } = new();
    public Dictionary<Guid, string> FailureByRequestId { get; } = new();
    public List<string> ParameterValues { get; } = new();

    public void OnEvent(TraceEvent traceEvent)
    {
        var line = BuildLogLine(traceEvent);
        lock (_gate)
        {
            LogLines.Add(line);
        }

        switch (traceEvent.ID)
        {
            case (TraceEventID)2:
                if (TryGetGuid(traceEvent, 0, out var startedRequestId))
                {
                    StartedRequestIds.Add(startedRequestId);
                }
                break;
            case (TraceEventID)3:
                if (TryGetGuid(traceEvent, 0, out var stoppedRequestId))
                {
                    StoppedRequestIds.Add(stoppedRequestId);
                }
                break;
            case (TraceEventID)4:
                if (TryGetGuid(traceEvent, 0, out var failedRequestId))
                {
                    var reason = Payload(traceEvent, 1);
                    var details = Payload(traceEvent, 2);
                    FailureByRequestId[failedRequestId] = $"{reason}: {details}";
                }
                break;
            case (TraceEventID)8:
                ParameterValues.Add($"{Payload(traceEvent, 2)}={Payload(traceEvent, 5)}");
                break;
        }
    }

    private static string BuildLogLine(TraceEvent traceEvent)
    {
        var builder = new StringBuilder();
        builder.Append($"eventId={traceEvent.ID}");
        builder.Append($", eventName={traceEvent.EventName}");

        for (var i = 0; i < traceEvent.PayloadNames.Length; i++)
        {
            builder.Append(i == 0 ? ", payloads=" : "; ");
            builder.Append(traceEvent.PayloadNames[i]);
            builder.Append('=');
            builder.Append(Payload(traceEvent, i));
        }

        return builder.ToString();
    }

    private static bool TryGetGuid(TraceEvent traceEvent, int index, out Guid value)
    {
        var payload = traceEvent.PayloadValue(index);
        switch (payload)
        {
            case Guid guid:
                value = guid;
                return true;
            case string text when Guid.TryParse(text, out var parsedGuid):
                value = parsedGuid;
                return true;
            default:
                value = Guid.Empty;
                return false;
        }
    }

    private static string Payload(TraceEvent traceEvent, int index)
    {
        var payload = traceEvent.PayloadValue(index);
        return payload switch
        {
            null => "<null>",
            Array array => $"[{string.Join(", ", array.Cast<object?>().Select(item => item?.ToString() ?? "<null>"))}]",
            _ => payload.ToString() ?? "<null>",
        };
    }
}

sealed class StartCapturePayload
{
    public Guid RequestId { get; set; }
    public TimeSpan Duration { get; set; }
    public CaptureParametersConfiguration Configuration { get; set; } = new();
}

sealed class StopCapturePayload
{
    public Guid RequestId { get; set; }
}

sealed class CaptureParametersConfiguration
{
    public MethodDescription[] Methods { get; set; } = Array.Empty<MethodDescription>();
    public bool UseDebuggerDisplayAttribute { get; set; }
    public int? CaptureLimit { get; set; }
}

sealed class MethodDescription
{
    public string ModuleName { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
}

sealed class ProbeLoadContext(string mainAssemblyPath) : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var resolvedPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return resolvedPath is null ? null : LoadFromAssemblyPath(resolvedPath);
    }
}
