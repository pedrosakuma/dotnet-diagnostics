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
    public static readonly StdioRootPrincipalAccessor Instance = new();

    private static readonly BearerPrincipal RootPrincipal = new(
        name: "stdio-root",
        scopes: System.Collections.Immutable.ImmutableHashSet.Create(BearerPrincipal.RootScope));

    public BearerPrincipal? Current => RootPrincipal;

    /// <summary>True when <paramref name="accessor"/> is exactly the stdio-transport singleton
    /// registered by <c>Program.RunStdioAsync</c> — a reliable, zero-new-plumbing way to detect
    /// "this call arrived over --stdio" (issue #665 Part A's <c>launch</c> path is stdio-only).</summary>
    public static bool IsCurrent(IPrincipalAccessor accessor) => ReferenceEquals(accessor, Instance);
}
