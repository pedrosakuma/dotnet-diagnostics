using System.Text.Json;
using DotnetDiagnostics.Core.CaptureRecording;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Drilldown;

namespace DotnetDiagnostics.Core.UseCases;

/// <summary>A fully materialized, offline snapshot and the metadata needed for host policy checks.</summary>
public sealed record DurableCaptureOpenResult(
    DiagnosticHandle Handle, IReadOnlyList<string> SupportedViews, CaptureInfo Capture, CaptureArtifactInfo Artifact)
{
    /// <summary>Versioned reference metadata for a composed capture; select a child for typed drilldown.</summary>
    public DurableCaptureComposition? Composition { get; init; }
}

/// <summary>Small association whose lifetime is bounded by the registered snapshot's lifetime.</summary>
public sealed record DurableCaptureHandleBinding(
    string CaptureId, string ArtifactId, IReadOnlyList<string> SupportedViews)
{
    /// <summary>Original producer facts, never a grant of host scopes or authorization.</summary>
    public CaptureArtifactInfo? Artifact { get; init; }
}

/// <summary>
/// Host-neutral opt-in persistence around existing collection operations. Hosts remain responsible
/// for authorizing the producer, kind, and requested view, in addition to capture ownership.
/// </summary>
public sealed class DurableCaptureUseCases
{
    private readonly SqliteCaptureStore _store;
    private readonly IDiagnosticHandleStore _handles;
    private readonly CaptureStoreOptions _options;
    private readonly DurableCaptureBindings _bindings;

    public DurableCaptureUseCases(SqliteCaptureStore store, IDiagnosticHandleStore handles, CaptureStoreOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _handles = handles ?? throw new ArgumentNullException(nameof(handles));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        _bindings = new(handles);
    }

    /// <summary>Routes a child operation before callbacks start; the outer capture owns persistence.</summary>
    public async Task<DiagnosticResult<T>> RunChildAsync<T>(string kind, string name,
        Func<CancellationToken, Task<DiagnosticResult<T>>> collect, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collect);
        var child = CaptureRecordingContext.CreateChild(kind, name);
        using var scope = child is null ? null : CaptureRecordingContext.Enter(child);
        try
        {
            var result = await collect(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Child collector returned no diagnostic result.");
            if (result.Handle is { } handle && _handles.TryGetWithKind(handle) is { } lookup)
                child?.ArtifactRegistered(lookup.Handle, lookup.Artifact);
            child?.ReportCompletion(result.Error, result.Cancelled, result.Data);
            return result;
        }
        catch (OperationCanceledException)
        {
            child?.ReportCompletion(null, cancelled: true);
            throw;
        }
        catch (Exception ex)
        {
            child?.ReportCompletion(new("ChildCollectionFailed", ex.Message), cancelled: false);
            throw;
        }
    }

    /// <summary>
    /// Wraps a heterogeneous host dispatcher. The required projection must expose its original
    /// structured error/cancellation and typed payload, not serialize or reinterpret the host wrapper.
    /// </summary>
    public async Task<DurableCaptureOperationResult<T>> CaptureOperationAsync<T>(
        string name, string kind, CaptureAccess access, Func<CancellationToken, Task<T>> collect,
        Func<T, DiagnosticResult<object?>> describeResult, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collect);
        ArgumentNullException.ThrowIfNull(describeResult);
        T? original = default;
        var hasResult = false;
        var outcome = await CaptureAsync<object?>(name, kind, access, async token =>
        {
            original = await collect(token).ConfigureAwait(false);
            hasResult = true;
            return describeResult(original);
        }, cancellationToken).ConfigureAwait(false);
        return new(original, hasResult, outcome);
    }

    public async Task<DiagnosticResult<T>> CaptureAsync<T>(
        string name, string kind, CaptureAccess access,
        Func<CancellationToken, Task<DiagnosticResult<T>>> collect, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collect);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        kind = DurableCaptureKinds.Canonical(kind);
        var writer = await _store.CreateAsync(new(name), access, cancellationToken).ConfigureAwait(false);
        var created = DateTimeOffset.UtcNow;
        var artifacts = new List<CaptureArtifactInfo>();
        DiagnosticResult<T>? result = null;
        Exception? persistenceFailure = null;
        CaptureInfo? info = null;
        var stopped = false;
        try
        {
            var defaultId = writer.AddArtifact(kind, name);
            artifacts.Add(new(defaultId, kind, name));
            var pending = new DurableCaptureHandleBinding(writer.Reference.CaptureId, defaultId, Array.Empty<string>());
            var sink = new SqliteCaptureObservationSink(writer, defaultId, kind, name, _options,
                (handle, artifact) => _bindings.Set(handle, artifact, pending));
            try
            {
                using (CaptureRecordingContext.Enter(sink))
                    result = await collect(cancellationToken).ConfigureAwait(false);
                if (result is null)
                    throw new InvalidOperationException("Collector returned no diagnostic result.");
            }
            catch (OperationCanceledException)
            {
                result = new("Collection was interrupted; the capture requires explicit recovery.", [])
                {
                    Cancelled = true,
                };
            }
            catch (Exception ex)
            {
                result = DiagnosticResult.Fail<T>("Collection failed; partial capture retained.",
                    new("CollectionFailed", ex.Message));
            }

            // A custom handle store may not implement registration announcements.
            if (result.Handle is { } returnedHandle && _handles.TryGetWithKind(returnedHandle) is { } lookup)
                sink.ArtifactRegistered(lookup.Handle, lookup.Artifact);
            var retained = sink.Finish();
            var snapshots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in retained.Nodes)
            {
                if (node.ArtifactId != defaultId) artifacts.Add(new(node.ArtifactId, node.Kind, node.Name));
                var hasChildren = retained.Nodes.Any(child => child.ParentArtifactId == node.ArtifactId);
                var matching = node.Artifacts.Count(a => a.Kind == node.Kind);
                foreach (var registered in node.Artifacts)
                {
                    try
                    {
                        var id = matching == 1 && registered.Kind == node.Kind && !hasChildren
                            ? node.ArtifactId : writer.AddArtifact(registered.Kind, registered.Kind);
                        if (id != node.ArtifactId) artifacts.Add(new(id, registered.Kind, registered.Kind));
                        var process = result.ResolvedProcess;
                        var provenance = new CaptureArtifactProvenance(
                            ProcessId: registered.Handle.ProcessId > 0 ? registered.Handle.ProcessId : null,
                            ProducingTool: registered.Handle.ProducingTool,
                            OriginalHandleOrigin: registered.Handle.Origin.ToString(),
                            RuntimeName: process?.ProcessId == registered.Handle.ProcessId ? process.Runtime.ToString() : null,
                            RuntimeVersion: process?.ProcessId == registered.Handle.ProcessId ? process.RuntimeVersion : null);
                        writer.SetArtifactProvenance(id, provenance);
                        var artifactIndex = artifacts.FindIndex(a => a.ArtifactId == id);
                        artifacts[artifactIndex] = artifacts[artifactIndex] with { Provenance = provenance };
                        WriteSnapshot(writer, id, registered.Kind, registered.Artifact);
                        snapshots.Add(id);
                        var views = Array.AsReadOnly(CaptureArtifactCodec
                            .GetSupportedSnapshotViews(registered.Kind, registered.Artifact).ToArray());
                        _bindings.Set(registered.Handle, registered.Artifact,
                            new(writer.Reference.CaptureId, id, views) { Artifact = artifacts[artifactIndex] });
                    }
                    catch (Exception ex) when (IsPersistenceException(ex))
                    {
                        persistenceFailure ??= ex;
                    }
                }

                if (node.Artifacts.Length > 1 || node.Artifacts.Length == 1 && (matching != 1 || hasChildren))
                    persistenceFailure ??= new NotSupportedException(
                        "Each collection needs an explicit child scope; an aggregate cannot own ambiguous child snapshots.");
                if (!hasChildren && node.Artifacts.Length == 0)
                {
                    var data = node.ArtifactId == defaultId ? (object?)result.Data : node.Result;
                    try
                    {
                        if (data is not null)
                        {
                            WriteSnapshot(writer, node.ArtifactId, node.Kind, data);
                            snapshots.Add(node.ArtifactId);
                        }
                        else if (node.Error is null && !node.Cancelled && !result.IsError && !result.Cancelled)
                            throw new NotSupportedException(
                                $"Capture kind '{node.Kind}' returned neither a registered snapshot nor supported typed data.");
                    }
                    catch (Exception ex) when (IsPersistenceException(ex)) { persistenceFailure ??= ex; }
                }
            }

            if (retained.Overflow)
                persistenceFailure ??= new CaptureStoreException(CaptureErrorCode.CapacityExceeded,
                    "Registered artifact count exceeded MaxArtifacts; capture is incomplete.");
            foreach (var node in Enumerable.Reverse(retained.Nodes))
            {
                if (!retained.Nodes.Any(child => child.ParentArtifactId == node.ArtifactId)) continue;
                var descendants = Descendants(retained.Nodes, node.ArtifactId);
                var composition = new DurableCaptureComposition(descendants.Select(child => new DurableCaptureChild(
                    child.ArtifactId, child.Kind, child.Name, child.ParentArtifactId!, child.Offered, child.Accepted,
                    child.SourceRejected, child.Sources, child.SourceReportsRejected, child.Error, child.Cancelled,
                    snapshots.Contains(child.ArtifactId))).ToArray());
                try
                {
                    writer.SetSnapshot(node.ArtifactId, DurableCaptureCompositionCodec.SnapshotVersion,
                        DurableCaptureCompositionCodec.Encode(node.Kind, composition, _options.MaxSnapshotBytes));
                    snapshots.Add(node.ArtifactId);
                }
                catch (Exception ex) when (IsPersistenceException(ex)) { persistenceFailure ??= ex; }
            }
            if (retained.Nodes.Any(node => node.Error is not null || node.Cancelled))
                result = result with { Error = result.Error ?? new DiagnosticError("CaptureChildIncomplete",
                    "One or more capture children failed or were cancelled; explicit recovery is required.") };

            if (persistenceFailure is null && !result.IsError && !result.Cancelled &&
                !cancellationToken.IsCancellationRequested)
            {
                // Once collection is over, finish admitted persistence independently of request cancellation.
                stopped = true;
                info = await writer.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsPersistenceException(ex))
        {
            persistenceFailure ??= ex;
        }
        finally
        {
            if (!stopped)
            {
                try { await writer.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) when (IsPersistenceException(ex)) { persistenceFailure ??= ex; }
            }
        }

        result ??= DiagnosticResult.Fail<T>("Capture could not be persisted.",
            new("CapturePersistenceFailed", persistenceFailure?.Message ?? "Capture initialization failed."));
        if (cancellationToken.IsCancellationRequested) result = result with { Cancelled = true };
        if (persistenceFailure is not null)
        {
            var failure = new DiagnosticError("CapturePersistenceFailed", persistenceFailure.Message);
            result = result with
            {
                Error = result.Error ?? failure,
                Hints = [.. result.Hints, new("capture_describe",
                    "Persistence was incomplete: " + failure.Message)],
            };
        }

        if (info is null)
        {
            try { info = await DescribeAsync(writer.Reference.CaptureId, access, CancellationToken.None).ConfigureAwait(false); }
            catch (CaptureStoreException)
            {
                // Even a broken manifest must not hide the identity of the package that was created.
                info = new(writer.Reference.CaptureId, access.OwnerId, name, null, created,
                    CaptureState.Interrupted, artifacts.AsReadOnly(),
                    writer.GetMetrics().Quality with { Interrupted = true, UnknownTail = true });
            }
        }
        return result with { Capture = info };
    }

    public Task<CaptureCatalogPage> ListAsync(CaptureAccess access, int pageSize = 100,
        string? afterCaptureId = null, CancellationToken cancellationToken = default)
        => _store.ListAsync(access, pageSize, afterCaptureId, cancellationToken);

    /// <summary>Reads metadata, including interrupted captures, without creating a diagnostic handle.</summary>
    public async Task<CaptureInfo> DescribeAsync(string captureId, CaptureAccess access,
        CancellationToken cancellationToken = default)
    {
        CapturePackage.ValidateId(captureId);
        string? after = null;
        do
        {
            var page = await _store.ListAsync(access, _options.MaxCatalogPageSize, after, cancellationToken).ConfigureAwait(false);
            var found = page.Captures.FirstOrDefault(c => c.CaptureId == captureId);
            if (found is not null) return found;
            after = page.NextAfterCaptureId;
        } while (after is not null);
        throw new CaptureStoreException(CaptureErrorCode.NotFound, "Capture is unavailable to this caller.");
    }

    public async Task<DurableCaptureOpenResult> OpenAsync(string captureId, string artifactId,
        CaptureAccess access, CancellationToken cancellationToken = default)
    {
        using var reader = await _store.OpenAsync(captureId, access, cancellationToken).ConfigureAwait(false);
        var artifact = reader.Info.Artifacts.FirstOrDefault(a => a.ArtifactId == artifactId)
            ?? throw new CaptureStoreException(CaptureErrorCode.NotFound, "Capture artifact was not found.");
        var snapshot = reader.ReadSnapshot(artifactId)
            ?? throw new CaptureStoreException(CaptureErrorCode.UnsupportedFormat, "Artifact has no supported compatibility snapshot.");
        object decoded;
        IReadOnlyList<string> views;
        DurableCaptureComposition? composition = null;
        try
        {
            if (snapshot.Version == DurableCaptureCompositionCodec.SnapshotVersion)
            {
                composition = DurableCaptureCompositionCodec.Decode(artifact.Kind, artifactId, snapshot, reader.Info, _options);
                decoded = composition;
                views = Array.Empty<string>();
            }
            else
            {
                decoded = CaptureArtifactCodec.Decode(artifact.Kind, snapshot.Version, snapshot.Utf8Json.Span, _options.MaxSnapshotBytes);
                views = Array.AsReadOnly(CaptureArtifactCodec.GetSupportedSnapshotViews(artifact.Kind, decoded).ToArray());
            }
        }
        catch (Exception ex) when (IsPersistenceException(ex))
        {
            throw new CaptureStoreException(CaptureErrorCode.UnsupportedFormat,
                "Capture snapshot cannot be safely reopened: " + ex.Message, ex);
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Stored paths and PIDs are provenance, never an instruction to reattach to a process or file.
        var handle = _handles.RegisterWithMetadata(artifact.Provenance?.ProcessId ?? 0,
            composition is null ? artifact.Kind : DurableCaptureCompositionCodec.HandleKind,
            decoded, TimeSpan.FromMinutes(30),
            evictWhenProcessExits: false, origin: HandleOrigin.Imported,
            producingTool: artifact.Provenance?.ProducingTool);
        var binding = new DurableCaptureHandleBinding(captureId, artifactId, views) { Artifact = artifact };
        _bindings.Set(handle, decoded, binding);
        return new(handle, views, reader.Info, artifact) { Composition = composition };
    }

    public async Task<CaptureRecordPage> QueryRecordsAsync(string captureId, CaptureRecordQuery query,
        CaptureAccess access, CancellationToken cancellationToken = default)
    {
        using var reader = await _store.OpenAsync(captureId, access, cancellationToken).ConfigureAwait(false);
        return reader.Query(query);
    }

    public Task DeleteAsync(string captureId, CaptureAccess access, CancellationToken cancellationToken = default)
        => _store.DeleteAsync(captureId, access, cancellationToken);

    public Task<CaptureInfo> RecoverAsync(string captureId, CaptureAccess access, CancellationToken cancellationToken = default)
        => _store.RecoverAsync(captureId, access, cancellationToken);

    public DurableCaptureHandleBinding? LookupBinding(string handle)
        => _bindings.Lookup(handle);

    /// <summary>Revalidates ownership and deletion on every query; materialization does not grant access.</summary>
    public async Task<DurableCaptureHandleBinding> AuthorizeHandleAsync(string handle, CaptureAccess access,
        CancellationToken cancellationToken = default)
    {
        var binding = LookupBinding(handle)
            ?? throw new CaptureStoreException(CaptureErrorCode.NotFound, "Durable handle is unknown or has expired.");
        if (binding.Artifact is null)
            throw new CaptureStoreException(CaptureErrorCode.Incomplete,
                "Durable handle has no successfully persisted artifact binding.");
        using var reader = await _store.OpenAsync(binding.CaptureId, access, cancellationToken).ConfigureAwait(false);
        if (!reader.Info.Artifacts.Any(a => a.ArtifactId == binding.ArtifactId))
            throw new CaptureStoreException(CaptureErrorCode.NotFound, "Capture artifact is no longer available.");
        return binding;
    }

    /// <summary>Must precede existing dispatchers: restored snapshots cannot request live-dependent views.</summary>
    public async Task<DurableCaptureHandleBinding> AuthorizeViewAsync(string handle, string view,
        CaptureAccess access, CancellationToken cancellationToken = default)
    {
        var binding = await AuthorizeHandleAsync(handle, access, cancellationToken).ConfigureAwait(false);
        if (!binding.SupportedViews.Contains(view, StringComparer.Ordinal))
            throw new CaptureStoreException(CaptureErrorCode.Forbidden,
                "The requested view is not an allowlisted offline snapshot view.");
        return binding;
    }

    private void WriteSnapshot(CaptureWriter writer, string id, string kind, object artifact)
        => writer.SetSnapshot(id, CaptureArtifactCodec.FormatVersion,
            CaptureArtifactCodec.Encode(kind, artifact, _options.MaxSnapshotBytes));

    private static bool IsPersistenceException(Exception ex)
        => ex is CaptureStoreException or IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            NotSupportedException or ArgumentException or InvalidOperationException or FormatException or OverflowException;

    private static CaptureRecordingNode[] Descendants(CaptureRecordingNode[] nodes, string parent)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { parent };
        var result = new List<CaptureRecordingNode>();
        foreach (var node in nodes)
            if (node.ParentArtifactId is { } id && ids.Contains(id))
            {
                ids.Add(node.ArtifactId);
                result.Add(node);
            }
        return result.ToArray();
    }
}
