using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;

namespace DotnetDiagnostics.Mcp.Hosting;

/// <summary>Loads a Kestrel certificate from ECS/Kubernetes-friendly PEM
/// environment secrets without writing the private key to a shared filesystem.</summary>
internal static class PemCertificateLoader
{
    public const string CertificatePemKey = "MCP_TLS_CERTIFICATE_PEM";
    public const string PrivateKeyPemKey = "MCP_TLS_PRIVATE_KEY_PEM";

    public static X509Certificate2? Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var certificatePem = configuration[CertificatePemKey];
        var privateKeyPem = configuration[PrivateKeyPemKey];
        var hasCertificate = !string.IsNullOrWhiteSpace(certificatePem);
        var hasPrivateKey = !string.IsNullOrWhiteSpace(privateKeyPem);

        if (!hasCertificate && !hasPrivateKey)
        {
            return null;
        }

        if (!hasCertificate || !hasPrivateKey)
        {
            throw new InvalidOperationException(
                $"{CertificatePemKey} and {PrivateKeyPemKey} must be configured together.");
        }

        try
        {
            return X509Certificate2.CreateFromPem(certificatePem, privateKeyPem);
        }
        catch (Exception ex) when (
            ex is ArgumentException ||
            ex is System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidOperationException(
                "The configured MCP TLS certificate or private key is not valid PEM.",
                ex);
        }
    }
}
