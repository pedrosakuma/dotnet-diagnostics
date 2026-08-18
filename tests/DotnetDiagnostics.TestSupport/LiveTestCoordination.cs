namespace DotnetDiagnostics.TestSupport;

/// <summary>
/// Reusable, bounded coordination patterns for live EventPipe/ClrMD tests (issue #853). These
/// replace two recurring hazards identified in issue #848 and fixed ad hoc in PRs #849/#850:
/// a finite pre-dispatch workload that can fully complete (or never start) before a concurrent
/// EventPipe session finishes arming, and a fixed "sleep, then read once" assumption that a
/// fixture has published the state a test intends to inspect. Both helpers assert readiness
/// instead of assuming elapsed time implies readiness, and both remain bounded — they are not
/// a substitute for a global retry wrapper around whole tests, which would mask regressions
/// instead of proving evidence exists.
/// </summary>
public static class LiveTestCoordination
{
    /// <summary>
    /// Polls <paramref name="probe"/> until it reports readiness or <paramref name="timeout"/>
    /// elapses. Use this instead of a fixed <c>Task.Delay</c> before a one-shot read of
    /// asynchronously published fixture state (e.g. a leaked-timer or leaked-handle counter);
    /// the probe should perform the actual snapshot/query so its last result can double as the
    /// value under test. Returns the last observed value whether or not it satisfied
    /// <paramref name="isReady"/>, so a caller can hand it straight to a FluentAssertions
    /// assertion for a precise, diagnosable failure instead of a generic timeout message.
    /// </summary>
    /// <param name="probe">Produces the next observation. Invoked at least once.</param>
    /// <param name="isReady">Returns true once <paramref name="probe"/>'s result is sufficient.</param>
    /// <param name="timeout">Upper bound on total time spent polling.</param>
    /// <param name="pollInterval">Delay between unsuccessful attempts.</param>
    /// <param name="cancellationToken">Propagated to the delay between attempts.</param>
    public static async Task<T> PollUntilAsync<T>(
        Func<Task<T>> probe,
        Func<T, bool> isReady,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(isReady);

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var observed = await probe().ConfigureAwait(false);
            var now = DateTime.UtcNow;
            if (isReady(observed) || now >= deadline)
            {
                return observed;
            }

            // Cap the delay to whatever remains of the timeout so a slow probe followed by a
            // long pollInterval cannot push a subsequent, doomed-to-fail probe meaningfully past
            // the advertised bound.
            var remaining = deadline - now;
            await Task.Delay(remaining < pollInterval ? remaining : pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Polls a boolean <paramref name="condition"/> until it returns true, or throws
    /// <see cref="TimeoutException"/> with <paramref name="timeoutMessage"/> after
    /// <paramref name="timeout"/>. Use for simple readiness gates (e.g. "the fixture reports it
    /// has started N background operations") that don't need to surface the observed value.
    /// </summary>
    public static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan pollInterval,
        string timeoutMessage,
        CancellationToken cancellationToken = default)
    {
        var ready = await PollUntilAsync(
            condition,
            static ready => ready,
            timeout,
            pollInterval,
            cancellationToken).ConfigureAwait(false);

        if (!ready)
        {
            throw new TimeoutException(timeoutMessage);
        }
    }

    /// <summary>
    /// Starts a background workload that stays active until the test explicitly stops it —
    /// intended to be stopped after a collector call returns, not after a fixed iteration count.
    /// A finite burst races EventPipe/ClrMD session arm-up (~500ms-1s) and can complete before, or
    /// entirely miss, the collection window on a loaded CI runner; this keeps generating evidence
    /// for the whole window instead. Dispose (or call <see cref="StopAsync"/>) after the collector
    /// call you are driving evidence for has returned.
    /// </summary>
    public static BackgroundWorkload StartBackgroundWorkload(
        Func<CancellationToken, Task> iteration,
        TimeSpan? initialDelay = null,
        TimeSpan? pace = null)
        => BackgroundWorkload.Start(iteration, initialDelay, pace);
}

/// <summary>
/// A cancellation-driven background loop started by <see cref="LiveTestCoordination.StartBackgroundWorkload"/>.
/// Stop it (via <see cref="StopAsync"/> or <see cref="DisposeAsync"/>) once the collector call it is
/// feeding has returned, rather than bounding it by a fixed iteration count.
/// </summary>
public sealed class BackgroundWorkload : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Task _loop;
    private bool _stopped;

    private BackgroundWorkload(Task loop, CancellationTokenSource cts)
    {
        _loop = loop;
        _cts = cts;
    }

    internal static BackgroundWorkload Start(
        Func<CancellationToken, Task> iteration,
        TimeSpan? initialDelay,
        TimeSpan? pace)
    {
        ArgumentNullException.ThrowIfNull(iteration);

        var cts = new CancellationTokenSource();
        var token = cts.Token;
        var loop = Task.Run(async () =>
        {
            try
            {
                if (initialDelay is { } delay && delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }

                while (!token.IsCancellationRequested)
                {
                    await iteration(token).ConfigureAwait(false);
                    if (pace is { } cadence && cadence > TimeSpan.Zero)
                    {
                        await Task.Delay(cadence, token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Expected: the test stops the workload once the collector call it feeds returns.
            }
        }, CancellationToken.None);

        return new BackgroundWorkload(loop, cts);
    }

    /// <summary>Signals the loop to stop and awaits its completion.</summary>
    public async Task StopAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _cts.Cancel();
        await _loop.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
