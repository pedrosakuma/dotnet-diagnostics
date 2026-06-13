using System.Diagnostics.CodeAnalysis;

namespace DotnetDiagnostics.Core.GatedCapture;

/// <summary>
/// The single runtime metric a <see cref="TriggerPredicate"/> samples for bounded threshold-gated
/// capture (issue #419). Each value maps to one <c>System.Runtime</c> EventCounter so the whole
/// watch runs over a single EventPipe session — no OS-specific polling, works unchanged inside a
/// container sidecar.
/// </summary>
public enum GatedCaptureMetric
{
    /// <summary>Process CPU usage (%), from <c>System.Runtime/cpu-usage</c>.</summary>
    Cpu,

    /// <summary>Managed GC heap size in MB, from <c>System.Runtime/gc-heap-size</c>.</summary>
    GcHeapMb,

    /// <summary>Resident working set in MB, from <c>System.Runtime/working-set</c> (RSS proxy).</summary>
    RssMb,

    /// <summary>ThreadPool thread count, from <c>System.Runtime/threadpool-thread-count</c>.</summary>
    ThreadCount,

    /// <summary>Active timer count, from <c>System.Runtime/active-timer-count</c>.</summary>
    ActiveTimerCount,
}

/// <summary>Parsing + EventCounter mapping helpers for <see cref="GatedCaptureMetric"/>.</summary>
public static class GatedCaptureMetrics
{
    private const string Provider = "System.Runtime";

    /// <summary>Canonical predicate tokens accepted by <see cref="TryParse"/>, in display order.</summary>
    public static readonly IReadOnlyList<string> Tokens = new[]
    {
        "cpu", "gcHeapMb", "rssMb", "threadCount", "activeTimerCount",
    };

    /// <summary>The <c>System.Runtime</c> EventCounter that backs <paramref name="metric"/>.</summary>
    public static (string Provider, string Counter) Counter(GatedCaptureMetric metric) => metric switch
    {
        GatedCaptureMetric.Cpu => (Provider, "cpu-usage"),
        GatedCaptureMetric.GcHeapMb => (Provider, "gc-heap-size"),
        GatedCaptureMetric.RssMb => (Provider, "working-set"),
        GatedCaptureMetric.ThreadCount => (Provider, "threadpool-thread-count"),
        GatedCaptureMetric.ActiveTimerCount => (Provider, "active-timer-count"),
        _ => throw new ArgumentOutOfRangeException(nameof(metric)),
    };

    /// <summary>The canonical predicate token for <paramref name="metric"/> (e.g. <c>gcHeapMb</c>).</summary>
    public static string Token(GatedCaptureMetric metric) => metric switch
    {
        GatedCaptureMetric.Cpu => "cpu",
        GatedCaptureMetric.GcHeapMb => "gcHeapMb",
        GatedCaptureMetric.RssMb => "rssMb",
        GatedCaptureMetric.ThreadCount => "threadCount",
        GatedCaptureMetric.ActiveTimerCount => "activeTimerCount",
        _ => throw new ArgumentOutOfRangeException(nameof(metric)),
    };

    /// <summary>Parses a metric token (case-insensitive) into a <see cref="GatedCaptureMetric"/>.</summary>
    public static bool TryParse(string? token, [NotNullWhen(true)] out GatedCaptureMetric? metric)
    {
        metric = (token ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "cpu" => GatedCaptureMetric.Cpu,
            "gcheapmb" => GatedCaptureMetric.GcHeapMb,
            "rssmb" => GatedCaptureMetric.RssMb,
            "threadcount" => GatedCaptureMetric.ThreadCount,
            "activetimercount" => GatedCaptureMetric.ActiveTimerCount,
            _ => (GatedCaptureMetric?)null,
        };

        return metric is not null;
    }
}
