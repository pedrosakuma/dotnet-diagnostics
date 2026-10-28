using System.Reflection;

namespace DotnetDiagnostics.Cli;

internal static class CliBuildVersion
{
    internal static string Current { get; } =
        typeof(CliBuildVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CliBuildVersion).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";
}
