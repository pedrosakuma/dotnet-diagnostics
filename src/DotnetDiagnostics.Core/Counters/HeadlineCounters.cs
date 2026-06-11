namespace DotnetDiagnostics.Core.Counters;

/// <summary>
/// Filters a <see cref="CounterSnapshot"/> down to the "headline" counters the LLM needs to
/// triage cheaply at <see cref="DotnetDiagnostics.Core.Collection.SamplingDepth.Summary"/>:
/// CPU, working set, GC heap, gen-2 collections, thread-pool depth, active timers, exceptions/sec,
/// monitor lock contention, plus ASP.NET Core request rate / failures and Meter p95 request
/// duration. Everything else stays reachable via the handle store.
/// </summary>
/// <remarks>
/// Lives in Core (moved out of the Server tool layer in #288) so the host-neutral
/// <see cref="DotnetDiagnostics.Core.UseCases.EventCollectionUseCases"/> can summarise a counter
/// snapshot without any MCP knowledge; the Server triage tool consumes it from here too.
/// </remarks>
public static class HeadlineCounters
{
    private static readonly HashSet<(string Provider, string Name)> Headline = new()
    {
        ("System.Runtime", "cpu-usage"),
        ("System.Runtime", "working-set"),
        ("System.Runtime", "gc-heap-size"),
        ("System.Runtime", "gen-2-gc-count"),
        ("System.Runtime", "time-in-gc"),
        ("System.Runtime", "alloc-rate"),
        ("System.Runtime", "threadpool-thread-count"),
        ("System.Runtime", "threadpool-queue-length"),
        ("System.Runtime", "active-timer-count"),
        ("System.Runtime", "exception-count"),
        ("System.Runtime", "monitor-lock-contention-count"),
        ("Microsoft.AspNetCore.Hosting", "requests-per-second"),
        ("Microsoft.AspNetCore.Hosting", "failed-requests"),
        ("Microsoft.AspNetCore.Hosting", "current-requests"),
        ("Microsoft-AspNetCore-Server-Kestrel", "connections-per-second"),
    };

    public static IReadOnlyList<CounterValue> FilterCounters(IReadOnlyList<CounterValue> all)
    {
        var hits = new List<CounterValue>(Headline.Count);
        foreach (var c in all)
        {
            if (Headline.Contains((c.Provider, c.Name)))
            {
                hits.Add(c);
            }
        }

        return hits;
    }

    public static IReadOnlyList<MeterInstrumentValue> FilterMeters(IReadOnlyList<MeterInstrumentValue> all)
        => all.Where(IsHeadlineMeter).ToList();

    public static MeterInstrumentValue? FindRequestDuration(IReadOnlyList<MeterInstrumentValue> all)
        => all.FirstOrDefault(IsHeadlineMeter);

    private static bool IsHeadlineMeter(MeterInstrumentValue meter)
        => string.Equals(meter.Instrument, "http.server.request.duration", StringComparison.Ordinal) &&
           meter.Histogram is not null;
}
