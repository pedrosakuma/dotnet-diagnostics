using System.Globalization;
using DotnetDiagnostics.Core.ProcessDiscovery;

namespace DotnetDiagnostics.Cli;

internal static class CliProcessSelector
{
    public static bool TryResolveName(
        string selector,
        IReadOnlyList<DotnetProcess> processes,
        out int processId,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        ArgumentNullException.ThrowIfNull(processes);

        processId = 0;
        error = null;

        var exactMatches = processes
            .Where(p => CandidateNames(p).Any(name => name.Equals(selector, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.ProcessId)
            .ToArray();

        if (exactMatches.Length == 1)
        {
            processId = exactMatches[0].ProcessId;
            return true;
        }

        var matches = exactMatches.Length > 1
            ? exactMatches
            : processes
                .Where(p => CandidateNames(p).Any(name => name.StartsWith(selector, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(p => p.ProcessId)
                .ToArray();

        if (matches.Length == 1)
        {
            processId = matches[0].ProcessId;
            return true;
        }

        if (matches.Length == 0)
        {
            error = processes.Count == 0
                ? $"No .NET process name starts with '{selector}' because no .NET processes are visible."
                : $"No .NET process name starts with '{selector}'. Visible processes: {FormatProcessList(processes)}.";
            return false;
        }

        error = $"--pid '{selector}' is ambiguous; matches: {FormatProcessList(matches)}.";
        return false;
    }

    private static IEnumerable<string> CandidateNames(DotnetProcess process)
    {
        if (!string.IsNullOrWhiteSpace(process.ManagedEntrypointAssemblyName))
        {
            yield return process.ManagedEntrypointAssemblyName;

            var fileName = Path.GetFileName(process.ManagedEntrypointAssemblyName);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                yield return fileName;
                var withoutExtension = Path.GetFileNameWithoutExtension(fileName);
                if (!string.IsNullOrWhiteSpace(withoutExtension))
                {
                    yield return withoutExtension;
                }
            }
        }
    }

    private static string FormatProcessList(IEnumerable<DotnetProcess> processes)
        => string.Join(", ", processes.Take(10).Select(p => string.Create(
            CultureInfo.InvariantCulture,
            $"pid {p.ProcessId} ({p.ManagedEntrypointAssemblyName ?? "<unknown>"})")));
}
