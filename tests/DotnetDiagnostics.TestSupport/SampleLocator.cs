namespace DotnetDiagnostics.TestSupport;

/// <summary>
/// Resolves the on-disk build output of a sample / helper project by walking up from the
/// test host's base directory. Mirrors the convention used by the live test suite: probe up
/// to eight parent directories for <c>&lt;topLevelDirectory&gt;/&lt;projectDirectoryName&gt;</c>,
/// then prefer the Release build over Debug.
/// </summary>
public static class SampleLocator
{
    /// <summary>
    /// Locates the published <c>&lt;assemblyName&gt;.dll</c> for a project living under
    /// <paramref name="topLevelDirectory"/>/<paramref name="projectDirectoryName"/>.
    /// Returns <see langword="null"/> when the project directory or its build output is not found.
    /// </summary>
    public static string? LocateProjectDll(string topLevelDirectory, string projectDirectoryName, string assemblyName)
    {
        var probe = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var projectDir = Path.Combine(probe, topLevelDirectory, projectDirectoryName);
            if (Directory.Exists(projectDir))
            {
                foreach (var configuration in new[] { "Release", "Debug" })
                {
                    var dll = Path.Combine(projectDir, "bin", configuration, "net10.0", $"{assemblyName}.dll");
                    if (File.Exists(dll))
                    {
                        return dll;
                    }
                }

                return null;
            }

            probe = Path.GetFullPath(Path.Combine(probe, ".."));
        }

        return null;
    }

    /// <summary>Locates <c>samples/&lt;sampleName&gt;/bin/&lt;config&gt;/net10.0/&lt;sampleName&gt;.dll</c>.</summary>
    public static string? LocateSampleDll(string sampleName = "CoreClrSample")
        => LocateProjectDll("samples", sampleName, sampleName);
}
