using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnosticsMcp.TestSupport;

/// <summary>
/// Readiness gates shared by the live-test harness: poll until a spawned process exposes its
/// .NET diagnostic IPC endpoint, and until its Kestrel HTTP endpoint accepts requests. Both
/// poll-and-wait rather than racing the sample's startup, which is the historical source of
/// flakiness in this suite.
/// </summary>
public static class DiagnosticReadiness
{
    /// <summary>
    /// Polls <see cref="DiagnosticsClient.GetPublishedProcesses"/> until <paramref name="pid"/>
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
    /// </summary>
    public static async Task WaitForHttpReadyAsync(string baseUrl, TimeSpan timeout, string readinessPath = "/")
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync(readinessPath, CancellationToken.None).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Socket not fully ready yet; retry until the deadline.
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        throw SkipException.ForReason($"Sample did not accept HTTP requests on {baseUrl}{readinessPath} within the timeout.");
    }
}
