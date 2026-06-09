using BenchmarkDotNet.Configs;

namespace DotnetDiagnostics.BenchmarkDotNet;

/// <summary>
/// Idiomatic opt-in for the dotnet-diagnostics diagnoser — annotate a benchmark class with
/// <c>[DotnetDiagnosticsDiagnoser]</c> (just like <c>[MemoryDiagnoser]</c>) to attach the
/// in-process EventPipe capture and the per-run offenders report. Individual benchmark methods
/// still opt into a specific collect kind via <see cref="DiagnosticKindAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class DotnetDiagnosticsDiagnoserAttribute : Attribute, IConfigSource
{
    public DotnetDiagnosticsDiagnoserAttribute()
    {
        Config = ManualConfig.CreateEmpty().AddDiagnoser(new DotnetDiagnosticsDiagnoser());
    }

    public IConfig Config { get; }
}
