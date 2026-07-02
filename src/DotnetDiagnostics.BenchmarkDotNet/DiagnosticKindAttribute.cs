namespace DotnetDiagnostics.BenchmarkDotNet;

/// <summary>
/// Marks a benchmark method with the dotnet-diagnostics kind(s) that best explain its workload (e.g.
/// <c>gc</c> for an allocation-heavy method, <c>contention</c> for a lock storm). The
/// <see cref="DotnetDiagnosticsDiagnoser"/> reads this to decide which EventPipe collector to attach
/// <b>in-process</b> to the benchmark's child process while it runs.
/// </summary>
/// <remarks>
/// Prefer the type-safe <see cref="BenchmarkDiagnosticKind"/> overload
/// (<c>[DiagnosticKind(BenchmarkDiagnosticKind.Gc, BenchmarkDiagnosticKind.Contention)]</c>) — it is
/// discoverable via IntelliSense and validated at compile time. The <c>string</c> overload remains for
/// back-compat; an unknown token there only fails at BenchmarkDotNet validation time.
/// <para>
/// EventPipe collectors must not run concurrently against the same PID, so multiple kinds are
/// collected sequentially within the measurement window. Keep the count small (1–2) and the per-kind
/// duration short relative to the job's actual-run length.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DiagnosticKindAttribute : Attribute
{
    /// <summary>Default per-kind collection window in seconds.</summary>
    public const int DefaultDurationSeconds = 5;

    /// <param name="kinds">Comma-separated list of <c>collect</c> kinds (e.g. <c>"gc"</c> or <c>"gc,counters"</c>).</param>
    /// <param name="durationSeconds">Per-kind collection window in seconds (must be &gt;= 1).</param>
    public DiagnosticKindAttribute(string kinds, int durationSeconds = DefaultDurationSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kinds);
        Kinds = kinds;
        DurationSeconds = durationSeconds;
        KindList = kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Type-safe, IntelliSense-discoverable overload. Pass one or more <see cref="BenchmarkDiagnosticKind"/>
    /// values (e.g. <c>[DiagnosticKind(BenchmarkDiagnosticKind.Gc, BenchmarkDiagnosticKind.Contention)]</c>).
    /// Override the default window per-kind with the named <see cref="DurationSeconds"/> argument.
    /// </summary>
    /// <param name="kinds">One or more diagnostic kinds; must be non-empty.</param>
    public DiagnosticKindAttribute(params BenchmarkDiagnosticKind[] kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Length == 0)
        {
            throw new ArgumentException("At least one diagnostic kind is required.", nameof(kinds));
        }

        var tokens = Array.ConvertAll(kinds, BenchmarkDiagnosticKinds.Token);
        Kinds = string.Join(',', tokens);
        DurationSeconds = DefaultDurationSeconds;
        KindList = tokens;
    }

    /// <summary>Comma-separated list of <c>collect</c> kinds (e.g. <c>"gc"</c> or <c>"gc,counters"</c>).</summary>
    public string Kinds { get; }

    /// <summary>
    /// Per-kind collection window in seconds. Settable so the <see cref="BenchmarkDiagnosticKind"/>
    /// overload can override it via a named argument (<c>DurationSeconds = 8</c>).
    /// </summary>
    public int DurationSeconds { get; set; }

    /// <summary>The parsed, trimmed, non-empty kinds.</summary>
    public IReadOnlyList<string> KindList { get; }
}
