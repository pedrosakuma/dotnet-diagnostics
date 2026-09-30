using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Cli;

/// <summary>Stable capture storage, never the mutable dump/export/session artifact sandbox.</summary>
internal sealed class CliCaptureRootProvider : IArtifactRootProvider
{
    public CliCaptureRootProvider(string? explicitRoot)
    {
        var configured = explicitRoot;
        if (configured is null)
        {
            configured = Environment.GetEnvironmentVariable("MCP_ARTIFACT_ROOT");
        }
        if (string.IsNullOrWhiteSpace(configured))
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
            {
                throw new CaptureStoreException(CaptureErrorCode.InvalidInput,
                    "No local application data directory is available. Supply --capture-root <stable-directory>.");
            }
            configured = Path.Combine(local, "dotnet-diagnostics");
        }
        Root = Path.GetFullPath(configured);
    }

    public string Root { get; }

    internal static CaptureAccess CurrentAccess()
        => new($"local:{Environment.MachineName}:{Environment.UserDomainName}:{Environment.UserName}");
}
