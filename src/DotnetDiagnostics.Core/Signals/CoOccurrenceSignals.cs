namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Runs the registered cross-signal correlation <see cref="ISignalProvider{TContext}"/>s (same-
/// window co-occurrence; see #528) and returns a ranked, capped set of <see cref="SignalGroup"/>s.
/// </summary>
public static class CoOccurrenceSignals
{
    private static readonly IReadOnlyList<ISignalProvider<CoOccurrenceContext>> Providers =
        new ISignalProvider<CoOccurrenceContext>[]
        {
            new CoOccurrenceProvider(),
        };

    /// <summary>Runs every registered provider over the context and ranks the union by salience.</summary>
    public static IReadOnlyList<SignalGroup> Detect(CoOccurrenceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SignalRanker.Rank(Providers.SelectMany(p => p.Detect(context)));
    }
}
