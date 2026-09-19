using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnostics.TestSupport;

/// <summary>
/// Readiness gates shared by the live-test harness: poll until a spawned process exposes its
/// .NET diagnostic IPC endpoint, and until its Kestrel HTTP endpoint accepts requests. Both
/// poll-and-wait rather than racing the sample's startup, which is the historical source of
/// flakiness in this suite.
/// </summary>
public static class DiagnosticReadiness
{
    /// <summary>
    /// Polls <see cref="DiagnosticsClient.GetPublishedProcesses()"/> until <paramref name="pid"/>
    /// advertises a diagnostic endpoint, or throws <see cref="TimeoutException"/> after
    /// <paramref name="timeout"/>.
    /// </summary>
    public static async Task WaitForDiagnosticEndpointAsync(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (DiagnosticsClient.GetPublishedProcesses().Contains(pid))
            {
                return;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        throw new TimeoutException($"pid {pid} did not expose a diagnostic endpoint within {timeout}.");
    }

    /// <summary>
    /// Polls <paramref name="baseUrl"/><paramref name="readinessPath"/> until it returns a success
    /// status, or throws <see cref="SkipException"/> after <paramref name="timeout"/>. Kestrel
    /// occasionally logs its listening URL just before the socket is fully bound, hence the retry.
    /// The monotonic deadline cancels both in-flight requests and polling delays.
    /// </summary>
    public static Task WaitForHttpReadyAsync(string baseUrl, TimeSpan timeout, string readinessPath = "/")
        => WaitForHttpReadyAsync(baseUrl, timeout, readinessPath, CancellationToken.None);

    /// <summary>The same readiness gate with caller cancellation, propagated as
    /// <see cref="OperationCanceledException"/> rather than a readiness timeout.</summary>
    public static async Task WaitForHttpReadyAsync(string baseUrl, TimeSpan timeout, string readinessPath,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        await WaitForHttpReadyAsync(http, timeout, readinessPath, TimeProvider.System, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WaitForHttpReadyAsync(HttpClient http, TimeSpan timeout, string readinessPath,
        TimeProvider timeProvider, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero) throw HttpTimeout(http, readinessPath);
        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                try
                {
                    using var response = await http.GetAsync(readinessPath, linked.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // Socket not fully ready yet; retry until the same deadline.
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw HttpTimeout(http, readinessPath);
        }
    }

    private static SkipException HttpTimeout(HttpClient http, string readinessPath)
        => SkipException.ForReason($"Sample did not accept HTTP requests on {http.BaseAddress?.OriginalString}{readinessPath} within the timeout.");
}
