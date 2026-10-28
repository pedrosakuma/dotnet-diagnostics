using System.Text.Json;
using DotnetDiagnostics.Core.Requests;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli;

internal static partial class CliStreamingProtocol
{
    private static readonly string[] JointQueryViews = ["requests", "related-activities"];
    /// <summary>Session-local one-slot lifetime, independent of the generic multi-handle registry.</summary>
    private sealed class JointCaptureSlot(
        IServiceProvider services,
        ProtocolWriter writer,
        TextWriter stderr,
        CliInvestigationEvidence investigationEvidence) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private RequestActivityCapture? _capture;
        private string? _handle;
        private DateTimeOffset _expiresAt;
        private CancellationTokenSource? _cancel;
        private Task _running = Task.CompletedTask;
        private string? _requestId;
        private Timer? _expiry;
        public bool IsRunning => !_running.IsCompleted;

        internal static bool IsJointQuery(JsonElement root) =>
            TryGetString(root, "view", out var view) && view == "related-activities" ||
            TryGetString(root, "handle", out var handle) && handle.StartsWith("request-activities-", StringComparison.Ordinal);

        public async Task StartAsync(JsonElement root, bool liveSessionActive, CancellationToken cancellationToken)
        {
            var requestId = root.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            try
            {
                if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128)
                    throw new ArgumentException("A non-empty requestId of at most 128 characters is required.");
                if (!TryGetInt32(root, "processId", out var pid) || pid <= 0)
                    throw new ArgumentException("A positive processId is required.");
                var duration = 10;
                if (root.TryGetProperty("durationSeconds", out _) && !TryGetInt32(root, "durationSeconds", out duration))
                    throw new ArgumentException("durationSeconds must be an integer.");
                if (!root.TryGetProperty("sources", out var sourceArray) || sourceArray.ValueKind != JsonValueKind.Array ||
                    sourceArray.GetArrayLength() is < 1 or > 20 ||
                    sourceArray.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
                    throw new ArgumentException("sources must contain 1-20 exact ActivitySource names.");
                var options = new RequestActivityOptions(duration, sourceArray.EnumerateArray().Select(e => e.GetString()!).ToArray());
                options.Validate();
                // Reject unsupported scope instead of silently accepting persistence, tags or privileged options.
                foreach (var property in root.EnumerateObject())
                    if (property.Name is not ("type" or "requestId" or "kind" or "processId" or "durationSeconds" or "sources"))
                        throw new ArgumentException($"Unsupported joint-capture field '{property.Name}'.");
                if (liveSessionActive)
                    throw new ArgumentException("Stop the active live session before starting a joint capture.");
                foreach (var kind in JointSafetyKinds)
                {
                    var safety = await CliSafetyPreflight.RunAsync(
                        new CliOptions { Command = "collect", Kind = kind, Pid = pid },
                        handles: null, CliExecutionContext.OneShot, interactive: false,
                        TextReader.Null, TextWriter.Null, stderr, artifactRoot: null, cancellationToken).ConfigureAwait(false);
                    if (safety != CliSafetyPreflightDisposition.Proceed)
                    {
                        await ErrorAsync(requestId, "capture_safety_rejected", "Joint capture did not clear both existing request and Activity safety profiles.").ConfigureAwait(false);
                        return;
                    }
                }
                var collector = services.GetRequiredService<IRequestActivityCollector>();
                await ClearAsync().ConfigureAwait(false);
                _requestId = requestId;
                _cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _cancel.CancelAfter(TimeSpan.FromSeconds(duration + 45));
                await WriteJointAsync(new
                {
                    type = "capture-started",
                    requestId,
                    kind = "request-activities",
                    processId = pid,
                }, cancellationToken).ConfigureAwait(false);
                _running = CollectAsync(collector, requestId, pid, options, _cancel.Token);
            }
            catch (ArgumentException ex)
            {
                await ErrorAsync(requestId, "invalid_capture", ex.Message).ConfigureAwait(false);
            }
        }

        private async Task CollectAsync(IRequestActivityCollector collector, string requestId, int pid,
            RequestActivityOptions options, CancellationToken token)
        {
            try
            {
                var capture = await collector.CollectAsync(pid, options, token).ConfigureAwait(false);
                string handle;
                DateTimeOffset expiresAt;
                var captureId = capture.CaptureId;
                lock (_gate)
                {
                    _capture = capture;
                    _handle = handle = "request-activities-" + capture.CaptureId;
                    _expiresAt = expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
                    _expiry = new Timer(_ => Expire(captureId), null, TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
                }
                await WriteJointAsync(new
                {
                    type = "capture",
                    requestId,
                    kind = "request-activities",
                    processId = pid,
                    result = new
                    {
                        handle,
                        handleExpiresAt = expiresAt,
                        capture.CaptureId,
                        capture.RequestedStart,
                        capture.RequestedDurationSeconds,
                        capture.Sources,
                        requests = capture.QueryRequests(capture.CaptureId),
                    },
                    investigationEvidence = investigationEvidence.CreateRequestActivities(
                        capture,
                        CliInvestigationEvidence.ReadProcessStart(pid),
                        handle,
                        expiresAt),
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await ErrorAsync(requestId, "capture_cancelled", "Joint capture cancelled before retained evidence became available.").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Expire();
                // Do not transport target/provider exception messages, which may contain private payloads.
                await ErrorAsync(requestId, "capture_failed", $"Joint capture failed ({ex.GetType().Name}); no joint artifact is available.").ConfigureAwait(false);
            }
        }

        public async Task QueryAsync(JsonElement root)
        {
            var requestId = TryGetString(root, "requestId", out var id) ? id : null;
            try
            {
                if (string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("requestId is required.");
                if (!TryGetString(root, "handle", out var handle) || !TryGetString(root, "captureId", out var captureId) ||
                    !TryGetString(root, "view", out var view)) throw new ArgumentException("handle, captureId and view are required.");
                foreach (var property in root.EnumerateObject())
                    if (property.Name is not ("type" or "requestId" or "handle" or "captureId" or "view" or "requestRowId" or "topN") ||
                        property.Name == "requestRowId" && view != "related-activities")
                        throw new ArgumentException($"Unsupported joint-query field '{property.Name}'.");
                var top = 100;
                if (root.TryGetProperty("topN", out _) && !TryGetInt32(root, "topN", out top) ||
                    top is < 1 or > RequestActivityCapture.MaxQueryRows)
                    throw new ArgumentException("topN must be between 1 and 100.");
                object payload;
                lock (_gate)
                {
                    if (_capture is null || handle != _handle || DateTimeOffset.UtcNow >= _expiresAt)
                        throw new InvalidOperationException("Joint evidence unavailable: replaced, expired, disconnected, or not retained in this CLI session.");
                    payload = view switch
                    {
                        "requests" => _capture.QueryRequests(captureId, top),
                        "related-activities" when TryGetString(root, "requestRowId", out var rowId) =>
                            _capture.QueryRelated(captureId, rowId, top),
                        _ => throw new ArgumentException("Use requests or related-activities with an exact requestRowId."),
                    };
                }
                await WriteJointAsync(new { type = "query", requestId, handle, view, result = payload }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ArgumentException ex) { await ErrorAsync(requestId, "invalid_query", ex.Message).ConfigureAwait(false); }
            catch (InvalidOperationException ex) { await ErrorAsync(requestId, "handle_unavailable", ex.Message).ConfigureAwait(false); }
        }

        public async Task CancelAsync(string requestId)
        {
            if (_requestId != requestId || !IsRunning)
            {
                await ErrorAsync(requestId, "capture_not_found", "The requested capture is not active.").ConfigureAwait(false);
                return;
            }
            await _cancel!.CancelAsync().ConfigureAwait(false);
            await _running.ConfigureAwait(false);
        }

        public async Task ClearAsync()
        {
            try
            {
                if (_cancel is not null) await _cancel.CancelAsync().ConfigureAwait(false);
                await _running.ConfigureAwait(false);
            }
            finally
            {
                _cancel?.Dispose();
                _cancel = null;
                _requestId = null;
                _running = Task.CompletedTask;
                Expire();
            }
        }

        private void Expire(string? captureId = null)
        {
            lock (_gate)
            {
                if (captureId is not null && _capture?.CaptureId != captureId) return;
                _capture = null; _handle = null;
                _expiry?.Dispose();
                _expiry = null;
            }
        }

        private Task ErrorAsync(string? requestId, string code, string message) =>
            WriteJointAsync(new { type = "error", requestId, code, message }, CancellationToken.None);

        private async Task WriteJointAsync(object frame, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(frame, JsonOptions);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxJointFrameBytes)
                throw new InvalidOperationException("Joint frame exceeds MaxJointFrameBytes=1048576.");
            using var writeBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            writeBudget.CancelAfter(TimeSpan.FromSeconds(5));
            await writer.WriteSerializedAsync(json, writeBudget.Token).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync() => await ClearAsync().ConfigureAwait(false);
    }
    private static readonly string[] JointSafetyKinds = ["requests", "activities"];
    internal const int MaxJointFrameBytes = 1024 * 1024;
}
