namespace DotnetDiagnostics.TestSupport;

/// <summary>
/// Tuning knobs for <see cref="LiveSampleProcess.StartPublishedAsync"/>. Defaults reproduce the
/// historical inline harness: bind Kestrel to an ephemeral loopback port, drain stdio, and wait
/// for the diagnostic endpoint — but do not block on HTTP readiness unless asked.
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

    /// <summary>Timeout for harvesting the listening URL and for HTTP readiness.</summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
