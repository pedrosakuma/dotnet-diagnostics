using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DotnetDiagnostics.Core.Captures;

internal sealed record CaptureWorkerProbe(string Executable, string SqliteLibrary, string PrivateDirectory,
    string TrustedFixture, string BenignMarker, int HelperProcessId, string HelperAddress, string HelperExecutable);

internal sealed record CaptureWorkerCapabilities(int LandlockAbi, int SqliteVersion, int DeniedProbes,
    int FixtureValue, long VmInstructions, long PeakObservedRss, TimeSpan MaximumObservationGap, int OutputBytes);

internal sealed record CaptureWorkerLimits
{
    internal TimeSpan WallTime { get; init; } = TimeSpan.FromSeconds(120);
    internal TimeSpan CpuTime { get; init; } = TimeSpan.FromSeconds(60);
    internal long ResidentBytes { get; init; } = 256L * 1024 * 1024;
    internal void Validate()
    {
        if (WallTime <= TimeSpan.Zero || WallTime > TimeSpan.FromSeconds(120) ||
            CpuTime <= TimeSpan.Zero || CpuTime > TimeSpan.FromSeconds(60) ||
            ResidentBytes < 1 || ResidentBytes > 256L * 1024 * 1024)
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Worker limits may only reduce the admitted ceilings.");
    }
}

/// <summary>
/// Internal capability milestone, not archive admission. Every path/fixture is supplied by trusted
/// host/test code. No external capture is accepted and public import remains unavailable.
/// </summary>
internal static class IsolatedCaptureWorker
{
    private const int OutputLimit = 4096;

    internal static Task<CaptureWorkerCapabilities> ProbeTrustedFixtureAsync(CaptureWorkerProbe probe,
        CaptureWorkerLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        limits ??= new();
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw Unsupported("WorkerPlatformUnavailable");
        ValidatePaths(probe);
        return Task.Run(() => Run(probe, limits, cancellationToken), CancellationToken.None);
    }

    private static void ValidatePaths(CaptureWorkerProbe probe)
    {
        foreach (var path in new[] { probe.Executable, probe.SqliteLibrary, probe.PrivateDirectory,
            probe.TrustedFixture, probe.BenignMarker, probe.HelperExecutable })
        {
            if (!Path.IsPathFullyQualified(path) || path.Length > 2048 || path.Contains('\0'))
                throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Worker assets require bounded absolute trusted paths.");
            CapturePackage.RejectLinks(path);
            if (!File.Exists(path) && !Directory.Exists(path)) throw Unsupported("WorkerAssetUnavailable");
        }
        var relative = Path.GetRelativePath(probe.PrivateDirectory, probe.TrustedFixture);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Trusted fixture must belong to private worker staging.");
        if (probe.HelperProcessId <= 0 || probe.HelperAddress.Length is < 1 or > 16 ||
            !ulong.TryParse(probe.HelperAddress, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "A purpose-created helper identity is required.");
        if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(probe.PrivateDirectory) &
            (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
             UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw Unsupported("WorkerStagingNotPrivate");
    }

    private static CaptureWorkerCapabilities Run(CaptureWorkerProbe probe, CaptureWorkerLimits limits, CancellationToken token)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(probe.Executable)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = probe.PrivateDirectory
        };
        start.Environment.Clear();
        foreach (var argument in new[] { nonce, probe.SqliteLibrary, probe.PrivateDirectory,
            new Uri(probe.TrustedFixture).AbsoluteUri + "?immutable=1", probe.BenignMarker,
            probe.HelperProcessId.ToString(CultureInfo.InvariantCulture), probe.HelperAddress, probe.HelperExecutable })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        var wall = Stopwatch.StartNew();
        var observations = new CaptureWorkerObservation(limits);
        using var io = new CancellationTokenSource();
        Task<string>? frame = null;
        Task<string>? errors = null;
        var started = false;
        CaptureWorkerCapabilities? capabilities = null;
        Exception? failure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            try { started = process.Start(); }
            catch (System.ComponentModel.Win32Exception ex) { throw Unsupported("WorkerLaunchUnavailable", ex); }
            if (!started) throw Unsupported("WorkerLaunchUnavailable");
            errors = ReadBoundedAsync(process.StandardError.BaseStream, OutputLimit, false, io.Token);
            frame = ReadBoundedAsync(process.StandardOutput.BaseStream, 256, true, io.Token);
            Await(frame, mandatory: false);
            var ready = frame.GetAwaiter().GetResult().TrimEnd('\n').Split(' ');
            if (ready.Length == 2 && ready[0] == "UNSUPPORTED") throw Unsupported(ready[1]);
            if (ready.Length != 4 || ready[0] != "READY" || ready[1] != "1" || ready[2] != nonce ||
                !int.TryParse(ready[3], CultureInfo.InvariantCulture, out var abi) || abi < 3)
                throw Unsupported("WorkerHandshakeInvalid");
            frame = ReadBoundedAsync(process.StandardOutput.BaseStream, OutputLimit, false, io.Token);
            Observe();
            process.StandardInput.Write("GO\n");
            process.StandardInput.Flush();
            process.StandardInput.Close();
            Await(frame, mandatory: true);
            Await(errors, mandatory: true);
            while (!process.HasExited) { Check(); Observe(); Thread.Sleep(1); }
            observations.CheckGap(wall.Elapsed);
            if (process.ExitCode == 78) throw Unsupported(frame.GetAwaiter().GetResult().Trim());
            if (process.ExitCode != 0 || errors.GetAwaiter().GetResult().Length != 0)
                throw CapturePackage.Error(CaptureErrorCode.StorageFailure, "Worker exited unsuccessfully; no admission result exists.");
            var text = frame.GetAwaiter().GetResult();
            var result = text.TrimEnd('\n').Split(' ');
            if (result.Length != 7 || result[0] != "RESULT" || result[1] != "1" || result[2] != nonce ||
                !int.TryParse(result[3], CultureInfo.InvariantCulture, out var denied) || denied != 21 ||
                !int.TryParse(result[4], CultureInfo.InvariantCulture, out var value) ||
                !int.TryParse(result[5], CultureInfo.InvariantCulture, out var version) || version < 3031000 ||
                !long.TryParse(result[6], CultureInfo.InvariantCulture, out var instructions) || instructions is < 1000 or > 200000000)
                throw Unsupported("WorkerResultInvalid");
            capabilities = new(abi, version, denied, value, instructions, observations.PeakRss,
                observations.MaximumGap, Encoding.UTF8.GetByteCount(text));
        }
        catch (Exception ex) { failure = ex; }
        try
        {
            if (started && !process.HasExited)
            {
                process.Kill(); // Containment denies child creation; this is not the containment mechanism.
                if (!process.WaitForExit(5000))
                    throw CapturePackage.Error(CaptureErrorCode.StorageFailure, "Worker termination could not be confirmed.");
            }
        }
        catch (Exception ex)
        {
            failure = CapturePackage.Error(CaptureErrorCode.StorageFailure, "Worker cleanup failed; no result is accepted.",
                failure is null ? ex : new AggregateException(failure, ex));
        }
        finally
        {
            io.Cancel();
            ObserveIoFailure(frame);
            ObserveIoFailure(errors);
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return capabilities!;

        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (wall.Elapsed > limits.WallTime) throw Limit("WorkerWallTime");
            if (errors?.IsFaulted == true) errors.GetAwaiter().GetResult();
        }
        void Observe()
        {
            Check();
            if (process.HasExited) { observations.CheckGap(wall.Elapsed); return; }
            try
            {
                process.Refresh();
                var rss = process.WorkingSet64;
                var cpu = process.TotalProcessorTime;
                if (rss == 0 && process.HasExited) { observations.CheckGap(wall.Elapsed); return; }
                observations.Record(wall.Elapsed, rss, cpu);
            }
            catch (InvalidOperationException) when (process.HasExited) { observations.CheckGap(wall.Elapsed); }
            catch (System.ComponentModel.Win32Exception ex) { throw Unsupported("WorkerObservationUnavailable", ex); }
            catch (IOException ex) { throw Unsupported("WorkerObservationUnavailable", ex); }
        }
        void Await(Task task, bool mandatory)
        {
            while (!task.IsCompleted)
            {
                Check();
                if (mandatory) Observe();
                Thread.Sleep(1);
            }
            if (mandatory) observations.CheckGap(wall.Elapsed);
            task.GetAwaiter().GetResult();
        }
    }

    private static void ObserveIoFailure(Task? task)
    {
        if (task is null) return;
        // Observe cancellation/pipe faults after the original result has already failed.
        _ = task.ContinueWith(static completed => _ = completed.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, int maximum, bool line, CancellationToken token)
    {
        var bytes = new byte[maximum + 1];
        var count = 0;
        while (true)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count, line ? 1 : bytes.Length - count), token).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
            if (count > maximum) throw Limit("WorkerOutputBytes");
            if (line && bytes[count - 1] == '\n') break;
        }
        return Encoding.UTF8.GetString(bytes, 0, count);
    }

    internal static CaptureStoreException Limit(string reason) =>
        CapturePackage.Error(CaptureErrorCode.CapacityExceeded, reason + ": worker result invalidated; no input admitted.");
    internal static CaptureStoreException Unsupported(string reason, Exception? inner = null) =>
        CapturePackage.Error(CaptureErrorCode.UnsupportedFormat, reason + ": isolated import is unavailable.", inner);
}

internal sealed class CaptureWorkerObservation(CaptureWorkerLimits limits)
{
    private TimeSpan? _last;
    internal long PeakRss { get; private set; }
    internal TimeSpan MaximumGap { get; private set; }
    internal void CheckGap(TimeSpan now)
    {
        if (_last is not { } previous) return;
        var gap = now - previous;
        if (gap > MaximumGap) MaximumGap = gap;
        if (gap < TimeSpan.Zero || gap > TimeSpan.FromMilliseconds(10))
            throw IsolatedCaptureWorker.Limit("WorkerObservationGap");
    }
    internal void Record(TimeSpan now, long rss, TimeSpan cpu)
    {
        if (rss <= 0 || cpu < TimeSpan.Zero) throw IsolatedCaptureWorker.Unsupported("WorkerObservationUnavailable");
        CheckGap(now);
        _last = now;
        PeakRss = Math.Max(PeakRss, rss);
        if (rss > limits.ResidentBytes) throw IsolatedCaptureWorker.Limit("WorkerResidentBytes");
        if (cpu > limits.CpuTime) throw IsolatedCaptureWorker.Limit("WorkerCpuTime");
    }
}
