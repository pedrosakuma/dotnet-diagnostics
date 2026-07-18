using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using DotnetDiagnostics.BenchmarkDotNet;

namespace DiagnosedBenchmarks;

internal sealed class CleanPerfRegressionConfig : ManualConfig
{
    public CleanPerfRegressionConfig(string artifactsPath)
    {
        ArtifactsPath = artifactsPath;
        AddJob(Job.Default
            .WithWarmupCount(3)
            .WithIterationCount(5)
            .WithLaunchCount(1)
            .WithId("Clean"));
        AddDiagnoser(MemoryDiagnoser.Default);
        AddLogger(ConsoleLogger.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddExporter(MarkdownExporter.GitHub);
    }
}

internal sealed class DiagnosticPerfRegressionConfig : ManualConfig
{
    public DiagnosticPerfRegressionConfig(string artifactsPath, DotnetDiagnosticsDiagnoser diagnoser)
    {
        ArtifactsPath = artifactsPath;
        AddJob(Job.Default
            .WithStrategy(RunStrategy.Monitoring)
            .WithWarmupCount(0)
            .WithIterationCount(1)
            .WithInvocationCount(1)
            .WithUnrollFactor(1)
            .WithId("Diagnose"));
        AddDiagnoser(diagnoser);
        AddLogger(ConsoleLogger.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddExporter(MarkdownExporter.GitHub);
    }
}

internal sealed class WaitDiagnosticPerfRegressionConfig : ManualConfig
{
    public WaitDiagnosticPerfRegressionConfig(string artifactsPath, DotnetDiagnosticsDiagnoser diagnoser)
    {
        ArtifactsPath = artifactsPath;
        AddJob(Job.Default
            .WithStrategy(RunStrategy.Monitoring)
            .WithWarmupCount(0)
            .WithIterationCount(1)
            .WithInvocationCount(1)
            .WithUnrollFactor(1)
            .WithLaunchCount(1)
            .WithId("DiagnoseWait"));
        AddDiagnoser(diagnoser);
        AddLogger(ConsoleLogger.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddExporter(MarkdownExporter.GitHub);
    }
}
