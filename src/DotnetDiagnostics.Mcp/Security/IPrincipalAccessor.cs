using Microsoft.AspNetCore.Http;

namespace DotnetDiagnostics.Mcp.Security;

/// <summary>
/// Resolves the <see cref="BearerPrincipal"/> active for the current call, abstracting
/// over the HTTP transport (where the principal is stamped on
/// <see cref="HttpContext.Items"/> by <c>BearerTokenMiddleware</c>) and the stdio
/// transport (which has no HTTP context — the local client owns the process, so
/// authorization degrades to "root scope" per docs/authorization.md#default-policy-by-transport).
/// </summary>
public interface IPrincipalAccessor
{
    /// <summary>The principal for the current call, or <c>null</c> when no principal
    /// can be resolved. Implementations must never log or echo bearer values.</summary>
    BearerPrincipal? Current { get; }
}

/// <summary>HTTP-transport implementation: reads the principal stamped by
/// <c>BearerTokenMiddleware</c> off <see cref="HttpContext.Items"/>, with an
/// async-flow-local override for a verified pod-internal scope delegation.</summary>
internal sealed class HttpContextPrincipalAccessor : IPrincipalAccessor
{
    private readonly IHttpContextAccessor _accessor;
    private readonly AsyncLocal<BearerPrincipal?> _delegatedPrincipal = new();

    public HttpContextPrincipalAccessor(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    public BearerPrincipal? Current =>
        _delegatedPrincipal.Value ?? _accessor.HttpContext?.GetBearerPrincipal();

    public IDisposable PushDelegation(BearerPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var previous = _delegatedPrincipal.Value;
        // Store the immutable principal directly in AsyncLocal. MCP task promotion captures
        // this ExecutionContext while the handler runs; restoring the parent slot below does
        // not mutate the value captured by that background task.
        _delegatedPrincipal.Value = principal;
        return new DelegationLease(this, previous);
    }

    private sealed class DelegationLease(
        HttpContextPrincipalAccessor accessor,
        BearerPrincipal? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                // Replace only the disposing flow's slot. Never clear a shared mutable holder:
                // promoted task flows must retain their verified principal until completion.
                accessor._delegatedPrincipal.Value = previous;
            }
        }
    }

}

/// <summary>Stdio-transport implementation: returns a synthetic root principal so every
/// <c>[RequireScope]</c>-gated tool remains callable. The local MCP client owns the
/// process lifecycle — there is no transport-level identity to project (docs/authorization.md#default-policy-by-transport).</summary>
internal sealed class StdioRootPrincipalAccessor : IPrincipalAccessor
{
    public static readonly StdioRootPrincipalAccessor Instance = new(
        System.Collections.Immutable.ImmutableHashSet.Create(BearerPrincipal.RootScope));

    private readonly BearerPrincipal _principal;

    private StdioRootPrincipalAccessor(System.Collections.Immutable.ImmutableHashSet<string> scopes)
        => _principal = new(name: "stdio-root", scopes);

    public BearerPrincipal? Current => _principal;

    internal static StdioRootPrincipalAccessor FromConfiguration(IConfiguration configuration)
    {
        var enabled = configuration.GetValue<bool>("Stdio:CaptureBytes");
        var modifiers = configuration.GetSection("Stdio:CaptureModifiers").Get<string[]>() ?? [];
        if (modifiers.Length != 0 && !enabled)
            throw new InvalidOperationException("Stdio capture modifiers require Stdio:CaptureBytes=true.");
        if (!enabled) return Instance;
        var scopes = Instance._principal.Scopes.Add("module-bytes-read");
        foreach (var modifier in modifiers)
        {
            if (modifier is not ("sensitive-heap-read" or "sensitive-parameter-read" or "eventsource-any"))
                throw new InvalidOperationException("Stdio:CaptureModifiers contains an unsupported modifier.");
            scopes = scopes.Add(modifier);
        }
        return new(scopes);
    }

    /// <summary>Only the local host can construct this accessor; a remote principal's name is irrelevant.</summary>
    public static bool IsCurrent(IPrincipalAccessor accessor) => accessor is StdioRootPrincipalAccessor;
}
