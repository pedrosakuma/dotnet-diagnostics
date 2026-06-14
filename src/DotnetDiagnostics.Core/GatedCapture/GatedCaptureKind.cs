using System.Diagnostics.CodeAnalysis;

namespace DotnetDiagnostics.Core.GatedCapture;

/// <summary>
/// The heavier artifact captured the moment a <see cref="TriggerPredicate"/> trips (issue #419).
/// Mirrors the existing one-shot capture tools; the captured artifact registers under the same
/// handle kinds so the standard <c>query_snapshot</c> drilldown reaches it.
/// </summary>
public enum GatedCaptureKind
{
    /// <summary>Write a process dump (mini) to disk via the diagnostic IPC channel.</summary>
    Dump,

    /// <summary>Collect a CPU sample (EventPipe SampleProfiler) and register a <c>cpu-sample</c> handle.</summary>
    CpuSample,

    /// <summary>Walk the live managed heap via ClrMD and register a <c>heap-snapshot</c> handle.</summary>
    Heap,

    /// <summary>Capture a thread + lock snapshot via ClrMD and register a <c>thread-snapshot</c> handle.</summary>
    ThreadSnapshot,
}

/// <summary>Parsing helpers for <see cref="GatedCaptureKind"/>.</summary>
public static class GatedCaptureKinds
{
    /// <summary>Canonical CLI/MCP tokens accepted by <see cref="TryParse"/>, in display order.</summary>
    public static readonly IReadOnlyList<string> Tokens = new[]
    {
        "dump", "cpu-sample", "heap", "thread-snapshot",
    };

    /// <summary>The canonical token for <paramref name="kind"/> (e.g. <c>cpu-sample</c>).</summary>
    public static string Token(GatedCaptureKind kind) => kind switch
    {
        GatedCaptureKind.Dump => "dump",
        GatedCaptureKind.CpuSample => "cpu-sample",
        GatedCaptureKind.Heap => "heap",
        GatedCaptureKind.ThreadSnapshot => "thread-snapshot",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Parses a capture-kind token (case-insensitive) into a <see cref="GatedCaptureKind"/>.</summary>
    public static bool TryParse(string? token, [NotNullWhen(true)] out GatedCaptureKind? kind)
    {
        kind = (token ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "dump" => GatedCaptureKind.Dump,
            "cpu-sample" or "cpusample" or "cpu" => GatedCaptureKind.CpuSample,
            "heap" or "heap-snapshot" => GatedCaptureKind.Heap,
            "thread-snapshot" or "threadsnapshot" or "threads" => GatedCaptureKind.ThreadSnapshot,
            _ => (GatedCaptureKind?)null,
        };

        return kind is not null;
    }
}
