namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Groups counters by <b>intra-window movement</b>: which counters moved the most between the first
/// and last observed value in the collection window (ThreadPool queue length climbing, working set
/// growing, contention count rising, …). Says <i>which counter moved and by how much</i>, never
/// <i>what that means</i> — no interpretation, no <c>suggestedFix</c>.
/// </summary>
/// <remarks>
/// Movement is graded by a scale-invariant ratio — <c>(last - first) / max(|first|, |last|)</c> — so a
/// single threshold works across counters with wildly different units (percent, bytes, item counts).
/// The ratio is bounded in <c>[-1, 1]</c>: it saturates at ±1 for a counter that went from (near) zero
/// to non-zero or vice versa, and is proportionally smaller for a same-sign change. Counters where both
/// values are (near) zero are skipped — nothing moved.
/// </remarks>
public sealed class CounterTrendProvider : ISignalProvider<CounterTrendContext>
{
    /// <summary>Minimum |relative change| (see remarks) for the top mover to be salient.</summary>
    public const double MinRelativeChange = 0.4;

    /// <summary>Floor below which both values are treated as "no signal" (avoids firing on float noise around zero).</summary>
    public const double ZeroFloor = 1e-6;

    private const int MaxBuckets = 5;

    /// <inheritdoc/>
    public IEnumerable<SignalGroup> Detect(CounterTrendContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Last is not { Count: > 0 } last || context.First is not { Count: > 0 } first || first.Count != last.Count)
        {
            yield break;
        }

        var moves = new List<(string Key, string? Unit, double Delta, double RelativeChange)>(last.Count);
        for (var i = 0; i < last.Count; i++)
        {
            var l = last[i];
            var f = first[i];
            var denom = Math.Max(Math.Abs(f.Value), Math.Abs(l.Value));
            if (denom < ZeroFloor)
            {
                continue;
            }

            var delta = l.Value - f.Value;
            var relativeChange = delta / denom;
            moves.Add((string.IsNullOrWhiteSpace(l.DisplayName) ? l.Name : l.DisplayName, l.Unit, delta, relativeChange));
        }

        if (moves.Count == 0)
        {
            yield break;
        }

        var ranked = moves.OrderByDescending(m => Math.Abs(m.RelativeChange)).ToArray();
        var top1 = ranked[0];
        if (Math.Abs(top1.RelativeChange) < MinRelativeChange)
        {
            yield break;
        }

        var buckets = ranked
            .Take(MaxBuckets)
            .Select(m => new SignalBucket(m.Key, m.Delta, m.Unit, context.HandleId))
            .ToArray();

        var direction = top1.Delta >= 0 ? "increased" : "decreased";
        var percent = Math.Round(Math.Abs(top1.RelativeChange) * 100.0, 1);
        yield return new SignalGroup(
            Signal: "counters.trend",
            Summary: $"{top1.Key} {direction} the most over the window ({percent:0.#}% of its own range, delta {top1.Delta:+0.###;-0.###}{top1.Unit}).",
            Salience: Math.Min(1.0, Math.Abs(top1.RelativeChange)),
            Buckets: buckets,
            NextAction: new NextActionHint(
                "query_snapshot",
                "Break the counter snapshot down by provider to see the full picture.",
                new Dictionary<string, object?> { ["handle"] = context.HandleId, ["view"] = "byProvider" }));
    }
}
