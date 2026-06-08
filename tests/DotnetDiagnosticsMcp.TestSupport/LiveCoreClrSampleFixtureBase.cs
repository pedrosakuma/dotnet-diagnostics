namespace DotnetDiagnosticsMcp.TestSupport;

using Xunit;

/// <summary>
/// Shared xunit class fixture that boots a single <c>CoreClrSample</c> instance for the lifetime
/// of a test class and exposes its PID. Derive a project-local fixture from this so the concrete
/// fixture type identity stays inside the consuming test assembly (required by
/// <c>IClassFixture&lt;T&gt;</c>), while the spawn/teardown logic lives here.
/// </summary>
public abstract class LiveCoreClrSampleFixtureBase : IAsyncLifetime
{
    private LiveSampleProcess? _sample;

    /// <summary>OS process id of the running CoreClrSample.</summary>
    public int ProcessId => _sample?.ProcessId ?? throw new InvalidOperationException("Sample not started.");

    /// <summary>The running sample process, or <see langword="null"/> before initialization.</summary>
    protected LiveSampleProcess? Sample => _sample;

    public async Task InitializeAsync()
        => _sample = await LiveSampleProcess.StartPublishedAsync("CoreClrSample").ConfigureAwait(false);

    public async Task DisposeAsync()
    {
        if (_sample is not null)
        {
            await _sample.DisposeAsync().ConfigureAwait(false);
        }
    }
}
