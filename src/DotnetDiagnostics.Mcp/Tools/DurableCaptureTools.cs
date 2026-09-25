using System.Text.Json;
using System.Text;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Security;

namespace DotnetDiagnostics.Mcp.Tools;

/// <summary>Host authorization and bounded presentation for the existing tools' durable branches.</summary>
public sealed class DurableCaptureTools(SqliteCaptureStore store, DurableCaptureUseCases captures)
{
    private readonly DurableCaptureUseCases _captures = captures;
    internal const int MaximumResponseBytes = 1024 * 1024;
    internal const string ResourceDenial =
        "{\"error\":\"Durable handles require current authorization and bounded query_snapshot projections; raw Resources are unavailable.\"}";
    private static readonly JsonSerializerOptions BudgetOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    internal static async Task<DiagnosticResult<T>> CollectAsync<T>(
        DurableCaptureTools? service, IPrincipalAccessor principalAccessor, bool persist,
        string tool, string kind, Func<CancellationToken, Task<DiagnosticResult<T>>> collect,
        CancellationToken cancellationToken)
    {
        if (!persist)
            return await collect(cancellationToken).ConfigureAwait(false);
        if (!TryAccess(principalAccessor, out var access))
            return Forbidden<T>("A current authenticated principal is required for persistence.");
        if (service is null)
            return Unavailable<T>();
        if (string.IsNullOrWhiteSpace(kind))
            return DiagnosticResult.Fail<T>("A collection kind is required.",
                new DiagnosticError("InvalidArgument", "A collection kind is required.", "kind"));
        try
        {
            var result = await service._captures.CaptureAsync(
                tool, kind, access!, collect, cancellationToken).ConfigureAwait(false);
            return result with
            {
                Error = result.Error is { Kind: "CapturePersistenceFailed" } persistenceError
                    ? new DiagnosticError("CaptureStoreError", persistenceError.Message, persistenceError.Kind)
                    : result.Error,
                Hints = result.Hints.Select(hint => hint.NextTool == "capture_describe"
                    ? new NextActionHint("get_bytes", hint.Reason, new Dictionary<string, object?>
                    {
                        ["kind"] = "captures", ["captureAction"] = "describe",
                        ["captureId"] = result.Capture?.CaptureId,
                    })
                    : hint).ToArray(),
            };
        }
        catch (CaptureStoreException exception)
        {
            return StoreFailure<T>(exception);
        }
    }

    internal async Task<DiagnosticResult<object>> LifecycleAsync(
        IPrincipalAccessor principalAccessor, string action, string? captureId,
        int pageSize, string? afterCaptureId, CancellationToken cancellationToken)
    {
        if (!TryAccess(principalAccessor, out var access))
            return Forbidden<object>("A current authenticated principal is required.");
        var principal = principalAccessor.Current!;
        if (!principal.HasExplicitScope("module-bytes-read") || !principal.HasScope("investigation-export"))
            return Forbidden<object>("Capture lifecycle requires module-bytes-read and investigation-export.");

        if (string.IsNullOrWhiteSpace(action))
            return Invalid("captureAction must be list, describe, delete, or recover.");
        action = action.Trim().ToLowerInvariant();
        if (action is not ("list" or "describe" or "delete" or "recover"))
            return Invalid("captureAction must be list, describe, delete, or recover.");
        if (action == "list" && captureId is not null)
            return Invalid("captureId cannot be combined with captureAction='list'.");
        if (action != "list" && string.IsNullOrWhiteSpace(captureId))
            return Invalid("captureId is required for describe, delete, and recover.");
        if (action == "delete" && !principal.HasExplicitScope("delete-artifact"))
            return Forbidden<object>("Capture deletion requires the explicit delete-artifact scope.");

        try
        {
            if (action == "list")
            {
                var page = await store.ListAsync(access!, pageSize, afterCaptureId, cancellationToken).ConfigureAwait(false);
                return Bound(DiagnosticResult.Ok<object>(page, $"{page.Captures.Count} durable capture(s)."));
            }
            if (action == "delete")
            {
                await store.DeleteAsync(captureId!, access!, cancellationToken).ConfigureAwait(false);
                return DiagnosticResult.Ok<object>(new { CaptureId = captureId, Deleted = true }, "Durable capture deleted.");
            }
            if (action == "recover")
            {
                var recovered = await store.RecoverAsync(captureId!, access!, cancellationToken).ConfigureAwait(false);
                return Bound(DiagnosticResult.Ok<object>(recovered,
                    "Recovery created a derived capture; the source was not modified.") with { Capture = recovered });
            }
            var info = await _captures.DescribeAsync(captureId!, access!, cancellationToken).ConfigureAwait(false);
            return Bound(DiagnosticResult.Ok<object>(info, "Durable capture metadata.") with { Capture = info });
        }
        catch (CaptureStoreException exception)
        {
            return StoreFailure<object>(exception);
        }
    }

    internal async Task<DiagnosticResult<object>> QueryRecordsAsync(
        IPrincipalAccessor principalAccessor, string captureId, string artifactId,
        DateTimeOffset? from, DateTimeOffset? to, int? threadId, string? category, string? name,
        long afterRecordId, int pageSize, CancellationToken cancellationToken)
    {
        if (!TryAccess(principalAccessor, out var access))
            return Forbidden<object>("A current authenticated principal is required.");
        try
        {
            using var reader = await store.OpenAsync(captureId, access!, cancellationToken).ConfigureAwait(false);
            var artifact = reader.Info.Artifacts.SingleOrDefault(item => item.ArtifactId == artifactId);
            if (artifact is null)
                return Invalid("artifactId does not identify an artifact in this capture.");
            var denial = AuthorizeArtifact(principalAccessor.Current!, artifact, "records");
            if (denial is not null)
                return denial;
            var query = new CaptureRecordQuery(
                artifactId, from, to, threadId, category, name, afterRecordId, pageSize);
            var page = reader.Query(query);
            if (page.Records.Count == 0 &&
                reader.Query(new CaptureRecordQuery(artifactId, PageSize: 1)).Records.Count == 0)
                return DiagnosticResult.Fail<object>(
                    "No retained normalized records are available for this artifact; use a supported snapshot view.",
                    new DiagnosticError("CaptureStoreError",
                        "Record coverage is not established by an empty retained stream; this does not establish that the source emitted zero events.",
                        "RecordsUnavailable")) with { Capture = reader.Info };
            while (true)
            {
                var result = DiagnosticResult.Ok<object>(page, "Retained normalized records; see capture quality for loss and coverage.")
                    with { Capture = reader.Info };
                if (Fits(result))
                    return result;
                if (page.Records.Count <= 1)
                    return TooLarge();
                query = query with { PageSize = page.Records.Count / 2 };
                page = reader.Query(query);
            }
        }
        catch (CaptureStoreException exception)
        {
            return StoreFailure<object>(exception);
        }
    }

    internal async Task<(string? Handle, DiagnosticResult<object>? Error)> PrepareAsync(
        IPrincipalAccessor principalAccessor, string captureId, string artifactId,
        string? view, CancellationToken cancellationToken)
    {
        if (!TryAccess(principalAccessor, out var access))
            return (null, Forbidden<object>("A current authenticated principal is required."));
        try
        {
            var info = await _captures.DescribeAsync(captureId, access!, cancellationToken).ConfigureAwait(false);
            var artifact = info.Artifacts.SingleOrDefault(item => item.ArtifactId == artifactId);
            if (artifact is null)
                return (null, Invalid("artifactId does not identify an artifact in this capture."));
            var denial = AuthorizeArtifact(principalAccessor.Current!, artifact, view);
            if (denial is not null)
                return (null, denial);
            var opened = await _captures.OpenAsync(captureId, artifactId, access!, cancellationToken).ConfigureAwait(false);
            await _captures.AuthorizeViewAsync(opened.Handle.Id, CanonicalView(
                    opened.SupportedViews, EffectiveView(artifact.Kind, view)),
                access!, cancellationToken).ConfigureAwait(false);
            return (opened.Handle.Id, null);
        }
        catch (CaptureStoreException exception)
        {
            return (null, StoreFailure<object>(exception));
        }
    }

    internal bool IsDurableHandle(string handle) => _captures.LookupBinding(handle) is not null;

    internal async Task<DiagnosticResult<object>> DecorateQueryAsync(
        DiagnosticResult<object> result, IPrincipalAccessor principalAccessor,
        string handle, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var binding = _captures.LookupBinding(handle);
        if (binding is null)
            return result;
        if (!TryAccess(principalAccessor, out var access))
            return Forbidden<object>("A current authenticated principal is required.");
        try
        {
            var info = await _captures.DescribeAsync(binding.CaptureId, access!, cancellationToken).ConfigureAwait(false);
            return Bound(result with
            {
                Handle = handle,
                HandleExpiresAt = expiresAt,
                Capture = info,
                Hints = [.. result.Hints, new NextActionHint("query_snapshot",
                    "Historical snapshot-only views: " + string.Join(", ", binding.SupportedViews),
                    new Dictionary<string, object?>
                    {
                        ["captureId"] = binding.CaptureId, ["artifactId"] = binding.ArtifactId,
                    })],
            });
        }
        catch (CaptureStoreException exception)
        {
            return StoreFailure<object>(exception);
        }
    }

    internal async Task<DiagnosticResult<object>?> ValidateHandleAsync(
        IPrincipalAccessor principalAccessor, string handle, string? view,
        CancellationToken cancellationToken)
    {
        var binding = _captures.LookupBinding(handle);
        if (binding is null)
            return null;
        if (!TryAccess(principalAccessor, out var access))
            return Forbidden<object>("A current authenticated principal is required.");
        try
        {
            await _captures.AuthorizeHandleAsync(handle, access!, cancellationToken).ConfigureAwait(false);
            var info = await _captures.DescribeAsync(binding.CaptureId, access!, cancellationToken).ConfigureAwait(false);
            var artifact = info.Artifacts.Single(item => item.ArtifactId == binding.ArtifactId);
            var denial = AuthorizeArtifact(principalAccessor.Current!, artifact, view);
            if (denial is not null)
                return denial;
            await _captures.AuthorizeViewAsync(handle, CanonicalView(
                    binding.SupportedViews, EffectiveView(artifact.Kind, view)),
                access!, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (CaptureStoreException exception)
        {
            return StoreFailure<object>(exception);
        }
    }

    private static string EffectiveView(string kind, string? view)
        => !string.IsNullOrWhiteSpace(view) ? view.Trim() : kind switch
        {
            "heap-snapshot" => "top-types",
            "thread-snapshot" => "top-blocked",
            "off-cpu-snapshot" => "topStacks",
            "cpu-sample" or "allocation-sample" or "native-alloc-sample" or "native-lock-contention-sample" => "call-tree",
            _ => "summary",
        };

    private static string CanonicalView(IReadOnlyList<string> supported, string view)
        => supported.FirstOrDefault(item => string.Equals(item, view, StringComparison.OrdinalIgnoreCase)) ?? view;

    internal static DiagnosticResult<object>? AuthorizeArtifact(
        BearerPrincipal principal, CaptureArtifactInfo artifact, string? view)
    {
        if (!QuerySnapshotTool.RegisteredKinds.Contains(artifact.Kind) &&
            artifact.Kind is not ("cpu-efficiency-sample" or "requests-now"))
            return Forbidden<object>("This artifact kind has no reviewed durable read authorization policy.");
        if (artifact.Kind == "requests-now")
            return principal.HasScope("ptrace") ? null : Forbidden<object>("Request snapshots require ptrace.");
        if (!QuerySnapshotTool.AuthorizeKind(principal, artifact.Kind, view, out var failure))
            return failure;
        if (artifact.Kind is "cpu-sample" or "allocation-sample" or "native-alloc-sample" or "native-lock-contention-sample" &&
            !principal.HasScope("eventpipe"))
            return Forbidden<object>("Durable sample reads also require the producer's eventpipe scope.");
        if (artifact.Kind == "heap-snapshot" && !principal.HasScope("ptrace"))
            return Forbidden<object>("Durable heap reads conservatively require heap-read and ptrace; stored origin does not reduce current authority.");
        if (artifact.Provenance?.ProducingTool == "collect_sample" && !principal.HasScope("eventpipe"))
            return Forbidden<object>("The stored sample producer requires current eventpipe authority.");
        if (artifact.Provenance?.ProducingTool == "collect_thread_snapshot" && !principal.HasScope("ptrace"))
            return Forbidden<object>("The stored thread producer requires current ptrace authority.");
        if (artifact.Provenance?.ProducingTool == "inspect_heap" &&
            (!principal.HasScope("heap-read") || !principal.HasScope("ptrace")))
            return Forbidden<object>("The stored heap producer requires current heap-read and ptrace authority.");
        if (string.Equals(view?.Trim(), "records", StringComparison.OrdinalIgnoreCase))
        {
            if (artifact.Kind == "heap-snapshot" && !principal.HasExplicitScope("sensitive-heap-read"))
                return Forbidden<object>("Heap records require explicit sensitive-heap-read.");
            if (artifact.Kind == "event-source" && !principal.HasExplicitScope("eventsource-any"))
                return Forbidden<object>("Generic EventSource records require explicit eventsource-any.");
        }
        return null;
    }

    private static bool TryAccess(IPrincipalAccessor accessor, out CaptureAccess? access)
    {
        var principal = accessor.Current;
        access = principal is null ? null : new CaptureAccess(
            principal.OwnershipKey, principal.HasScope(BearerPrincipal.RootScope));
        return access is not null;
    }

    internal static DiagnosticResult<T> Unavailable<T>()
        => DiagnosticResult.Fail<T>("Durable capture services are unavailable.",
            new DiagnosticError("CaptureStoreError", "Durable capture services are unavailable.", "Unavailable"));

    internal static DiagnosticResult<T> StoreFailure<T>(CaptureStoreException exception)
        => DiagnosticResult.Fail<T>("Durable capture operation failed.",
            new DiagnosticError("CaptureStoreError", exception.Message, exception.Code.ToString()),
            new NextActionHint("get_bytes",
                "Inspect the capture with kind='captures', captureAction='describe'. Interrupted evidence requires explicit captureAction='recover'; recovery creates a derived package, never runs automatically."));

    private static DiagnosticResult<T> Forbidden<T>(string message)
        => DiagnosticResult.Fail<T>(message, new DiagnosticError("InsufficientScope", message));

    private static DiagnosticResult<object> Invalid(string message)
        => DiagnosticResult.Fail<object>(message, new DiagnosticError("InvalidArgument", message));

    internal static DiagnosticResult<object> Bound(DiagnosticResult<object> result)
        => Fits(result) ? result : TooLarge();

    private static bool Fits(DiagnosticResult<object> result)
    {
        var structured = JsonSerializer.SerializeToUtf8Bytes(result, BudgetOptions);
        var text = JsonSerializer.SerializeToUtf8Bytes(Encoding.UTF8.GetString(structured));
        return (long)structured.Length + text.Length <= MaximumResponseBytes - 4096;
    }

    private static DiagnosticResult<object> TooLarge()
        => DiagnosticResult.Fail<object>("Durable response exceeds the wire budget; narrow the query or page size.",
            new DiagnosticError("CaptureStoreError", "The indented structured/text response plus wrapper allowance exceeds 1 MiB.", "CapacityExceeded"));
}
