using System.Diagnostics;
using System.Runtime.InteropServices;
using DotnetDbgMcp.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDbgMcp.Core.ProcessDiscovery;

/// <summary>
/// Default implementation of <see cref="IProcessDiscovery"/> for processes on the local machine.
/// </summary>
public sealed class LocalProcessDiscovery : IProcessDiscovery
{
    private readonly ILogger<LocalProcessDiscovery> _logger;

    public LocalProcessDiscovery(ILogger<LocalProcessDiscovery>? logger = null)
    {
        _logger = logger ?? NullLogger<LocalProcessDiscovery>.Instance;
    }

    public IReadOnlyList<DotnetProcess> ListProcesses()
    {
        var result = new List<DotnetProcess>();
        foreach (var pid in DiagnosticsClient.GetPublishedProcesses())
        {
            var info = SafeGetProcess(pid);
            if (info is not null)
            {
                result.Add(info);
            }
        }

        return result;
    }

    public DotnetProcess? TryGetProcess(int processId) => SafeGetProcess(processId);

    private DotnetProcess? SafeGetProcess(int pid)
    {
        try
        {
            var client = new DiagnosticsClient(pid);
            var snapshot = ProcessInfoReflection.TryGet(client);
            if (snapshot is not null)
            {
                return new DotnetProcess(
                    ProcessId: (int)snapshot.ProcessId,
                    CommandLine: snapshot.CommandLine,
                    OperatingSystem: snapshot.OperatingSystem,
                    ProcessArchitecture: snapshot.ProcessArchitecture,
                    RuntimeVersion: snapshot.ClrProductVersionString,
                    ManagedEntrypointAssemblyName: snapshot.ManagedEntrypointAssemblyName);
            }

            return BuildFallback(pid);
        }
        catch (Exception ex) when (
            ex is ServerNotAvailableException ||
            ex is TimeoutException ||
            ex is UnauthorizedAccessException ||
            ex is IOException)
        {
            LogUnreachable(pid, ex);
            return null;
        }
    }

    private static DotnetProcess? BuildFallback(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return new DotnetProcess(
                ProcessId: pid,
                CommandLine: SafeReadCommandLine(p),
                OperatingSystem: RuntimeInformation.OSDescription,
                ProcessArchitecture: RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
                RuntimeVersion: string.Empty,
                ManagedEntrypointAssemblyName: p.ProcessName);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string SafeReadCommandLine(Process p)
    {
        try
        {
            return p.MainModule?.FileName ?? p.ProcessName;
        }
        catch (Exception)
        {
            return p.ProcessName;
        }
    }

    private void LogUnreachable(int pid, Exception ex)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(ex, "Process {Pid} does not expose a diagnostic endpoint or is unreachable.", pid);
        }
    }
}
