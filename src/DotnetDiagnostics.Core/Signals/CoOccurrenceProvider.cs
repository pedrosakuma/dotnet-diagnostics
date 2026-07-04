namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Correlates already-derived per-collector signal groupings captured in the <b>same window</b>
/// (e.g. one <c>collect_events(kind="sweep")</c> fan-out): does more than one collector's own
/// signal grouping stand out at once? Says <i>which groupings co-occurred</i>, never <i>why</i> —
/// no causal inference, purely an observed co-occurrence over signals each collector already
/// computed independently.
/// </summary>
/// <remarks>
/// Only fires when at least <see cref="MinSources"/> collectors each produced a non-empty signal
/// grouping in the window — a single salient collector (the common case) never triggers a spurious
/// correlation. Salience is the <i>minimum</i> of the contributing groupings' top salience, so the
/// correlation is never rated higher than its weakest ingredient.
/// </remarks>
public sealed class CoOccurrenceProvider : ISignalProvider<CoOccurrenceContext>
{
    /// <summary>Minimum number of collectors that must each have a non-empty signal grouping.</summary>
    public const int MinSources = 2;

    private const int MaxBuckets = 5;

    /// <inheritdoc/>
    public IEnumerable<SignalGroup> Detect(CoOccurrenceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var salient = context.Sources
            .Where(s => s.Signals.Count > 0 && s.HandleId is not null)
            .ToArray();

        if (salient.Length < MinSources)
        {
            yield break;
        }

        var buckets = salient
            .Take(MaxBuckets)
            .Select(s => new SignalBucket(
                $"{s.Kind}: {s.Signals[0].Signal}",
                s.Signals.Max(g => g.Salience),
                null,
                s.HandleId))
            .ToArray();

        var salience = salient.Min(s => s.Signals.Max(g => g.Salience));
        var kinds = string.Join(", ", salient.Select(s => s.Kind));

        yield return new SignalGroup(
            Signal: "correlation.co-occurrence",
            Summary: $"{salient.Length} signal grouping(s) stood out together in this window: {kinds}.",
            Salience: salience,
            Buckets: buckets,
            NextAction: null);
    }
}
