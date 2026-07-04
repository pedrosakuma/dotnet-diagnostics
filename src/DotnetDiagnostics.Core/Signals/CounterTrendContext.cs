using DotnetDiagnostics.Core.Counters;

namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Already-collected counter data a <see cref="ISignalProvider{TContext}"/> groups over.
/// <see cref="First"/> and <see cref="Last"/> are the first- and last-observed value per counter
/// within the collection window (same key set, same order) — the intra-window trend the provider
/// grades, in the absence of a supplied baseline (see issue #527's non-goal: no automatic wiring to
/// <c>compare_to_baseline</c>, which is a client-driven, JSON-supplied comparison).
/// </summary>
/// <param name="First">First-observed value per counter (may equal <see cref="Last"/> when only one tick fired).</param>
/// <param name="Last">Last-observed value per counter (what <see cref="CounterSnapshot.Counters"/> already carries).</param>
/// <param name="HandleId">Drill-down handle the snapshot was registered under, referenced by every bucket.</param>
public sealed record CounterTrendContext(
    IReadOnlyList<CounterValue> First,
    IReadOnlyList<CounterValue> Last,
    string HandleId);
