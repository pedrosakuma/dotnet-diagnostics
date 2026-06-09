using System.Text;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;

namespace DotnetDiagnostics.BenchmarkDotNet;

/// <summary>
/// A BenchmarkDotNet <see cref="IExporter"/> that aggregates the per-benchmark diagnostic captures
/// produced by <see cref="DotnetDiagnosticsDiagnoser"/> into a single "biggest offenders" markdown
/// report so any micro-optimization can be verified against the indicators it targets.
/// </summary>
public sealed class DotnetDiagnosticsReportExporter : IExporter
{
    private readonly DotnetDiagnosticsDiagnoser _diagnoser;

    internal DotnetDiagnosticsReportExporter(DotnetDiagnosticsDiagnoser diagnoser)
    {
        _diagnoser = diagnoser;
    }

    public string Name => "dotnet-diagnostics-report";

    public void ExportToLog(Summary summary, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.WriteLine(BuildMarkdown(_diagnoser.Entries));
    }

    public IEnumerable<string> ExportToFiles(Summary summary, ILogger consoleLogger)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var path = Path.Combine(summary.ResultsDirectoryPath, $"{summary.Title}-dotnet-diagnostics-report.md");
        File.WriteAllText(path, BuildMarkdown(_diagnoser.Entries));
        return new[] { path };
    }

    internal static string BuildMarkdown(IReadOnlyCollection<BenchmarkDiagnosticEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# dotnet-diagnostics — biggest offenders");
        sb.AppendLine();

        if (entries.Count == 0)
        {
            sb.AppendLine("_No diagnostic captures were recorded. Tag benchmark methods with `[DiagnosticKind(\"gc\")]` and add `[DotnetDiagnosticsDiagnoser]` to the class._");
            return sb.ToString();
        }

        foreach (var group in entries
            .GroupBy(e => e.Benchmark, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append("## ").AppendLine(group.Key);
            sb.AppendLine();
            sb.AppendLine("| kind | status | headline | artifact |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach (var entry in group.OrderBy(e => e.Kind, StringComparer.Ordinal))
            {
                var status = entry.IsError ? "⚠ error" : "ok";
                sb.Append("| ")
                    .Append(entry.Kind).Append(" | ")
                    .Append(status).Append(" | ")
                    .Append(Escape(entry.Headline)).Append(" | ")
                    .Append('`').Append(Path.GetFileName(entry.ArtifactPath)).Append('`')
                    .AppendLine(" |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Escape(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
