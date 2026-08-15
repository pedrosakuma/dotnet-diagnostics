using System.Text;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using DotnetDiagnostics.Core.CpuSampling;

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
        logger.WriteLine(BuildMarkdown(_diagnoser.Entries, _diagnoser.Digests));
    }

    public IEnumerable<string> ExportToFiles(Summary summary, ILogger consoleLogger)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var path = Path.Combine(summary.ResultsDirectoryPath, $"{summary.Title}-dotnet-diagnostics-report.md");
        File.WriteAllText(path, BuildMarkdown(_diagnoser.Entries, _diagnoser.Digests));
        return new[] { path };
    }

    internal static string BuildMarkdown(
        IReadOnlyCollection<BenchmarkDiagnosticEntry> entries,
        IReadOnlyDictionary<string, InvestigationDigest>? digests = null)
    {
        digests ??= new Dictionary<string, InvestigationDigest>(StringComparer.Ordinal);
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

            if (digests.TryGetValue(group.Key, out var digest))
            {
                AppendDigest(sb, digest);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders the cross-collector "investigation digest" (issue #827) for a benchmark that carried
    /// both <c>cpu</c> and <c>allocation</c> <see cref="DiagnosticKindAttribute"/>s — the same
    /// correlation <c>InvestigationDigestBuilder</c> renders for the MCP <c>collect_batch</c> tool
    /// (issue #825) and the CLI <c>session</c> REPL, reused here instead of reimplemented.
    /// </summary>
    private static void AppendDigest(StringBuilder sb, InvestigationDigest digest)
    {
        sb.AppendLine("### Cross-collector investigation digest (cpu + allocation)");
        sb.AppendLine();

        if (digest.TopCpuSelfTime is { Count: > 0 } topCpu)
        {
            sb.Append("- **Top CPU self-time:** ")
                .AppendLine(Escape(string.Join(", ", topCpu.Select(m =>
                    FormattableString.Invariant($"{m.Method} ({m.ExclusivePercent:N1}%)")))));
        }

        if (digest.TopCpuWaitCategories is { Count: > 0 } topWait)
        {
            sb.Append("- **Top wait categories:** ")
                .AppendLine(Escape(string.Join(", ", topWait.Select(w =>
                    FormattableString.Invariant($"{w.WaitReason} ({w.ExclusivePercent:N1}%)")))));
        }

        if (digest.HotPathLeaf is { } leaf)
        {
            sb.Append("- **Hot-path leaf:** ")
                .AppendLine(Escape(FormattableString.Invariant(
                    $"{leaf.Method} (depth {digest.HotPathDepth}, {leaf.InclusivePercent:N1}% inclusive)")));
        }

        if (digest.TopAllocationTypes is { Count: > 0 } topTypes)
        {
            sb.Append("- **Top allocation types (bytes):** ")
                .AppendLine(Escape(string.Join(", ", topTypes.Select(t =>
                    FormattableString.Invariant($"{t.TypeName} ({t.TotalBytes:N0} bytes)")))));
        }

        if (digest.TopAllocationCallsites is { Count: > 0 } topSites)
        {
            sb.Append("- **Top allocation call sites:** ")
                .AppendLine(Escape(string.Join(", ", topSites.Select(s =>
                    FormattableString.Invariant($"{s.Frame.Method} ({s.TotalBytes:N0} bytes)")))));
        }

        sb.AppendLine();
    }

    private static string Escape(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
