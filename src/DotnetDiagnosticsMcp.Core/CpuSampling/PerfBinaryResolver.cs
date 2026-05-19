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

        Array.Sort(versioned, static (a, b) => CompareKernelVersionDescending(
            Path.GetFileName(a), Path.GetFileName(b)));

        foreach (var dir in versioned)
        {
            yield return Path.Combine(dir, "perf");
        }
    }

    /// <summary>
    /// Compares two <c>linux-tools-X.Y.Z-N-FLAVOUR</c> directory names so that the
    /// newer kernel version sorts first. Ordinal string comparison would put
    /// <c>linux-tools-6.8.0-60</c> before <c>linux-tools-6.11.0-1</c>, which is
    /// the opposite of what we want.
    /// </summary>
    internal static int CompareKernelVersionDescending(string left, string right)
    {
        // a is sorted before b iff this returns negative. We want descending, so we
        // compare b's components against a's.
        var leftParts = ParseKernelVersion(left);
        var rightParts = ParseKernelVersion(right);
        var max = Math.Max(leftParts.Length, rightParts.Length);
        for (var i = 0; i < max; i++)
        {
            var l = i < leftParts.Length ? leftParts[i] : -1;
            var r = i < rightParts.Length ? rightParts[i] : -1;
            if (l != r) return r.CompareTo(l);
        }
        return string.CompareOrdinal(right, left);
    }

    private static int[] ParseKernelVersion(string dirName)
    {
        var rest = dirName.StartsWith(LinuxToolsPrefix, StringComparison.Ordinal)
            ? dirName.AsSpan(LinuxToolsPrefix.Length)
            : dirName.AsSpan();
        var numbers = new List<int>(capacity: 4);
        int i = 0;
        while (i < rest.Length)
        {
            if (char.IsDigit(rest[i]))
            {
                int start = i;
                while (i < rest.Length && char.IsDigit(rest[i])) i++;
                if (int.TryParse(rest[start..i], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var n))
                {
                    numbers.Add(n);
                }
            }
            else
            {
                i++;
            }
        }
        return numbers.ToArray();
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
