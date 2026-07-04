namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// One collector's contribution to a <see cref="CoOccurrenceContext"/>: its already-computed
/// signal groupings (possibly empty) and the handle they were registered under, tagged with the
/// collector kind (e.g. "counters", "gc", "exceptions") for the correlation summary/buckets.
/// </summary>
/// <param name="Kind">Short collector/tool label (e.g. "counters", "gc", "exceptions").</param>
/// <param name="HandleId">Drill-down handle the collector registered, if any.</param>
/// <param name="Signals">Signal groupings already derived for this collector's window (may be empty).</param>
public sealed record CorrelationSource(string Kind, string? HandleId, IReadOnlyList<SignalGroup> Signals);

/// <summary>
/// Already-collected, per-collector signal groupings captured over the <b>same window</b> (e.g. a
/// <c>collect_events(kind="sweep")</c> fan-out), for <see cref="ISignalProvider{TContext}"/>s that
/// correlate across them rather than within a single collector's own data.
/// </summary>
/// <param name="Sources">One entry per collector that ran in the window.</param>
public sealed record CoOccurrenceContext(IReadOnlyList<CorrelationSource> Sources);
