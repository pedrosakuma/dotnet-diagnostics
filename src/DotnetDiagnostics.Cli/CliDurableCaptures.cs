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

    internal static async Task<CliCaptureMetadata> DescribeMetadataAsync(
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
        var store = new SqliteCaptureStore(new CliCaptureRootProvider(root), options);
        using var reader = await store.OpenAsync(capture.CaptureId, CliCaptureRootProvider.CurrentAccess(), cancellationToken).ConfigureAwait(false);
        foreach (var artifact in capture.Artifacts)
        {
            var snapshot = reader.ReadSnapshot(artifact.ArtifactId);
            var recordsAvailable = reader.Query(new(artifact.ArtifactId, PageSize: 1)).Records.Count > 0;
            if (snapshot is null)
            {
                result.Add(artifact.ArtifactId, recordsAvailable ? ["records"] : []);
                continue;
            }
            try
            {
                if (snapshot.Version == DurableCaptureSnapshotMetadata.Version)
                {
                    var metadata = DurableCaptureSnapshotMetadata.Decode(artifact.Kind, snapshot, options.MaxSnapshotBytes);
                    snapshot = metadata.Snapshot;
                    streams.Add(artifact.ArtifactId, metadata.Stream);
                    recordsAvailable |= metadata.Stream.Available;
                }
                if (snapshot.Version == 0)
                {
                    result.Add(artifact.ArtifactId, recordsAvailable ? ["records"] : []);
                    continue;
                }
                if (snapshot.Version == DurableCaptureCompositionCodec.SnapshotVersion)
                {
                    var composition = DurableCaptureCompositionCodec.Decode(
                        artifact.Kind, artifact.ArtifactId, snapshot, reader.Info, options);
                    compositions.Add(artifact.ArtifactId, composition);
                    result.Add(artifact.ArtifactId, recordsAvailable ? ["records"] : []);
                    continue;
                }
                var decoded = CaptureArtifactCodec.Decode(artifact.Kind, snapshot.Version,
                    snapshot.Utf8Json.Span, options.MaxSnapshotBytes);
                var offline = CaptureArtifactCodec.GetSupportedSnapshotViews(artifact.Kind, decoded);
                IReadOnlyList<string> recordViews = recordsAvailable ? ["records"] : [];
                result.Add(artifact.ArtifactId,
                    [.. recordViews, .. offline.Intersect(CliCommands.SessionViewsFor(artifact.Kind), StringComparer.Ordinal)]);
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
