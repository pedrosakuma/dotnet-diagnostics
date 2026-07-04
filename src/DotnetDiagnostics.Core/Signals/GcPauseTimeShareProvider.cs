namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Surfaces the <b>share of the collection window spent paused</b> for GC — a neutral magnitude
/// signal, not a diagnosis. The consumer reads "12% of the window was GC pause time" and decides
/// whether that is a problem for this workload; a low share produces nothing.
/// </summary>
public sealed class GcPauseTimeShareProvider : ISignalProvider<GcSignalContext>
{
    /// <summary>Minimum pause-time share of the window for the signal to be salient.</summary>
    public const double MinShare = 0.05;

    /// <inheritdoc/>
    public IEnumerable<SignalGroup> Detect(GcSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Duration <= TimeSpan.Zero || context.TotalCollections == 0)
        {
            yield break;
        }

        var share = context.TotalPauseTime.TotalSeconds / context.Duration.TotalSeconds;
        if (share < MinShare)
        {
            yield break;
        }

        var percent = Math.Round(share * 100.0, 1);
        yield return new SignalGroup(
            Signal: "gc.pause-time-share",
            Summary: $"GC pause time is {percent:0.#}% of the window ({context.TotalPauseTime.TotalMilliseconds:F0}ms of {context.Duration.TotalMilliseconds:F0}ms, {context.TotalCollections} collection(s)).",
            Salience: Math.Min(1.0, share),
            Buckets: new[] { new SignalBucket("pause-time", percent, "%", context.HandleId) },
            NextAction: new NextActionHint(
                "query_snapshot",
                "Inspect the pause histogram and longest individual pauses.",
                new Dictionary<string, object?> { ["handle"] = context.HandleId, ["view"] = "pauseHistogram" }));
    }
}
