using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>
/// <see cref="IInvestigationTransportManager"/> for operator-configured external MCP
/// endpoints (<see cref="ExternalMcpProfile"/>). Enforces all SSRF-safety requirements
/// from issue #710:
/// <list type="bullet">
/// <item>Model supplies no URI or upstream bearer — both come from operator config only.</item>
/// <item>URL is the profile-configured <c>/mcp</c> path; no model-supplied paths.</item>
/// <item>DNS answers are validated against <c>AllowedCidrs</c> before connect.</item>
/// <item>IPv4-mapped IPv6 (<c>::ffff:x.x.x.x</c>) unwrapped to IPv4 before CIDR check
///   to prevent IPv6-alias bypass.</item>
/// <item>TCP connection goes to the validated resolved IP (prevents DNS rebinding).</item>
/// <item>Original hostname retained for TLS SNI and certificate validation.</item>
/// <item>No system proxy (<c>UseProxy=false</c>), no redirects, no cookies, no
///   automatic decompression.</item>
/// <item>Bounded connect timeout via <see cref="SocketsHttpHandler.ConnectTimeout"/>.</item>
/// <item>Bounded call timeout via <see cref="HttpClient.Timeout"/>.</item>
/// <item>Response <c>Content-Length</c> checked against <c>MaxResponseBytes</c> before
///   the body is read.</item>
/// <item>****** is never logged, returned in errors, or serialized.</item>
/// </list>
/// </summary>
internal sealed class SsrfSafeExternalMcpTransportManager : IInvestigationTransportManager, IAsyncDisposable
{
    private readonly OrchestratorOptions _options;
    private readonly ILogger<SsrfSafeExternalMcpTransportManager> _logger;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _closedHandles = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public SsrfSafeExternalMcpTransportManager(
        OrchestratorOptions options,
        ILogger<SsrfSafeExternalMcpTransportManager> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<HttpClient> GetOrCreateClientAsync(InvestigationHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.ExternalMcp is null)
        {
            throw new InvalidOperationException(
                $"Handle {handle.HandleId} does not carry ExternalMcp metadata; " +
                "cannot create an external MCP transport for it.");
        }

        lock (_gate)
        {
            if (_closedHandles.Contains(handle.HandleId))
            {
                throw new OrchestratorException(
                    OrchestratorErrorKinds.ExternalMcpConnectFailed,
                    $"Investigation {handle.HandleId} is closed; its external MCP transport cannot be recreated.");
            }

            if (_entries.TryGetValue(handle.HandleId, out var existing))
            {
                return Task.FromResult(existing.Client);
            }

            var entry = BuildEntry(handle);
            _entries[handle.HandleId] = entry;
            _logger.LogInformation(
                "External MCP transport registered for investigation {HandleId} → profile '{ProfileName}'.",
                handle.HandleId, handle.ExternalMcp.ProfileName);
            return Task.FromResult(entry.Client);
        }
    }

    /// <inheritdoc/>
    public Task CloseAsync(string handleId)
    {
        Entry? removed;
        lock (_gate)
        {
            _closedHandles.Add(handleId);
            if (!_entries.Remove(handleId, out removed)) return Task.CompletedTask;
        }
        SafeDispose(removed!, handleId);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        List<KeyValuePair<string, Entry>> snapshot;
        lock (_gate)
        {
            snapshot = new List<KeyValuePair<string, Entry>>(_entries);
            _entries.Clear();
        }
        foreach (var kv in snapshot)
        {
            SafeDispose(kv.Value, kv.Key);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void SafeDispose(Entry entry, string handleId)
    {
        try { entry.Client.Dispose(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing external MCP HttpClient for {HandleId} threw.", handleId);
        }
    }

    private Entry BuildEntry(InvestigationHandle handle)
    {
        var ext = handle.ExternalMcp!;
        if (!_options.ExternalMcpProfiles.TryGetValue(ext.ProfileName, out var profile))
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.ExternalMcpConnectFailed,
                $"External MCP profile '{ext.ProfileName}' is not in the server configuration.");
        }

        var allowedCidrs = BuildCidrList(profile.AllowedCidrs);
        var allowedPorts = new HashSet<int>(profile.AllowedPorts);

        var handler = new SocketsHttpHandler
        {
            // SSRF safety: no system proxy, no redirects, no cookies, no decompression.
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            // Bound TCP connect time.
            ConnectTimeout = TimeSpan.FromSeconds(profile.ConnectTimeoutSeconds),
            // Pool lifetime: external endpoints are stable; 5-minute reuse is safe.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // Bound concurrent outstanding connections to this endpoint per issue #710's
            // "bounded ... concurrency" requirement.
            MaxConnectionsPerServer = profile.MaxConcurrency,
            ConnectCallback = (ctx, ct) => ConnectAsync(ctx, allowedCidrs, allowedPorts, ct),
        };

        // Defense-in-depth: limit how much of an abandoned response body is drained
        // before closing the connection.
        handler.MaxResponseDrainSize = (int)Math.Min(profile.MaxResponseBytes, int.MaxValue);

        var innerHandler = new MaxResponseBytesHandler(profile.MaxResponseBytes, handler, ownsInner: true);

        var client = new HttpClient(innerHandler, disposeHandler: true)
        {
            BaseAddress = ext.Url,
            Timeout = TimeSpan.FromSeconds(profile.CallTimeoutSeconds),
        };

        // Transport-owned credential injection: bearer is never returned to callers.
        // ext.BearerToken comes from the profile (config), not from the model.
        if (!string.IsNullOrEmpty(ext.BearerToken))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ext.BearerToken);
        }

        return new Entry(client);
    }

    // ── DNS + CIDR validation ──────────────────────────────────────────────────────────────

    private static async ValueTask<System.IO.Stream> ConnectAsync(
        SocketsHttpConnectionContext ctx,
        List<ParsedCidr> allowedCidrs,
        HashSet<int> allowedPorts,
        CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var port = ctx.DnsEndPoint.Port;

        // 1. Port must be in the allowlist (defence against port smuggling via profile URL).
        if (allowedPorts.Count > 0 && !allowedPorts.Contains(port))
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.ExternalMcpSsrfRejected,
                $"Connection to port {port} is not in the allowed ports for this external MCP profile.");
        }

        // 2. DNS-resolve the hostname.
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.ExternalMcpSsrfRejected,
                $"DNS resolution of '{host}' failed: {ex.GetType().Name}.", ex);
        }

        if (addresses.Length == 0)
        {
            throw new OrchestratorException(
                OrchestratorErrorKinds.ExternalMcpSsrfRejected,
                $"DNS resolution of '{host}' returned no addresses.");
        }

        // 3. Validate all resolved addresses against the CIDR allowlist.
        //    Connect only to the first address that passes — prevents DNS rebinding
        //    by not re-resolving between validation and connect.
        IPAddress? connectAddress = null;
        foreach (var address in addresses)
        {
            // Unwrap IPv4-mapped IPv6 (::ffff:x.x.x.x) before CIDR check.
            // This prevents an attacker from bypassing an IPv4-only allowlist by
            // presenting the address in IPv6-mapped form.
            var checkAddr = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

            if (IsAddressAllowed(checkAddr, allowedCidrs))
            {
                connectAddress = address;
                break;
            }
        }

        if (connectAddress is null)
        {
            // Do NOT include resolved IPs in the error to avoid info-leaking internal topology.
            throw new OrchestratorException(
                OrchestratorErrorKinds.ExternalMcpSsrfRejected,
                $"No DNS address for '{host}' falls within the configured allowed CIDRs.");
        }

        // 4. Connect to the validated IP directly — never re-resolves after check
        //    (closes the TOCTOU DNS rebinding window).
        //    SNI and certificate CN/SAN validation still use the original hostname
        //    from the request URI (via SocketsHttpHandler's TLS layer on top of
        //    this stream), so certificate validation is unaffected.
        var socket = new Socket(connectAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;
        try
        {
            await socket.ConnectAsync(new IPEndPoint(connectAddress, port), ct).ConfigureAwait(false);
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException) { socket.Dispose(); throw; }
        catch (Exception ex)
        {
            socket.Dispose();
            throw new OrchestratorException(
                OrchestratorErrorKinds.ExternalMcpConnectFailed,
                $"TCP connect to the external MCP endpoint failed: {ex.GetType().Name}.", ex);
        }
    }

    // ── CIDR helpers ──────────────────────────────────────────────────────────────────────

    /// <summary>Parses and caches the profile CIDR strings into efficient byte-array form.</summary>
    private static List<ParsedCidr> BuildCidrList(IList<string> cidrStrings)
    {
        var result = new List<ParsedCidr>(cidrStrings.Count);
        foreach (var cidr in cidrStrings)
        {
            var slashIdx = cidr.IndexOf('/', StringComparison.Ordinal);
            if (slashIdx < 0) continue;
            if (!IPAddress.TryParse(cidr.AsSpan(0, slashIdx), out var network)) continue;
            if (!int.TryParse(cidr.AsSpan(slashIdx + 1), out var prefix)) continue;
            var maxPrefix = network.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
            if (prefix < 0 || prefix > maxPrefix) continue;
            result.Add(new ParsedCidr(network.GetAddressBytes(), prefix));
        }
        return result;
    }

    private static bool IsAddressAllowed(IPAddress address, List<ParsedCidr> cidrs)
    {
        var addrBytes = address.GetAddressBytes();
        foreach (var cidr in cidrs)
        {
            if (addrBytes.Length != cidr.NetworkBytes.Length) continue;
            if (IsInCidr(addrBytes, cidr.NetworkBytes, cidr.PrefixLength)) return true;
        }
        return false;
    }

    private static bool IsInCidr(byte[] address, byte[] network, int prefixLength)
    {
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (address[i] != network[i]) return false;
        }

        if (remainingBits > 0)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            if ((address[fullBytes] & mask) != (network[fullBytes] & mask)) return false;
        }

        return true;
    }

    // ── Inner types ───────────────────────────────────────────────────────────────────────

    private sealed record ParsedCidr(byte[] NetworkBytes, int PrefixLength);
    private sealed record Entry(HttpClient Client);

    /// <summary>
    /// DelegatingHandler that rejects responses whose declared <c>Content-Length</c>
    /// exceeds <see cref="MaxBytes"/>. SSE streams (chunked encoding, no content-length)
    /// pass through and are bounded by the MCP protocol's own framing.
    /// </summary>
    internal sealed class MaxResponseBytesHandler : DelegatingHandler
    {
        internal readonly long MaxBytes;

        public MaxResponseBytesHandler(long maxBytes, HttpMessageHandler inner, bool ownsInner)
            : base(inner)
        {
            MaxBytes = maxBytes;
            // DelegatingHandler.Dispose(true) calls inner.Dispose() iff
            // AutoDisposeHandler is set to true (the default in .NET 7+).
            // Older API: pass ownsInner through a custom field is not needed;
            // base.InnerHandler is set by the base ctor and disposed by base.Dispose.
            _ = ownsInner; // documented intent — base always disposes the inner
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // Reject oversized non-streaming responses before the body is buffered.
            if (response.Content?.Headers.ContentLength is long declared && declared > MaxBytes)
            {
                response.Dispose();
                throw new OrchestratorException(
                    OrchestratorErrorKinds.ExternalMcpSsrfRejected,
                    $"Response Content-Length {declared} exceeds MaxResponseBytes {MaxBytes}.");
            }

            return response;
        }
    }

    /// <summary>
    /// Validates all profiles in <paramref name="options"/> and throws
    /// <see cref="InvalidOperationException"/> with a descriptive message if any profile
    /// is invalid. Called during DI registration so the server refuses to start on
    /// misconfiguration.
    /// </summary>
    internal static void ValidateProfiles(OrchestratorOptions options)
    {
        foreach (var (name, profile) in options.ExternalMcpProfiles)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    "Orchestrator:ExternalMcpProfiles contains an entry with an empty or whitespace profile name. " +
                    "Profile names must be non-empty identifiers.");
            }

            var urlError = ValidateProfileUrl(name, profile.Url);
            if (urlError is not null)
            {
                throw new InvalidOperationException(
                    $"Orchestrator:ExternalMcpProfiles['{name}'].Url is invalid: {urlError}");
            }

            if (profile.AllowedCidrs.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Orchestrator:ExternalMcpProfiles['{name}'].AllowedCidrs is empty. " +
                    "At least one CIDR block must be allowed; an empty list blocks all traffic.");
            }

            foreach (var cidr in profile.AllowedCidrs)
            {
                var cidrError = ValidateCidr(cidr);
                if (cidrError is not null)
                {
                    throw new InvalidOperationException(
                        $"Orchestrator:ExternalMcpProfiles['{name}'].AllowedCidrs contains invalid entry '{cidr}': {cidrError}");
                }
            }

            if (profile.AllowedPorts.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Orchestrator:ExternalMcpProfiles['{name}'].AllowedPorts is empty. " +
                    "At least one allowed port must be specified.");
            }

            if (profile.MaxConcurrency < 1)
            {
                throw new InvalidOperationException(
                    $"Orchestrator:ExternalMcpProfiles['{name}'].MaxConcurrency is {profile.MaxConcurrency}. " +
                    "MaxConcurrency must be at least 1.");
            }

            foreach (var portValue in profile.AllowedPorts)
            {
                if (portValue < 1 || portValue > 65535)
                {
                    throw new InvalidOperationException(
                        $"Orchestrator:ExternalMcpProfiles['{name}'].AllowedPorts contains invalid port {portValue}. " +
                        "Ports must be in the range 1–65535.");
                }
            }

            if (Uri.TryCreate(profile.Url, UriKind.Absolute, out var profileUri) &&
                !profile.AllowedPorts.Contains(profileUri.Port == -1 ? GetDefaultPort(profileUri.Scheme) : profileUri.Port))
            {
                throw new InvalidOperationException(
                    $"Orchestrator:ExternalMcpProfiles['{name}'].Url port is not in AllowedPorts. " +
                    "The URL's port must be explicitly listed in AllowedPorts.");
            }
        }
    }

    private static string? ValidateProfileUrl(string profileName, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "URL is required and must not be empty";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "URL is not a valid absolute URI";

        if (uri.Scheme != "http" && uri.Scheme != "https")
            return $"URL scheme must be 'http' or 'https'; got '{uri.Scheme}'";

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "URL must not contain userinfo (credentials embedded in URL are not allowed)";

        // AbsolutePath is the normalized path after resolving dot segments, so
        // checking the normalized form catches /mcp/../ and similar bypass attempts.
        if (uri.AbsolutePath != "/mcp")
            return $"URL path must be exactly '/mcp'; got '{uri.AbsolutePath}'";

        if (!string.IsNullOrEmpty(uri.Query))
            return "URL must not contain a query string";

        if (!string.IsNullOrEmpty(uri.Fragment))
            return "URL must not contain a fragment";

        return null;
    }

    private static string? ValidateCidr(string cidr)
    {
        var slashIdx = cidr.IndexOf('/', StringComparison.Ordinal);
        if (slashIdx < 0)
            return "must be in CIDR notation (e.g. '10.0.0.0/8' or 'fd00::/8')";

        if (!IPAddress.TryParse(cidr.AsSpan(0, slashIdx), out var network))
            return "network part is not a valid IP address";

        if (!int.TryParse(cidr.AsSpan(slashIdx + 1), out var prefix))
            return "prefix length is not a valid integer";

        var maxPrefix = network.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        if (prefix < 0 || prefix > maxPrefix)
            return $"prefix length {prefix} is out of range for {network.AddressFamily}";

        return null;
    }

    private static int GetDefaultPort(string scheme) => scheme switch
    {
        "https" => 443,
        "http" => 80,
        _ => -1
    };
}
