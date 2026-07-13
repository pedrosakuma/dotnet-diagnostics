using System.Globalization;
using BenchmarkDotNet.Reports;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace DiagnosedBenchmarks;

internal static class NettraceSelfTimeAnalyzer
{
    public static int AnalyzeToConsole(string tracePath, string? processNameHint = null, string? containsFilter = null, int topN = 10)
    {
        if (string.IsNullOrWhiteSpace(tracePath))
        {
            Console.Error.WriteLine("Missing trace path.");
            return 1;
        }

        if (!File.Exists(tracePath))
        {
            Console.Error.WriteLine($"Trace not found: {tracePath}");
            return 1;
        }

        var etlxPath = TraceLog.CreateFromEventPipeDataFile(tracePath);
        try
        {
            using var traceLog = new TraceLog(etlxPath);
            var process = FindProcess(traceLog, processNameHint);
            if (process is null)
            {
                Console.Error.WriteLine("No matching managed benchmark process found in the trace.");
                return 1;
            }

            var selfCounts = new Dictionary<string, long>(StringComparer.Ordinal);
            long totalSamples = 0;

            foreach (var traceEvent in process.EventsInProcess)
            {
                if (!string.Equals(traceEvent.ProviderName, "Microsoft-DotNETCore-SampleProfiler", StringComparison.Ordinal) ||
                    !string.Equals(traceEvent.EventName, "Thread/Sample", StringComparison.Ordinal))
                {
                    continue;
                }

                var callStack = traceEvent.CallStack();
                if (callStack is null)
                {
                    continue;
                }

                totalSamples++;
                var leaf = FormatFrame(callStack);
                if (!string.IsNullOrWhiteSpace(containsFilter) &&
                    leaf.IndexOf(containsFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                selfCounts.TryGetValue(leaf, out long current);
                selfCounts[leaf] = current + 1;
            }

            Console.WriteLine($"Trace: {tracePath}");
            Console.WriteLine($"Process: {process.Name} (pid {process.ProcessID})");
            Console.WriteLine($"Total samples: {totalSamples.ToString("N0", CultureInfo.InvariantCulture)}");
            Console.WriteLine();
            Console.WriteLine(string.IsNullOrWhiteSpace(containsFilter)
                ? "Top exclusive/self-time methods"
                : $"Top exclusive/self-time methods containing '{containsFilter}'");
            Console.WriteLine("--------------------------------");

            foreach (var pair in selfCounts
                         .OrderByDescending(static pair => pair.Value)
                         .Take(topN))
            {
                var percent = totalSamples == 0 ? 0 : pair.Value * 100.0 / totalSamples;
                Console.WriteLine($"{percent,6:F2}%  {pair.Value,8:N0}  {pair.Key}");
            }

            return 0;
        }
        finally
        {
            TryDelete(etlxPath);
        }
    }

    public static int ListProcessesToConsole(string tracePath)
    {
        if (!File.Exists(tracePath))
        {
            Console.Error.WriteLine($"Trace not found: {tracePath}");
            return 1;
        }

        var etlxPath = TraceLog.CreateFromEventPipeDataFile(tracePath);
        try
        {
            using var traceLog = new TraceLog(etlxPath);
            foreach (var process in traceLog.Processes
                         .Where(static process => process is not null)
                         .Select(process => new
                         {
                             process.ProcessID,
                             process.Name,
                             Samples = CountSampleEvents(process),
                         })
                         .Where(static process => process.Samples > 0)
                         .OrderByDescending(static process => process.Samples)
                         .Take(20))
            {
                Console.WriteLine($"{process.ProcessID,8}  {process.Samples,8:N0}  {process.Name}");
            }

            return 0;
        }
        finally
        {
            TryDelete(etlxPath);
        }
    }

    public static IReadOnlyDictionary<string, long> ParseAllocatedByBenchmark(string benchmarkResultsMarkdownPath)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!File.Exists(benchmarkResultsMarkdownPath))
        {
            return result;
        }

        foreach (var line in File.ReadLines(benchmarkResultsMarkdownPath))
        {
            if (!line.StartsWith("| ", StringComparison.Ordinal) ||
                line.Contains("---", StringComparison.Ordinal) ||
                line.Contains("Method", StringComparison.Ordinal))
            {
                continue;
            }

            var columns = line.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 2)
            {
                continue;
            }

            var method = columns[0];
            var allocated = columns.LastOrDefault(static c => c.EndsWith('B') || c.EndsWith("KB", StringComparison.Ordinal) || c.EndsWith("MB", StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(allocated))
            {
                continue;
            }

            var bytes = SizeValue.Parse(allocated).Bytes;
            result[method] = bytes;
        }

        return result;
    }

    private static TraceProcess? FindProcess(TraceLog traceLog, string? processNameHint)
    {
        if (int.TryParse(processNameHint, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
        {
            return traceLog.Processes
                .Where(static process => process is not null)
                .FirstOrDefault(process => process.ProcessID == pid);
        }

        var namedCandidates = traceLog.Processes
            .Where(static process => process is not null && !string.IsNullOrWhiteSpace(process.Name))
            .Where(process =>
                process.Name.Contains("DiagnosedBenchmarks", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(processNameHint) && process.Name.Contains(processNameHint, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(CountSampleEvents)
            .ThenByDescending(static process => process.ProcessID)
            .ToList();
        if (namedCandidates.Count > 0)
        {
            return namedCandidates[0];
        }

        return traceLog.Processes
            .Where(static process => process is not null)
            .OrderByDescending(CountSampleEvents)
            .ThenByDescending(static process => process.ProcessID)
            .FirstOrDefault();
    }

    private static long CountSampleEvents(TraceProcess process)
        => process.EventsInProcess.Count(traceEvent =>
            string.Equals(traceEvent.ProviderName, "Microsoft-DotNETCore-SampleProfiler", StringComparison.Ordinal) &&
            string.Equals(traceEvent.EventName, "Thread/Sample", StringComparison.Ordinal));

    private static string FormatFrame(dynamic callStack)
    {
        var address = callStack.CodeAddress;
        if (address?.Method is { } method && !string.IsNullOrWhiteSpace((string?)method.FullMethodName))
        {
            return method.FullMethodName;
        }

        var name = (string?)address?.FullMethodName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var module = (string?)address?.ModuleFile?.Name;
        if (!string.IsNullOrWhiteSpace(module))
        {
            return module + "!<unknown>";
        }

        return "<unknown>";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private readonly record struct SizeValue(double Value, string Unit)
    {
        public long Bytes => Unit switch
        {
            "B" => (long)Value,
            "KB" => (long)(Value * 1024),
            "MB" => (long)(Value * 1024 * 1024),
            "GB" => (long)(Value * 1024 * 1024 * 1024),
            _ => 0,
        };

        public static SizeValue Parse(string raw)
        {
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return default;
            }

            return new SizeValue(value, parts[1]);
        }
    }
}
