using BenchmarkDotNet.Running;
using DiagnosedBenchmarks;

BenchmarkSwitcher
    .FromAssembly(typeof(WorkloadBenchmarks).Assembly)
    .Run(args, new DiagnosedConfig());
