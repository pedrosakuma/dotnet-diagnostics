namespace DotnetDiagnostics.Core.Networking;

/// <summary>Collects a curated view of the stable .NET networking EventSources over a fixed window.</summary>
public interface INetworkingCollector
{
    /// <summary>
    /// Subscribes to <c>System.Net.Http</c>, <c>System.Net.NameResolution</c>,
    /// <c>System.Net.Security</c> and <c>System.Net.Sockets</c> for <paramref name="duration"/> and
    /// aggregates their events + EventCounters into a <see cref="NetworkingSnapshot"/>.
    /// </summary>
    Task<NetworkingSnapshot> CollectAsync(
        int processId,
        TimeSpan duration,
        int intervalSeconds = 1,
        CancellationToken cancellationToken = default);
}
