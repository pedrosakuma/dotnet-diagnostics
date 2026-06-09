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
/// A diagnostic-oriented BenchmarkDotNet config: a single <see cref="RunStrategy.Monitoring"/> job
/// whose actual-run window is long enough for an EventPipe collection to observe real work, plus
/// the <see cref="DotnetDiagnosticsDiagnoser"/> (in-process drill-down) and the native
/// <see cref="MemoryDiagnoser"/> (clean allocation numbers) side by side.
///
/// <para>
/// The monitoring job's timing is deliberately NOT publication-grade — it exists so the diagnostic
/// collectors have a stable, multi-second window to attach to. For real measurement use the default
/// config; for diagnosis, run with this one.
/// </para>
/// </summary>
public sealed class DiagnosedConfig : ManualConfig
{
    public DiagnosedConfig()
    {
        AddJob(Job.Default
            .WithStrategy(RunStrategy.Monitoring)
            .WithWarmupCount(1)
            .WithIterationCount(2)
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
