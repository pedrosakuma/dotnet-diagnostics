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

BenchmarkSwitcher
    .FromAssembly(typeof(WorkloadBenchmarks).Assembly)
    .Run(args, new DiagnosedConfig());

return 0;
