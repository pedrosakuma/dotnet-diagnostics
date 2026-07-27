using System.Security.Cryptography;
using System.Text;

namespace DotnetDiagnostics.Mcp.Security;

/// <summary>
/// Builds non-secret, collision-resistant ownership identifiers from authenticated
/// identity coordinates. Display names are deliberately excluded from authorization.
/// </summary>
internal static class PrincipalOwnershipKey
{
    internal static string ForOpaqueEntry(string entryId)
        => Create("opaque-bearer", Normalize(entryId));

    internal static string ForJwt(
        string scheme,
        string issuer,
        string audience,
        string? client,
        string? subject)
    {
        if (string.IsNullOrWhiteSpace(client) && string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException(
                "A JWT ownership key requires a stable client or subject identifier.");
        }

        if (string.IsNullOrWhiteSpace(client))
        {
            return Create(
                "oidc-jwt-subject",
                Normalize(scheme),
                NormalizeIssuer(issuer),
                Normalize(audience),
                Normalize(subject!));
        }

        if (string.IsNullOrWhiteSpace(subject))
        {
            return Create(
                "oidc-jwt-client",
                Normalize(scheme),
                NormalizeIssuer(issuer),
                Normalize(audience),
                Normalize(client));
        }

        return Create(
            "oidc-jwt",
            Normalize(scheme),
            NormalizeIssuer(issuer),
            Normalize(audience),
            Normalize(client),
            Normalize(subject));
    }

    internal static string ForSystem(string identity)
        => Create("system", Normalize(identity));

    internal static string ForSynthetic(string identity)
        => Create("synthetic", Normalize(identity));

    private static string Create(string provider, params string[] components)
    {
        var canonical = new StringBuilder(provider.Length + 64);
        Append(canonical, provider);
        foreach (var component in components)
        {
            Append(canonical, component);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return "owner-v1:" + Convert.ToHexStringLower(hash);
    }

    private static void Append(StringBuilder builder, string value)
        => builder.Append(value.Length).Append(':').Append(value);

    private static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }

    private static string NormalizeIssuer(string issuer)
    {
        var trimmed = Normalize(issuer).TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed;
        }

        return uri.GetComponents(
                UriComponents.SchemeAndServer | UriComponents.Path,
                UriFormat.UriEscaped)
            .TrimEnd('/');
    }
}
