using System.Diagnostics;

namespace DotnetDiagnostics.TestSupport;

/// <summary>
/// Detects which shared CoreCLR runtimes (<c>Microsoft.NETCore.App</c>) are installed on the
/// current host, so cross-version tests can skip cleanly on hosts that don't happen to have an
/// older runtime installed rather than failing with a confusing launch error. See
/// docs/research/multi-version-target-support.md for why this matters (this repo diagnoses
/// target processes on runtimes other than the one it's built/run with).
/// </summary>
public static class InstalledRuntimes
{
    private static readonly Lazy<IReadOnlyList<string>> Versions = new(Discover);

    /// <summary>
    /// True when a <c>Microsoft.NETCore.App</c> shared runtime whose major version equals
    /// <paramref name="major"/> is installed (e.g. <c>8</c> matches any installed <c>8.x.y</c>).
    /// </summary>
    public static bool HasMajorVersion(int major)
        => Versions.Value.Any(v => v.Split('.')[0] == major.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static IReadOnlyList<string> Discover()
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet", "--list-runtimes")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            // Lines look like: "Microsoft.NETCore.App 8.0.26 [/path/to/shared/Microsoft.NETCore.App]"
            return output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("Microsoft.NETCore.App", StringComparison.Ordinal))
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 2)
                .Select(parts => parts[1])
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
