using System.Diagnostics;

namespace DotnetDiagnostics.TestSupport;

internal interface IOwnedSampleChild : IDisposable
{
    Process Process { get; }
    int Id { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    TextReader Stdout { get; }
    TextReader Stderr { get; }
    void Kill();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal sealed class OwnedSampleChild(Process process) : IOwnedSampleChild
{
    public Process Process => process;
    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public int ExitCode => process.ExitCode;
    public TextReader Stdout => process.StandardOutput;
    public TextReader Stderr => process.StandardError;
    public void Kill() => process.Kill(entireProcessTree: true);
    public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
    public void Dispose() => process.Dispose();
}

internal sealed record LiveSampleHooks
{
    internal Func<ProcessStartInfo, IOwnedSampleChild> Start { get; init; } = info =>
        new OwnedSampleChild(Process.Start(info) ?? throw new InvalidOperationException("Sample process did not start."));
    internal Func<int, TimeSpan, CancellationToken, Task> DiagnosticReady { get; init; } =
        DiagnosticReadiness.WaitForDiagnosticEndpointAsync;
    internal Func<string, TimeSpan, string, CancellationToken, Task> HttpReady { get; init; } =
        DiagnosticReadiness.WaitForHttpReadyAsync;
}
