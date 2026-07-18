using System.Globalization;
using DotnetDiagnostics.BenchmarkDotNet.Regression;

namespace DiagnosedBenchmarks;

internal static class PerfPairedExperimentRunner
{
    public static int Run(string[] args)
    {
        var options = Options.Parse(args);
        var pairSpecs = options.Values("pair").Select(PairSpec.Parse).OrderBy(static pair => pair.PairNumber).ToArray();
        if (pairSpecs.Length == 0)
        {
            throw new ArgumentException("At least one --pair <number|order|main.json|pr.json> is required.");
        }

        var pairs = pairSpecs.Select(static pair => new PerfPairedMeasurement(
            pair.PairNumber,
            pair.Order,
            ReadRun(pair.MainPath),
            ReadRun(pair.PullRequestPath))).ToArray();
        var diagnosticPath = options.Single("diagnostic");
        var diagnostic = diagnosticPath is null
            ? null
            : PerfRegressionReportSerializer.DeserializeDiagnosticRun(File.ReadAllText(diagnosticPath));
        var feasibility = ReadFeasibility(options);
        var policy = new PerfPairedRegressionPolicy(
            options.ValueOrDefault("policy-version", PerfPairedRegressionPolicy.PolicyV1));
        var (manifest, report) = PerfPairedComparisonAnalyzer.Analyze(pairs, feasibility, diagnostic, policy);

        var manifestPath = options.RequiredSingle("output-manifest");
        var feasibilityPath = options.RequiredSingle("output-feasibility");
        var jsonPath = options.RequiredSingle("output-json");
        var markdownPath = options.RequiredSingle("output-markdown");
        var markdown = PerfRegressionReportSerializer.BuildPairedMarkdown(report);
        Write(manifestPath, PerfRegressionReportSerializer.SerializePairedManifest(manifest));
        Write(feasibilityPath, PerfRegressionReportSerializer.SerializeFeasibility(feasibility));
        Write(jsonPath, PerfRegressionReportSerializer.SerializePairedReport(report));
        Write(markdownPath, markdown);
        Console.WriteLine(markdown);
        return 0;
    }

    private static PerfExperimentFeasibility ReadFeasibility(Options options)
    {
        var stagePath = options.RequiredSingle("stage-metrics");
        var stages = File.ReadLines(stagePath)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Select(ParseStage)
            .ToArray();
        var jobStart = DateTimeOffset.FromUnixTimeMilliseconds(
            long.Parse(options.RequiredSingle("job-start-unix-ms"), CultureInfo.InvariantCulture));
        var elapsedMinutes = Math.Round(
            Math.Max(0, (DateTimeOffset.UtcNow - jobStart).TotalMinutes),
            4);
        return new PerfExperimentFeasibility(
            options.ValueOrDefault("evidence-scope", "single_github_hosted_vm"),
            elapsedMinutes,
            DirectoryBytes(options.RequiredSingle("compact-root")),
            DirectoryBytes(options.RequiredSingle("raw-root")),
            stages);
    }

    private static PerfExperimentStageMetric ParseStage(string line)
    {
        var fields = line.Split('\t');
        if (fields.Length != 6)
        {
            throw new FormatException(
                $"Stage metric must contain six tab-separated fields; found {fields.Length}: '{line}'.");
        }
        return new PerfExperimentStageMetric(
            ParseStageKind(fields[0]),
            fields[1],
            double.Parse(fields[2], CultureInfo.InvariantCulture),
            long.Parse(fields[3], CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(fields[4]) ? null : fields[4],
            string.IsNullOrWhiteSpace(fields[5])
                ? null
                : int.Parse(fields[5], CultureInfo.InvariantCulture));
    }

    private static PerfExperimentStageKind ParseStageKind(string value)
        => value switch
        {
            "checkout" => PerfExperimentStageKind.Checkout,
            "restore_build" => PerfExperimentStageKind.RestoreBuild,
            "clean_pair" => PerfExperimentStageKind.CleanPair,
            "diagnostics" => PerfExperimentStageKind.Diagnostics,
            "report" => PerfExperimentStageKind.Report,
            "upload" => PerfExperimentStageKind.Upload,
            _ => throw new FormatException($"Unknown experiment stage kind '{value}'."),
        };

    private static long DirectoryBytes(string path)
        => Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(static file => new FileInfo(file).Length)
            : 0;

    private static PerfMeasurementRun ReadRun(string path)
        => PerfRegressionReportSerializer.DeserializeRun(File.ReadAllText(path));

    private static void Write(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private sealed record PairSpec(
        int PairNumber,
        PerfPairOrder Order,
        string MainPath,
        string PullRequestPath)
    {
        public static PairSpec Parse(string value)
        {
            var fields = value.Split('|');
            if (fields.Length != 4)
            {
                throw new FormatException(
                    $"Pair must use '<number>|<main_then_pr|pr_then_main>|<main.json>|<pr.json>': '{value}'.");
            }
            var order = fields[1] switch
            {
                "main_then_pr" => PerfPairOrder.MainThenPullRequest,
                "pr_then_main" => PerfPairOrder.PullRequestThenMain,
                _ => throw new FormatException($"Unknown pair order '{fields[1]}'."),
            };
            return new PairSpec(
                int.Parse(fields[0], CultureInfo.InvariantCulture),
                order,
                fields[2],
                fields[3]);
        }
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

        public string? Single(string key)
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
