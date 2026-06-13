using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.GatedCapture;

/// <summary>
/// Default <see cref="IThresholdGatedCaptureCollector"/>. Owns a single bounded watch lifecycle:
/// one EventPipe metric session feeds samples, a predicate gates each sample, and a capture pump
/// fires the heavier artifact up to <c>maxCaptures</c> times. The watch ends the instant
/// <c>maxCaptures</c> is reached, when the window elapses, or when the target exits — then returns.
/// </summary>
public sealed class ThresholdGatedCaptureCollector : IThresholdGatedCaptureCollector
{
    private readonly IGatedMetricSampler _sampler;
    private readonly Func<int, CancellationToken, Task> _waitForExit;

    /// <summary>Production constructor: live EventPipe metric sampler + real process-exit watch.</summary>
    public ThresholdGatedCaptureCollector(ILogger<ThresholdGatedCaptureCollector>? logger = null)
        : this(new EventPipeMetricSampler(logger ?? NullLogger<ThresholdGatedCaptureCollector>.Instance), DefaultWaitForExitAsync)
    {
    }

    /// <summary>Test seam: inject a synthetic metric sampler and exit watcher.</summary>
    internal ThresholdGatedCaptureCollector(IGatedMetricSampler sampler, Func<int, CancellationToken, Task> waitForExit)
    {
        _sampler = sampler;
        _waitForExit = waitForExit;
    }

    public async Task<GatedCaptureResult> WatchAndCaptureAsync(
        int processId,
        TriggerPredicate predicate,
        GatedCaptureKind captureKind,
        TimeSpan window,
        int maxCaptures,
        TimeSpan sampleInterval,
        Func<GatedCaptureTrigger, CancellationToken, Task<GatedCaptureOutcome>> captureCallback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(captureCallback);
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window), "Window must be positive.");
        if (maxCaptures < 1) throw new ArgumentOutOfRangeException(nameof(maxCaptures), "maxCaptures must be >= 1.");
        if (sampleInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sampleInterval), "Sample interval must be positive.");

        var startedAt = DateTimeOffset.UtcNow;
        var captures = new List<GatedCaptureRecord>(maxCaptures);
        var notes = new List<string>();

        var gate = new object();
        var samplesObserved = 0;
        double? first = null;
        double? last = null;
        double? peak = null;
        var tripped = false;

        var trips = Channel.CreateBounded<double>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

        void OnSample(double value)
        {
            lock (gate)
            {
                samplesObserved++;
                first ??= value;
                last = value;
                peak = peak is null
                    ? value
                    : predicate.IsUpperBound ? Math.Max(peak.Value, value) : Math.Min(peak.Value, value);

                if (predicate.Evaluate(value))
                {
                    tripped = true;
                    trips.Writer.TryWrite(value);
                }
            }
        }

        using var armedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        async Task CapturePumpAsync()
        {
            try
            {
                while (captures.Count < maxCaptures)
                {
                    bool more;
                    try
                    {
                        more = await trips.Reader.WaitToReadAsync(armedCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (!more || !trips.Reader.TryRead(out var observed))
                    {
                        break;
                    }

                    var trigger = new GatedCaptureTrigger(processId, observed, DateTimeOffset.UtcNow, captures.Count);
                    GatedCaptureOutcome outcome;
                    try
                    {
                        // Pass the caller token (not the armed token) so an in-flight capture is
                        // allowed to finish even if the window elapses mid-capture.
                        outcome = await captureCallback(trigger, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        outcome = new GatedCaptureOutcome(
                            $"Capture failed: {ex.Message}", Error: ex.GetType().Name + ": " + ex.Message);
                    }

                    captures.Add(new GatedCaptureRecord(
                        Index: trigger.CaptureIndex,
                        ObservedValue: observed,
                        TrippedAt: trigger.TrippedAt,
                        CaptureKind: GatedCaptureKinds.Token(captureKind),
                        Summary: outcome.Summary,
                        Handle: outcome.Handle,
                        HandleExpiresAt: outcome.HandleExpiresAt,
                        ArtifactPath: outcome.ArtifactPath,
                        Error: outcome.Error));
                }
            }
            finally
            {
                trips.Writer.TryComplete();
                // Reaching maxCaptures (or an empty channel) ends the watch immediately.
                await armedCts.CancelAsync().ConfigureAwait(false);
            }
        }

        async Task<bool> ExitWatchAsync()
        {
            try
            {
                await _waitForExit(processId, armedCts.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        var samplerTask = Task.Run(() => _sampler.SampleAsync(processId, predicate.Metric, sampleInterval, OnSample, armedCts.Token), CancellationToken.None);
        var pumpTask = CapturePumpAsync();
        var exitTask = ExitWatchAsync();
        var windowTask = Task.Delay(window, CancellationToken.None);

        // samplerTask is included so an immediate metric-source failure (e.g. the EventPipe session
        // cannot start — diagnostic socket unavailable, permission denied, target already gone) ends
        // the watch promptly instead of idling until the window elapses and reporting a false
        // "no samples" success.
        await Task.WhenAny(windowTask, pumpTask, exitTask, samplerTask).ConfigureAwait(false);
        await armedCts.CancelAsync().ConfigureAwait(false);

        try { await pumpTask.ConfigureAwait(false); } catch (Exception) { }
        var processExited = false;
        try { processExited = await exitTask.ConfigureAwait(false); } catch (Exception) { }
        Exception? samplerFault = null;
        try { await samplerTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { samplerFault = ex; }

        // A sampler fault that produced no samples and fired no capture — and is not explained by the
        // target exiting or the caller cancelling — means the watch never observed the metric at all.
        // Surface it as a real error rather than a misleading empty success.
        if (samplerFault is not null && samplesObserved == 0 && captures.Count == 0 &&
            !processExited && !cancellationToken.IsCancellationRequested)
        {
            throw new GatedCaptureSamplerException(processId, predicate.Metric, samplerFault);
        }

        var (provider, counterName) = GatedCaptureMetrics.Counter(predicate.Metric);
        var endedByMaxCaptures = captures.Count >= maxCaptures;
        var windowExpired = !endedByMaxCaptures && !processExited && !cancellationToken.IsCancellationRequested;

        lock (gate)
        {
            if (samplesObserved == 0)
            {
                notes.Add($"No '{counterName}' samples arrived during the {window.TotalSeconds:F0}s window — the EventPipe counter session may not have produced a value yet (sessions take ~500 ms–1 s to start).");
            }

            if (processExited)
            {
                notes.Add("Target process exited during the watch window.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                notes.Add("Watch was cancelled by the caller before completing.");
            }

            return new GatedCaptureResult(
                ProcessId: processId,
                Metric: GatedCaptureMetrics.Token(predicate.Metric),
                Counter: $"{provider}/{counterName}",
                Predicate: predicate.ToString(),
                CaptureKind: GatedCaptureKinds.Token(captureKind),
                StartedAt: startedAt,
                Duration: DateTimeOffset.UtcNow - startedAt,
                Window: window,
                MaxCaptures: maxCaptures,
                SamplesObserved: samplesObserved,
                FirstObservedValue: first,
                LastObservedValue: last,
                PeakObservedValue: peak,
                Tripped: tripped,
                WindowExpired: windowExpired,
                ProcessExited: processExited,
                Captures: captures,
                Notes: notes);
        }
    }

    private static async Task DefaultWaitForExitAsync(int processId, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // Process is not visible to us (cannot watch exit). Wait until the watch is cancelled
            // rather than falsely reporting an immediate exit.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return;
        }

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
