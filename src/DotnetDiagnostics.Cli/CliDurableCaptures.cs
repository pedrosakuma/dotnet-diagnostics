using System.Runtime.CompilerServices;
using System.Text.Json;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.UseCases;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli;

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

    internal static async Task<Dictionary<string, IReadOnlyList<string>>> DescribeViewsAsync(
        string? root, CaptureInfo capture, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (capture.State != CaptureState.Sealed)
        {
            foreach (var artifact in capture.Artifacts) result.Add(artifact.ArtifactId, []);
            return result;
        }

        var options = new CaptureStoreOptions();
        var store = new SqliteCaptureStore(new CliCaptureRootProvider(root), options);
        using var reader = await store.OpenAsync(capture.CaptureId, CliCaptureRootProvider.CurrentAccess(), cancellationToken).ConfigureAwait(false);
        foreach (var artifact in capture.Artifacts)
        {
            var snapshot = reader.ReadSnapshot(artifact.ArtifactId);
            if (snapshot is null)
            {
                result.Add(artifact.ArtifactId, ["records"]);
                continue;
            }
            try
            {
                var decoded = CaptureArtifactCodec.Decode(artifact.Kind, snapshot.Version,
                    snapshot.Utf8Json.Span, options.MaxSnapshotBytes);
                var offline = CaptureArtifactCodec.GetSupportedSnapshotViews(artifact.Kind, decoded);
                result.Add(artifact.ArtifactId,
                    ["records", .. offline.Intersect(CliCommands.SessionViewsFor(artifact.Kind), StringComparer.Ordinal)]);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                throw new CaptureStoreException(CaptureErrorCode.UnsupportedFormat,
                    $"Artifact {artifact.ArtifactId} has no safely supported snapshot representation: {ex.Message}", ex);
            }
        }
        return result;
    }
}
