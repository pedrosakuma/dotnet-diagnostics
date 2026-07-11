using System.Collections.Immutable;
using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Mcp.Security;

/// <summary>
/// v1 <see cref="IPrincipalResolver"/> backed by an in-memory table parsed from the
/// <c>Auth:BearerTokens</c> configuration section (canonical shape per docs/authorization.md#bearer-tokens-config).
/// </summary>
/// <remarks>
/// Construction is the only validation path: duplicates, empty fields, and missing scopes
/// fail loudly at startup rather than at request time. Lookup is indexed by the presented
/// token's UTF-8 length + SHA-256 digest so the server does not linearly scan every
/// configured token per request, but comparisons within the matched candidate bucket remain
/// constant-time and never short-circuit on a hit.
/// </remarks>
internal sealed class BearerTokenRegistry : IPrincipalResolver
{
    // Stored as (utf8 bytes, principal) so the raw string can be discarded after construction.
    // The index key is the token's exact UTF-8 length plus its full SHA-256 digest; collisions
    // still fall back to constant-time byte comparisons within the matched bucket.
    private readonly Dictionary<TokenIndexKey, Entry[]> _entriesByIndex;
    private readonly int _count;

    public static readonly BearerTokenRegistry Empty = new(Array.Empty<Entry>());

    private BearerTokenRegistry(IReadOnlyList<Entry> entries)
    {
        _count = entries.Count;
        _entriesByIndex = BuildIndex(entries);
    }

    /// <summary>Count of registered tokens. Used by startup diagnostics; the values
    /// themselves are never enumerated.</summary>
    public int Count => _count;

    public BearerPrincipal? TryResolve(string presentedBearer)
    {
        if (string.IsNullOrEmpty(presentedBearer))
        {
            return null;
        }

        var byteCount = Encoding.UTF8.GetByteCount(presentedBearer);
        byte[]? rented = null;
        Span<byte> presentedBytes = byteCount <= 256
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
        Span<byte> hashBytes = stackalloc byte[32];

        try
        {
            Encoding.UTF8.GetBytes(presentedBearer.AsSpan(), presentedBytes);
            SHA256.HashData(presentedBytes, hashBytes);

            if (!_entriesByIndex.TryGetValue(TokenIndexKey.Create(byteCount, hashBytes), out var candidates))
            {
                return null;
            }

            BearerPrincipal? match = null;
            foreach (var entry in candidates)
            {
                if (CryptographicOperations.FixedTimeEquals(entry.TokenBytes, presentedBytes))
                {
                    match = entry.Principal;
                }
            }

            return match;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(presentedBytes);
            CryptographicOperations.ZeroMemory(hashBytes);
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
    }

    /// <summary>Builds a registry from <paramref name="configuration"/> plus the legacy
    /// <c>MCP_BEARER_TOKEN</c> environment variable, honouring the docs/authorization.md#backward-compatibility
    /// coexistence + back-compat rules:
    /// <list type="bullet">
    ///   <item>Scoped tokens win when both shapes are configured; the legacy var is
    ///         ignored and a Warning is logged exactly once.</item>
    ///   <item>Otherwise the legacy var resolves to a synthetic <c>legacy-root</c>
    ///         principal holding the <see cref="BearerPrincipal.RootScope"/> wildcard.</item>
    ///   <item>When neither shape is configured and
    ///         <paramref name="allowEphemeralFallback"/> is true (loopback / stdio dev
    ///         mode), a 32-byte hex token is generated and surfaced as a
    ///         <c>legacy-root</c> principal — same ergonomics as today.</item>
    ///   <item>When neither shape is configured and
    ///         <paramref name="allowEphemeralFallback"/> is false (non-loopback bind),
    ///         construction throws — the H9/B1 bind guard.</item>
    /// </list></summary>
    public static BearerTokenRegistry Build(
        IConfiguration configuration,
        ILogger logger,
        bool allowEphemeralFallback)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var scopedEntries = ParseScopedTokens(configuration);
        var legacyToken = Environment.GetEnvironmentVariable("MCP_BEARER_TOKEN");
        var hasLegacy = !string.IsNullOrWhiteSpace(legacyToken);

        if (scopedEntries.Count > 0)
        {
            if (hasLegacy)
            {
                logger.LogWarning(
                    "Legacy MCP_BEARER_TOKEN ignored because Auth:BearerTokens is configured. " +
                    "Remove MCP_BEARER_TOKEN to silence this warning.");
            }

            logger.LogInformation(
                "Bearer auth: loaded {Count} scoped token(s) from Auth:BearerTokens.",
                scopedEntries.Count);

            return new BearerTokenRegistry(BuildEntries(scopedEntries));
        }

        if (hasLegacy)
        {
            logger.LogInformation(
                "Bearer auth: loaded legacy MCP_BEARER_TOKEN (resolves to '{Name}' with root scope).",
                BearerPrincipal.LegacyRootName);

            return new BearerTokenRegistry(BuildLegacyEntries(legacyToken!));
        }

        if (!allowEphemeralFallback)
        {
            // H9 (issue #162) — refuse to generate an ephemeral token when bound to a
            // non-loopback address.
            throw new InvalidOperationException(
                "Refusing to start: server is configured to bind to a non-loopback address but " +
                "no bearer credentials are configured. Set Auth:BearerTokens or MCP_BEARER_TOKEN " +
                "to an operator-managed secret before exposing the MCP endpoint, or restrict " +
                "--urls / ASPNETCORE_URLS to loopback (http://127.0.0.1:<port>) for local development.");
        }

        var ephemeral = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        logger.LogWarning(
            "MCP_BEARER_TOKEN not set and no Auth:BearerTokens configured. " +
            "Generated ephemeral token for this run: {Token}",
            ephemeral);
        return new BearerTokenRegistry(BuildLegacyEntries(ephemeral));
    }

    private static Entry[] BuildEntries(
        IReadOnlyList<ScopedTokenEntry> source)
    {
        var arr = new Entry[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            var entry = source[i];
            var principal = new BearerPrincipal(entry.Name, entry.Scopes);
            arr[i] = new Entry(Encoding.UTF8.GetBytes(entry.Token), principal);
        }
        return arr;
    }

    private static Entry[] BuildLegacyEntries(string token)
    {
        var principal = new BearerPrincipal(
            BearerPrincipal.LegacyRootName,
            ImmutableHashSet.Create(BearerPrincipal.RootScope));
        return new[] { new Entry(Encoding.UTF8.GetBytes(token), principal) };
    }

    private static Dictionary<TokenIndexKey, Entry[]> BuildIndex(IReadOnlyList<Entry> entries)
    {
        var buckets = new Dictionary<TokenIndexKey, List<Entry>>();
        foreach (var entry in entries)
        {
            var hashBytes = SHA256.HashData(entry.TokenBytes);
            var key = TokenIndexKey.Create(entry.TokenBytes.Length, hashBytes);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new List<Entry>();
                buckets[key] = bucket;
            }

            bucket.Add(entry);
        }

        return buckets.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToArray());
    }

    /// <summary>Parses <c>Auth:BearerTokens</c> with strict validation. Errors are raised
    /// without including any token value in the exception message.</summary>
    private static IReadOnlyList<ScopedTokenEntry> ParseScopedTokens(IConfiguration configuration)
    {
        var section = configuration.GetSection("Auth:BearerTokens");
        if (!section.Exists())
        {
            return Array.Empty<ScopedTokenEntry>();
        }

        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ScopedTokenEntry>();

        var index = 0;
        foreach (var child in section.GetChildren())
        {
            var name = child["Name"];
            var token = child["Token"];
            var scopeChildren = child.GetSection("Scopes").GetChildren()
                .Select(c => c.Value)
                .ToArray();

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    $"Auth:BearerTokens[{index}] is missing a non-empty Name.");
            }
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    $"Auth:BearerTokens entry '{name}' is missing a non-empty Token.");
            }
            if (scopeChildren.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Auth:BearerTokens entry '{name}' must declare at least one scope.");
            }

            var scopes = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            foreach (var scope in scopeChildren)
            {
                if (string.IsNullOrWhiteSpace(scope))
                {
                    throw new InvalidOperationException(
                        $"Auth:BearerTokens entry '{name}' contains an empty scope string.");
                }
                scopes.Add(scope.Trim());
            }

            if (!seenNames.Add(name))
            {
                throw new InvalidOperationException(
                    $"Auth:BearerTokens contains duplicate Name '{name}'. Names must be unique.");
            }
            if (!seenTokens.Add(token))
            {
                // Mention only the *name* of the second occurrence — never the token value.
                throw new InvalidOperationException(
                    $"Auth:BearerTokens entry '{name}' reuses a Token value already registered for another entry.");
            }

            result.Add(new ScopedTokenEntry(name, token, scopes.ToImmutable()));
            index++;
        }

        return result;
    }

    private sealed record Entry(byte[] TokenBytes, BearerPrincipal Principal);
    private sealed record ScopedTokenEntry(string Name, string Token, ImmutableHashSet<string> Scopes);

    private readonly record struct TokenIndexKey(
        int Length,
        ulong Hash0,
        ulong Hash1,
        ulong Hash2,
        ulong Hash3)
    {
        public static TokenIndexKey Create(int length, ReadOnlySpan<byte> hashBytes)
        {
            if (hashBytes.Length != 32)
            {
                throw new ArgumentOutOfRangeException(nameof(hashBytes), "SHA-256 hashes must be 32 bytes.");
            }

            return new TokenIndexKey(
                length,
                BinaryPrimitives.ReadUInt64LittleEndian(hashBytes[..8]),
                BinaryPrimitives.ReadUInt64LittleEndian(hashBytes.Slice(8, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(hashBytes.Slice(16, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(hashBytes.Slice(24, 8)));
        }
    }
}
