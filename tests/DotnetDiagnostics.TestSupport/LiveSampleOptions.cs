namespace DotnetDiagnostics.TestSupport;

/// <summary>
/// Tuning knobs for <see cref="LiveSampleProcess.StartPublishedAsync"/>. Defaults bind Kestrel to
/// an ephemeral loopback port, drain bounded stdio tails, and wait for the diagnostic endpoint.
/// HTTP readiness is opt-in; all enabled startup gates share one total deadline.
/// </summary>
public sealed record LiveSampleOptions
{
    /// <summary>Extra environment variables layered on top of the harness defaults
    /// (<c>DOTNET_NOLOGO=1</c>, <c>ASPNETCORE_ENVIRONMENT=Development</c>). Overrides win.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>When true (default) the sample is launched with <c>--urls http://127.0.0.1:0</c>.</summary>
    public bool BindHttpPort { get; init; } = true;

    /// <summary>When true, the harness harvests the "Now listening on:" URL from stdout so callers
    /// can await it via <see cref="LiveSampleProcess.WaitForListeningUrlAsync"/>.</summary>
    public bool HarvestListeningUrl { get; init; }

    /// <summary>When true, <see cref="LiveSampleProcess.StartPublishedAsync"/> additionally blocks
    /// until the harvested URL accepts HTTP requests and stamps <see cref="LiveSampleProcess.BaseUrl"/>.
    /// Implies <see cref="HarvestListeningUrl"/>.</summary>
    public bool WaitForHttpReady { get; init; }

    /// <summary>Readiness path polled when <see cref="WaitForHttpReady"/> is set. Defaults to <c>/</c>.</summary>
    public string ReadinessPath { get; init; } = "/";

    /// <summary>Timeout for the diagnostic-endpoint readiness gate.</summary>
    public TimeSpan DiagnosticTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Shared timeout for listening-URL harvesting plus HTTP readiness.</summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Total startup budget shared by diagnostic, URL and HTTP gates.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Separate total budget for owned process termination and reader draining.</summary>
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Optional retained phase/output evidence, also available on startup failure.</summary>
    public LiveSampleEvidence? Evidence { get; init; }
}
