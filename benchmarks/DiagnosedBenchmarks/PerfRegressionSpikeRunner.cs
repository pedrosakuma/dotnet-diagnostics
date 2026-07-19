using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using DotnetDiagnostics.BenchmarkDotNet;
using DotnetDiagnostics.BenchmarkDotNet.Regression;

namespace DiagnosedBenchmarks;

internal static class PerfRegressionSpikeRunner
{
    private const string WorkloadId = "issue-647-ci-regression-pilots";
    private const string WorkloadVersion = "v2";
    private const int WaitDiagnosticLaunches = 3;

    private static readonly Dictionary<string, BenchmarkContract> CleanContracts =
        new Dictionary<string, BenchmarkContract>(StringComparer.Ordinal)
        {
            [nameof(PerfRegressionCleanBenchmarks.CpuLookupBaseline)] =
                new("cpu-string-lookup", PerfMeasurementRun.BaselineVariant, false),
            [nameof(PerfRegressionCleanBenchmarks.CpuLookupCandidate)] =
                new("cpu-string-lookup", PerfMeasurementRun.CandidateVariant, false),
            [nameof(PerfRegressionCleanBenchmarks.AllocationBaseline)] =
                new("allocation-churn", PerfMeasurementRun.BaselineVariant, false),
            [nameof(PerfRegressionCleanBenchmarks.AllocationCandidate)] =
                new("allocation-churn", PerfMeasurementRun.CandidateVariant, false),
            [nameof(PerfRegressionCleanBenchmarks.WaitBaseline)] =
                new("sync-over-async", PerfMeasurementRun.BaselineVariant, false),
            [nameof(PerfRegressionCleanBenchmarks.WaitCandidate)] =
                new("sync-over-async", PerfMeasurementRun.CandidateVariant, false),
            [nameof(PerfRegressionCleanBenchmarks.ControlBaseline)] =
                new("unchanged-control", PerfMeasurementRun.BaselineVariant, true),
            [nameof(PerfRegressionCleanBenchmarks.ControlCandidate)] =
                new("unchanged-control", PerfMeasurementRun.CandidateVariant, true),
        };

    private static readonly Dictionary<string, DiagnosticContract> DiagnosticContracts =
        new Dictionary<string, DiagnosticContract>(StringComparer.Ordinal)
        {
            [nameof(PerfRegressionDiagnosticBenchmarks.DiagnoseCpuLookupCandidate)] =
                new("cpu-string-lookup", "CultureAwareLookupCandidate"),
            [nameof(PerfRegressionDiagnosticBenchmarks.DiagnoseAllocationCandidate)] =
                new("allocation-churn", "AllocationCandidate", IsControl: false),
            [nameof(PerfRegressionWaitDiagnosticBenchmarks.DiagnoseWaitCandidate)] =
                new("sync-over-async", "parsed ThreadPool blocking or starvation adjustment", IsControl: false),
            [nameof(PerfRegressionWaitDiagnosticBenchmarks.DiagnoseWaitControl)] =
                new("sync-over-async", "no parsed ThreadPool blocking or starvation adjustment", IsControl: true),
        };

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            return Usage("Missing mode.");
        }

        if (string.Equals(args[0], "paired-report", StringComparison.OrdinalIgnoreCase))
        {
            return PerfPairedExperimentRunner.Run(args[1..]);
        }
        if (string.Equals(args[0], "calibration-report", StringComparison.OrdinalIgnoreCase))
        {
            return PerfCalibrationRunner.Run(args[1..]);
        }

        var options = Options.Parse(args[1..]);
        return args[0].ToLowerInvariant() switch
        {
            "measure" => Measure(options),
            "diagnose" => Diagnose(options),
            "report" => Report(options),
            _ => Usage($"Unknown mode '{args[0]}'."),
        };
    }

    private static int Measure(Options options)
    {
        var output = options.RequiredSingle("output");
        var artifacts = options.RequiredSingle("artifacts");
        var runId = options.RequiredSingle("run-id");
        var baselineBuild = BuildIdentity(options, "baseline");
        var candidateBuild = BuildIdentity(options, "candidate");
        var runnerClass = options.ValueOrDefault("runner-class", "local");
        var runnerImage = options.Single("runner-image");

        var summary = BenchmarkRunner.Run<PerfRegressionCleanBenchmarks>(
            new CleanPerfRegressionConfig(Path.GetFullPath(artifacts)));
        EnsureSuccessful(summary, CleanContracts.Count);
        var observations = summary.Reports.Select(ToObservation).ToArray();

        var run = new PerfMeasurementRun(
            PerfMeasurementRun.SchemaV1,
            runId,
            DateTimeOffset.UtcNow,
            baselineBuild,
            candidateBuild,
            Environment(summary, runnerClass, runnerImage),
            Workload(),
            observations);

        Write(output, PerfRegressionReportSerializer.SerializeRun(run));
        Console.WriteLine($"Clean measurement run written to {Path.GetFullPath(output)}");
        return 0;
    }

    private static int Diagnose(Options options)
    {
        var output = options.RequiredSingle("output");
        var artifacts = options.RequiredSingle("artifacts");
        var runnerClass = options.ValueOrDefault("runner-class", "local");
        var runnerImage = options.Single("runner-image");
        var candidateBuild = BuildIdentity(options, "candidate");

        using var diagnoser = new DotnetDiagnosticsDiagnoser();
        var summary = BenchmarkRunner.Run<PerfRegressionDiagnosticBenchmarks>(
            new DiagnosticPerfRegressionConfig(Path.GetFullPath(artifacts), diagnoser));
        EnsureSuccessful(summary, 2);

        Summary? waitSummary = null;
        var waitEntries = new List<BenchmarkDiagnosticEntry>();
        for (var launch = 1; launch <= WaitDiagnosticLaunches; launch++)
        {
            using var waitDiagnoser = new DotnetDiagnosticsDiagnoser();
            waitSummary = BenchmarkRunner.Run<PerfRegressionWaitDiagnosticBenchmarks>(
                new WaitDiagnosticPerfRegressionConfig(
                    Path.Combine(Path.GetFullPath(artifacts), $"wait-launch-{launch}"),
                    waitDiagnoser));
            EnsureSuccessful(waitSummary, 2);
            waitEntries.AddRange(waitDiagnoser.Entries);
        }

        var rows = new List<PerfDiagnosticAttribution>();
        foreach (var entry in diagnoser.Entries.Concat(waitEntries))
        {
            var contract = DiagnosticContracts.SingleOrDefault(pair =>
                entry.Benchmark.Contains(pair.Key, StringComparison.Ordinal)).Value;
            if (contract is null)
            {
                continue;
            }

            var artifactText = File.Exists(entry.ArtifactPath)
                ? File.ReadAllText(entry.ArtifactPath)
                : string.Empty;
            var threadPoolEvidence = string.Equals(entry.Kind, "threadpool", StringComparison.Ordinal)
                ? ThreadPoolPerfDiagnosticExtractor.Extract(artifactText)
                : null;
            var signals = threadPoolEvidence?.Signals ?? ExtractSignals(entry.Kind, artifactText);
            var matched = threadPoolEvidence is not null
                ? contract.IsControl
                    ? !threadPoolEvidence.HasCausalWait
                    : threadPoolEvidence.HasCausalWait
                : entry.Headline.Contains(contract.ExpectedEvidence, StringComparison.Ordinal)
                    || signals.Any(signal =>
                        (signal.StableId?.Contains(contract.ExpectedEvidence, StringComparison.Ordinal) ?? false)
                        || signal.DisplayName.Contains(contract.ExpectedEvidence, StringComparison.Ordinal));
            var relativeArtifactPath = Path.GetRelativePath(
                Path.GetDirectoryName(Path.GetFullPath(output))!,
                entry.ArtifactPath);
            rows.Add(new PerfDiagnosticAttribution(
                contract.Scenario,
                entry.Kind,
                entry.Headline,
                relativeArtifactPath,
                contract.ExpectedEvidence,
                matched,
                entry.IsError,
                signals,
                RawArtifact(entry.ArtifactPath, relativeArtifactPath),
                contract.IsControl));
        }

        var run = new PerfDiagnosticRun(
            PerfDiagnosticRun.SchemaV1,
            DateTimeOffset.UtcNow,
            candidateBuild,
            Environment(waitSummary!, runnerClass, runnerImage),
            Workload(),
            rows);
        Write(output, PerfRegressionReportSerializer.SerializeDiagnosticRun(run));
        Console.WriteLine($"Diagnostic attribution run written to {Path.GetFullPath(output)}");
        return 0;
    }

    private static int Report(Options options)
    {
        var runPaths = options.Values("run");
        if (runPaths.Count == 0)
        {
            throw new ArgumentException("At least one --run <measurement.json> is required.");
        }

        var runs = runPaths
            .Select(path => PerfRegressionReportSerializer.DeserializeRun(File.ReadAllText(path)))
            .ToArray();
        var diagnosticPath = options.Single("diagnostic");
        var diagnostic = diagnosticPath is null
            ? null
            : PerfRegressionReportSerializer.DeserializeDiagnosticRun(File.ReadAllText(diagnosticPath));
        var report = PerfRegressionAnalyzer.Analyze(runs, diagnostic);

        var jsonPath = options.RequiredSingle("output-json");
        var markdownPath = options.RequiredSingle("output-markdown");
        var markdown = PerfRegressionReportSerializer.BuildMarkdown(report);
        Write(jsonPath, PerfRegressionReportSerializer.SerializeReport(report));
        Write(markdownPath, markdown);
        Console.WriteLine(markdown);
        return 0;
    }

    private static PerfBenchmarkObservation ToObservation(BenchmarkReport report)
    {
        var method = report.BenchmarkCase.Descriptor.WorkloadMethod.Name;
        if (!CleanContracts.TryGetValue(method, out var contract))
        {
            throw new InvalidOperationException($"No perf-regression contract exists for benchmark method '{method}'.");
        }

        var statistics = report.ResultStatistics
            ?? throw new InvalidOperationException($"Benchmark '{method}' did not produce result statistics.");
        var allocated = report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase) ?? 0;
        return new PerfBenchmarkObservation(
            contract.Scenario,
            contract.Variant,
            contract.IsControl,
            statistics.Mean,
            allocated,
            statistics.StandardDeviation,
            statistics.N);
    }

    private static PerfEnvironmentProvenance Environment(
        Summary summary,
        string runnerClass,
        string? runnerImage)
    {
        var host = summary.HostEnvironmentInfo;
        var gcMode = $"server={host.IsServerGC};concurrent={host.IsConcurrentGC}";
        return new PerfEnvironmentProvenance(
            host.RuntimeVersion,
            RuntimeInformation.OSDescription,
            RuntimeInformation.RuntimeIdentifier,
            host.Architecture,
            gcMode,
            runnerClass,
            runnerImage);
    }

    private static PerfWorkloadProvenance Workload()
        => new(
            WorkloadId,
            WorkloadVersion,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["allocation-baseline-payload-count"] = "10",
                ["allocation-candidate-extra-payload-count"] = "2",
                ["cpu-lookup-values"] = "32",
                ["cpu-lookup-passes"] = "256",
                ["diagnostic-window-seconds"] = "5",
                ["wait-diagnostic-eventpipe-startup-delay-ms"] = "2000",
                ["wait-diagnostic-launches"] = WaitDiagnosticLaunches.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["wait-diagnostic-window-seconds"] = "8",
                ["wait-diagnostic-workers"] = "warmed-threadpool-thread-count-plus-8-clamped-12-64",
                ["wait-operation-count"] = "8",
                ["wait-operation-delay-ms"] = "1",
            });

    private static PerfBuildIdentity BuildIdentity(Options options, string prefix)
        => new(
            options.RequiredSingle($"{prefix}-build-id"),
            options.Single($"{prefix}-commit"),
            options.Single($"{prefix}-version"));

    private static IReadOnlyList<PerfDiagnosticSignal> ExtractSignals(string kind, string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.StartsWith("//", StringComparison.Ordinal))
        {
            return Array.Empty<PerfDiagnosticSignal>();
        }

        using var document = JsonDocument.Parse(json);
        return kind switch
        {
            "cpu" => CpuSignals(document.RootElement),
            "allocation" => AllocationSignals(document.RootElement),
            _ => Array.Empty<PerfDiagnosticSignal>(),
        };
    }

    private static List<PerfDiagnosticSignal> CpuSignals(JsonElement root)
    {
        var data = root.GetProperty("Data");
        var totalSamples = data.GetProperty("TotalSamples").GetInt64();
        var signals = new List<PerfDiagnosticSignal>
        {
            new("cpu.totalSamples", "Total CPU samples", null, totalSamples, "samples", PerfSignalDirection.Neutral),
        };
        var hotspots = data.GetProperty("TopHotspots")
            .EnumerateArray()
            .Where(static hotspot => string.Equals(
                hotspot.GetProperty("Frame").GetProperty("Module").GetString(),
                "DiagnosedBenchmarks",
                StringComparison.Ordinal))
            .ToArray();
        foreach (var hotspot in hotspots.Take(10))
        {
            var frame = hotspot.GetProperty("Frame");
            var module = frame.GetProperty("Module").GetString() ?? string.Empty;
            var method = frame.GetProperty("Method").GetString() ?? string.Empty;
            var stableId = $"{module}!{method}";
            var inclusive = hotspot.GetProperty("InclusiveSamples").GetInt64();
            signals.Add(new(
                "cpu.hotspot.inclusivePercent",
                method,
                stableId,
                Percent(inclusive, totalSamples),
                "%",
                PerfSignalDirection.Lower));
        }
        foreach (var hotspot in hotspots
                     .OrderByDescending(static row => row.GetProperty("ExclusiveSamples").GetInt64())
                     .Take(10))
        {
            var frame = hotspot.GetProperty("Frame");
            var module = frame.GetProperty("Module").GetString() ?? string.Empty;
            var method = frame.GetProperty("Method").GetString() ?? string.Empty;
            var stableId = $"{module}!{method}";
            var exclusive = hotspot.GetProperty("ExclusiveSamples").GetInt64();
            signals.Add(new(
                "cpu.hotspot.exclusivePercent",
                method,
                stableId,
                Percent(exclusive, totalSamples),
                "%",
                PerfSignalDirection.Lower));
        }
        return signals;
    }

    private static List<PerfDiagnosticSignal> AllocationSignals(JsonElement root)
    {
        var data = root.GetProperty("Data");
        var duration = TimeSpan.Parse(data.GetProperty("Duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var seconds = Math.Max(duration.TotalSeconds, 0.001);
        var signals = new List<PerfDiagnosticSignal>
        {
            new(
                "allocation.bytesPerSecond",
                "Total allocated bytes per second",
                null,
                Math.Round(data.GetProperty("TotalBytes").GetInt64() / seconds, 2),
                "B/s",
                PerfSignalDirection.Lower),
            new(
                "allocation.eventsPerSecond",
                "Allocation events per second",
                null,
                Math.Round(data.GetProperty("TotalEvents").GetInt64() / seconds, 2),
                "events/s",
                PerfSignalDirection.Lower),
        };

        foreach (var type in data.GetProperty("TopByBytes").EnumerateArray().Take(10))
        {
            var typeName = type.GetProperty("TypeName").GetString() ?? "<unknown>";
            signals.Add(new(
                "allocation.type.bytesPerSecond",
                typeName,
                typeName,
                Math.Round(type.GetProperty("TotalBytes").GetInt64() / seconds, 2),
                "B/s",
                PerfSignalDirection.Lower));
        }
        foreach (var site in data.GetProperty("TopBySite").EnumerateArray().Take(10))
        {
            var frame = site.GetProperty("Frame");
            var module = frame.GetProperty("Module").GetString() ?? string.Empty;
            var method = frame.GetProperty("Method").GetString() ?? string.Empty;
            signals.Add(new(
                "allocation.site.bytesPerSecond",
                method,
                $"{module}!{method}",
                Math.Round(site.GetProperty("TotalBytes").GetInt64() / seconds, 2),
                "B/s",
                PerfSignalDirection.Lower));
        }
        return signals;
    }

    private static double Percent(long value, long total)
        => total == 0 ? 0 : Math.Round(value * 100.0 / total, 2);

    private static PerfRawArtifactReference? RawArtifact(string fullPath, string relativePath)
    {
        if (!File.Exists(fullPath))
        {
            return null;
        }
        using var stream = File.OpenRead(fullPath);
        return new PerfRawArtifactReference(
            relativePath,
            stream.Length,
            Convert.ToHexStringLower(SHA256.HashData(stream)),
            RetentionDays: 30);
    }

    private static void EnsureSuccessful(Summary summary, int expectedReports)
    {
        var failed = summary.Reports.Where(static report => !report.Success).ToArray();
        if (failed.Length > 0)
        {
            throw new InvalidOperationException(
                $"BenchmarkDotNet reported {failed.Length} failed benchmark case(s): "
                + string.Join(", ", failed.Select(static report => report.BenchmarkCase.DisplayInfo)));
        }
        if (summary.Reports.Length != expectedReports)
        {
            throw new InvalidOperationException(
                $"BenchmarkDotNet produced {summary.Reports.Length} report(s); expected {expectedReports}.");
        }
    }

    private static void Write(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static int Usage(string error)
    {
        Console.Error.WriteLine(error);
        Console.Error.WriteLine(
            "Usage: perf-regression measure|diagnose|report|paired-report|calibration-report [--key value]. "
            + "Measure requires --run-id, --output, --artifacts, --baseline-build-id, --candidate-build-id. "
            + "Diagnose requires --output, --artifacts, --candidate-build-id. "
            + "Report requires repeated --run, optional --diagnostic, --output-json, --output-markdown. "
            + "Paired-report requires repeated --pair plus stage metrics and output paths.");
        return 2;
    }

    private sealed record BenchmarkContract(string Scenario, string Variant, bool IsControl);

    private sealed record DiagnosticContract(string Scenario, string ExpectedEvidence, bool IsControl = false);

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
                result.Add(key[2..], args[index + 1]);
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

        private void Add(string key, string value)
        {
            if (!_values.TryGetValue(key, out var values))
            {
                values = [];
                _values.Add(key, values);
            }
            values.Add(value);
        }
    }
}
