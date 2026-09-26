using System.Security.Cryptography;
using System.Collections;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Mcp.Hosting;
using DotnetDiagnostics.Mcp.Security;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Tools;

/// <summary>Owner/session-bound MCP byte transfers; Core owns all staged files and operation reservations.</summary>
public sealed class PortableCaptureTools(
    SqliteCaptureStore store, PortableTransferOptions options, TimeProvider clock,
    IHttpContextAccessor http) : BackgroundService
{
    internal const int ChunkBytes = 24 * 1024;
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _sync = new();
    private readonly Dictionary<string, Transfer> _transfers = new(StringComparer.Ordinal);
    private readonly string _localSession = Guid.NewGuid().ToString("N");
    private long _rateWindow;
    private int _rateCalls;
    private readonly PortableCaptureUseCases _control = new(store, static (_, _) => ValueTask.CompletedTask, timeProvider: clock);

    internal async Task<DiagnosticResult<object>> InvokeAsync(IPrincipalAccessor accessor, McpServer? server,
        string action, JsonElement? input, CancellationToken cancellationToken)
    {
        try
        {
            BearerPrincipal principal;
            try { principal = Require(accessor.Current); }
            catch (CaptureStoreException)
            {
                if (accessor.Current is { } revoked) CancelOwner(revoked.OwnershipKey);
                throw;
            }
            RateLimit();
            if (input is not { ValueKind: JsonValueKind.Object } arguments)
                throw Error(CaptureErrorCode.InvalidInput, "captureTransfer must be an object.");
            var refresh = StdioRootPrincipalAccessor.IsCurrent(accessor)
                ? new Func<CancellationToken, ValueTask<BearerPrincipal?>>(token =>
                {
                    token.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(accessor.Current);
                })
                : PortablePrincipalRefresh.Get(http.HttpContext);
            if (refresh is null) throw Error(CaptureErrorCode.Forbidden, "Current policy cannot be refreshed.");
            var session = StdioRootPrincipalAccessor.IsCurrent(accessor) ? _localSession : server?.SessionId;
            await SweepAsync().ConfigureAwait(false);
            if (action is "export-start" or "import-start")
            {
                if (string.IsNullOrWhiteSpace(session)) throw Error(CaptureErrorCode.InvalidInput, "SessionRequired");
                return Bound(await StartAsync(principal, session, refresh, action, arguments).ConfigureAwait(false));
            }
            if (action == "import-result" ||
                action == "transfer-status" && !arguments.TryGetProperty("transferId", out _))
            {
                Fields(arguments, action == "import-result"
                    ? ["operationId", "requestedUtc", "afterEntry", "pageSize"]
                    : ["operationId", "requestedUtc"]);
                var key = Operation(arguments);
                Transfer? active;
                lock (_sync) active = _transfers.Values.FirstOrDefault(item =>
                    item.Owner == principal.OwnershipKey && item.Operation == key && !item.Released);
                if (active is not null)
                {
                    active.Refresh = refresh;
                    if (action == "transfer-status") return Bound(Status(active));
                    throw Error(CaptureErrorCode.Busy, "Import is still active.");
                }
                var service = Service(refresh, principal.OwnershipKey);
                var result = await service.GetImportResultAsync(key, Access(principal), cancellationToken).ConfigureAwait(false);
                await AuthorizeResultAsync(result, refresh, principal.OwnershipKey, cancellationToken).ConfigureAwait(false);
                if (action == "transfer-status")
                    return Bound(new { operationId = key.Id, state = result.Complete ? "Completed" : result.Cancelled ? "Cancelled" : "Failed",
                        result.BundleId, result.Complete, result.Cancelled, result.Failure, entryCount = result.Entries.Count });
                var after = Integer(arguments, "afterEntry", -1);
                if (Integer(arguments, "pageSize", 1) != 1 || after < -1 || after > 15)
                    throw Error(CaptureErrorCode.InvalidInput, "Result pages contain at most one entry.");
                var page = result.Entries.Skip(checked((int)after + 1)).Take(1).ToArray();
                return Bound(new { result.OperationId, result.BundleId, result.ArchiveSha256, result.Complete,
                    result.Cancelled, result.Failure, entries = page,
                    nextAfterEntry = after + 2 < result.Entries.Count ? (long?)(after + 1) : null });
            }
            var transferId = Id(arguments, "transferId");
            Transfer transfer;
            lock (_sync)
            {
                if (!_transfers.TryGetValue(transferId, out transfer!) ||
                    transfer.Owner != principal.OwnershipKey || transfer.Session != session)
                    throw Error(CaptureErrorCode.Forbidden, "Transfer is unavailable to this session.");
                transfer.Refresh = refresh;
            }
            if (!transfer.Gate.Wait(0, cancellationToken)) throw Error(CaptureErrorCode.Busy, "Transfer call is already active.");
            try
            {
                await CurrentAsync(transfer.Refresh, transfer.Owner, cancellationToken).ConfigureAwait(false);
                if (action == "transfer-status")
                {
                    Fields(arguments, ["transferId"]);
                    return Bound(Status(transfer));
                }
                if (action == "transfer-cancel")
                {
                    Fields(arguments, ["transferId"]);
                    transfer.Lifetime.Cancel();
                    if (!Terminal(transfer.State)) transfer.State = "Cancelled";
                    transfer.TerminalUtc ??= clock.GetUtcNow();
                    _ = CleanupAsync(transfer);
                    return Bound(Status(transfer));
                }
                if (action == "import-commit" && transfer.Upload is not null && Terminal(transfer.State))
                {
                    Fields(arguments, ["transferId"]);
                    return Bound(Status(transfer));
                }
                if (Terminal(transfer.State) || transfer.Released)
                    throw Error(CaptureErrorCode.InvalidInput, "TransferExpired");
                if (action == "import-commit")
                {
                    Fields(arguments, ["transferId"]);
                    if (transfer.Upload is null) throw Error(CaptureErrorCode.InvalidInput, "WrongDirection");
                    if (transfer.State is "Validating" or "Publishing") return Bound(Status(transfer));
                    if (transfer.Lease?.ReceivedBytes != transfer.Upload.ArchiveBytes)
                        throw Error(CaptureErrorCode.Incomplete, "UploadIncomplete");
                    transfer.State = "Validating";
                    transfer.Work = Task.Run(() => CommitAsync(transfer), CancellationToken.None);
                    return Bound(Status(transfer));
                }
                if (action is not ("download-chunk" or "upload-chunk"))
                    throw Error(CaptureErrorCode.InvalidInput, "Unknown transfer action.");
                if (transfer.ChunkCalls >= 65_536)
                {
                    transfer.Lifetime.Cancel();
                    transfer.State = "Failed";
                    transfer.TerminalUtc = clock.GetUtcNow();
                    _ = CleanupAsync(transfer);
                    throw Error(CaptureErrorCode.CapacityExceeded, "ChunkCalls");
                }
                transfer.ChunkCalls++;
                if (action == "download-chunk")
                {
                    Fields(arguments, ["transferId", "offset", "count"]);
                    if (transfer.State != "Ready" || transfer.Lease?.Export is not { } export)
                        throw Error(CaptureErrorCode.Busy, "ExportNotReady");
                    var offset = Integer(arguments, "offset");
                    if (Integer(arguments, "count") != ChunkBytes || offset < 0 || offset > export.ArchiveBytes ||
                        offset != export.ArchiveBytes && offset % ChunkBytes != 0)
                        throw Error(CaptureErrorCode.InvalidInput, "InvalidChunkRange");
                    var bytes = new byte[(int)Math.Min(ChunkBytes, export.ArchiveBytes - offset)];
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transfer.Lifetime.Token);
                    var count = await transfer.Lease.ReadAsync(offset, bytes, linked.Token).ConfigureAwait(false);
                    if (count > 0 && !transfer.DownloadedOffsets[checked((int)(offset / ChunkBytes))])
                    {
                        transfer.DownloadedOffsets[checked((int)(offset / ChunkBytes))] = true;
                        transfer.NextOffset = Math.Max(transfer.NextOffset, offset + count);
                        transfer.ProgressUtc = clock.GetUtcNow();
                    }
                    return Bound(new { offset, count, base64 = Convert.ToBase64String(bytes),
                        sha256 = Hash(bytes), eof = offset + count == export.ArchiveBytes });
                }
                Fields(arguments, ["transferId", "offset", "base64", "sha256"]);
                if (transfer.State != "Receiving" || transfer.Lease is null)
                    throw Error(CaptureErrorCode.InvalidInput, "UploadNotReceiving");
                var position = Integer(arguments, "offset");
                var encoded = Text(arguments, "base64", 32 * 1024);
                var claimedHash = HashText(arguments, "sha256");
                if (encoded.Length == 0 || encoded.Length % 4 != 0 || encoded.Any(char.IsWhiteSpace))
                    throw Error(CaptureErrorCode.InvalidInput, "InvalidBase64");
                var decoded = new byte[encoded.Length / 4 * 3];
                if (!Convert.TryFromBase64String(encoded, decoded, out var written) || written > ChunkBytes)
                    throw Error(CaptureErrorCode.InvalidInput, "InvalidBase64");
                if (Convert.ToBase64String(decoded, 0, written) != encoded || Hash(decoded.AsSpan(0, written)) != claimedHash)
                    throw Error(CaptureErrorCode.CorruptPackage, "ChunkHashMismatch");
                if (position == transfer.LastOffset && transfer.LastBytes is { } previous &&
                    previous.AsSpan().SequenceEqual(decoded.AsSpan(0, written)))
                    return Bound(new { acceptedCount = written, nextOffset = transfer.NextOffset, replay = true });
                if (position != transfer.NextOffset)
                {
                    var error = Error(CaptureErrorCode.InvalidInput, "OffsetMismatch");
                    error.Data["NextOffset"] = transfer.NextOffset;
                    throw error;
                }
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transfer.Lifetime.Token))
                    await transfer.Lease.AppendAsync(position, decoded.AsMemory(0, written), linked.Token).ConfigureAwait(false);
                transfer.LastOffset = position;
                transfer.LastBytes = decoded.AsSpan(0, written).ToArray();
                transfer.NextOffset = transfer.Lease.ReceivedBytes;
                transfer.ProgressUtc = clock.GetUtcNow();
                return Bound(new { acceptedCount = written, nextOffset = transfer.NextOffset, replay = false });
            }
            catch (CaptureStoreException exception) when (exception.Code == CaptureErrorCode.Forbidden)
            {
                transfer.Lifetime.Cancel();
                transfer.State = "Failed";
                transfer.TerminalUtc ??= clock.GetUtcNow();
                _ = CleanupAsync(transfer);
                throw;
            }
            finally { transfer.Gate.Release(); }
        }
        catch (CaptureStoreException exception)
        {
            var failure = Failure(exception);
            return new DiagnosticResult<object>("Portable capture request failed.", [],
                new DiagnosticError(exception.Code == CaptureErrorCode.Forbidden ? "InsufficientScope" : "CaptureStoreError",
                    failure.Reason, exception.Code.ToString()))
            { Data = new { failure, nextOffset = exception.Data["NextOffset"] as long?,
                retryAfterMilliseconds = exception.Code == CaptureErrorCode.Busy ? 1000 : (int?)null } };
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            return DiagnosticResult.Fail<object>("Invalid portable transfer arguments.",
                new DiagnosticError("CaptureStoreError", "Invalid portable transfer arguments.", "InvalidInput"));
        }
    }

    private async Task<object> StartAsync(BearerPrincipal principal, string session,
        Func<CancellationToken, ValueTask<BearerPrincipal?>> refresh, string action, JsonElement input)
    {
        Fields(input, action == "export-start"
            ? ["operationId", "requestedUtc", "entries"] : ["operationId", "requestedUtc", "archiveBytes", "archiveSha256"]);
        var key = Operation(input);
        CaptureExportSelection[]? selections = null;
        CaptureImportRequest? upload = null;
        if (action == "export-start")
        {
            if (!input.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array ||
                entries.GetArrayLength() is < 1 or > 16)
                throw Error(CaptureErrorCode.InvalidInput, "Select one to sixteen captures.");
            selections = entries.EnumerateArray().Select(entry =>
            {
                Fields(entry, ["captureId", "label"]);
                var label = entry.TryGetProperty("label", out var value) && value.ValueKind != JsonValueKind.Null
                    ? Text(entry, "label", 256) : null;
                if (label is not null && (Encoding.UTF8.GetByteCount(label) > 256 || label.Any(char.IsControl)))
                    throw Error(CaptureErrorCode.InvalidInput, "InvalidLabel");
                return new CaptureExportSelection(Id(entry, "captureId"), label);
            }).ToArray();
        }
        else
        {
            if (options.Worker is null) throw Error(CaptureErrorCode.UnsupportedFormat, "ImportWorkerUnavailable");
            options.Worker.Validate();
            var bytes = Integer(input, "archiveBytes");
            if (bytes is <= 0 or > 512L * 1024 * 1024) throw Error(CaptureErrorCode.CapacityExceeded, "MaxArchiveBytes");
            upload = new(key, bytes, HashText(input, "archiveSha256"));
        }
        var fingerprint = Hash(JsonSerializer.SerializeToUtf8Bytes<object>(selections is not null ? selections : upload!));
        Transfer transfer;
        lock (_sync)
        {
            var existing = _transfers.Values.FirstOrDefault(item => item.Owner == principal.OwnershipKey && item.Operation.Id == key.Id);
            if (existing is not null)
            {
                if (existing.Operation != key || existing.Fingerprint != fingerprint || existing.Session != session)
                    throw Error(CaptureErrorCode.InvalidInput, "OperationConflict");
                return Status(existing);
            }
            if (_transfers.Values.Count(item => !item.Released) >= 2 ||
                _transfers.Values.Any(item => !item.Released && item.Owner == principal.OwnershipKey))
                throw Error(CaptureErrorCode.Busy, "Portable transfer slots are occupied.");
            if (_transfers.Count >= 64) throw Error(CaptureErrorCode.CapacityExceeded, "MaxTransfers");
            string id;
            do { id = Guid.NewGuid().ToString("N"); } while (_transfers.ContainsKey(id));
            transfer = new(id, key, principal.OwnershipKey, session, fingerprint, refresh, clock.GetUtcNow(), upload);
            _transfers.Add(id, transfer);
        }
        var service = Service(token => transfer.Refresh(token), transfer.Owner);
        if (selections is not null)
        {
            var request = new CaptureExportRequest(key, selections);
            transfer.Work = Task.Run(async () =>
            {
                try
                {
                    transfer.Lease = await service.PrepareExportAsync(request, Access(principal), transfer.Lifetime.Token).ConfigureAwait(false);
                    transfer.Export = transfer.Lease.Export;
                    if (!transfer.Lifetime.IsCancellationRequested) transfer.State = "Ready";
                }
                catch (Exception exception) { MarkFailure(transfer, exception); }
                finally
                {
                    if (Terminal(transfer.State)) _ = CleanupAsync(transfer);
                }
            }, CancellationToken.None);
        }
        else
        {
            try
            {
                transfer.Lease = service.BeginUpload(upload!, Access(principal), async (entries, phase, _, token) =>
                {
                    if (phase == PortableAuthorizationPhase.Publish) transfer.State = "Publishing";
                    var current = await CurrentAsync(transfer.Refresh, transfer.Owner, token).ConfigureAwait(false);
                    foreach (var entry in entries) AuthorizeWhole(current, entry.Source);
                });
                transfer.Result = transfer.Lease.ExistingResult;
                transfer.State = transfer.Result is null ? "Receiving" : transfer.Result.Complete ? "Completed" : "Failed";
                if (transfer.Result is not null) _ = CleanupAsync(transfer);
            }
            catch (Exception exception) { MarkFailure(transfer, exception); _ = CleanupAsync(transfer); }
        }
        transfer.Gate.Release();
        await Task.CompletedTask.ConfigureAwait(false);
        return Status(transfer);
    }

    private async Task CommitAsync(Transfer transfer)
    {
        try
        {
            await CurrentAsync(transfer.Refresh, transfer.Owner, transfer.Lifetime.Token).ConfigureAwait(false);
            transfer.Result = await transfer.Lease!.CommitAsync(transfer.Lifetime.Token).ConfigureAwait(false);
            if (transfer.State != "Expired")
                transfer.State = transfer.Result.Complete ? "Completed" : transfer.Result.Cancelled ? "Cancelled" : "Failed";
        }
        catch (Exception exception) { MarkFailure(transfer, exception); }
        finally { transfer.TerminalUtc ??= clock.GetUtcNow(); _ = CleanupAsync(transfer); }
    }

    private PortableCaptureUseCases Service(Func<CancellationToken, ValueTask<BearerPrincipal?>> refresh, string owner)
        => new(store, async (capture, token) => AuthorizeWhole(await CurrentAsync(refresh, owner, token).ConfigureAwait(false), capture),
            timeProvider: clock, importWorker: options.Worker);

    private static CaptureAccess Access(BearerPrincipal principal) => new(principal.OwnershipKey, principal.HasScope("root"));
    private static BearerPrincipal Require(BearerPrincipal? principal)
        => principal is not null && principal.HasExplicitScope("module-bytes-read") && principal.HasScope("investigation-export")
            ? principal : throw Error(CaptureErrorCode.Forbidden, "Capture bytes require explicit module-bytes-read and investigation-export.");

    private static async ValueTask<BearerPrincipal> CurrentAsync(
        Func<CancellationToken, ValueTask<BearerPrincipal?>> refresh, string owner, CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var current = Require(await refresh(linked.Token).AsTask().WaitAsync(linked.Token).ConfigureAwait(false));
        if (current.OwnershipKey != owner) throw Error(CaptureErrorCode.Forbidden, "Current owner changed.");
        return current;
    }

    internal static void AuthorizeWhole(BearerPrincipal principal, CaptureInfo capture)
    {
        foreach (var artifact in capture.Artifacts)
        {
            if (artifact.Provenance?.ProducingTool is { } producer &&
                producer is not ("collect_events" or "collect_sample" or "collect_batch" or "collect_thread_snapshot" or "inspect_heap"))
                throw Error(CaptureErrorCode.Forbidden, "Unknown producer policy.");
            if (DurableCaptureTools.AuthorizeArtifact(principal, artifact, artifact.Kind is "batch" or "sweep" ? "summary" : "records") is not null ||
                artifact.Kind == "thread-snapshot" && !principal.HasScope("heap-read"))
                throw Error(CaptureErrorCode.Forbidden, "Whole-capture evidence requires every producer and sensitive-record scope.");
        }
    }

    private async Task AuthorizeResultAsync(PortableImportResult result,
        Func<CancellationToken, ValueTask<BearerPrincipal?>> refresh, string owner, CancellationToken token)
    {
        foreach (var entry in result.Entries)
        {
            if (entry.Mapping is null) continue;
            var current = await CurrentAsync(refresh, owner, token).ConfigureAwait(false);
            using var reader = await store.OpenAsync(entry.Mapping.LocalCaptureId, Access(current), token).ConfigureAwait(false);
            AuthorizeWhole(current, reader.Info);
        }
    }

    private void RateLimit()
    {
        lock (_sync)
        {
            var second = clock.GetUtcNow().ToUnixTimeSeconds();
            if (second != _rateWindow) { _rateWindow = second; _rateCalls = 0; }
            if (_rateCalls >= 100) throw Error(CaptureErrorCode.Busy, "RateLimit");
            _rateCalls++;
        }
        _control.AdmitTransferCall();
    }

    private static object Status(Transfer transfer) => new
    {
        transferId = transfer.Id, operationId = transfer.Operation.Id,
        direction = transfer.Upload is null ? "download" : "upload", state = transfer.State,
        expiresUtc = transfer.CreatedUtc.AddHours(1), idleExpiresUtc = transfer.ProgressUtc.AddMinutes(5),
        maximumChunkBytes = ChunkBytes, nextOffset = transfer.NextOffset,
        archiveBytes = transfer.Upload?.ArchiveBytes ?? transfer.Export?.ArchiveBytes,
        archiveSha256 = transfer.Upload?.ArchiveSha256 ?? transfer.Export?.ArchiveSha256,
        bundleId = transfer.Export?.BundleId ?? transfer.Result?.BundleId,
        failure = transfer.Failure ?? transfer.Result?.Failure,
    };

    private void MarkFailure(Transfer transfer, Exception exception)
    {
        if (transfer.State != "Expired") transfer.State = transfer.Lifetime.IsCancellationRequested ? "Cancelled" : "Failed";
        transfer.TerminalUtc ??= clock.GetUtcNow();
        transfer.Failure = exception is CaptureStoreException known ? Failure(known)
            : new(CaptureErrorCode.StorageFailure, "TransferFailed", null, null, null, null);
    }

    private Task CleanupAsync(Transfer transfer)
    {
        lock (transfer)
        {
            if (transfer.CleanupWork is { IsCompleted: false } running) return running;
            return transfer.CleanupWork = CleanupCoreAsync(transfer);
        }
    }

    private async Task CleanupCoreAsync(Transfer transfer)
    {
        // Yield until a just-created background operation has assigned its Task.
        await Task.Yield();
        await transfer.Work.ConfigureAwait(false);
        await transfer.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (transfer.Released) return;
            if (transfer.Lease is not null) await transfer.Lease.DisposeAsync().ConfigureAwait(false);
            transfer.Lease = null;
            transfer.LastBytes = null;
            transfer.Released = true;
            transfer.TerminalUtc ??= clock.GetUtcNow();
        }
        catch (Exception)
        {
            transfer.Failure = new(CaptureErrorCode.StorageFailure, "CleanupFailed", null, null, null, null);
            transfer.State = "Failed";
        }
        finally { transfer.Gate.Release(); }
    }

    private Task SweepAsync()
    {
        Transfer[] transfers;
        lock (_sync) transfers = _transfers.Values.ToArray();
        foreach (var transfer in transfers)
        {
            var now = clock.GetUtcNow();
            if (!Terminal(transfer.State) && (now >= transfer.CreatedUtc.AddHours(1) ||
                transfer.State is not ("Validating" or "Publishing") && now >= transfer.ProgressUtc.AddMinutes(5)))
            {
                transfer.State = "Expired";
                transfer.TerminalUtc = now;
                transfer.Lifetime.Cancel();
            }
            if (Terminal(transfer.State) && !transfer.Released) _ = CleanupAsync(transfer);
            if (transfer.Released && transfer.TerminalUtc?.AddMinutes(5) <= now)
            {
                lock (_sync) _transfers.Remove(transfer.Id);
                transfer.Lifetime.Dispose();
            }
        }
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await new PortableCaptureUseCases(store, static (_, _) => ValueTask.CompletedTask,
            timeProvider: clock).CleanupExpiredExportsAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await SweepAsync().ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Transfer[] transfers;
        lock (_sync) transfers = _transfers.Values.ToArray();
        foreach (var transfer in transfers) { transfer.Lifetime.Cancel(); transfer.State = "Cancelled"; }
        foreach (var transfer in transfers)
        {
            await transfer.Work.ConfigureAwait(false);
            await CleanupAsync(transfer).ConfigureAwait(false);
        }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool Terminal(string state) => state is "Completed" or "Failed" or "Cancelled" or "Expired";

    internal void CancelSession(string session, BearerPrincipal principal)
    {
        Transfer[] transfers;
        lock (_sync) transfers = _transfers.Values.Where(item => item.Session == session &&
            item.Owner == principal.OwnershipKey && !item.Released).ToArray();
        foreach (var transfer in transfers)
        {
            transfer.Lifetime.Cancel();
            if (!Terminal(transfer.State)) transfer.State = "Cancelled";
            transfer.TerminalUtc ??= clock.GetUtcNow();
            _ = CleanupAsync(transfer);
        }

    }

    private void CancelOwner(string owner)
    {
        Transfer[] transfers;
        lock (_sync) transfers = _transfers.Values.Where(item => item.Owner == owner && !item.Released).ToArray();
        foreach (var transfer in transfers)
        {
            transfer.Lifetime.Cancel();
            transfer.State = "Failed";
            transfer.TerminalUtc ??= clock.GetUtcNow();
            _ = CleanupAsync(transfer);
        }
    }
    private static CaptureStoreException Error(CaptureErrorCode code, string reason) => new(code, reason);
    private static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static PortableFailure Failure(CaptureStoreException exception)
    {
        var reason = exception.Message.Split(':', 2)[0];
        if (reason.Length > 128 || reason.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-')))
            reason = exception.Code.ToString();
        return new(exception.Code, reason, null, exception.Data["PortableLimit"] as string,
            exception.Data["PortableObserved"] as long?, exception.Data["PortableMaximum"] as long?);
    }

    private static DiagnosticResult<object> Bound(object data)
    {
        var result = DiagnosticResult.Ok<object>(data, "Portable capture transfer.");
        var structured = JsonSerializer.SerializeToUtf8Bytes(result, Wire);
        var text = JsonSerializer.SerializeToUtf8Bytes(Encoding.UTF8.GetString(structured));
        return (long)structured.Length + text.Length <= 128 * 1024 - 8192 ? result :
            DiagnosticResult.Fail<object>("Portable response exceeds its wire budget.",
                new DiagnosticError("CaptureStoreError", "Portable response exceeds its wire budget.", "CapacityExceeded"));
    }

    private static void Fields(JsonElement input, string[] allowed)
    {
        if (input.ValueKind != JsonValueKind.Object) throw Error(CaptureErrorCode.InvalidInput, "Object required.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in input.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw Error(CaptureErrorCode.InvalidInput, "Unknown or duplicate transfer field.");
    }

    private static string Text(JsonElement input, string name, int maximum)
    {
        if (!input.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } text || text.Length > maximum)
            throw Error(CaptureErrorCode.InvalidInput, "Invalid transfer text field.");
        return text;
    }

    private static string Id(JsonElement input, string name)
    {
        var value = Text(input, name, 32);
        if (value.Length != 32 || value.Any(character => !char.IsAsciiHexDigit(character) || char.IsUpper(character)))
            throw Error(CaptureErrorCode.InvalidInput, "Invalid opaque ID.");
        return value;
    }

    private static string HashText(JsonElement input, string name)
    {
        var value = Text(input, name, 64);
        if (value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character) || char.IsUpper(character)))
            throw Error(CaptureErrorCode.InvalidInput, "Invalid SHA-256.");
        return value;
    }

    private static long Integer(JsonElement input, string name, long? fallback = null)
        => !input.TryGetProperty(name, out var value) && fallback is { } number ? number
            : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result) ? result
            : throw Error(CaptureErrorCode.InvalidInput, "Integer required.");

    private PortableOperationKey Operation(JsonElement input)
    {
        if (!input.TryGetProperty("requestedUtc", out var value) || !value.TryGetDateTimeOffset(out var utc) || utc.Offset != TimeSpan.Zero)
            throw Error(CaptureErrorCode.InvalidInput, "UTC operation time required.");
        if (utc > clock.GetUtcNow().AddMinutes(5) || utc <= clock.GetUtcNow().AddHours(-24))
            throw Error(CaptureErrorCode.InvalidInput, "OperationExpired");
        return new(Id(input, "operationId"), utc);
    }

    private sealed class Transfer(string id, PortableOperationKey operation, string owner, string session,
        string fingerprint, Func<CancellationToken, ValueTask<BearerPrincipal?>> refresh,
        DateTimeOffset created, CaptureImportRequest? upload)
    {
        internal string Id { get; } = id;
        internal PortableOperationKey Operation { get; } = operation;
        internal string Owner { get; } = owner;
        internal string Session { get; } = session;
        internal string Fingerprint { get; } = fingerprint;
        internal Func<CancellationToken, ValueTask<BearerPrincipal?>> Refresh = refresh;
        internal CaptureImportRequest? Upload { get; } = upload;
        internal DateTimeOffset CreatedUtc { get; } = created;
        internal DateTimeOffset ProgressUtc = created;
        internal DateTimeOffset? TerminalUtc;
        internal CancellationTokenSource Lifetime { get; } = new();
        internal SemaphoreSlim Gate { get; } = new(0, 1);
        internal PortableCaptureTransfer? Lease;
        internal PortableImportResult? Result;
        internal PortableExportResult? Export;
        internal PortableFailure? Failure;
        internal Task Work = Task.CompletedTask;
        internal string State = "Preparing";
        internal bool Released;
        internal int ChunkCalls;
        internal Task? CleanupWork;
        internal long NextOffset;
        internal long LastOffset = -1;
        internal byte[]? LastBytes;
        internal BitArray DownloadedOffsets { get; } = new((512 * 1024 * 1024 + ChunkBytes - 1) / ChunkBytes);
    }
}
