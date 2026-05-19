using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DotnetDiagnosticsMcp.Core.CpuSampling;

/// <summary>
/// Resolves a usable <c>perf</c> binary on Linux hosts. On Debian/Ubuntu/WSL the
/// distro ships a wrapper at <c>/usr/bin/perf</c> that prints
/// "WARNING: perf not found for kernel X — you may need to install
/// linux-tools-X" and exits non-zero unless the matching <c>linux-tools-*</c>
/// package is installed. This resolver walks a candidate list (configured path
/// first, then a kernel-version-matched path under <c>/usr/lib/linux-tools-*/</c>,
/// then any other versioned install ordered newest-first) and picks the first
/// one whose <c>--version</c> probe succeeds.
/// </summary>
internal static class PerfBinaryResolver
{
    private const string LinuxToolsRoot = "/usr/lib";
    private const string LinuxToolsPrefix = "linux-tools-";

    /// <summary>
    /// Pure resolution function — injectable probe and candidate enumerator make
    /// this unit-testable without touching the filesystem or spawning processes.
    /// </summary>
    /// <param name="configuredPath">The path the caller asked for (e.g. <c>"perf"</c>).</param>
    /// <param name="enumerateCandidates">Returns the ordered candidate list
    /// (without the configured path, which is always tried first).</param>
    /// <param name="probe">Returns <c>true</c> when the candidate is a working perf binary.</param>
    public static string? Resolve(
        string configuredPath,
        Func<IEnumerable<string>> enumerateCandidates,
        Func<string, bool> probe)
    {
        ArgumentNullException.ThrowIfNull(configuredPath);
        ArgumentNullException.ThrowIfNull(enumerateCandidates);
        ArgumentNullException.ThrowIfNull(probe);

        if (probe(configuredPath))
        {
            return configuredPath;
        }

        foreach (var candidate in enumerateCandidates())
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            if (string.Equals(candidate, configuredPath, StringComparison.Ordinal)) continue;
            if (probe(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// Default candidate enumerator: kernel-version-matched
    /// <c>/usr/lib/linux-tools-&lt;uname -r&gt;/perf</c> first, then every other
    /// <c>/usr/lib/linux-tools-*/perf</c> sorted newest-version-first.
    /// </summary>
    public static IEnumerable<string> EnumerateDefaultLinuxToolsCandidates()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) yield break;
        if (!Directory.Exists(LinuxToolsRoot)) yield break;

        string? kernelRelease = TryGetKernelRelease();
        if (!string.IsNullOrEmpty(kernelRelease))
        {
            yield return Path.Combine(LinuxToolsRoot, LinuxToolsPrefix + kernelRelease, "perf");
        }

        string[] versioned;
        try
        {
            versioned = Directory.GetDirectories(LinuxToolsRoot, LinuxToolsPrefix + "*");
        }
        catch
        {
            yield break;
        }

        Array.Sort(versioned, static (a, b) => string.CompareOrdinal(
            Path.GetFileName(b), Path.GetFileName(a)));

        foreach (var dir in versioned)
        {
            yield return Path.Combine(dir, "perf");
        }
    }

    /// <summary>
    /// Default probe: spawns <c>{path} --version</c> with a 2 s timeout. Treats
    /// any non-zero exit, stderr-only output, or "WARNING: perf not found"
    /// banner as unusable.
    /// </summary>
    public static bool ProbePerfVersion(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return false;
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(true); } catch { /* best effort */ }
                return false;
            }
            if (p.ExitCode != 0) return false;
            string stdout = p.StandardOutput.ReadToEnd();
            if (stdout.Contains("WARNING: perf not found", StringComparison.Ordinal)) return false;
            return stdout.StartsWith("perf version", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetKernelRelease()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "uname",
                Arguments = "-r",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return null;
            if (!p.WaitForExit(1000))
            {
                try { p.Kill(true); } catch { /* best effort */ }
                return null;
            }
            if (p.ExitCode != 0) return null;
            return p.StandardOutput.ReadToEnd().Trim();
        }
        catch
        {
            return null;
        }
    }
}
