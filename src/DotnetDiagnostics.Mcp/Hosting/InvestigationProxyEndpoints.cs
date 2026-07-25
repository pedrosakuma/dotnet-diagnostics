using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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
using ModelContextProtocol.Protocol;

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

        PooledRequestBodyBuffer? bufferedBody = null;
        if (HasBody(context.Request))
        {
            var proxyOptions = context.RequestServices.GetRequiredService<OrchestratorOptions>();
            try
            {
                bufferedBody = await ReadBoundedAsync(
                    context.Request.Body,
                    proxyOptions.ProxyRequestSizeLimitBytes,
                    context.RequestAborted).ConfigureAwait(false);
            }
            catch (RequestBodyTooLargeException)
            {
                await WriteProblemAsync(context, StatusCodes.Status413PayloadTooLarge,
                    "ProxyBodyTooLarge",
                    $"Request body exceeds the configured proxy limit of {proxyOptions.ProxyRequestSizeLimitBytes} bytes.")
                    .ConfigureAwait(false);
                return;
            }
        }
        using var bufferedBodyLease = bufferedBody;
        var scopeRegistry = context.RequestServices.GetRequiredService<ToolScopeRegistry>();
        var callerPrincipal = context.GetBearerPrincipal();
        var scopePolicies = ToolScopeResolutionPolicies.FromServices(context.RequestServices);

        if (bufferedBody is not null)
        {
            var jsonContent = IsJsonContentType(context.Request.ContentType);
            if (!UsesUtf8Charset(context.Request.ContentType))
            {
                await WriteProblemAsync(context, StatusCodes.Status400BadRequest,
                    "ProxyJsonCharsetUnsupported",
                    "The investigation proxy accepts request bodies only as UTF-8.")
                    .ConfigureAwait(false);
                return;
            }

            var rejection = FindRejectedToolCall(
                bufferedBody.WrittenMemory,
                scopeRegistry,
                callerPrincipal,
                scopePolicies,
                failClosedOnInvalidJson: jsonContent);
            if (rejection is not null)
            {
                if (rejection.Kind == ProxyToolRejectionKind.Malformed)
                {
                    logger.LogWarning(
                        "Proxy rejected malformed JSON-RPC tool request for handle {HandleId}.",
                        handleId);
                    await WriteProblemAsync(context, StatusCodes.Status400BadRequest,
                        "ProxyToolRequestMalformed",
                        "The JSON-RPC request is malformed or contains duplicate object keys.")
                        .ConfigureAwait(false);
                }
                else if (rejection.Kind == ProxyToolRejectionKind.ResourceNotAllowed)
                {
                    logger.LogWarning(
                        "Proxy rejected pod-local diagnostic Resource URI '{ResourceUri}' for handle {HandleId}.",
                        rejection.ToolName, handleId);
                    await WriteProblemAsync(context, StatusCodes.Status403Forbidden,
                        "ProxyResourceNotAllowed",
                        $"Resource '{rejection.ToolName}' cannot traverse the investigation proxy. " +
                        "Use query_snapshot with the scopes required by the underlying diagnostic handle.")
                        .ConfigureAwait(false);
                }
                else if (rejection.Kind == ProxyToolRejectionKind.NotAllowed)
                {
                    logger.LogWarning(
                        "Proxy rejected disallowed tool name '{Tool}' for handle {HandleId}.",
                        rejection.ToolName, handleId);
                    await WriteProblemAsync(context, StatusCodes.Status403Forbidden,
                        "ProxyToolNotAllowed",
                        $"Tool '{rejection.ToolName}' is not in the diagnostic proxy allowlist. " +
                        "Only the documented diagnostics tools may traverse the proxy.")
                        .ConfigureAwait(false);
                }
                else
                {
                    logger.LogWarning(
                        "Proxy rejected tool '{Tool}' for handle {HandleId}: missing scope {MissingScope}.",
                        rejection.ToolName, handleId, rejection.MissingScope);
                    await WriteProblemAsync(context, StatusCodes.Status403Forbidden,
                        "ProxyToolScopeDenied",
                        rejection.MissingExplicitScope
                            ? $"Tool '{rejection.ToolName}' requires the literal modifier scope '{rejection.MissingScope}'."
                            : $"Tool '{rejection.ToolName}' requires scope '{rejection.MissingScope}'.")
                        .ConfigureAwait(false);
                }
                return;
            }
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

        byte[]? delegatedBody = null;
        if (bufferedBody is not null && ContainsToolCall(bufferedBody.WrittenMemory))
        {
            if (callerPrincipal is null || string.IsNullOrWhiteSpace(handle.InternalScopeDelegationKey))
            {
                await WriteProblemAsync(context, StatusCodes.Status502BadGateway,
                    "ProxyDelegationUnavailable",
                    "The pod-local internal authorization channel is unavailable. Re-attach to the pod.")
                    .ConfigureAwait(false);
                return;
            }

            delegatedBody = ToolScopeDelegation.AddToJsonRpcBody(
                bufferedBody.WrittenMemory,
                scopeRegistry,
                scopePolicies,
                callerPrincipal,
                handle.InternalScopeDelegationKey,
                context.RequestServices.GetService<TimeProvider>());
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
        if (ContainsUnsafePathSegment(trimmedRest))
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound,
                "ProxyPathNotAllowed",
                "Dot segments and backslashes are not permitted in the proxy path.").ConfigureAwait(false);
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

        try
        {
            if (bufferedBody is not null)
            {
                upstream.Content = delegatedBody is null
                    ? new ByteArrayContent(bufferedBody.Buffer, 0, bufferedBody.Length)
                    : new ByteArrayContent(delegatedBody);
                foreach (var h in context.Request.Headers)
                {
                    if (!h.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    upstream.Content.Headers.TryAddWithoutValidation(h.Key, (IEnumerable<string>)h.Value!);
                }
            }

            HttpResponseMessage response;
            var swSend = System.Diagnostics.Stopwatch.StartNew();
            logger.LogDebug(
                "Proxy upstream send begin: handle={HandleId} method={Method} path={Path} bodyLen={BodyLen}",
                handleId, context.Request.Method, targetPath, upstream.Content?.Headers.ContentLength);
            try
            {
                response = await client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted)
                    .ConfigureAwait(false);
                logger.LogDebug(
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
    /// Returns true when <paramref name="path"/> contains a relative dot segment
    /// or backslash in raw or percent-encoded form. Defense against path traversal
    /// that could escape the <c>/mcp</c> upstream prefix once <see cref="UriBuilder"/>
    /// normalizes the path.
    /// </summary>
    private static bool ContainsUnsafePathSegment(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return true;
        }
        if (decoded.Contains('\\'))
        {
            return true;
        }
        foreach (var segment in decoded.Split('/'))
        {
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
    /// the first allowlist or scope rejection, else null. Batch envelopes run the
    /// allowlist pass before authorization so a later unknown tool cannot be masked
    /// by an earlier known-but-unauthorized call.
    /// </summary>
    private static ProxyToolRejection? FindRejectedToolCall(
        ReadOnlyMemory<byte> body,
        ToolScopeRegistry scopeRegistry,
        BearerPrincipal? principal,
        ToolScopeResolutionPolicies policies,
        bool failClosedOnInvalidJson)
    {
        if (body.IsEmpty) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (HasDuplicateObjectKeys(document.RootElement))
            {
                return new ProxyToolRejection(
                    ProxyToolRejectionKind.Malformed,
                    "<malformed>",
                    null,
                    false);
            }
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    var rejection = FindRejectedToolCall(
                        item,
                        scopeRegistry,
                        principal,
                        policies,
                        authorizeScopes: false);
                    if (rejection is not null)
                    {
                        return rejection;
                    }
                }

                foreach (var item in document.RootElement.EnumerateArray())
                {
                    var rejection = FindRejectedToolCall(
                        item,
                        scopeRegistry,
                        principal,
                        policies,
                        authorizeScopes: true);
                    if (rejection is not null)
                    {
                        return rejection;
                    }
                }

                return null;
            }

            return FindRejectedToolCall(
                document.RootElement,
                scopeRegistry,
                principal,
                policies,
                authorizeScopes: true);
        }
        catch (JsonException)
        {
            return failClosedOnInvalidJson
                ? new ProxyToolRejection(
                    ProxyToolRejectionKind.Malformed,
                    "<malformed>",
                    null,
                    false)
                : null;
        }
        catch (ArgumentException)
        {
            return new ProxyToolRejection(
                ProxyToolRejectionKind.Malformed,
                "<malformed>",
                null,
                false);
        }
    }

    private static ProxyToolRejection? FindRejectedToolCall(
        JsonElement envelope,
        ToolScopeRegistry scopeRegistry,
        BearerPrincipal? principal,
        ToolScopeResolutionPolicies policies,
        bool authorizeScopes)
    {
        if (envelope.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (HasNonCanonicalProperty(envelope, "method") ||
            HasNonCanonicalProperty(envelope, "params"))
        {
            return new ProxyToolRejection(
                ProxyToolRejectionKind.Malformed,
                "<noncanonical-json-rpc>",
                null,
                false);
        }
        if (!envelope.TryGetProperty("method", out var method) ||
            method.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        if (string.Equals(method.GetString(), "resources/read", StringComparison.Ordinal))
        {
            if (!envelope.TryGetProperty("params", out var resourceParams) ||
                resourceParams.ValueKind != JsonValueKind.Object ||
                HasNonCanonicalProperty(resourceParams, "uri") ||
                !resourceParams.TryGetProperty("uri", out var uri) ||
                uri.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(uri.GetString()))
            {
                return new ProxyToolRejection(
                    ProxyToolRejectionKind.Malformed,
                    "<malformed-resource-uri>",
                    null,
                    false);
            }

            var resourceUri = uri.GetString()!;
            return InvestigationProxyResourcePolicy.CanTraverseProxy(resourceUri)
                ? null
                : new ProxyToolRejection(
                    ProxyToolRejectionKind.ResourceNotAllowed,
                    resourceUri,
                    null,
                    false);
        }

        if (!string.Equals(method.GetString(), "tools/call", StringComparison.Ordinal))
        {
            return null;
        }

        if (!envelope.TryGetProperty("params", out var requestParams) ||
            requestParams.ValueKind != JsonValueKind.Object)
        {
            return new ProxyToolRejection(
                ProxyToolRejectionKind.NotAllowed,
                "<missing-name>",
                null,
                false);
        }
        if (HasNonCanonicalProperty(requestParams, "name") ||
            HasNonCanonicalProperty(requestParams, "arguments") ||
            HasNonCanonicalProperty(requestParams, "task") ||
            HasNonCanonicalProperty(requestParams, "_meta"))
        {
            return new ProxyToolRejection(
                ProxyToolRejectionKind.Malformed,
                "<noncanonical-tool-params>",
                null,
                false);
        }
        if (!requestParams.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String)
        {
            return new ProxyToolRejection(
                ProxyToolRejectionKind.NotAllowed,
                "<missing-name>",
                null,
                false);
        }

        var toolName = name.GetString()!;
        if (!InvestigationProxyToolAllowlist.IsAllowed(toolName))
        {
            return new ProxyToolRejection(
                ProxyToolRejectionKind.NotAllowed,
                toolName,
                null,
                false);
        }

        if (!authorizeScopes)
        {
            return null;
        }

        IDictionary<string, JsonElement>? arguments = null;
        if (requestParams.TryGetProperty("arguments", out var argumentObject))
        {
            if (argumentObject.ValueKind == JsonValueKind.Object)
            {
                arguments = argumentObject.EnumerateObject()
                    .ToDictionary(static property => property.Name, static property => property.Value, StringComparer.Ordinal);
            }
            else if (argumentObject.ValueKind != JsonValueKind.Null)
            {
                return new ProxyToolRejection(
                    ProxyToolRejectionKind.Malformed,
                    toolName,
                    null,
                    false);
            }
        }

        try
        {
            if (requestParams.Deserialize<CallToolRequestParams>() is null)
            {
                return new ProxyToolRejection(
                    ProxyToolRejectionKind.Malformed,
                    toolName,
                    null,
                    false);
            }
        }
        catch (JsonException)
        {
            return new ProxyToolRejection(
                ProxyToolRejectionKind.Malformed,
                toolName,
                null,
                false);
        }

        var authorization = scopeRegistry.Authorize(
            toolName,
            arguments,
            principal,
            proxyInvocation: true,
            policies: policies);
        return authorization.IsAllowed
            ? null
            : new ProxyToolRejection(
                ProxyToolRejectionKind.ScopeDenied,
                toolName,
                authorization.MissingScope,
                authorization.MissingExplicitScope);
    }

    private static bool ContainsToolCall(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                return document.RootElement.EnumerateArray().Any(IsToolCall);
            }
            return IsToolCall(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }

        static bool IsToolCall(JsonElement envelope)
            => envelope.ValueKind == JsonValueKind.Object &&
               envelope.TryGetProperty("method", out var method) &&
               method.ValueKind == JsonValueKind.String &&
               string.Equals(method.GetString(), "tools/call", StringComparison.Ordinal);
    }

    private static bool IsJsonContentType(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed) ||
            string.IsNullOrWhiteSpace(parsed.MediaType))
        {
            return false;
        }

        return string.Equals(parsed.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            parsed.MediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool UsesUtf8Charset(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return true;
        }
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed))
        {
            return false;
        }
        var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parsed.Parameters)
        {
            if (!parameterNames.Add(parameter.Name))
            {
                return false;
            }
        }
        if (string.IsNullOrWhiteSpace(parsed.CharSet))
        {
            return true;
        }

        return string.Equals(
            parsed.CharSet.Trim('"'),
            Encoding.UTF8.WebName,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasDuplicateObjectKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateObjectKeys(property.Value))
                {
                    return true;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (HasDuplicateObjectKeys(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasNonCanonicalProperty(JsonElement value, string canonicalName)
        => value.ValueKind == JsonValueKind.Object &&
           !value.TryGetProperty(canonicalName, out _) &&
           value.EnumerateObject().Any(property =>
               string.Equals(property.Name, canonicalName, StringComparison.OrdinalIgnoreCase));

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

        public ReadOnlyMemory<byte> WrittenMemory => Buffer.AsMemory(0, Length);

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

    private enum ProxyToolRejectionKind
    {
        Malformed,
        ResourceNotAllowed,
        NotAllowed,
        ScopeDenied,
    }

    private sealed record ProxyToolRejection(
        ProxyToolRejectionKind Kind,
        string ToolName,
        string? MissingScope,
        bool MissingExplicitScope);
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
