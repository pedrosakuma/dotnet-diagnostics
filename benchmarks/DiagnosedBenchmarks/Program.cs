using BenchmarkDotNet.Running;
using DiagnosedBenchmarks;

if (args.Length > 0 && string.Equals(args[0], "loadgen", StringComparison.OrdinalIgnoreCase))
{
    return await SampleLoadGenerator.RunAsync(args[1..]).ConfigureAwait(false);
}

if (args.Length > 0 && string.Equals(args[0], "analyze-traces", StringComparison.OrdinalIgnoreCase))
{
    return await HotpathTraceAnalyzer.RunAsync(args[1..]).ConfigureAwait(false);
}

if (args.Length >= 2 && string.Equals(args[0], "--analyze-nettrace", StringComparison.Ordinal))
{
    return NettraceSelfTimeAnalyzer.AnalyzeToConsole(
        tracePath: args[1],
        processNameHint: args.Length >= 3 ? args[2] : null,
        containsFilter: args.Length >= 4 ? args[3] : null);
}

if (args.Length >= 2 && string.Equals(args[0], "--list-nettrace-processes", StringComparison.Ordinal))
{
    return NettraceSelfTimeAnalyzer.ListProcessesToConsole(args[1]);
}

if (args.Length >= 2 && string.Equals(args[0], "--analyze-trace", StringComparison.Ordinal))
{
    var report = GroupCHotpathTraceAnalyzer.Analyze(args[1]);
    Console.WriteLine($"{report.TracePath} | pid {report.ProcessId} | samples {report.TotalSamples}");
    foreach (var method in report.Methods)
    {
        Console.WriteLine($"{method.ExclusivePercent,5:0.0}% {method.Method}");
    }

    return 0;
}

var runStartedUtc = DateTimeOffset.UtcNow;

BenchmarkSwitcher
    .FromAssembly(typeof(WorkloadBenchmarks).Assembly)
    .Run(args, new DiagnosedConfig());

GroupCHotpathTraceAnalyzer.PrintRecentReports(
    Path.Combine(Directory.GetCurrentDirectory(), "BenchmarkDotNet.Artifacts"),
    runStartedUtc);

return 0;
