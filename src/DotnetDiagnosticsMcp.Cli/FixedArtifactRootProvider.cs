using System.Runtime.InteropServices;
using DotnetDiagnosticsMcp.Core.Artifacts;

namespace DotnetDiagnosticsMcp.Cli;

/// <summary>
/// CLI-scoped <see cref="IArtifactRootProvider"/> backed by an explicit, already-resolved root
/// directory (issue #288). The standalone CLI uses this to point the artifact sandbox at a
/// command-specific location — the <c>dump --out</c> directory, or the directory of a
/// <c>get-bytes --dump-file</c> — <b>without</b> mutating the process-global <c>MCP_ARTIFACT_ROOT</c>
/// environment variable, which would leak across commands run in the same process (e.g. tests).
/// </summary>
internal sealed class FixedArtifactRootProvider : IArtifactRootProvider
{
    public FixedArtifactRootProvider(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        EnsureRestrictivePermissions(Root);
    }

    public string Root { get; }

    private static void EnsureRestrictivePermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // POSIX: u=rwx (0700). Best-effort, mirroring EnvironmentArtifactRootProvider — failures are
        // non-fatal (e.g. a pre-existing directory owned with stricter perms).
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
