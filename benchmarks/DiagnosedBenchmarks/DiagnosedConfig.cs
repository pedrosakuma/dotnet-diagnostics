using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using DotnetDiagnostics.BenchmarkDotNet;

namespace DiagnosedBenchmarks;

/// <summary>
/// A diagnostic-oriented BenchmarkDotNet config: a short <see cref="RunStrategy.Monitoring"/> job
/// whose actual-run window is long enough for collector hotpaths to do meaningful work, plus the
/// reusable dotnet-diagnostics diagnoser and the native <see cref="MemoryDiagnoser"/> side by side.
/// </summary>
public sealed class DiagnosedConfig : ManualConfig
{
    public DiagnosedConfig()
    {
        AddJob(Job.Default
            .WithStrategy(RunStrategy.Monitoring)
            .WithWarmupCount(1)
            .WithIterationCount(1)
            .WithInvocationCount(1)
            .WithUnrollFactor(1)
            .WithId("Diagnose"));

        AddDiagnoser(new DotnetDiagnosticsDiagnoser());
        AddDiagnoser(MemoryDiagnoser.Default);

        AddLogger(ConsoleLogger.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddExporter(MarkdownExporter.GitHub);
    }
}
