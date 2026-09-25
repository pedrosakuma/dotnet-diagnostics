using System.Runtime.CompilerServices;
using System.Text.Json;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli;

internal sealed record CliCaptureMetadata(
    IReadOnlyDictionary<string, IReadOnlyList<string>> Views,
    IReadOnlyDictionary<string, DurableCaptureComposition> Compositions,
    IReadOnlyDictionary<string, DurableCaptureRecordStreamInfo> RecordStreams);

internal sealed class CliDurableCaptures
{
    private const int MaximumRoots = 32;
    private static readonly ConditionalWeakTable<IDiagnosticHandleStore, CliDurableCaptures> Hosts = new();
    private readonly IDiagnosticHandleStore _handles;
    private readonly Dictionary<string, DurableCaptureUseCases> _roots =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private CliDurableCaptures(IDiagnosticHandleStore handles) => _handles = handles;

    internal static CliDurableCaptures For(IServiceProvider services)
        => Hosts.GetValue(services.GetRequiredService<IDiagnosticHandleStore>(), static handles => new(handles));

    internal DurableCaptureUseCases Get(string? root)
    {
        var provider = new CliCaptureRootProvider(root);
        lock (_roots)
        {
            if (_roots.TryGetValue(provider.Root, out var existing))
            {
                return existing;
            }
            if (_roots.Count == MaximumRoots)
            {
                throw new CaptureStoreException(CaptureErrorCode.CapacityExceeded,
                    $"This session reached MaximumRoots={MaximumRoots}. Start a new session to open another capture root.");
            }
            var options = new CaptureStoreOptions();
            var service = new DurableCaptureUseCases(new SqliteCaptureStore(provider, options), _handles, options);
            _roots.Add(provider.Root, service);
            return service;
        }
    }

    internal DurableCaptureUseCases? FindBinding(string handle)
    {
        lock (_roots)
        {
            return _roots.Values.FirstOrDefault(service => service.LookupBinding(handle) is not null);
        }
    }

    internal async Task<CliCaptureMetadata> DescribeMetadataAsync(
        string? root, CaptureInfo capture, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var compositions = new Dictionary<string, DurableCaptureComposition>(StringComparer.Ordinal);
        var streams = new Dictionary<string, DurableCaptureRecordStreamInfo>(StringComparer.Ordinal);
        if (capture.State != CaptureState.Sealed)
        {
            foreach (var artifact in capture.Artifacts) result.Add(artifact.ArtifactId, []);
            return new(result, compositions, streams);
        }

        var options = new CaptureStoreOptions();
        var service = Get(root);
        var store = new SqliteCaptureStore(new CliCaptureRootProvider(root), options);
        using var reader = await store.OpenAsync(capture.CaptureId, CliCaptureRootProvider.CurrentAccess(), cancellationToken).ConfigureAwait(false);
        foreach (var artifact in capture.Artifacts)
        {
            var offline = await service.DescribeArtifactViewsAsync(
                capture.CaptureId, artifact.ArtifactId, CliCaptureRootProvider.CurrentAccess(), cancellationToken).ConfigureAwait(false);
            result.Add(artifact.ArtifactId,
                offline.Where(view => view is "records" or "children" ||
                    CliCommands.SessionViewsFor(artifact.Kind).Contains(view, StringComparer.Ordinal)).ToArray());
            var snapshot = reader.ReadSnapshot(artifact.ArtifactId);
            if (snapshot is null)
            {
                continue;
            }
            try
            {
                if (snapshot.Version == DurableCaptureSnapshotMetadata.Version)
                {
                    var metadata = DurableCaptureSnapshotMetadata.Decode(artifact.Kind, snapshot, options.MaxSnapshotBytes);
                    snapshot = metadata.Snapshot;
                    streams.Add(artifact.ArtifactId, metadata.Stream);
                }
                if (snapshot.Version == DurableCaptureCompositionCodec.SnapshotVersion)
                {
                    var composition = DurableCaptureCompositionCodec.Decode(
                        artifact.Kind, artifact.ArtifactId, snapshot, reader.Info, options);
                    compositions.Add(artifact.ArtifactId, composition);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
            {
                throw new CaptureStoreException(CaptureErrorCode.UnsupportedFormat,
                    $"Artifact {artifact.ArtifactId} has no safely supported snapshot representation: {ex.Message}", ex);
            }
        }
        return new(result, compositions, streams);
    }
}
