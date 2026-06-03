using System.Globalization;
using System.Text.RegularExpressions;

namespace DotnetDiagnosticsMcp.Core.NativeAlloc;

/// <summary>
/// Resolves the C library shared object a target process is actually running against by parsing
/// its <c>/proc/&lt;pid&gt;/maps</c>. Kept as a pure, dependency-free parser so the (security- and
/// container-sensitive) path logic is unit-testable without a live process.
/// </summary>
/// <remarks>
/// The path printed in <c>maps</c> is in the <b>target's mount namespace</b>. In a Kubernetes
/// sidecar the diagnostics container has a different filesystem, so the same string may point at
/// the wrong libc (or nothing). <c>perf probe -x</c> must open the <i>target's</i> inode, so the
/// resolver returns the path re-rooted through <c>/proc/&lt;pid&gt;/root</c> — which the kernel
/// maps to the target's mount namespace regardless of the sidecar's own filesystem.
/// </remarks>
public static partial class ProcMapsLibcResolver
{
    // glibc: libc.so.6, libc-2.31.so, libc.so, libc.musl-x86_64.so.1 ; musl loader: ld-musl-x86_64.so.1
    // The optional [.-] separator after "libc" prevents matching libc++ / libcrypto / etc.
    [GeneratedRegex(@"^(libc([.-][^/]*)?\.so(\.\d+)*|ld-musl-[^/]*\.so(\.\d+)*)$", RegexOptions.IgnoreCase)]
    private static partial Regex LibcBasenameRegex();

    /// <summary>The libc mapping discovered for a target process.</summary>
    /// <param name="InNamespacePath">Path exactly as it appears in the target's <c>maps</c> (its own mount namespace).</param>
    /// <param name="HostPath">The same object re-rooted through <c>/proc/&lt;pid&gt;/root</c>, openable from the diagnostics container.</param>
    public sealed record LibcMapping(string InNamespacePath, string HostPath);

    /// <summary>
    /// Returns the libc mapping for <paramref name="processId"/>, or <c>null</c> when
    /// <c>/proc/&lt;pid&gt;/maps</c> is unreadable or contains no recognizable libc.
    /// </summary>
    public static LibcMapping? Resolve(int processId)
    {
        string maps;
        try
        {
            maps = File.ReadAllText($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/maps");
        }
        catch
        {
            return null;
        }

        return Parse(maps, processId);
    }

    /// <summary>
    /// Pure parser over the raw <c>maps</c> text. Prefers an executable libc mapping (the one that
    /// actually carries the allocator code) and falls back to any libc mapping. Exposed
    /// <c>internal</c> for unit tests that feed fixture text.
    /// </summary>
    internal static LibcMapping? Parse(string mapsText, int processId)
    {
        if (string.IsNullOrEmpty(mapsText)) return null;

        string? anyMatch = null;
        foreach (var rawLine in mapsText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;

            // Format: address perms offset dev inode pathname
            // e.g. 7f..-7f.. r-xp 00000000 08:01 1234 /usr/lib/x86_64-linux-gnu/libc.so.6
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6) continue;

            var perms = fields[1];
            // pathname can contain spaces; rejoin everything from field index 5 onward.
            var path = string.Join(' ', fields[5..]);
            if (path.Length == 0 || path[0] != '/') continue; // skip [heap], anon, etc.

            var basename = path[(path.LastIndexOf('/') + 1)..];
            if (!LibcBasenameRegex().IsMatch(basename)) continue;

            anyMatch ??= path;
            if (perms.Length >= 3 && perms[2] == 'x')
            {
                return new LibcMapping(path, ToHostPath(processId, path));
            }
        }

        return anyMatch is null ? null : new LibcMapping(anyMatch, ToHostPath(processId, anyMatch));
    }

    private static string ToHostPath(int processId, string inNamespacePath)
        => $"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/root{inNamespacePath}";
}
