using BenchmarkDotNet.Running;
using DiagnosedBenchmarks;

if (args.Length >= 2 && string.Equals(args[0], "--analyze-nettrace", StringComparison.Ordinal))
{
    Environment.ExitCode = NettraceSelfTimeAnalyzer.AnalyzeToConsole(
        tracePath: args[1],
        processNameHint: args.Length >= 3 ? args[2] : null,
        containsFilter: args.Length >= 4 ? args[3] : null);
    return;
}

if (args.Length >= 2 && string.Equals(args[0], "--list-nettrace-processes", StringComparison.Ordinal))
{
    Environment.ExitCode = NettraceSelfTimeAnalyzer.ListProcessesToConsole(args[1]);
    return;
}

BenchmarkSwitcher
    .FromAssembly(typeof(WorkloadBenchmarks).Assembly)
    .Run(args, new DiagnosedConfig());
