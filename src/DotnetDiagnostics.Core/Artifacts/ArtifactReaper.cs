using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.Artifacts;

/// <summary>
/// Host-neutral TTL reaper for the artifact root. Reads <c>MCP_ARTIFACT_TTL_HOURS</c>
/// (default 24h; <c>0</c> or negative disables pruning) and periodically deletes artifacts older
/// than the TTL via <see cref="IArtifactLifecycle.Prune"/>. Bounded, pure cleanup — not an
/// always-on monitoring daemon: it sleeps between sweeps and only removes aged files inside the
/// root, so a sidecar doing repeated WithHeap dumps no longer fills <c>/tmp</c>.
/// </summary>
public sealed class ArtifactReaper
{
    /// <summary>Environment variable that sets the artifact TTL in hours.</summary>
    public const string TtlHoursEnvironmentVariable = "MCP_ARTIFACT_TTL_HOURS";

    public const double DefaultTtlHours = 24;

    private readonly IArtifactLifecycle _lifecycle;
    private readonly ILogger _logger;

    public ArtifactReaper(IArtifactLifecycle lifecycle, ILogger? logger = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Resolves the configured TTL. Returns <see cref="TimeSpan.Zero"/> when disabled.</summary>
    public static TimeSpan ResolveTtl(double defaultHours = DefaultTtlHours)
    {
        var configured = Environment.GetEnvironmentVariable(TtlHoursEnvironmentVariable);
        var hours = defaultHours;
        if (!string.IsNullOrWhiteSpace(configured)
            && double.TryParse(configured, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            hours = parsed;
        }

        // Reject NaN/Infinity/out-of-range so a malformed env var cannot throw from
        // TimeSpan.FromHours and abort host startup; fall back to disabled instead.
        if (!double.IsFinite(hours) || hours <= 0)
        {
            return TimeSpan.Zero;
        }

        // Cap absurd values to keep TimeSpan.FromHours in range (10 years is effectively "never").
        const double maxHours = 24 * 365 * 10;
        return TimeSpan.FromHours(Math.Min(hours, maxHours));
    }

    /// <summary>Runs one prune sweep. Returns the number of artifacts deleted.</summary>
    public int Sweep(TimeSpan ttl, DateTimeOffset? nowUtc = null)
    {
        if (ttl <= TimeSpan.Zero)
        {
            return 0;
        }

        var pruned = _lifecycle.Prune(ttl, nowUtc);
        if (pruned.Count > 0)
        {
            _logger.LogInformation(
                "Artifact reaper pruned {Count} artifact(s) older than {TtlHours}h under {Root}.",
                pruned.Count, ttl.TotalHours, _lifecycle.Root);
        }

        return pruned.Count;
    }

    /// <summary>Loops sweeping at <paramref name="interval"/> until cancelled. No-op when disabled.</summary>
    public async Task RunAsync(TimeSpan ttl, TimeSpan interval, CancellationToken cancellationToken)
    {
        if (ttl <= TimeSpan.Zero)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Sweep(ttl);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Artifact reaper sweep failed; will retry on the next tick.");
            }

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
