namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// One salient, <b>diagnosis-agnostic</b> signal grouping — the compact "vector" the engine forwards
/// instead of the full raw payload. Think edge / IoT: a huge volume of raw signal is captured, but
/// only the dimensions that <i>stand out</i> are pushed forward, so the consumer (LLM / human) sees
/// where a signal concentrates / how signals co-move and drills only where it matters.
/// </summary>
/// <remarks>
/// A signal group <b>describes a grouping, it does not diagnose</b>. It never names a root cause or
/// prescribes a fix — that is the consumer's job. The value is dimensionality reduction and
/// correlation (concentration, roll-ups by a neutral dimension, trends, cross-signal co-occurrence),
/// not classification. Two reasons this stays heuristic and diagnosis-agnostic: (a) plain grouping
/// already yields something a human or LLM recognizes instantly; (b) any predictive model is
/// structurally hard to train — the ground-truth label ("was this the real problem?") only ever comes
/// from the very consumer that already saw the signal, so any accumulated dataset is contaminated by
/// the suggestion (consumer-side leakage). Signals are advisory: every <see cref="Buckets"/> member
/// references a drill-down handle, so the consumer can always drill and disagree.
/// </remarks>
/// <param name="Signal">
/// Stable id of the <b>grouping dimension</b> (not a diagnosis), e.g. <c>cpu.self-time.concentration</c>
/// or <c>cpu.self-time.by-namespace</c>.
/// </param>
/// <param name="Summary">One-line description of what stands out — no asserted cause, no prescribed fix.</param>
/// <param name="Salience">How far this grouping stands out, in <c>[0, 1]</c> (magnitude / concentration), not bug-severity.</param>
/// <param name="Buckets">The top members of the grouping, each with a magnitude and a drill-down handle.</param>
/// <param name="NextAction">Optional neutral pointer at the next tool call to drill into the grouping.</param>
public sealed record SignalGroup(
    string Signal,
    string Summary,
    double Salience,
    IReadOnlyList<SignalBucket> Buckets,
    NextActionHint? NextAction = null);

/// <summary>
/// One member of a <see cref="SignalGroup"/> — a grouped key with its magnitude. Keeps the payload
/// small by referencing a drill-down <see cref="Handle"/> instead of inlining the underlying blob.
/// </summary>
/// <param name="Key">The grouped key (e.g. a namespace, a method frame, an exception type).</param>
/// <param name="Magnitude">The member's magnitude within the grouping (e.g. a percentage or a count).</param>
/// <param name="Unit">Optional unit for <see cref="Magnitude"/> (e.g. "%", "samples", "bytes").</param>
/// <param name="Handle">Optional drill-down handle the member was derived from.</param>
public sealed record SignalBucket(
    string Key,
    double Magnitude,
    string? Unit = null,
    string? Handle = null);
