using DotnetDiagnostics.BenchmarkDotNet.Regression;

namespace DiagnosedBenchmarks;

internal static class PerfCalibrationRunner
{
    public static int Run(string[] args)
    {
        var options = Options.Parse(args);
        var cohorts = options.Values("cohort")
            .Select(static path => PerfRegressionReportSerializer.DeserializeCalibrationCohort(
                File.ReadAllText(path)))
            .ToArray();
        var policy = new PerfCalibrationPolicy(
            options.ValueOrDefault("policy-version", PerfCalibrationPolicy.PolicyV1));
        var (evidence, report) = PerfCalibrationAnalyzer.Analyze(cohorts, policy);
        var markdown = PerfRegressionReportSerializer.BuildCalibrationMarkdown(report);
        Write(
            options.RequiredSingle("output-evidence"),
            PerfRegressionReportSerializer.SerializeCalibrationEvidence(evidence));
        Write(
            options.RequiredSingle("output-json"),
            PerfRegressionReportSerializer.SerializeCalibrationReport(report));
        Write(options.RequiredSingle("output-markdown"), markdown);
        Console.WriteLine(markdown);
        return 0;
    }

    private static void Write(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private sealed class Options
    {
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

        public static Options Parse(string[] args)
        {
            var result = new Options();
            for (var index = 0; index < args.Length; index += 2)
            {
                var key = args[index];
                if (!key.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                {
                    throw new ArgumentException($"Expected '--key value' at argument {index + 1}.");
                }
                if (!result._values.TryGetValue(key[2..], out var values))
                {
                    values = [];
                    result._values.Add(key[2..], values);
                }
                values.Add(args[index + 1]);
            }
            return result;
        }

        public string RequiredSingle(string key)
            => Single(key) ?? throw new ArgumentException($"Missing required option --{key}.");

        public string ValueOrDefault(string key, string fallback) => Single(key) ?? fallback;

        private string? Single(string key)
        {
            var values = Values(key);
            return values.Count switch
            {
                0 => null,
                1 => values[0],
                _ => throw new ArgumentException($"Option --{key} may only be specified once."),
            };
        }

        public IReadOnlyList<string> Values(string key)
            => _values.TryGetValue(key, out var values) ? values : Array.Empty<string>();
    }
}
