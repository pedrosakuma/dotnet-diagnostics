using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnostics.Mcp.Orchestrator;
using DotnetDiagnostics.Mcp.Orchestrator.Investigations;
using DotnetDiagnostics.Mcp.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Mcp.Hosting;

/// <summary>
/// Maps the orchestrator's per-handle reverse proxy at <c>{ProxyBasePath}/{handleId}/mcp</c>.
/// Validates the handle, enforces per-owner authorization, resolves the cached
/// <see cref="HttpClient"/> from <see cref="IPortForwardManager"/>, swaps the
/// client-supplied <c>Authorization</c> header for the per-attach Pod-local bearer
/// token, forwards the request to the ephemeral container's diagnostics MCP, and
/// streams the response back. The Pod-local secret never leaves the orchestrator
/// process.
/// </summary>
/// <remarks>
/// <para>
/// H7 (issue #164): the route is restricted to the single <c>/mcp</c> path and to
/// the HTTP methods Streamable HTTP actually uses (POST for JSON-RPC, GET for SSE
/// reconnect, DELETE for session termination). Any other path or method returns
/// 404 — new endpoints on the pod-local MCP do NOT become automatically reachable.
/// </para>
/// <para>
/// When the handle carries an <c>OwnerBearerName</c>, the caller's authenticated
/// bearer identity must match. Mismatch → structured 403 envelope. Handles minted
/// without an owner (stdio attach, framework calls with no projected bearer identity)
/// remain reachable by every authenticated caller for dev-time stdio ergonomics.
/// </para>
/// <para>
/// M5 (issue #164): the route applies the "mcp" rate-limit policy (per-IP fixed
/// window) and a <see cref="OrchestratorOptions.ProxyRequestSizeLimitBytes"/> cap
/// to bound per-request buffering by a misbehaving authenticated client.
/// </para>
/// </remarks>
internal static class InvestigationProxyEndpoints
{
    // H7: only the methods Streamable HTTP needs against /mcp. Reject everything else.
    private static readonly string[] ProxyHttpMethods = new[] { "POST", "GET", "DELETE" };
    private static readonly string[] AllHttpMethods = new[]
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "CONNECT", "TRACE",
    };

    /// <summary>H6: shared rate-limiter policy name, also referenced from /mcp registration.</summary>
    internal const string RateLimiterPolicyName = "mcp";

    /// <summary>H6: the per-handle relative path segment forwarded to the pod-local MCP.</summary>
    internal const string McpPathSegment = "/mcp";

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailers", "Transfer-Encoding", "Upgrade", "Host",
        "Authorization", // stripped explicitly — orchestrator injects its own
        "Cookie", // stripped: never forward client cookies upstream.
    };

    public static IEndpointRouteBuilder MapInvestigationProxy(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.ServiceProvider.GetRequiredService<OrchestratorOptions>();
        var basePath = options.ProxyBasePath.TrimEnd('/');

        // H7: the route is bounded to /mcp (+ an optional trailing segment so the
        // proxy still works if the pod-local SDK ever decides to namespace under
        // /mcp/<session>/...) — any other rest is 404. Methods are bounded to the
        // Streamable-HTTP verbs only.
        var pattern = basePath + "/{handleId}/mcp/{**rest}";
        var mcpRoute = app.MapMethods(pattern, ProxyHttpMethods, HandleAsync);
        ApplyProxyEndpointMetadata(mcpRoute, options);

        // Same shape without the trailing path segment so the proxy works for the
        // canonical "POST /proxy/{id}/mcp" call without a wildcard segment.
        var mcpRootPattern = basePath + "/{handleId}/mcp";
        var mcpRootRoute = app.MapMethods(mcpRootPattern, ProxyHttpMethods, HandleAsync);
        ApplyProxyEndpointMetadata(mcpRootRoute, options);

        // H7: catch-all that returns a structured 404 for any other path under the
        // proxy prefix. Without this an attacker probing /proxy/{id}/something-else
        // would hit ASP.NET Core's generic 404 page; the structured envelope helps
        // a well-behaved LLM client recover and prevents the host fingerprint from
        // leaking through the default 404 body.
        var fallbackPattern = basePath + "/{handleId}/{**rest}";
        app.MapMethods(fallbackPattern, AllHttpMethods, HandleDisallowedAsync);

        return app;
    }

    private static void ApplyProxyEndpointMetadata(IEndpointConventionBuilder route, OrchestratorOptions options)
    {
        // M5: cap request body size to bound buffering by a misbehaving authenticated
        // client. We rely on Kestrel's MaxRequestBodySize feature; copying the limit
        // onto the endpoint metadata makes it endpoint-scoped without affecting /mcp
        // (which can keep the host-wide default).
        route.WithMetadata(new RequestSizeLimitMetadata(options.ProxyRequestSizeLimitBytes));
        // M5: rate-limit the proxy hot path under the shared "mcp" policy.
        route.RequireRateLimiting(RateLimiterPolicyName);
    }

    private static Task HandleDisallowedAsync(HttpContext context)
        => WriteProblemAsync(
            context,
            StatusCodes.Status404NotFound,
            "ProxyPathNotAllowed",
            $"The investigation proxy only accepts {string.Join('/', ProxyHttpMethods)} requests to '/proxy/{{handleId}}/mcp'. " +
            "All other paths and methods are rejected. See docs/central-orchestrator-design.md §6 for the documented surface.");

    private static async Task HandleAsync(HttpContext context)
    {
        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("DotnetDiagnostics.Mcp.Hosting.InvestigationProxy");

        var handleId = (string?)context.Request.RouteValues["handleId"];
        var rest = (string?)context.Request.RouteValues["rest"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(handleId))
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound, "ProxyHandleMissing", "Missing investigation handle id.").ConfigureAwait(false);
            return;
        }

        var store = context.RequestServices.GetRequiredService<IInvestigationStore>();
        var handle = store.GetById(handleId);
        if (handle is null)
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound,
                "ProxyHandleUnknown",
                $"Investigation handle '{handleId}' is unknown.").ConfigureAwait(false);
            return;
        }
        if (handle.State != InvestigationState.Active)
        {
            await WriteProblemAsync(context, StatusCodes.Status410Gone,
                "ProxyHandleNotActive",
                $"Investigation '{handleId}' is in state {handle.State} and cannot be proxied.").ConfigureAwait(false);
            return;
        }

        // Enforce per-owner authorization using bearer identity, not protocol-session
        // headers. Handles minted without an owner (stdio attach, framework calls
        // with no projected bearer identity) remain reachable by every authenticated caller.
        // When the deployment has explicitly opted into AllowCrossSessionAdmin
        // (Helm: orchestrator.allowCrossSessionAdmin=true), the check is bypassed
        // — this is the operator/central-orchestrator topology where a single
        // bearer is authoritative across MCP sessions.
        var orchOptions = context.RequestServices.GetRequiredService<OrchestratorOptions>();
        // B5.2 (docs/authorization.md#scopes) + B5.3 (issue #184): also accept the per-bearer
        // 'orchestrator-admin' modifier scope. The deployment-wide
        // AllowCrossSessionAdmin flag keeps working byte-for-byte; routing
        // through OrchestratorAdminBypassPolicy emits a one-shot deprecation
        // warning the first time the flag is what enables the bypass.
        var adminBypass = OrchestratorAdminBypassPolicy.IsBypassAllowed(context.GetBearerPrincipal(), orchOptions, logger);
        if (handle.OwnerBearerName is not null && !adminBypass)
        {
            var caller = context.GetBearerPrincipal()?.Name;
            if (!string.Equals(caller, handle.OwnerBearerName, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Cross-bearer proxy attempt rejected: handle={HandleId} owner=present caller={CallerPresent} method={Method} path={Path}.",
                    handleId, caller is null ? "absent" : "present", context.Request.Method, context.Request.Path);
                await WriteProblemAsync(context, StatusCodes.Status403Forbidden,
                    "ProxyOwnerMismatch",
                    $"Investigation handle '{handleId}' is owned by a different bearer identity. " +
                    "Re-attach with the bearer that minted the handle, or have an operator use the orchestrator-admin bypass.").ConfigureAwait(false);
                return;
            }
        }

        // H7: hard-cap path to the Mcp segment plus optional sub-paths the SDK may
        // emit (the SDK currently uses just "/mcp"). The route pattern already
        // restricts inbound paths but recompute the upstream path defensively to
        // prevent any future route refactor from forwarding a probe path.
        // Defense in depth: reject any dot-segment that could let UriBuilder
        // normalize a request out of /mcp (e.g. "../health"). The check is
        // applied to decoded segments so percent-encoded variants are also
        // rejected.
        var trimmedRest = rest.Trim('/');
        if (ContainsDotSegment(trimmedRest))
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound,
                "ProxyPathNotAllowed",
                "Dot segments are not permitted in the proxy path.").ConfigureAwait(false);
            return;
        }
        var targetPath = string.IsNullOrEmpty(trimmedRest) ? McpPathSegment : McpPathSegment + "/" + trimmedRest;

        var manager = context.RequestServices.GetRequiredService<IPortForwardManager>();
        HttpClient client;
        try
        {
            client = await manager.GetOrCreateClientAsync(handle, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OrchestratorException ex)
        {
            logger.LogWarning(ex, "Port-forward setup failed for {HandleId}.", handleId);
            await WriteProblemAsync(context, StatusCodes.Status502BadGateway, "ProxyUpstreamUnavailable", ex.Message).ConfigureAwait(false);
            return;
        }

        var targetUri = new UriBuilder(client.BaseAddress!) { Path = targetPath };
        if (context.Request.QueryString.HasValue) targetUri.Query = context.Request.QueryString.Value!.TrimStart('?');

        using var upstream = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri.Uri);
        // Force HTTP/1.1: the synthetic "http://pod-local" host has no ALPN; HttpClient may
        // attempt HTTP/2 negotiation and stall on the port-forward WS which carries opaque
        // bytes and has no h2 handshake.
        upstream.Version = HttpVersion.Version11;
        upstream.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        CopyRequestHeaders(context.Request, upstream, handle.PodLocalBearerToken);

        PooledRequestBodyBuffer? bufferedBody = null;
        try
        {
            if (HasBody(context.Request))
            {
                // M5: enforce the body cap pre-buffer. Kestrel's MaxRequestBodySize
                // (set via the endpoint metadata) is the primary gate; we also bound
                // the read here so a chunked-encoded body that lies about its length
                // can't outgrow the cap before forwarding.
                var options = context.RequestServices.GetRequiredService<OrchestratorOptions>();
                try
                {
                    bufferedBody = await ReadBoundedAsync(
                        context.Request.Body,
                        options.ProxyRequestSizeLimitBytes,
                        context.RequestAborted).ConfigureAwait(false);
                }
                catch (RequestBodyTooLargeException)
                {
                    await WriteProblemAsync(context, StatusCodes.Status413PayloadTooLarge,
                        "ProxyBodyTooLarge",
                        $"Request body exceeds the configured proxy limit of {options.ProxyRequestSizeLimitBytes} bytes.")
                        .ConfigureAwait(false);
                    return;
                }

                // H7: even though the route is /mcp only, a direct POST to the proxy
                // bypasses the in-process call-tool filter's allowlist. Apply the same
                // gate here on the JSON-RPC envelope so disallowed tool names never
                // reach the pod-local MCP regardless of how the body arrived.
                var disallowed = FindDisallowedToolName(bufferedBody.WrittenSpan);
                if (disallowed is not null)
                {
                    logger.LogWarning(
                        "Proxy rejected disallowed tool name '{Tool}' for handle {HandleId}.",
                        disallowed, handleId);
                    await WriteProblemAsync(context, StatusCodes.Status403Forbidden,
                        "ProxyToolNotAllowed",
                        $"Tool '{disallowed}' is not in the diagnostic proxy allowlist. " +
                        "Only the read-only diagnostic tools published by DiagnosticTools may traverse the proxy.")
                        .ConfigureAwait(false);
                    return;
                }

                upstream.Content = new ByteArrayContent(bufferedBody.Buffer, 0, bufferedBody.Length);
                foreach (var h in context.Request.Headers)
                {
                    if (!h.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    upstream.Content.Headers.TryAddWithoutValidation(h.Key, (IEnumerable<string>)h.Value!);
                }
            }

            HttpResponseMessage response;
            var swSend = System.Diagnostics.Stopwatch.StartNew();
            logger.LogInformation(
                "Proxy upstream send begin: handle={HandleId} method={Method} path={Path} bodyLen={BodyLen}",
                handleId, context.Request.Method, targetPath, upstream.Content?.Headers.ContentLength);
            try
            {
                response = await client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted)
                    .ConfigureAwait(false);
                logger.LogInformation(
                    "Proxy upstream send end: handle={HandleId} status={Status} elapsedMs={Elapsed}",
                    handleId, (int)response.StatusCode, swSend.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Upstream request failed for {HandleId} → {Path}.", handleId, targetPath);
                await WriteProblemAsync(context, StatusCodes.Status502BadGateway,
                    "ProxyUpstreamFailed",
                    $"Pod-local diagnostics MCP did not respond: {ex.Message}").ConfigureAwait(false);
                return;
            }

            using (response)
            {
                context.Response.StatusCode = (int)response.StatusCode;
                CopyResponseHeaders(response, context.Response);

                // The pod-local MCP server uses Streamable HTTP semantics: the response to
                // the initial POST is often a long-lived text/event-stream where each
                // SSE event is a discrete chunk that the client must observe immediately
                // to complete its initialize handshake. ASP.NET Core's default response
                // body pipeline buffers writes until a threshold is hit or the request
                // completes; with CopyToAsync that means the first SSE event sits in the
                // buffer until the upstream stream closes — which for keep-alive sessions
                // never happens, and the client trips its 100s HttpClient.Timeout. Disable
                // buffering on the response body feature and stream chunks ourselves with
                // an explicit flush after every read so the SSE event reaches the wire as
                // soon as the upstream produces it.
                context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                await StreamWithFlushAsync(response.Content, context.Response.Body, context.RequestAborted)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            bufferedBody?.Dispose();
        }
    }

    private static async Task StreamWithFlushAsync(HttpContent content, Stream destination, CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(8 * 1024);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// M5: copy <paramref name="source"/> into a pooled byte buffer up to
    /// <paramref name="limit"/> bytes. Throws <see cref="RequestBodyTooLargeException"/>
    /// when the source produces more than the limit. Pass <c>limit &lt;= 0</c> to disable.
    /// </summary>
    private static async Task<PooledRequestBodyBuffer> ReadBoundedAsync(Stream source, long limit, CancellationToken cancellationToken)
    {
        const int DefaultBufferSize = 8 * 1024;
        if (limit <= 0)
        {
            limit = int.MaxValue;
        }

        var maxLength = limit > int.MaxValue ? int.MaxValue : (int)limit;
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maxLength, DefaultBufferSize));
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (length < maxLength)
                    {
                        var newSize = Math.Min(Math.Max(buffer.Length * 2, DefaultBufferSize), maxLength);
                        var expanded = ArrayPool<byte>.Shared.Rent(newSize);
                        Buffer.BlockCopy(buffer, 0, expanded, 0, length);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = expanded;
                    }
                }

                var writable = Math.Min(buffer.Length - length, maxLength - length);
                if (writable == 0)
                {
                    var overflowProbe = ArrayPool<byte>.Shared.Rent(1);
                    try
                    {
                        var overflowRead = await source.ReadAsync(overflowProbe.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                        if (overflowRead == 0)
                        {
                            return new PooledRequestBodyBuffer(buffer, length);
                        }

                        throw new RequestBodyTooLargeException();
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(overflowProbe);
                    }
                }

                var read = await source.ReadAsync(buffer.AsMemory(length, writable), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return new PooledRequestBodyBuffer(buffer, length);
                }

                length += read;
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private static void CopyRequestHeaders(HttpRequest source, HttpRequestMessage destination, string podToken)
    {
        foreach (var h in source.Headers)
        {
            if (HopByHopHeaders.Contains(h.Key)) continue;
            if (h.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) continue;
            destination.Headers.TryAddWithoutValidation(h.Key, (IEnumerable<string>)h.Value!);
        }
        destination.Headers.TryAddWithoutValidation("Authorization", $"Bearer {podToken}");
    }

    private static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse destination)
    {
        foreach (var h in source.Headers)
        {
            if (HopByHopHeaders.Contains(h.Key)) continue;
            destination.Headers[h.Key] = h.Value.ToArray();
        }
        if (source.Content is not null)
        {
            foreach (var h in source.Content.Headers)
            {
                if (HopByHopHeaders.Contains(h.Key)) continue;
                destination.Headers[h.Key] = h.Value.ToArray();
            }
        }
        // ASP.NET Core writes Transfer-Encoding itself when chunking; drop any upstream copy.
        destination.Headers.Remove("Transfer-Encoding");
    }

    private static bool HasBody(HttpRequest request)
        => !HttpMethods.IsGet(request.Method) &&
           !HttpMethods.IsHead(request.Method) &&
           !HttpMethods.IsDelete(request.Method);

    /// <summary>
    /// Returns true when any segment of <paramref name="path"/> is a relative
    /// dot segment (".", "..") in raw or percent-encoded form. Defense against
    /// path-traversal that could escape the <c>/mcp</c> upstream prefix once
    /// <see cref="UriBuilder"/> normalizes the path.
    /// </summary>
    private static bool ContainsDotSegment(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var raw in path.Split('/'))
        {
            string segment;
            try { segment = Uri.UnescapeDataString(raw); }
            catch (UriFormatException) { return true; }
            if (segment is "." or "..") return true;
        }
        return false;
    }

    /// <summary>
    /// H7 (deep defense): when the request body is a JSON-RPC <c>tools/call</c>
    /// envelope (or a batch containing one), require every <c>params.name</c>
    /// to be in the diagnostic-tool allowlist. Other JSON-RPC methods
    /// (<c>initialize</c>, <c>resources/*</c>, <c>prompts/*</c>) pass through so
    /// the SDK handshake keeps working — only tool invocation is gated. Returns
    /// the disallowed tool name when rejection is required, else null.
    /// </summary>
    private static string? FindDisallowedToolName(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty) return null;
        try
        {
            var reader = new Utf8JsonReader(body, isFinalBlock: true, state: default);
            if (!reader.Read())
            {
                return null;
            }

            return reader.TokenType switch
            {
                JsonTokenType.StartArray => FindDisallowedToolNameInArray(ref reader),
                JsonTokenType.StartObject => FindDisallowedToolNameInObject(ref reader),
                _ => null,
            };
        }
        catch (JsonException)
        {
            // Non-JSON body — let the upstream MCP handle it; the allowlist is
            // a tool-invocation gate and non-JSON bodies are not invocations.
            return null;
        }
    }

    private static string? FindDisallowedToolNameInArray(ref Utf8JsonReader reader)
    {
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.EndArray:
                    return null;
                case JsonTokenType.StartObject:
                    var found = FindDisallowedToolNameInObject(ref reader);
                    if (found is not null)
                    {
                        return found;
                    }

                    break;
                default:
                    if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                    {
                        reader.Skip();
                    }

                    break;
            }
        }

        throw new JsonException("Incomplete JSON array.");
    }

    private static string? FindDisallowedToolNameInObject(ref Utf8JsonReader reader)
    {
        string? method = null;
        string? toolName = null;
        var hasParamsObject = false;
        var hasName = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (!string.Equals(method, "tools/call", StringComparison.Ordinal))
                {
                    return null;
                }

                if (!hasParamsObject || !hasName)
                {
                    // Malformed tools/call — reject by returning a sentinel that
                    // hits the structured 403 path; clients should send a
                    // well-formed envelope.
                    return "<missing-name>";
                }

                return InvestigationProxyToolAllowlist.IsAllowed(toolName)
                    ? null
                    : toolName ?? "<null>";
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected object property.");
            }

            var propertyName = reader.GetString();
            if (!reader.Read())
            {
                throw new JsonException("Incomplete JSON object.");
            }

            if (string.Equals(propertyName, "method", StringComparison.Ordinal))
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    method = reader.GetString();
                }

                if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                {
                    reader.Skip();
                }

                continue;
            }

            if (string.Equals(propertyName, "params", StringComparison.Ordinal))
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    hasParamsObject = true;
                    toolName = FindToolNameInParamsObject(ref reader, ref hasName);
                }
                else if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                {
                    reader.Skip();
                }

                continue;
            }

            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                reader.Skip();
            }
        }

        throw new JsonException("Incomplete JSON object.");
    }

    private static string? FindToolNameInParamsObject(ref Utf8JsonReader reader, ref bool hasName)
    {
        string? toolName = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return toolName;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected params property.");
            }

            var propertyName = reader.GetString();
            if (!reader.Read())
            {
                throw new JsonException("Incomplete params object.");
            }

            if (string.Equals(propertyName, "name", StringComparison.Ordinal))
            {
                hasName = reader.TokenType == JsonTokenType.String;
                toolName = hasName ? reader.GetString() : null;
            }

            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                reader.Skip();
            }
        }

        throw new JsonException("Incomplete params object.");
    }

    private static Task WriteProblemAsync(HttpContext context, int status, string detail)
        => WriteProblemAsync(context, status, kind: null, detail);

    private static Task WriteProblemAsync(HttpContext context, int status, string? kind, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        var kindFragment = kind is null
            ? string.Empty
            : ",\"kind\":" + System.Text.Json.JsonSerializer.Serialize(kind);
        return context.Response.WriteAsync(
            $"{{\"status\":{status}{kindFragment},\"detail\":{System.Text.Json.JsonSerializer.Serialize(detail)}}}");
    }

    private sealed class RequestBodyTooLargeException : Exception
    {
    }

    private sealed class PooledRequestBodyBuffer(byte[] buffer, int length) : IDisposable
    {
        private byte[]? _buffer = buffer;

        public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(PooledRequestBodyBuffer));

        public int Length { get; } = length;

        public ReadOnlySpan<byte> WrittenSpan => Buffer.AsSpan(0, Length);

        public void Dispose()
        {
            if (_buffer is null)
            {
                return;
            }

            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }
    }
}

/// <summary>
/// Endpoint-scoped metadata applying <see cref="OrchestratorOptions.ProxyRequestSizeLimitBytes"/>
/// to a route. Stored alongside the standard ASP.NET Core <c>IRequestSizeLimitMetadata</c>
/// so the framework's max-request-body-size middleware caps incoming requests before they
/// reach the proxy handler.
/// </summary>
internal sealed class RequestSizeLimitMetadata(long maxRequestBodySize)
    : Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata
{
    public long? MaxRequestBodySize { get; } = maxRequestBodySize;
}
