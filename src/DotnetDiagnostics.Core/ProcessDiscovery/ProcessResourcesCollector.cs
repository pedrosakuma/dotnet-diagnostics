using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DotnetDiagnostics.Core.Counters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.ProcessDiscovery;

/// <summary>
/// Cross-platform process resource collector.
/// <list type="bullet">
/// <item><term>Linux</term><description>Reads <c>/proc/&lt;pid&gt;/fd</c>, <c>/proc/&lt;pid&gt;/net/tcp{,6}</c> and <c>/proc/&lt;pid&gt;/limits</c>.</description></item>
/// <item><term>Windows</term><description>Calls <c>GetProcessHandleCount</c>; per-handle/socket breakdown is not yet implemented.</description></item>
/// </list>
/// </summary>
public sealed partial class ProcessResourcesCollector : IProcessResourcesCollector
{
    private const int MaxClassifiedFdEntries = 10_000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const long RssDominatedMinimumGapBytes = 128L * 1024 * 1024;
    private static readonly TimeSpan ManagedHeapProbeDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ManagedHeapProbeTimeout = TimeSpan.FromSeconds(6);
    private static readonly string[] SystemRuntimeProvider = ["System.Runtime"];

    private readonly string _procRoot;
    private readonly ILogger<ProcessResourcesCollector> _logger;
    private readonly TimeProvider _clock;
    private readonly ICounterCollector _counterCollector;

    public ProcessResourcesCollector(
        ILogger<ProcessResourcesCollector>? logger = null,
        TimeProvider? clock = null,
        string procRoot = "/proc",
        ICounterCollector? counterCollector = null)
    {
        _logger = logger ?? NullLogger<ProcessResourcesCollector>.Instance;
        _clock = clock ?? TimeProvider.System;
        _procRoot = procRoot;
        _counterCollector = counterCollector ?? new EventPipeCounterCollector();
    }

    /// <inheritdoc />
    public async Task<ProcessResources> CollectAsync(
        int processId,
        int durationSeconds,
        int sampleEverySeconds,
        CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        var samples = new List<CollectedSnapshot>();

        if (durationSeconds == 0)
        {
            var snapshot = TakeSample(processId, notes);
            var managedHeap = await ProbeManagedGcHeapBytesAsync(processId, cancellationToken).ConfigureAwait(false);
            AddProbeNotes(notes, managedHeap.Notes);
            snapshot = snapshot with { ManagedVsNative = BuildManagedVsNative(snapshot.RssBytes, managedHeap.GcHeapBytes, notes) };
            return snapshot.ToReport(processId, notes, trend: null);
        }

        var startedAt = _clock.GetUtcNow();
        var deadline = startedAt.AddSeconds(durationSeconds);
        var interval = TimeSpan.FromSeconds(sampleEverySeconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples.Add(TakeSample(processId, notes));

            var now = _clock.GetUtcNow();
            if (now >= deadline)
            {
                break;
            }

            var remaining = deadline - now;
            var delay = remaining < interval ? remaining : interval;
            if (delay <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        if (samples.Count == 0)
        {
            samples.Add(new CollectedSnapshot(_clock.GetUtcNow(), null, null, null, null, null, null, null));
        }

        var managed = await ProbeManagedGcHeapBytesAsync(processId, cancellationToken).ConfigureAwait(false);
        AddProbeNotes(notes, managed.Notes);
        var final = samples[^1];
        samples[^1] = final with { ManagedVsNative = BuildManagedVsNative(final.RssBytes, managed.GcHeapBytes, notes) };

        var trend = new ProcessResourcesTrend(samples.Select(static sample => sample.ToSample()).ToArray());
        return samples[^1].ToReport(processId, notes, trend);
    }

    private CollectedSnapshot TakeSample(int processId, List<string> notes)
    {
        var timestamp = _clock.GetUtcNow();
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return TakeLinuxSample(processId, timestamp, notes);
            }

            if (OperatingSystem.IsWindows())
            {
                return TakeWindowsSample(processId, timestamp, notes);
            }

            AddNoteOnce(notes, $"Process resource collection is not supported on {RuntimeInformation.OSDescription}. Only Linux and Windows are implemented.");
            return new CollectedSnapshot(timestamp, null, null, null, null, null, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to collect process resources for pid {ProcessId}", processId);
            AddNoteOnce(notes, $"Resource snapshot failed: {ex.GetType().Name}: {ex.Message}");
            return new CollectedSnapshot(timestamp, null, null, null, null, null, null, null);
        }
    }

    private CollectedSnapshot TakeLinuxSample(int processId, DateTimeOffset timestamp, List<string> notes)
    {
        var procDir = Path.Combine(_procRoot, processId.ToString(CultureInfo.InvariantCulture));
        var fd = ReadFdBreakdown(Path.Combine(procDir, "fd"), notes);
        IReadOnlySet<string>? socketInodes = null;
        if (fd.Success)
        {
            socketInodes = fd.SocketInodes ?? new HashSet<string>(StringComparer.Ordinal);
        }

        var sockets = ReadSocketBreakdown(processId, socketInodes, Path.Combine(procDir, "net", "tcp"), Path.Combine(procDir, "net", "tcp6"), notes);
        var limits = ReadLimits(processId, Path.Combine(procDir, "limits"), fd.FdCount, notes);

        var rssBytes = ReadLinuxRssBytes(Path.Combine(procDir, "status"), notes);
        return new CollectedSnapshot(timestamp, fd.FdCount, null, fd.Breakdown, sockets, limits, rssBytes, null);
    }

    [SupportedOSPlatform("windows")]
    private static CollectedSnapshot TakeWindowsSample(int processId, DateTimeOffset timestamp, List<string> notes)
    {
        uint handleCount = 0;
        IntPtr processHandle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (processHandle == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            AddNoteOnce(notes, $"OpenProcess({processId}) failed with error {error}; handle count is unavailable.");
        }
        else
        {
            try
            {
                if (!GetProcessHandleCount(processHandle, out handleCount))
                {
                    var error = Marshal.GetLastPInvokeError();
                    AddNoteOnce(notes, $"GetProcessHandleCount({processId}) failed with error {error}.");
                }
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        AddNoteOnce(notes, "Windows handle breakdown is not yet supported.");
        var rssBytes = ReadWindowsWorkingSetBytes(processId, notes);
        return new CollectedSnapshot(timestamp, null, handleCount == 0 && processHandle == IntPtr.Zero ? null : (int?)handleCount, null, null, null, rssBytes, null);
    }

    private static FdCollectionResult ReadFdBreakdown(string fdDirectory, List<string> notes)
    {
        if (!Directory.Exists(fdDirectory))
        {
            AddNoteOnce(notes, $"Could not read {fdDirectory}: directory not found or unreadable.");
            return default;
        }

        try
        {
            var total = 0;
            var sockets = 0;
            var regular = 0;
            var pipes = 0;
            var eventfds = 0;
            var other = 0;
            var overflow = 0;
            var socketInodes = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in Directory.EnumerateFileSystemEntries(fdDirectory))
            {
                total++;
                if (total > MaxClassifiedFdEntries)
                {
                    overflow++;
                    other++;
                    continue;
                }

                string? target;
                try
                {
                    target = new FileInfo(entry).LinkTarget;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AddNoteOnce(notes, $"Could not resolve one or more fd symlinks under {fdDirectory}: {ex.GetType().Name}.");
                    other++;
                    continue;
                }

                if (string.IsNullOrEmpty(target))
                {
                    other++;
                }
                else if (target.StartsWith("socket:[", StringComparison.Ordinal))
                {
                    sockets++;
                    var inode = ExtractSocketInode(target);
                    if (!string.IsNullOrEmpty(inode))
                    {
                        socketInodes.Add(inode);
                    }
                }
                else if (target.StartsWith('/'))
                {
                    regular++;
                }
                else if (target.StartsWith("pipe:[", StringComparison.Ordinal))
                {
                    pipes++;
                }
                else if (target.StartsWith("anon_inode:[eventfd]", StringComparison.Ordinal))
                {
                    eventfds++;
                }
                else
                {
                    other++;
                }
            }

            if (overflow > 0)
            {
                AddNoteOnce(notes, $"FD enumeration hit the {MaxClassifiedFdEntries} entry cap; {overflow} additional descriptors were counted as Other.");
            }

            return new FdCollectionResult(total, new FdBreakdown(sockets, regular, pipes, eventfds, other), socketInodes, Success: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddNoteOnce(notes, $"Could not enumerate {fdDirectory}: {ex.GetType().Name}.");
            return default;
        }
    }

    private static SocketBreakdown? ReadSocketBreakdown(int processId, IReadOnlySet<string>? socketInodes, string tcpPath, string tcp6Path, List<string> notes)
    {
        if (socketInodes is null)
        {
            AddNoteOnce(notes, $"Skipping TCP state attribution for pid {processId}: fd socket inode enumeration was unavailable.");
            return null;
        }

        var established = 0;
        var timeWait = 0;
        var closeWait = 0;
        var listen = 0;
        var other = 0;
        var anyFileRead = false;

        foreach (var path in new[] { tcpPath, tcp6Path })
        {
            try
            {
                if (!File.Exists(path))
                {
                    AddNoteOnce(notes, $"Could not read {path}: file not found.");
                    continue;
                }

                var lines = File.ReadLines(path);
                var isHeader = true;
                foreach (var line in lines)
                {
                    if (isHeader)
                    {
                        isHeader = false;
                        continue;
                    }

                    if (!TryParseTcpStateAndInode(line, out var state, out var inode))
                    {
                        continue;
                    }

                    anyFileRead = true;
                    if (!socketInodes.Contains(inode.ToString()))
                    {
                        continue;
                    }

                    if (state.SequenceEqual("01"))
                    {
                        established++;
                    }
                    else if (state.SequenceEqual("06"))
                    {
                        timeWait++;
                    }
                    else if (state.SequenceEqual("08"))
                    {
                        closeWait++;
                    }
                    else if (state.SequenceEqual("0A"))
                    {
                        listen++;
                    }
                    else
                    {
                        other++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddNoteOnce(notes, $"Could not read TCP state from {path}: {ex.GetType().Name}.");
            }
        }

        if (!anyFileRead)
        {
            AddNoteOnce(notes, $"No TCP state data was readable for pid {processId}.");
            return null;
        }

        return new SocketBreakdown(established, timeWait, closeWait, listen, other);
    }

    /// <summary>
    /// Parses the <c>st</c> (field 3) and <c>inode</c> (field 9) columns of a
    /// <c>/proc/net/tcp[6]</c> data row without allocating an array/string for every
    /// whitespace-separated column, since only those two fields are used.
    /// </summary>
    private static bool TryParseTcpStateAndInode(ReadOnlySpan<char> line, out ReadOnlySpan<char> state, out ReadOnlySpan<char> inode)
    {
        state = default;
        inode = default;

        var fieldIndex = 0;
        var position = 0;
        while (position < line.Length)
        {
            while (position < line.Length && line[position] == ' ')
            {
                position++;
            }

            if (position >= line.Length)
            {
                break;
            }

            var start = position;
            while (position < line.Length && line[position] != ' ')
            {
                position++;
            }

            var field = line[start..position];
            if (fieldIndex == 3)
            {
                state = field;
            }
            else if (fieldIndex == 9)
            {
                inode = field;
                return true;
            }

            fieldIndex++;
        }

        return false;
    }

    private static RLimits? ReadLimits(int processId, string limitsPath, int? fdCount, List<string> notes)
    {
        try
        {
            if (!File.Exists(limitsPath))
            {
                AddNoteOnce(notes, $"Could not read {limitsPath}: file not found.");
                return null;
            }

            foreach (var line in File.ReadLines(limitsPath))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 6)
                {
                    continue;
                }

                if (!parts[0].Equals("Max", StringComparison.Ordinal) ||
                    !parts[1].Equals("open", StringComparison.Ordinal) ||
                    !parts[2].Equals("files", StringComparison.Ordinal))
                {
                    continue;
                }

                var soft = ParseLimitValue(parts[^3]);
                var hard = ParseLimitValue(parts[^2]);
                double? fraction = fdCount is > 0 && soft is > 0
                    ? fdCount.Value / (double)soft.Value
                    : null;
                return new RLimits(soft, hard, fraction);
            }

            AddNoteOnce(notes, $"Could not find 'Max open files' in {limitsPath} for pid {processId}.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddNoteOnce(notes, $"Could not read {limitsPath}: {ex.GetType().Name}.");
            return null;
        }
    }

    internal static long? ReadLinuxRssBytes(string statusPath, List<string> notes)
    {
        try
        {
            if (!File.Exists(statusPath))
            {
                AddNoteOnce(notes, $"Could not read {statusPath}: file not found.");
                return null;
            }

            foreach (var line in File.ReadLines(statusPath))
            {
                if (!line.StartsWith("VmRSS:", StringComparison.Ordinal))
                {
                    continue;
                }

                var rest = line.AsSpan("VmRSS:".Length).TrimStart();
                var digitCount = 0;
                while (digitCount < rest.Length && char.IsDigit(rest[digitCount]))
                {
                    digitCount++;
                }

                if (digitCount > 0 && long.TryParse(rest[..digitCount], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kib))
                {
                    return kib * 1024;
                }

                AddNoteOnce(notes, $"Could not parse VmRSS from {statusPath}.");
                return null;
            }

            AddNoteOnce(notes, $"Could not find VmRSS in {statusPath}.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddNoteOnce(notes, $"Could not read RSS from {statusPath}: {ex.GetType().Name}.");
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static long? ReadWindowsWorkingSetBytes(int processId, List<string> notes)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WorkingSet64;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            AddNoteOnce(notes, $"Could not read WorkingSet64 for pid {processId}: {ex.GetType().Name}.");
            return null;
        }
    }

    private async Task<ManagedHeapProbeResult> ProbeManagedGcHeapBytesAsync(int processId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ManagedHeapProbeTimeout);

        try
        {
            var snapshot = await _counterCollector.CollectAsync(
                    processId,
                    ManagedHeapProbeDuration,
                    providers: SystemRuntimeProvider,
                    meters: Array.Empty<string>(),
                    intervalSeconds: 1,
                    maxInstrumentTimeSeries: 64,
                    cancellationToken: timeout.Token)
                .ConfigureAwait(false);

            var counter = snapshot.Counters.FirstOrDefault(c =>
                c.Provider == "System.Runtime" &&
                c.Name == "gc-heap-size");

            if (counter is null)
            {
                return new ManagedHeapProbeResult(null, ["System.Runtime gc-heap-size counter was not observed during the resources probe."]);
            }

            return new ManagedHeapProbeResult(ConvertGcHeapCounterToBytes(counter), snapshot.Notes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ManagedHeapProbeResult(null, [$"Managed GC heap probe timed out after {ManagedHeapProbeTimeout.TotalSeconds:F0}s."]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to collect managed GC heap size for pid {ProcessId}.", processId);
            return new ManagedHeapProbeResult(null, [$"Managed GC heap size unavailable: {ex.GetType().Name}: {ex.Message}"]);
        }
    }

    private static long ConvertGcHeapCounterToBytes(CounterValue counter)
    {
        var value = counter.Value;
        if (counter.Unit is { } unit)
        {
            if (unit.Equals("MB", StringComparison.OrdinalIgnoreCase))
            {
                return (long)Math.Round(value * 1024 * 1024);
            }

            if (unit.Equals("KB", StringComparison.OrdinalIgnoreCase))
            {
                return (long)Math.Round(value * 1024);
            }
        }

        return (long)Math.Round(value);
    }

    private static ManagedVsNativeMemory? BuildManagedVsNative(long? rssBytes, long? gcHeapBytes, List<string> notes)
    {
        if (rssBytes is null && gcHeapBytes is null)
        {
            return null;
        }

        long? rssMinusGc = rssBytes.HasValue && gcHeapBytes.HasValue
            ? Math.Max(0, rssBytes.Value - gcHeapBytes.Value)
            : null;
        double? ratio = rssBytes is > 0 && gcHeapBytes.HasValue
            ? Math.Round(gcHeapBytes.Value / (double)rssBytes.Value, 4)
            : null;
        bool? rssDominated = rssBytes.HasValue && gcHeapBytes.HasValue
            ? rssBytes.Value >= gcHeapBytes.Value * 2 && rssMinusGc >= RssDominatedMinimumGapBytes
            : null;

        var interpretation = rssDominated == true
            ? "RSS is much larger than the managed GC heap; investigate native allocations, fragmentation, pinned LOH/POH, mmap/file caches, or unmanaged libraries."
            : rssBytes.HasValue && gcHeapBytes.HasValue
                ? "RSS and managed GC heap are in the same order of magnitude."
                : null;

        if (rssDominated == true)
        {
            AddNoteOnce(notes, interpretation!);
        }

        return new ManagedVsNativeMemory(rssBytes, gcHeapBytes, rssMinusGc, ratio, rssDominated)
        {
            Interpretation = interpretation,
        };
    }

    private static void AddProbeNotes(List<string> notes, IReadOnlyList<string> probeNotes)
    {
        foreach (var note in probeNotes)
        {
            AddNoteOnce(notes, note);
        }
    }

    private static long? ParseLimitValue(string token)
        => token.Equals("unlimited", StringComparison.OrdinalIgnoreCase)
            ? null
            : long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

    private static string? ExtractSocketInode(string target)
    {
        const string prefix = "socket:[";
        if (!target.StartsWith(prefix, StringComparison.Ordinal) || !target.EndsWith(']'))
        {
            return null;
        }

        return target.Substring(prefix.Length, target.Length - prefix.Length - 1);
    }

    private static void AddNoteOnce(List<string> notes, string note)
    {
        if (!notes.Contains(note, StringComparer.Ordinal))
        {
            notes.Add(note);
        }
    }

    private readonly record struct FdCollectionResult(int? FdCount, FdBreakdown? Breakdown, IReadOnlySet<string>? SocketInodes, bool Success);

    private sealed record ManagedHeapProbeResult(long? GcHeapBytes, IReadOnlyList<string> Notes);

    private sealed record CollectedSnapshot(
        DateTimeOffset Timestamp,
        int? FdCount,
        int? HandleCount,
        FdBreakdown? Fd,
        SocketBreakdown? Sockets,
        RLimits? Limits,
        long? RssBytes,
        ManagedVsNativeMemory? ManagedVsNative)
    {
        public ProcessResourcesSample ToSample() => new(Timestamp, FdCount, HandleCount, Fd, Sockets, Limits)
        {
            ManagedVsNative = ManagedVsNative,
        };

        public ProcessResources ToReport(int processId, IReadOnlyList<string> notes, ProcessResourcesTrend? trend)
            => new(processId, Timestamp, FdCount, HandleCount, Fd, Sockets, Limits, notes.ToArray(), trend)
            {
                ManagedVsNative = ManagedVsNative,
            };
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessHandleCount(IntPtr hProcess, out uint handleCount);
}
