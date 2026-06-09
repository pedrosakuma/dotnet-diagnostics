using System.Diagnostics;

namespace DotnetDiagnostics.Core.Symbols;

/// <summary>
/// Builds a SymbolReader search path with a consistent precedence chain across diagnostics tools.
/// </summary>
public sealed class SymbolPathBuilder
{
    public const string McpSymbolPathEnvironmentVariable = "MCP_SYMBOL_PATH";
    public const string NtSymbolPathEnvironmentVariable = "_NT_SYMBOL_PATH";
    private const char SymbolPathSeparator = ';';

    private readonly string? _configuredDefaultSymbolPath;

    public SymbolPathBuilder(string? configuredDefaultSymbolPath = null)
    {
        _configuredDefaultSymbolPath = Normalize(configuredDefaultSymbolPath);
    }

    public string? BuildForProcess(int processId, string? explicitSymbolPath = null)
        => Build(explicitSymbolPath, EnumerateMainModuleDirectory(processId));

    public string? Build(string? explicitSymbolPath, IEnumerable<string?> localFallbackPaths)
    {
        ArgumentNullException.ThrowIfNull(localFallbackPaths);

        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddIfPresent(parts, seen, explicitSymbolPath);
        AddIfPresent(parts, seen, _configuredDefaultSymbolPath);
        AddIfPresent(parts, seen, Environment.GetEnvironmentVariable(NtSymbolPathEnvironmentVariable));

        foreach (var path in localFallbackPaths)
        {
            AddIfPresent(parts, seen, path);
        }

        return parts.Count == 0 ? null : string.Join(SymbolPathSeparator, parts);
    }

    internal static IEnumerable<string?> EnumerateMainModuleDirectory(int processId)
    {
        string? directory = null;
        try
        {
            using var process = Process.GetProcessById(processId);
            var mainModulePath = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(mainModulePath))
            {
                directory = Path.GetDirectoryName(mainModulePath);
            }
        }
        catch
        {
            // Best effort: the target may have exited or deny MainModule access.
        }

        if (!string.IsNullOrWhiteSpace(directory))
        {
            yield return directory;
        }
    }

    private static void AddIfPresent(List<string> parts, HashSet<string> seen, string? value)
    {
        var normalized = Normalize(value);
        if (normalized is null || !seen.Add(normalized))
        {
            return;
        }

        parts.Add(normalized);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
