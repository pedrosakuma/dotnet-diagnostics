using BenchmarkDotNet.Running;
using DiagnosedBenchmarks;

if (args.Length >= 2 && string.Equals(args[0], "--analyze-trace", StringComparison.Ordinal))
{
    var report = HotpathTraceAnalyzer.Analyze(args[1]);
    Console.WriteLine($"{report.TracePath} | pid {report.ProcessId} | samples {report.TotalSamples}");
    foreach (var method in report.Methods)
    {
        Console.WriteLine($"{method.ExclusivePercent,5:0.0}% {method.Method}");
    }

    return;
}

var runStartedUtc = DateTimeOffset.UtcNow;
BenchmarkSwitcher
    .FromAssembly(typeof(WorkloadBenchmarks).Assembly)
    .Run(args, new DiagnosedConfig());

HotpathTraceAnalyzer.PrintRecentReports(
    Path.Combine(Directory.GetCurrentDirectory(), "BenchmarkDotNet.Artifacts"),
    runStartedUtc);
