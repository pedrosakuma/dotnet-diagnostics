using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DotnetDiagnostics.Core.Captures;

internal sealed record CaptureWorkerProbe(string Executable, string SqliteLibrary, string PrivateDirectory,
    string TrustedFixture, string BenignMarker, int HelperProcessId, string HelperAddress, string HelperExecutable)
{
    internal bool WritableProfile { get; init; }
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
        if (probe.WritableProfile) start.ArgumentList.Add("--writable-profile-probe");
        var outcome = RunProtocol(start, nonce, limits, "GO\n"u8.ToArray(),
            (stream, ct) => ReadBoundedAsync(stream, OutputLimit, false, ct), token);
        var text = outcome.Result;
        var result = text.TrimEnd('\n').Split(' ');
        if (result.Length != 7 || result[0] != "RESULT" || result[1] != "1" || result[2] != nonce ||
            !int.TryParse(result[3], CultureInfo.InvariantCulture, out var denied) || denied != 21 ||
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
        Action<int>? beforeInput = null, Action? afterExit = null)
        => RunProtocol(start, nonce, limits, (stream, ct) => stream.WriteAsync(request, ct).AsTask(), receive, token, beforeInput, afterExit);

    private static ProtocolOutcome<T> RunProtocol<T>(ProcessStartInfo start, string nonce, CaptureWorkerLimits limits,
        Func<Stream, CancellationToken, Task> send, Func<Stream, CancellationToken, Task<T>> receive, CancellationToken token,
        Action<int>? beforeInput = null, Action? afterExit = null)
    {
        using var process = new Process { StartInfo = start };
        var wall = Stopwatch.StartNew();
        var observations = new CaptureWorkerObservation(limits);
        using var io = new CancellationTokenSource();
        Task? frame = null;
        Task? sending = null;
        Task<string>? errors = null;
        var started = false;
        ProtocolOutcome<T>? outcome = null;
        Exception? failure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            try { started = process.Start(); }
            catch (System.ComponentModel.Win32Exception ex) { throw Unsupported("WorkerLaunchUnavailable", ex); }
            if (!started) throw Unsupported("WorkerLaunchUnavailable");
            errors = ReadBoundedAsync(process.StandardError.BaseStream, OutputLimit, false, io.Token);
            var handshake = ReadBoundedAsync(process.StandardOutput.BaseStream, 256, true, io.Token);
            frame = handshake;
            observations.ProtocolPhase = "Handshake";
            Await(frame, mandatory: false);
            var ready = handshake.GetAwaiter().GetResult().TrimEnd('\n').Split(' ');
            if (ready.Length == 2 && ready[0] == "UNSUPPORTED") throw Unsupported(ready[1]);
            if (ready.Length != 4 || ready[0] != "READY" || ready[1] != "1" || ready[2] != nonce ||
                !int.TryParse(ready[3], CultureInfo.InvariantCulture, out var abi) || abi < 3)
                throw Unsupported("WorkerHandshakeInvalid");
            beforeInput?.Invoke(process.Id);
            var response = receive(process.StandardOutput.BaseStream, io.Token);
            frame = response;
            observations.Receiver = response;
            observations.ProtocolPhase = "InitialObservation";
            Observe();
            sending = Task.Run(() => send(process.StandardInput.BaseStream, io.Token), CancellationToken.None);
            observations.Sender = sending;
            observations.ProtocolPhase = "Sending";
            Await(sending, mandatory: true);
            observations.ProtocolPhase = "ClosingInput";
            process.StandardInput.Close();
            observations.ProtocolPhase = "Receiving";
            Await(frame, mandatory: true);
            observations.ProtocolPhase = "Stderr";
            Await(errors, mandatory: true);
            observations.ProtocolPhase = "ExitWait";
            while (!process.HasExited) { Check(); Observe(); Thread.Sleep(1); }
            Observe();
            observations.ProtocolPhase = "FinalChecks";
            Check();
            observations.CheckGap(wall.Elapsed);
            if (process.ExitCode == 78) throw Unsupported(response.GetAwaiter().GetResult()?.ToString()?.Trim() ?? "WorkerUnavailable");
            if (process.ExitCode != 0 || errors.GetAwaiter().GetResult().Length != 0)
                throw CapturePackage.Error(CaptureErrorCode.StorageFailure, "Worker exited unsuccessfully; no admission result exists.");
            outcome = new(response.GetAwaiter().GetResult(), abi, observations.PeakRss, observations.MaximumGap, wall.Elapsed);
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
            observations.CheckWallTime(wall.Elapsed);
            if (errors?.IsFaulted == true) errors.GetAwaiter().GetResult();
        }
        void Observe()
        {
            observations.PollStartedAt = wall.Elapsed;
            try { ObserveCore(); }
            finally { observations.LastCompletedPollDuration = wall.Elapsed - observations.PollStartedAt.Value; }
        }
        void ObserveCore()
        {
            observations.PollStage = "Check";
            Check();
            if (observations.Completed) return;
            observations.PollStage = "ExitProbe";
            if (process.HasExited) { observations.ConfirmExit(wall.Elapsed); return; }
            try
            {
                observations.PollStage = "Metrics";
                observations.MetricsStartedAt = wall.Elapsed;
                observations.MetricsFinishedAt = null;
                process.Refresh();
                var rss = process.WorkingSet64;
                var cpu = process.TotalProcessorTime;
                if (rss == 0)
                {
                    observations.PollStage = "ZeroRssExitConfirmation";
                    // Zero RSS is not exit evidence. Only this child's confirmed
                    // termination within the last valid sample's deadline qualifies.
                    var confirmed = observations.WaitForConfirmedExit(() => wall.Elapsed, process.WaitForExit, token);
                    observations.ConfirmExit(confirmed);
                    return;
                }
                var sampledAt = wall.Elapsed;
                observations.MetricsFinishedAt = sampledAt;
                observations.PollStage = "Record";
                observations.Record(sampledAt, rss, cpu);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                observations.PollStage = "MetricFailureConfirmedExit";
                observations.ConfirmExit(wall.Elapsed);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                observations.PollStage = "MetricFailureExitProbe";
                observations.MetricUnavailable(ex, () => process.HasExited, () => wall.Elapsed);
            }
            catch (IOException ex)
            {
                observations.PollStage = "MetricFailureExitProbe";
                observations.MetricUnavailable(ex, () => process.HasExited, () => wall.Elapsed);
            }
        }
        void Await(Task task, bool mandatory)
        {
            while (!task.IsCompleted)
            {
                Check();
                if (mandatory) Observe();
                Thread.Sleep(1);
            }
            if (mandatory)
            {
                observations.PollStage = "AwaitCompletionGap";
                observations.CheckGap(wall.Elapsed);
            }
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
    internal string ProtocolPhase { get; set; } = "Unspecified";
    internal string PollStage { get; set; } = "Unspecified";
    internal TimeSpan? PollStartedAt { get; set; }
    internal TimeSpan? LastCompletedPollDuration { get; set; }
    internal TimeSpan? MetricsStartedAt { get; set; }
    internal TimeSpan? MetricsFinishedAt { get; set; }
    internal Task? Sender { get; set; }
    internal Task? Receiver { get; set; }
    internal long PeakRss { get; private set; }
    internal TimeSpan MaximumGap { get; private set; }
    internal void CheckWallTime(TimeSpan elapsed)
    {
        if (elapsed > limits.WallTime) throw IsolatedCaptureWorker.Limit("WorkerWallTime");
    }
    internal bool Completed { get; private set; }
    internal void MetricUnavailable(Exception error, Func<bool> hasExited, Func<TimeSpan> elapsed)
    {
        bool exited;
        try { exited = hasExited(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            throw IsolatedCaptureWorker.Unsupported("WorkerObservationUnavailable", ex);
        }
        if (!exited) throw IsolatedCaptureWorker.Unsupported("WorkerObservationUnavailable", error);
        ConfirmExit(elapsed());
    }
    internal void ConfirmExit(TimeSpan observedAt)
    {
        if (Completed) return;
        if (_last is null) throw IsolatedCaptureWorker.Unsupported("WorkerObservationUnavailable");
        CheckWallTime(observedAt);
        CheckGap(observedAt);
        Completed = true;
    }
    internal TimeSpan WaitForConfirmedExit(Func<TimeSpan> elapsed, Func<int, bool> waitForExit,
        CancellationToken cancellationToken)
    {
        var now = elapsed();
        CheckStop(now);
        if (_last is not { } last) throw IsolatedCaptureWorker.Unsupported("WorkerObservationUnavailable");
        var deadline = last + TimeSpan.FromMilliseconds(10);
        while (true)
        {
            // Floor to the Process API's whole milliseconds. A sub-millisecond
            // remainder permits only a nonblocking exit probe, never a rounded-up wait.
            var remaining = deadline - now;
            var wallRemaining = limits.WallTime - now;
            var wait = (int)Math.Min(remaining.TotalMilliseconds, wallRemaining.TotalMilliseconds);
            bool exited;
            try { exited = waitForExit(wait); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                CheckStop(elapsed());
                throw IsolatedCaptureWorker.Unsupported("WorkerObservationUnavailable", ex);
            }
            now = elapsed();
            CheckStop(now);
            if (exited) return now;
            if (now == deadline) throw IsolatedCaptureWorker.Unsupported("WorkerObservationUnavailable");
        }

        void CheckStop(TimeSpan time)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckWallTime(time);
            CheckGap(time);
        }
    }
    internal void CheckGap(TimeSpan now)
    {
        if (Completed) return;
        if (_last is not { } previous) return;
        var gap = now - previous;
        if (gap > MaximumGap) MaximumGap = gap;
        if (gap < TimeSpan.Zero || gap > TimeSpan.FromMilliseconds(10))
        {
            var error = IsolatedCaptureWorker.Limit("WorkerObservationGap");
            error.Data["WorkerLastValidSampleTicks"] = previous.Ticks;
            error.Data["WorkerCurrentTicks"] = now.Ticks;
            error.Data["WorkerGapTicks"] = gap.Ticks;
            error.Data["WorkerGapLimitTicks"] = TimeSpan.FromMilliseconds(10).Ticks;
            error.Data["WorkerProtocolPhase"] = ProtocolPhase;
            error.Data["WorkerPollStage"] = PollStage;
            if (PollStartedAt is { } poll) error.Data["WorkerPollStartedTicks"] = poll.Ticks;
            if (LastCompletedPollDuration is { } duration) error.Data["WorkerLastCompletedPollDurationTicks"] = duration.Ticks;
            if (MetricsStartedAt is { } start) error.Data["WorkerMetricsStartedTicks"] = start.Ticks;
            if (MetricsFinishedAt is { } end) error.Data["WorkerMetricsFinishedTicks"] = end.Ticks;
            if (Sender is { } sender) error.Data["WorkerSenderStatus"] = sender.Status.ToString();
            if (Receiver is { } receiver) error.Data["WorkerReceiverStatus"] = receiver.Status.ToString();
            throw error;
        }
    }
    internal void Record(TimeSpan now, long rss, TimeSpan cpu)
    {
        if (Completed) throw IsolatedCaptureWorker.Unsupported("WorkerAlreadyExited");
        if (rss <= 0 || cpu < TimeSpan.Zero) throw IsolatedCaptureWorker.Unsupported("WorkerObservationUnavailable");
        CheckGap(now);
        _last = now;
        PeakRss = Math.Max(PeakRss, rss);
        if (rss > limits.ResidentBytes) throw IsolatedCaptureWorker.Limit("WorkerResidentBytes");
        if (cpu > limits.CpuTime) throw IsolatedCaptureWorker.Limit("WorkerCpuTime");
    }
}
