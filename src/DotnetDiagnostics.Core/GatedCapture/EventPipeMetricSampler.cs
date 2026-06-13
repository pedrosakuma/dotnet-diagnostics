using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
using DotnetDiagnostics.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.GatedCapture;

/// <summary>
/// Internal seam over the live metric feed so the bounded-watch orchestration in
/// <see cref="ThresholdGatedCaptureCollector"/> is unit-testable with a synthetic sampler.
/// </summary>
internal interface IGatedMetricSampler
{
    /// <summary>
    /// Streams successive observations of <paramref name="metric"/> from <paramref name="processId"/>,
    /// invoking <paramref name="onSample"/> once per emitted value until <paramref name="cancellationToken"/>
    /// fires (or the target's EventPipe session ends because it exited).
    /// </summary>
    Task SampleAsync(
        int processId,
        GatedCaptureMetric metric,
        TimeSpan interval,
        Action<double> onSample,
        CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IGatedMetricSampler"/>: a single <c>System.Runtime</c> EventPipe
/// EventCounters session that forwards the gated counter's per-interval value. One session covers
/// every supported metric (cpu / gc-heap / working-set / threadpool / timers) — no OS-specific
/// polling, identical behaviour inside a container sidecar.
/// </summary>
internal sealed class EventPipeMetricSampler : IGatedMetricSampler
{
    private readonly ILogger _logger;

    public EventPipeMetricSampler(ILogger? logger = null)
        => _logger = logger ?? NullLogger.Instance;

    public async Task SampleAsync(
        int processId,
        GatedCaptureMetric metric,
        TimeSpan interval,
        Action<double> onSample,
        CancellationToken cancellationToken)
    {
        var (provider, counterName) = GatedCaptureMetrics.Counter(metric);
        var intervalSeconds = Math.Max(1, (int)Math.Round(interval.TotalSeconds));
        var counterArguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EventCounterIntervalSec"] = intervalSeconds.ToString(CultureInfo.InvariantCulture),
        };
        var providers = new[]
        {
            new EventPipeProvider(provider, EventLevel.Verbose, (long)EventKeywords.All, counterArguments),
        };

        var client = new DiagnosticsClient(processId);
        var session = await client
            .StartEventPipeSessionWithTimeoutAsync(providers, requestRundown: false, circularBufferMB: 64, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        var processingTask = Task.Run(() =>
        {
            try
            {
                using var source = new EventPipeEventSource(session.EventStream);
                source.Dynamic.All += traceEvent =>
                {
                    if (!string.Equals(traceEvent.EventName, "EventCounters", StringComparison.Ordinal) ||
                        !string.Equals(traceEvent.ProviderName, provider, StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (TryExtract(traceEvent, counterName, out var value))
                    {
                        onSample(value);
                    }
                };

                source.Process();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Gated-capture metric session ended for pid {Pid}.", processId);
            }
        }, CancellationToken.None);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected — the orchestrator stops the watch by cancelling.
        }
        finally
        {
            try { await session.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            try
            {
                var drain = Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                await (await Task.WhenAny(processingTask, drain).ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (Exception) { }
            session.Dispose();
        }
    }

    private static bool TryExtract(TraceEvent traceEvent, string counterName, out double value)
    {
        value = 0;
        if (traceEvent.PayloadValue(0) is not IDictionary<string, object> outer ||
            !outer.TryGetValue("Payload", out var inner) || inner is not IDictionary<string, object> data)
        {
            return false;
        }

        if (data.TryGetValue("Name", out var nameObj) is false || nameObj is not string name ||
            !string.Equals(name, counterName, StringComparison.Ordinal))
        {
            return false;
        }

        if (data.TryGetValue("Mean", out var meanObj))
        {
            value = ToDouble(meanObj);
            return true;
        }

        if (data.TryGetValue("Increment", out var incObj))
        {
            value = ToDouble(incObj);
            return true;
        }

        return false;
    }

    private static double ToDouble(object? value) => value switch
    {
        double d => d,
        float f => f,
        long l => l,
        int i => i,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => 0,
    };
}
