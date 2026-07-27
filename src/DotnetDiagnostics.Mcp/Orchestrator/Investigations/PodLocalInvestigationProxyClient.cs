using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnostics.Mcp.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// Production <see cref="IInvestigationProxyClient"/>. Caches one MCP client per
/// investigation handle, sharing the underlying transport <see cref="System.Net.Http.HttpClient"/>
/// from <see cref="IInvestigationTransportManager"/>. The transport implementation injects
/// upstream credentials (e.g. the per-attach Pod-local bearer token) into the client's
/// <see cref="System.Net.Http.HttpClient.DefaultRequestHeaders"/> so this class remains
/// transport-neutral and does not need to know the token value.
/// </summary>
/// <remarks>
/// <para>
/// The cached client survives across calls so the MCP <c>initialize</c> handshake
/// runs once per handle. On a handle close (detach / TTL eviction / attach failure),
/// callers MUST invoke <see cref="DisposeForHandleAsync"/> so the next attach against
/// the same target can not reuse a stale transport. Application shutdown disposes all
/// outstanding clients via <see cref="DisposeAsync"/>.
/// </para>
/// <para>
/// Lookups are lock-free on the hot path: a per-handle <see cref="Lazy{T}"/> guards
/// the one-time client creation while readers see a fully-constructed client. Disposal
/// is serialized with a per-handle <see cref="SemaphoreSlim"/> only when a slot is
/// being torn down — the steady-state filter call pays zero locking cost.
/// </para>
/// </remarks>
internal sealed class PodLocalInvestigationProxyClient : IInvestigationProxyClient, IAsyncDisposable
{
    private readonly IInvestigationTransportManager _transportManager;
    private readonly ToolScopeRegistry _scopeRegistry;
    private readonly IPrincipalAccessor _principalAccessor;
    private readonly ToolScopeResolutionPolicies _scopePolicies;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, Lazy<Task<McpClient>>> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _closedHandles = new(StringComparer.Ordinal);
    private int _disposed;

    public PodLocalInvestigationProxyClient(
        IInvestigationTransportManager transportManager,
        ToolScopeRegistry scopeRegistry,
        IPrincipalAccessor principalAccessor,
        IServiceProvider services,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(transportManager);
        ArgumentNullException.ThrowIfNull(scopeRegistry);
        ArgumentNullException.ThrowIfNull(principalAccessor);
        ArgumentNullException.ThrowIfNull(services);
        _transportManager = transportManager;
        _scopeRegistry = scopeRegistry;
        _principalAccessor = principalAccessor;
        _scopePolicies = ToolScopeResolutionPolicies.FromServices(services);
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public async Task<CallToolResult> CallToolAsync(
        InvestigationHandle handle,
        CallToolRequestParams request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_closedHandles.ContainsKey(handle.HandleId))
        {
            throw new InvalidOperationException(
                $"Refusing to forward tool '{request.Name}' to investigation {handle.HandleId}: " +
                "the investigation has been closed.");
        }

        // H7 (issue #164): defense-in-depth allowlist check. The CallTool filter
        // is the primary gate, but enforcing again here guarantees that any
        // future code path that bypasses the filter (e.g. a direct call from a
        // unit test, a refactor that re-orders the request pipeline, or a
        // server-internal call site) still cannot drive a non-diagnostic tool
        // upstream with the auto-injected pod-local bearer.
        if (!InvestigationProxyToolAllowlist.IsAllowed(request.Name))
        {
            throw new InvalidOperationException(
                $"Refusing to forward tool '{request.Name}' to investigation {handle.HandleId}: " +
                "tool is not on the orchestrator's investigation-proxy allowlist (DiagnosticTools surface).");
        }

        if (_principalAccessor.Current is not { } caller ||
            string.IsNullOrWhiteSpace(handle.InternalScopeDelegationKey))
        {
            throw new InvalidOperationException(
                $"Refusing to forward tool '{request.Name}' to investigation {handle.HandleId}: " +
                "the internal scope-delegation context is unavailable.");
        }

        var authorization = _scopeRegistry.Authorize(
            request.Name,
            request.Arguments,
            caller,
            proxyInvocation: true,
            policies: _scopePolicies);
        if (!authorization.IsAllowed)
        {
            throw new InvalidOperationException(
                $"Refusing to forward tool '{request.Name}' to investigation {handle.HandleId}: " +
                $"the caller lacks required scope '{authorization.MissingScope}'.");
        }
        request = ToolScopeDelegation.Add(
            request,
            authorization,
            caller,
            handle.InternalScopeDelegationKey);

        var client = await GetOrCreateClientAsync(handle, cancellationToken).ConfigureAwait(false);
        if (_closedHandles.ContainsKey(handle.HandleId))
        {
            await DisposeForHandleAsync(handle.HandleId).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Refusing to forward tool '{request.Name}' to investigation {handle.HandleId}: " +
                "the investigation was closed before the pod call started.");
        }
        return await client.CallToolAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tears down the cached MCP client for a handle if one exists. Idempotent — safe
    /// to call from <c>detach</c>, the reaper, or attach failure paths. Errors during
    /// disposal are logged and swallowed: a partial close should never bubble back
    /// through the caller.
    /// </summary>
    public async Task DisposeForHandleAsync(string handleId)
    {
        if (string.IsNullOrEmpty(handleId)) return;
        _closedHandles.TryAdd(handleId, 0);
        if (!_clients.TryRemove(handleId, out var slot)) return;

        try
        {
            if (slot.IsValueCreated)
            {
                var client = await slot.Value.ConfigureAwait(false);
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<PodLocalInvestigationProxyClient>()
                .LogDebug(ex, "Disposing proxy MCP client for handle {HandleId} threw; ignoring.", handleId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        foreach (var kv in _clients.ToArray())
        {
            _clients.TryRemove(kv.Key, out _);
            try
            {
                if (kv.Value.IsValueCreated)
                {
                    var client = await kv.Value.Value.ConfigureAwait(false);
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _loggerFactory.CreateLogger<PodLocalInvestigationProxyClient>()
                    .LogDebug(ex, "Disposing proxy MCP client for handle {HandleId} during shutdown threw; ignoring.", kv.Key);
            }
        }
    }

    private Task<McpClient> GetOrCreateClientAsync(InvestigationHandle handle, CancellationToken cancellationToken)
    {
        var slot = _clients.GetOrAdd(
            handle.HandleId,
            id => new Lazy<Task<McpClient>>(() => CreateClientAsync(handle), LazyThreadSafetyMode.ExecutionAndPublication));

        return AwaitOrEvictAsync(handle.HandleId, slot, cancellationToken);
    }

    private async Task<McpClient> AwaitOrEvictAsync(string handleId, Lazy<Task<McpClient>> slot, CancellationToken cancellationToken)
    {
        try
        {
            // Pass the caller's CT through to the connect handshake so a slow upstream
            // doesn't pin the filter thread past the orchestrator-side request lifetime.
            return await slot.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !slot.Value.IsCompleted)
        {
            // Caller cancelled their wait but the underlying initialization is still
            // running (we issue it with CancellationToken.None so concurrent callers can
            // share the McpClient). Leave the slot in place so the next caller can latch
            // onto the in-flight Task instead of starting a duplicate handshake — and so
            // the materialized McpClient still ends up in _clients for DisposeAsync /
            // DisposeForHandleAsync to clean up.
            throw;
        }
        catch
        {
            // Initialization itself faulted (port-forward error, MCP handshake error)
            // or the wait surfaced a non-cancellation exception — drop the slot so the
            // next call retries instead of replaying the same failed Task. Schedule a
            // best-effort dispose of the failed client if it ever materializes.
            _clients.TryRemove(new KeyValuePair<string, Lazy<Task<McpClient>>>(handleId, slot));
            _ = DisposeIfMaterializedAsync(handleId, slot);
            throw;
        }
    }

    private async Task DisposeIfMaterializedAsync(string handleId, Lazy<Task<McpClient>> slot)
    {
        try
        {
            var client = await slot.Value.ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<PodLocalInvestigationProxyClient>()
                .LogDebug(ex, "Best-effort dispose of evicted proxy MCP client for handle {HandleId} failed; ignoring.", handleId);
        }
    }

    private async Task<McpClient> CreateClientAsync(InvestigationHandle handle)
    {
        // The port-forward manager keeps one HttpClient per handle keyed by HandleId.
        // We don't own it — disposal here would tear down the shared transport every
        // other consumer (proxy endpoint, etc.) is also using.
        var http = await _transportManager.GetOrCreateClientAsync(handle, CancellationToken.None).ConfigureAwait(false);

        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            Name = $"InvestigationProxy/{handle.HandleId}",
            TransportMode = HttpTransportMode.AutoDetect,
        };

        var transport = new HttpClientTransport(options, http, _loggerFactory, ownsHttpClient: false);
        return await McpClient.CreateAsync(transport, clientOptions: null, _loggerFactory, CancellationToken.None).ConfigureAwait(false);
    }
}
