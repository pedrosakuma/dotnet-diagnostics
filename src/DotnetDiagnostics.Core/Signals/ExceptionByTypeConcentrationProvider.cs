namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Groups exceptions by <b>type concentration</b>: does one exception type dominate the stream, or is
/// the volume spread thin across many types? Surfaces the top types with their share — a neutral
/// magnitude signal, not a diagnosis. The consumer reads "80% of exceptions are one type" and decides
/// what it means; a diffuse stream (no type stands out) produces nothing (no noise on the wire).
/// </summary>
/// <remarks>
/// Keyed off the exact <see cref="ExceptionSignalContext.ByType"/> counts, so it works on both the
/// standard exception stream and the crash-guard stream. This says <i>which type</i> concentrates,
/// never <i>why</i> it is thrown.
/// </remarks>
public sealed class ExceptionByTypeConcentrationProvider : ISignalProvider<ExceptionSignalContext>
{
    /// <summary>Minimum top-1 type share for the stream to count as "concentrated" (else diffuse — nothing salient).</summary>
    public const double MinTop1Share = 0.5;

    /// <summary>Floor on total exceptions before a concentration is worth surfacing (avoids firing on 1-2 events).</summary>
    public const long MinTotalExceptions = 3;

    private const int MaxBuckets = 5;

    /// <inheritdoc/>
    public IEnumerable<SignalGroup> Detect(ExceptionSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TotalExceptions < MinTotalExceptions || context.ByType is not { Count: > 0 })
        {
            yield break;
        }

        var ranked = context.ByType
            .Where(c => c.Count > 0)
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.ExceptionType, StringComparer.Ordinal)
            .ToArray();

        if (ranked.Length == 0)
        {
            yield break;
        }

        var top1Share = ranked[0].Count / (double)context.TotalExceptions;
        if (top1Share < MinTop1Share)
        {
            yield break;
        }

        var buckets = ranked
            .Take(MaxBuckets)
            .Select(c => new SignalBucket(c.ExceptionType, c.Count, "exceptions", context.HandleId))
            .ToArray();

        var top1Percent = Math.Round(top1Share * 100.0, 1);
        yield return new SignalGroup(
            Signal: "exceptions.by-type",
            Summary: $"Exceptions concentrate on one type: {ranked[0].ExceptionType} is {top1Percent:0.#}% ({ranked[0].Count}/{context.TotalExceptions}).",
            Salience: Math.Min(1.0, top1Share),
            Buckets: buckets,
            NextAction: new NextActionHint(
                "query_snapshot",
                "Drill into the exception snapshot to inspect the dominant type and its individual events.",
                new Dictionary<string, object?> { ["handle"] = context.HandleId, ["view"] = context.ByTypeDrillView }));
    }
}
