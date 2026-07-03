namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Groups CPU samples by <b>self-time concentration</b>: does exclusive (on-CPU) time pile up in a
/// few frames, or is it diffuse? Surfaces the top self-time frames with their share — a neutral
/// magnitude signal, not a diagnosis. The consumer reads "89% of self-time in one frame" and decides
/// what it means; a diffuse profile produces nothing (no noise on the wire).
/// </summary>
/// <remarks>
/// Keyed off self-time (exclusive), so it consults <see cref="CpuSignalContext.SelfTimeRanked"/> (the
/// full ranking, Resource path) when available and otherwise the uncapped global self-time leader
/// <see cref="CpuSignalContext.TopSelfTime"/> (inline path) — never the inclusive-capped hotspot list,
/// whose leaders are the invariant threadpool/dispatch roots.
/// </remarks>
public sealed class CpuSelfTimeConcentrationProvider : ISignalProvider<CpuSignalContext>
{
    /// <summary>Minimum top-1 self-time share for the profile to count as "concentrated" (else diffuse — nothing salient).</summary>
    public const double MinTop1Share = 0.15;

    private const int MaxBuckets = 5;

    /// <inheritdoc/>
    public IEnumerable<SignalGroup> Detect(CpuSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TotalSamples <= 0)
        {
            yield break;
        }

        var frames = new List<(string Method, long Exclusive)>();
        if (context.SelfTimeRanked is { Count: > 0 } ranked)
        {
            foreach (var m in ranked)
            {
                if (m.ExclusiveSamples <= 0)
                {
                    continue;
                }

                frames.Add((m.Method, m.ExclusiveSamples));
                if (frames.Count >= MaxBuckets)
                {
                    break;
                }
            }
        }
        else if (context.TopSelfTime is { ExclusiveSamples: > 0 } top)
        {
            frames.Add((top.Frame.Method, top.ExclusiveSamples));
        }

        if (frames.Count == 0)
        {
            yield break;
        }

        var top1Share = frames[0].Exclusive / (double)context.TotalSamples;
        if (top1Share < MinTop1Share)
        {
            yield break;
        }

        var buckets = frames
            .Select(f => new SignalBucket(f.Method, Math.Round(f.Exclusive * 100.0 / context.TotalSamples, 1), "%", context.HandleId))
            .ToArray();

        var summary = buckets.Length > 1
            ? $"CPU self-time is concentrated: {buckets[0].Magnitude:0.#}% in {frames[0].Method}; top {buckets.Length} frames account for {buckets.Sum(b => b.Magnitude):0.#}%."
            : $"CPU self-time is concentrated: {buckets[0].Magnitude:0.#}% in {frames[0].Method}.";

        yield return new SignalGroup(
            Signal: "cpu.self-time.concentration",
            Summary: summary,
            Salience: Math.Min(1.0, top1Share),
            Buckets: buckets,
            NextAction: new NextActionHint(
                "query_snapshot",
                "Rank methods by self-time (exclusive) and walk the call tree to the owning frame.",
                new Dictionary<string, object?> { ["handle"] = context.HandleId, ["view"] = "top-methods", ["rankBy"] = "exclusive" }));
    }
}
