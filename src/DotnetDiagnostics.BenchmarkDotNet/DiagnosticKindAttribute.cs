namespace DotnetDiagnostics.BenchmarkDotNet;

/// <summary>
/// Marks a benchmark method with the dotnet-diagnostics <c>collect</c> kind that best explains its
/// workload (e.g. <c>gc</c> for an allocation-heavy method, <c>contention</c> for a lock storm).
/// The <see cref="DotnetDiagnosticsDiagnoser"/> reads this to decide which EventPipe collector to
/// attach <b>in-process</b> to the benchmark's child process while it runs.
/// </summary>
/// <remarks>
/// EventPipe collectors must not run concurrently against the same PID, so multiple kinds are
/// collected sequentially within the measurement window. Keep the count small (1–2) and the
/// per-kind duration short relative to the job's actual-run length.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DiagnosticKindAttribute : Attribute
{
    /// <param name="kinds">Comma-separated list of <c>collect</c> kinds (e.g. <c>"gc"</c> or <c>"gc,counters"</c>).</param>
    /// <param name="durationSeconds">Per-kind collection window in seconds (must be &gt;= 1).</param>
    public DiagnosticKindAttribute(string kinds, int durationSeconds = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kinds);
        Kinds = kinds;
        DurationSeconds = durationSeconds;
        KindList = kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Comma-separated list of <c>collect</c> kinds (e.g. <c>"gc"</c> or <c>"gc,counters"</c>).</summary>
    public string Kinds { get; }

    /// <summary>Per-kind collection window in seconds.</summary>
    public int DurationSeconds { get; }

    /// <summary>The parsed, trimmed, non-empty kinds.</summary>
    public IReadOnlyList<string> KindList { get; }
}
