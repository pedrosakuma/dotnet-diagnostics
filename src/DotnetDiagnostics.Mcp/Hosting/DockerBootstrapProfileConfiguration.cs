namespace DotnetDiagnostics.Mcp.Hosting;

internal static class DockerBootstrapProfileConfiguration
{
    internal const string DirectoryPath = "/app/.dotnet-diagnostics/bootstrap-profiles";

    public static void AddTo(ConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!Directory.Exists(DirectoryPath))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json").Order(StringComparer.Ordinal))
        {
            configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
        }
    }
}
