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
    public static async Task WaitForDiagnosticEndpointAsync(int pid, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (DiagnosticsClient.GetPublishedProcesses().Contains(pid)) return;
                await Task.Delay(500, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"pid {pid} did not expose a diagnostic endpoint within {timeout}.");
        }
    }

    /// <summary>
    /// Polls <paramref name="baseUrl"/><paramref name="readinessPath"/> until it returns a success
    /// status, or throws <see cref="SkipException"/> after <paramref name="timeout"/>. Kestrel
    /// occasionally logs its listening URL just before the socket is fully bound, hence the retry.
    /// </summary>
    public static async Task WaitForHttpReadyAsync(string baseUrl, TimeSpan timeout, string readinessPath = "/",
        CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        await WaitForHttpReadyAsync(http, timeout, readinessPath, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WaitForHttpReadyAsync(HttpClient http, TimeSpan timeout, string readinessPath,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    using var response = await http.GetAsync(readinessPath, deadline.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                await Task.Delay(250, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw SkipException.ForReason($"Sample did not accept HTTP requests on {http.BaseAddress}{readinessPath} within the timeout.");
        }
    }
}
