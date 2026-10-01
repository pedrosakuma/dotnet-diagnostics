using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DotnetDiagnostics.Core.Captures;

internal sealed record CaptureWorkerProbe(string Executable, string SqliteLibrary, string PrivateDirectory,
    string TrustedFixture, string BenignMarker, int HelperProcessId, string HelperAddress, string HelperExecutable)
{
    internal bool WritableProfile { get; init; }
    internal Action<PortableWorkerIdentity, PortableWorkerIdentity>? BeforeInputWithMonitor { get; init; }
}

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
internal static partial class IsolatedCaptureWorker
{
    private const int OutputLimit = 4096;

    internal static Task<CaptureWorkerCapabilities> ProbeTrustedFixtureAsync(CaptureWorkerProbe probe,
        CaptureWorkerLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        limits ??= new();
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (PortableCaptureImportWorker.CurrentRuntimeIdentifier is null)
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
        if (probe.WritableProfile) start.ArgumentList.Add("--writable-profile-probe");
        var outcome = RunProtocol(start, nonce, limits, "GO\n"u8.ToArray(),
            (stream, ct) => ReadBoundedAsync(stream, OutputLimit, false, ct), token,
            beforeInputWithMonitor: probe.BeforeInputWithMonitor);
        var text = outcome.Result;
        var result = text.TrimEnd('\n').Split(' ');
        var expectedDeniedProbes = PortableCaptureImportWorker.CurrentRuntimeIdentifier switch
        {
            "linux-x64" => 21,
            "linux-arm64" => 19,
            _ => 0
        };
        if (result.Length != 7 || result[0] != "RESULT" || result[1] != "1" || result[2] != nonce ||
            !int.TryParse(result[3], CultureInfo.InvariantCulture, out var denied) || denied != expectedDeniedProbes ||
            !int.TryParse(result[4], CultureInfo.InvariantCulture, out var value) ||
            !int.TryParse(result[5], CultureInfo.InvariantCulture, out var version) || version < 3031000 ||
            !long.TryParse(result[6], CultureInfo.InvariantCulture, out var instructions) || instructions is < 1000 or > 200000000)
            throw Unsupported("WorkerResultInvalid");
        return new(outcome.Abi, version, denied, value, instructions, outcome.PeakRss,
            outcome.MaximumGap, Encoding.UTF8.GetByteCount(text));
    }

    private sealed record ProtocolOutcome<T>(T Result, int Abi, long PeakRss, TimeSpan MaximumGap, TimeSpan WallTime);

    private static ProtocolOutcome<T> RunProtocol<T>(ProcessStartInfo start, string nonce, CaptureWorkerLimits limits,
        byte[] request, Func<Stream, CancellationToken, Task<T>> receive, CancellationToken token,
        Action<int>? beforeInput = null, Action? afterExit = null,
        Action<PortableWorkerIdentity, PortableWorkerIdentity>? beforeInputWithMonitor = null)
        => RunProtocol(start, nonce, limits, (stream, ct) => stream.WriteAsync(request, ct).AsTask(), receive, token,
            beforeInput, afterExit, beforeInputWithMonitor);

    private static ProtocolOutcome<T> RunProtocol<T>(ProcessStartInfo start, string nonce, CaptureWorkerLimits limits,
        Func<Stream, CancellationToken, Task> send, Func<Stream, CancellationToken, Task<T>> receive, CancellationToken token,
        Action<int>? beforeInput = null, Action? afterExit = null,
        Action<PortableWorkerIdentity, PortableWorkerIdentity>? beforeInputWithMonitor = null)
    {
        PrepareMonitorStart(start, limits);
        using var process = new Process { StartInfo = start };
        var wall = Stopwatch.StartNew();
        using var io = new CancellationTokenSource();
        Task? frame = null;
        Task? sending = null;
        Task<string>? errors = null;
        var started = false;
        PortableWorkerIdentity? workerIdentity = null;
        ProtocolOutcome<T>? outcome = null;
        Exception? failure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            try { started = process.Start(); }
            catch (System.ComponentModel.Win32Exception ex) { throw Unsupported("WorkerLaunchUnavailable", ex); }
            if (!started) throw Unsupported("WorkerLaunchUnavailable");
            var monitorIdentity = PortableWorkerIdentity.Capture(process.Id);
            errors = ReadBoundedAsync(process.StandardError.BaseStream, 1024, false, io.Token);
            var monitorLine = ReadBoundedAsync(process.StandardOutput.BaseStream, 256, true, io.Token);
            frame = monitorLine;
            Await(frame);
            var monitor = ParseMonitorHandshake(monitorLine.GetAwaiter().GetResult(), nonce);
            workerIdentity = PortableWorkerIdentity.Capture(monitor.WorkerPid);
            if (workerIdentity.StartTime != monitor.WorkerStartTime) throw Unsupported("WorkerMonitorIdentityMismatch");
            beforeInputWithMonitor?.Invoke(workerIdentity, monitorIdentity);
            beforeInput?.Invoke(monitor.WorkerPid);
            Check();
            process.StandardInput.BaseStream.Write("ACK\n"u8);
            process.StandardInput.BaseStream.Flush();
            var handshake = ReadBoundedAsync(process.StandardOutput.BaseStream, 256, true, io.Token);
            frame = handshake;
            Await(frame);
            var ready = handshake.GetAwaiter().GetResult().TrimEnd('\n').Split(' ');
            if (ready.Length == 2 && ready[0] == "UNSUPPORTED") throw Unsupported(ready[1]);
            if (ready.Length != 4 || ready[0] != "READY" || ready[1] != "1" || ready[2] != nonce ||
                !int.TryParse(ready[3], CultureInfo.InvariantCulture, out var abi) || abi < 3)
            {
                if (handshake.GetAwaiter().GetResult().Length == 0 &&
                    (process.HasExited || process.WaitForExit(1000)) &&
                    ((IAsyncResult)errors).AsyncWaitHandle.WaitOne(1000))
                {
                    ThrowMonitorFailure(ParseMonitorReport(errors.GetAwaiter().GetResult(), nonce));
                }
                throw Unsupported("WorkerHandshakeInvalid");
            }
            var response = receive(process.StandardOutput.BaseStream, io.Token);
            frame = response;
            sending = Task.Run(() => send(process.StandardInput.BaseStream, io.Token), CancellationToken.None);
            Await(sending);
            process.StandardInput.Close();
            try { Await(frame); }
            catch (CaptureStoreException responseFailure)
                when (responseFailure.Message.StartsWith("Wire.Truncated:", StringComparison.Ordinal))
            {
                ThrowResponseFailure(responseFailure);
            }
            Await(errors);
            while (!process.HasExited) { Check(); Thread.Sleep(50); }
            Check();
            var report = ParseMonitorReport(errors.GetAwaiter().GetResult(), nonce);
            if (report.Outcome == "Exited" && report.Exit == 78)
            {
                var unsupported = response.GetAwaiter().GetResult()?.ToString()?.TrimEnd('\n').Split(' ');
                throw Unsupported(unsupported is { Length: 2 } && unsupported[0] == "UNSUPPORTED"
                    ? unsupported[1] : "WorkerUnavailable");
            }
            if (!IsSuccessfulMonitorExit(process.ExitCode, report))
            {
                ThrowMonitorFailure(report);
                throw CapturePackage.Error(CaptureErrorCode.StorageFailure, "Worker exited unsuccessfully; no admission result exists.");
            }
            outcome = new(response.GetAwaiter().GetResult(), abi, report.PeakRss,
                TimeSpan.FromTicks(report.MaxGapNs / 100), TimeSpan.FromTicks(report.WallNs / 100));
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("WORKER_DEBUG") == "1")
            {
                Console.Error.WriteLine("WORKER_DEBUG failure: " + ex);
                foreach (var key in ex.Data.Keys) Console.Error.WriteLine($"WORKER_DEBUG data {key}={ex.Data[key]}");
            }
            failure = ex;
        }
        try
        {
            if (started && !process.HasExited)
            {
                process.Kill(); // The monitor's parent-death signal terminates its worker child.
                if (!process.WaitForExit(5000))
                    throw CapturePackage.Error(CaptureErrorCode.StorageFailure, "Worker termination could not be confirmed.");
                ConfirmWorkerGone(workerIdentity);
            }
            io.Cancel();
            var pending = new[] { frame, sending, errors }.Where(static task => task is not null).Select(static task =>
                task!.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)).ToArray();
            if (!Task.WhenAll(pending).Wait(TimeSpan.FromSeconds(5)))
                throw CapturePackage.Error(CaptureErrorCode.StorageFailure,
                    "WorkerIoCleanupUnconfirmed: retain operation staging and reservations.");
            if (started && process.HasExited) afterExit?.Invoke();
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("WORKER_DEBUG") == "1")
                Console.Error.WriteLine("WORKER_DEBUG cleanup: " + ex);
            failure = CapturePackage.Error(CaptureErrorCode.StorageFailure, "Worker cleanup failed; no result is accepted.",
                failure is null ? ex : new AggregateException(failure, ex));
        }
        finally
        {
            io.Cancel();
            ObserveIoFailure(frame);
            ObserveIoFailure(sending);
            ObserveIoFailure(errors);
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return outcome!;

        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (wall.Elapsed > limits.WallTime) throw Limit("WorkerWallTime");
            if (errors?.IsFaulted == true) errors.GetAwaiter().GetResult();
        }
        void Await(Task task)
        {
            while (!task.IsCompleted)
            {
                if (((IAsyncResult)task).AsyncWaitHandle.WaitOne(50)) break;
                Check();
            }
            task.GetAwaiter().GetResult();
        }
        void ThrowResponseFailure(Exception responseFailure)
        {
            while (!process.HasExited) { Check(); Thread.Sleep(50); }
            Await(errors!);
            MonitorReport report;
            try { report = ParseMonitorReport(errors.GetAwaiter().GetResult(), nonce); }
            catch (Exception reportFailure)
            {
                throw CapturePackage.Error(CaptureErrorCode.StorageFailure,
                    "Worker response ended before the native monitor produced a valid final report.",
                    new AggregateException(responseFailure, reportFailure));
            }
            if (report.Outcome == "Exited" && report.Exit == 0 && report.WorkerStderrRetained == 0)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(responseFailure).Throw();
            if (report.Outcome == "Exited" && report.Exit == 78) throw Unsupported("WorkerUnavailable", responseFailure);
            ThrowMonitorFailure(report);
            throw CapturePackage.Error(CaptureErrorCode.StorageFailure,
                "Worker exited before completing its response; no admission result exists.", responseFailure);
        }
    }

    private static void ConfirmWorkerGone(PortableWorkerIdentity? workerIdentity)
    {
        if (workerIdentity is null) return;
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        while (true)
        {
            try
            {
                workerIdentity.RequireGone();
                return;
            }
            catch (CaptureStoreException) when (Stopwatch.GetTimestamp() < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    internal sealed record MonitorHandshake(int WorkerPid, long WorkerStartTime);

    internal sealed record MonitorReport(string Outcome, int Exit, int Signal, long PeakRss, long MaxGapNs, long Samples,
        long GapLastValidNs, long GapNowNs, long GapNs, long WallNs, long CpuNs,
        int Locked, int WorkerStderrRetained, string WorkerStderrHex,
        long MonitorThreadCpuDeltaNs, long MonitorInvoluntaryContextSwitchDelta);

    internal static bool IsSuccessfulMonitorExit(int processExitCode, MonitorReport report) =>
        processExitCode == 0 && report.Outcome == "Exited" && report.Exit == 0 && report.WorkerStderrRetained == 0;

    private static void PrepareMonitorStart(ProcessStartInfo start, CaptureWorkerLimits limits)
    {
        var worker = start.ArgumentList.ToArray();
        start.ArgumentList.Clear();
        start.ArgumentList.Add("--monitor");
        start.ArgumentList.Add(ToNanoseconds(limits.WallTime).ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(ToNanoseconds(limits.CpuTime).ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(limits.ResidentBytes.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--");
        foreach (var argument in worker) start.ArgumentList.Add(argument);

        static long ToNanoseconds(TimeSpan value) => checked(value.Ticks * 100);
    }

    internal static MonitorHandshake ParseMonitorHandshake(string line, string nonce)
    {
        var parts = line.TrimEnd('\n').Split(' ');
        if (parts.Length == 2 && parts[0] == "UNSUPPORTED") throw Unsupported(parts[1]);
        if (parts.Length != 5 || parts[0] != "MONITOR" || parts[1] != "1" || parts[2] != nonce ||
            !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0 ||
            !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var startTime) || startTime <= 0)
            throw Unsupported("WorkerMonitorHandshakeInvalid");
        return new(pid, startTime);
    }

    internal static MonitorReport ParseMonitorReport(string text, string nonce)
    {
        if (text.Length > 1024 || !text.EndsWith('\n') || text.IndexOf('\n') != text.Length - 1)
            throw Unsupported("WorkerMonitorReportInvalid");
        var parts = text.TrimEnd('\n').Split(' ');
        var keys = new[] { "outcome", "exit", "signal", "peakRss", "maxGapNs", "samples",
            "gapLastValidNs", "gapNowNs", "gapNs", "wallNs", "cpuNs", "locked", "workerStderrRetained",
            "workerStderrHex", "monitorThreadCpuDeltaNs", "monitorInvCtxSwDelta" };
        if (parts.Length != keys.Length + 3 || parts[0] != "MONITOR-RESULT" || parts[1] != "1" || parts[2] != nonce)
            throw Unsupported("WorkerMonitorReportInvalid");
        var values = new string[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            var prefix = keys[i] + "=";
            if (!parts[i + 3].StartsWith(prefix, StringComparison.Ordinal)) throw Unsupported("WorkerMonitorReportInvalid");
            values[i] = parts[i + 3][prefix.Length..];
        }
        var outcomes = new[] { "Exited", "WorkerResidentBytes", "WorkerCpuTime",
            "WorkerWallTime", "WorkerSignaled", "MonitorFailure" };
        if (!outcomes.Contains(values[0], StringComparer.Ordinal) ||
            !int.TryParse(values[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exit) ||
            !int.TryParse(values[2], NumberStyles.None, CultureInfo.InvariantCulture, out var signal) ||
            !long.TryParse(values[3], NumberStyles.None, CultureInfo.InvariantCulture, out var peakRss) ||
            !long.TryParse(values[4], NumberStyles.None, CultureInfo.InvariantCulture, out var maxGapNs) ||
            !long.TryParse(values[5], NumberStyles.None, CultureInfo.InvariantCulture, out var samples) ||
            !long.TryParse(values[6], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var gapLast) ||
            !long.TryParse(values[7], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var gapNow) ||
            !long.TryParse(values[8], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var gapNs) ||
            !long.TryParse(values[9], NumberStyles.None, CultureInfo.InvariantCulture, out var wallNs) ||
            !long.TryParse(values[10], NumberStyles.None, CultureInfo.InvariantCulture, out var cpuNs) ||
            !int.TryParse(values[11], NumberStyles.None, CultureInfo.InvariantCulture, out var locked) ||
            !int.TryParse(values[12], NumberStyles.None, CultureInfo.InvariantCulture, out var stderrBytes) ||
            !long.TryParse(values[14], NumberStyles.None, CultureInfo.InvariantCulture, out var monitorCpu) ||
            !long.TryParse(values[15], NumberStyles.None, CultureInfo.InvariantCulture, out var monitorSwitches) ||
            locked is not (0 or 1) || stderrBytes is < 0 or > 256 ||
            (stderrBytes == 0 ? values[13] != "-" :
                values[13].Length != stderrBytes * 2 || values[13].Any(static c => !char.IsAsciiHexDigit(c))) ||
            (values[0] == "Exited" && (exit < 0 || signal != 0)) ||
            (values[0] == "WorkerSignaled" && signal == 0))
            throw Unsupported("WorkerMonitorReportInvalid");
        return new(values[0], exit, signal, peakRss, maxGapNs, samples, gapLast, gapNow, gapNs, wallNs, cpuNs, locked,
            stderrBytes, values[13], monitorCpu, monitorSwitches);
    }

    internal static void ThrowMonitorFailure(MonitorReport report)
    {
        if (report.WorkerStderrRetained != 0)
        {
            var error = CapturePackage.Error(CaptureErrorCode.StorageFailure,
                "WorkerStderr: worker wrote to stderr; no admission result exists.");
            error.Data["WorkerStderrBytes"] = report.WorkerStderrRetained;
            error.Data["WorkerStderrHex"] = report.WorkerStderrHex;
            throw error;
        }
        if (report.Outcome is "WorkerResidentBytes" or "WorkerCpuTime" or "WorkerWallTime")
        {
            var error = Limit(report.Outcome);
            error.Data["WorkerSamples"] = report.Samples;
            error.Data["WorkerWallNs"] = report.WallNs;
            error.Data["WorkerCpuNs"] = report.CpuNs;
            error.Data["WorkerPeakRss"] = report.PeakRss;
            error.Data["WorkerMaximumObservationGapNs"] = report.MaxGapNs;
            throw error;
        }
        if (report.Outcome == "MonitorFailure") throw Unsupported("WorkerMonitorFailure");
        if (report.Outcome == "WorkerSignaled")
            throw CapturePackage.Error(CaptureErrorCode.StorageFailure,
                FormattableString.Invariant($"WorkerSignaled: signal={report.Signal}; no admission result exists."));
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
