using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DotnetDiagnostics.Core.Captures;

/// <summary>
/// Allocation-free RSS/CPU sampling of one child through a single held <c>/proc/&lt;pid&gt;/stat</c>
/// handle. The supervisor polls every millisecond; <see cref="System.Diagnostics.Process.Refresh"/>
/// plus <c>WorkingSet64</c>/<c>TotalProcessorTime</c> allocate several KiB per poll, which triggers
/// garbage collections on the polling thread itself. The held handle stays bound to the original
/// task: after the child is reaped, reads fail instead of observing a reused PID.
/// </summary>
internal sealed partial class WorkerProcfsSampler : IDisposable
{
    private const int BufferSize = 1024;
    private readonly SafeFileHandle _handle;
    private readonly byte[] _buffer = new byte[BufferSize];
    private readonly long _pageSize = Environment.SystemPageSize;
    private readonly long _ticksPerClock;

    private WorkerProcfsSampler(SafeFileHandle handle, long clockTicksPerSecond)
    {
        _handle = handle;
        _ticksPerClock = TimeSpan.TicksPerSecond / clockTicksPerSecond;
    }

    internal static WorkerProcfsSampler Open(int processId)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var hz = SysConf(ScClkTck);
        if (hz <= 0 || hz > TimeSpan.TicksPerSecond || TimeSpan.TicksPerSecond % hz != 0)
            throw new IOException("Clock tick rate is unavailable.");
        var handle = File.OpenHandle($"/proc/{processId}/stat", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new(handle, hz);
    }

    /// <summary>Reads resident bytes and user+system CPU. Throws <see cref="IOException"/> once the task is gone.</summary>
    internal void Read(out long residentBytes, out TimeSpan cpu)
    {
        var count = RandomAccess.Read(_handle, _buffer, 0);
        if (count <= 0 || count >= BufferSize) throw new IOException("Worker stat record is unavailable.");
        if (!TryParse(_buffer.AsSpan(0, count), _pageSize, _ticksPerClock, out residentBytes, out cpu))
            throw new IOException("Worker stat record is malformed.");
    }

    /// <summary>
    /// Parses utime (14), stime (15) and rss (24) from a stat record. The command name (2) may contain
    /// spaces and parentheses, so fields are counted after its last closing parenthesis.
    /// </summary>
    internal static bool TryParse(ReadOnlySpan<byte> stat, long pageSize, long ticksPerClock,
        out long residentBytes, out TimeSpan cpu)
    {
        residentBytes = 0;
        cpu = TimeSpan.Zero;
        var close = stat.LastIndexOf((byte)')');
        if (close < 0 || pageSize <= 0 || ticksPerClock <= 0) return false;
        var rest = stat[(close + 1)..];
        long utime = 0, stime = 0, rss = 0;
        var field = 2;
        var position = 0;
        while (field < 24)
        {
            if (position >= rest.Length || rest[position] != (byte)' ') return false;
            position++;
            field++;
            var start = position;
            while (position < rest.Length && rest[position] is not ((byte)' ' or (byte)'\n')) position++;
            if (position == start) return false;
            var token = rest[start..position];
            if (field == 14 && !TryParseUnsigned(token, out utime)) return false;
            if (field == 15 && !TryParseUnsigned(token, out stime)) return false;
            if (field == 24 && !TryParseSigned(token, out rss)) return false;
        }
        if (rss < 0 || rss > long.MaxValue / pageSize) return false;
        var clocks = utime + stime;
        if (clocks < 0 || clocks > TimeSpan.MaxValue.Ticks / ticksPerClock) return false;
        residentBytes = rss * pageSize;
        cpu = TimeSpan.FromTicks(clocks * ticksPerClock);
        return true;
    }

    private static bool TryParseUnsigned(ReadOnlySpan<byte> token, out long value)
    {
        value = 0;
        foreach (var digit in token)
        {
            if (digit is < (byte)'0' or > (byte)'9' || value > (long.MaxValue - 9) / 10) return false;
            value = value * 10 + (digit - '0');
        }
        return true;
    }

    private static bool TryParseSigned(ReadOnlySpan<byte> token, out long value)
    {
        if (token.Length > 1 && token[0] == (byte)'-')
        {
            var parsed = TryParseUnsigned(token[1..], out value);
            value = -value;
            return parsed;
        }
        return TryParseUnsigned(token, out value);
    }

    public void Dispose() => _handle.Dispose();

    private const int ScClkTck = 2;

    [LibraryImport("libc", EntryPoint = "sysconf")]
    private static partial long SysConf(int name);
}

/// <summary>Allocation-free CPU time of the calling thread, for bounded gap diagnostics only.</summary>
internal static partial class SupervisorThreadClock
{
    private const int ClockThreadCpuTimeId = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    internal static TimeSpan? Current()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            return ClockGetTime(ClockThreadCpuTimeId, out var value) == 0
                ? TimeSpan.FromTicks(value.Seconds * TimeSpan.TicksPerSecond + value.Nanoseconds / 100)
                : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport("libc", EntryPoint = "clock_gettime")]
    private static partial int ClockGetTime(int clockId, out TimeSpec value);
}
