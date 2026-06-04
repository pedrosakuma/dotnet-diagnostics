using System.Runtime.InteropServices;
using DotnetDiagnosticsMcp.Core.Artifacts;

namespace DotnetDiagnosticsMcp.Cli;

/// <summary>
/// CLI-scoped <see cref="IArtifactRootProvider"/> whose <see cref="Root"/> can be re-pointed between
/// commands (issue #300). The stateful <c>session</c> REPL builds the Core host <b>once</b>, so the
/// artifact sandbox provider is a long-lived singleton — but each command in the session may need a
/// different root (a <c>dump --out</c> directory, a <c>get-bytes --dump-file</c> directory, or the
/// per-session default temp root). The REPL flips <see cref="Set"/> before every command and calls
/// <see cref="Reset"/> in a <c>finally</c> so the one-shot artifact semantics (<see cref="CliHost"/>'s
/// <c>ResolveArtifactRoot</c>) are preserved without rebuilding the host (which would discard the
/// shared handle store that makes drill-down possible).
/// </summary>
/// <remarks>
/// Commands run strictly serially in the REPL loop, so a simple lock around the mutable root is
/// sufficient; the lock also makes the read visible to the dead-PID sweep / any incidental reader.
/// </remarks>
internal sealed class MutableArtifactRootProvider : IArtifactRootProvider
{
    private readonly object _gate = new();
    private readonly string _defaultRoot;
    private string _root;

    public MutableArtifactRootProvider(string defaultRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultRoot);
        _defaultRoot = EnsureRoot(defaultRoot);
        _root = _defaultRoot;
    }

    public string Root
    {
        get
        {
            lock (_gate)
            {
                return _root;
            }
        }
    }

    /// <summary>Re-points the sandbox at <paramref name="root"/> (created with restrictive perms) for
    /// the duration of the next command.</summary>
    public void Set(string root)
    {
        var resolved = EnsureRoot(root);
        lock (_gate)
        {
            _root = resolved;
        }
    }

    /// <summary>Restores the per-session default temp root after a command completes.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _root = _defaultRoot;
        }
    }

    private static string EnsureRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var full = Path.GetFullPath(root);
        Directory.CreateDirectory(full);
        EnsureRestrictivePermissions(full);
        return full;
    }

    private static void EnsureRestrictivePermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        // POSIX: u=rwx (0700). Best-effort, mirroring FixedArtifactRootProvider — failures are
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
