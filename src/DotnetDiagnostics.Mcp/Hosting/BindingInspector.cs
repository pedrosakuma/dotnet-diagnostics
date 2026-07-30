using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace DotnetDiagnostics.Mcp.Hosting;

/// <summary>
/// H9/B1 startup helpers. Centralised so the non-loopback bind detection used by the
/// bearer-auth bind guard (docs/authorization.md#default-policy-by-transport + Program.cs) is unit-testable and lives in
/// one place.
/// </summary>
internal static class BindingInspector
{
    private static readonly string[] UrlConfigKeys =
        { "urls", "ASPNETCORE_URLS", "DOTNET_URLS" };

    private static readonly string[] HttpPortOnlyConfigKeys =
    {
        "HTTP_PORTS", "ASPNETCORE_HTTP_PORTS", "DOTNET_HTTP_PORTS",
    };

    private static readonly string[] HttpsPortOnlyConfigKeys =
    {
        "HTTPS_PORTS", "ASPNETCORE_HTTPS_PORTS", "DOTNET_HTTPS_PORTS",
    };

    /// <summary>Returns <c>true</c> when the host is configured to bind to any
    /// non-loopback address via <c>app.Urls</c>, the <c>urls</c> / <c>ASPNETCORE_URLS</c>
    /// / <c>DOTNET_URLS</c> keys, the port-only env keys (<c>HTTP_PORTS</c> family —
    /// always wildcard), or <c>Kestrel:Endpoints:*:Url</c>.</summary>
    public static bool HasNonLoopbackBinding(WebApplication app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(app);
        return HasNonLoopbackBinding(app.Urls, configuration);
    }

    /// <summary>Overload that takes the <c>app.Urls</c> collection directly, for unit
    /// tests that cannot construct a real <see cref="WebApplication"/>.</summary>
    public static bool HasNonLoopbackBinding(ICollection<string> appUrls, IConfiguration configuration)
        => InspectBindings(appUrls, configuration).HasNonLoopbackBinding;

    /// <summary>Returns <c>true</c> when any non-loopback listener uses cleartext
    /// HTTP. HTTPS listeners and loopback-only HTTP listeners do not match.</summary>
    public static bool HasNonLoopbackHttpBinding(WebApplication app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(app);
        return HasNonLoopbackHttpBinding(app.Urls, configuration);
    }

    /// <summary>Collection overload for tests and pre-start policy checks.</summary>
    public static bool HasNonLoopbackHttpBinding(ICollection<string> appUrls, IConfiguration configuration)
        => InspectBindings(appUrls, configuration).HasNonLoopbackHttpBinding;

    private static BindingExposure InspectBindings(ICollection<string> appUrls, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(appUrls);
        ArgumentNullException.ThrowIfNull(configuration);

        var candidates = new List<string>(capacity: 8);

        if (appUrls.Count > 0)
        {
            candidates.AddRange(appUrls);
        }

        foreach (var key in UrlConfigKeys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                candidates.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        foreach (var key in HttpPortOnlyConfigKeys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return new BindingExposure(
                    HasNonLoopbackBinding: true,
                    HasNonLoopbackHttpBinding: true);
            }
        }

        var hasNonLoopbackBinding = false;
        foreach (var key in HttpsPortOnlyConfigKeys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
            {
                hasNonLoopbackBinding = true;
            }
        }

        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var url = endpoint["Url"];
            if (!string.IsNullOrWhiteSpace(url))
            {
                candidates.Add(url);
            }
        }

        foreach (var raw in candidates)
        {
            var exposure = InspectUrl(raw);
            hasNonLoopbackBinding |= exposure.HasNonLoopbackBinding;
            if (exposure.HasNonLoopbackHttpBinding)
            {
                return new BindingExposure(
                    HasNonLoopbackBinding: true,
                    HasNonLoopbackHttpBinding: true);
            }
        }

        return new BindingExposure(
            hasNonLoopbackBinding,
            HasNonLoopbackHttpBinding: false);
    }

    public static bool IsNonLoopbackUrl(string raw) => InspectUrl(raw).HasNonLoopbackBinding;

    private static BindingExposure InspectUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return default;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return default;
        }

        var host = uri.Host;
        if (string.IsNullOrEmpty(host))
        {
            return default;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return default;
        }

        bool nonLoopback;
        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            nonLoopback = !System.Net.IPAddress.IsLoopback(ip);
        }
        else
        {
            // Wildcards and DNS names both represent a network-visible listener.
            nonLoopback = true;
        }

        return new BindingExposure(
            nonLoopback,
            nonLoopback && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));
    }

    private readonly record struct BindingExposure(
        bool HasNonLoopbackBinding,
        bool HasNonLoopbackHttpBinding);
}
