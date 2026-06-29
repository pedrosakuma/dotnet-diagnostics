using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Core.Artifacts;

/// <summary>
/// Hosted wrapper that drives <see cref="ArtifactReaper"/> for the lifetime of the host. The TTL
/// is read once from <c>MCP_ARTIFACT_TTL_HOURS</c> (default 24h, 0=disabled); when disabled the
/// service exits immediately so it never spins. Pure cleanup, not always-on monitoring.
/// </summary>
public sealed class ArtifactReaperBackgroundService : BackgroundService
{
    private readonly ArtifactReaper _reaper;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _interval;

    public ArtifactReaperBackgroundService(
        IArtifactLifecycle lifecycle,
        ILogger<ArtifactReaperBackgroundService>? logger = null,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        _reaper = new ArtifactReaper(lifecycle, logger);
        _ttl = ArtifactReaper.ResolveTtl();
        _interval = interval ?? TimeSpan.FromMinutes(15);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => _reaper.RunAsync(_ttl, _interval, stoppingToken);
}
