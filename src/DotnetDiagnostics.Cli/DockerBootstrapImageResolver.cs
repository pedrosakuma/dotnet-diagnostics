using System.Reflection;

namespace DotnetDiagnostics.Cli;

internal static class DockerBootstrapImageResolver
{
    internal const string ImageRepository = "ghcr.io/pedrosakuma/dotnet-diagnostics";
    internal const string DevelopmentTag = "edge";
    internal const string ImageTagMetadataKey = "DockerBootstrapImageTag";

    internal static string Resolve(string? explicitImage)
        => Resolve(
            explicitImage,
            typeof(DockerBootstrapImageResolver).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(static attribute =>
                    string.Equals(attribute.Key, ImageTagMetadataKey, StringComparison.Ordinal))
                ?.Value);

    internal static string Resolve(string? explicitImage, string? embeddedTag)
    {
        if (!string.IsNullOrWhiteSpace(explicitImage))
        {
            return explicitImage;
        }

        var tag = string.IsNullOrWhiteSpace(embeddedTag) ? DevelopmentTag : embeddedTag;
        return $"{ImageRepository}:{tag}";
    }
}
