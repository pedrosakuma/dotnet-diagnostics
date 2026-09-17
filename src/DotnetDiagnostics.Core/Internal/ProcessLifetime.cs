using System.ComponentModel;
using System.Diagnostics;

namespace DotnetDiagnostics.Core.Internal;

internal static class ProcessLifetime
{
    internal static DateTimeOffset? TryReadStart(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}
