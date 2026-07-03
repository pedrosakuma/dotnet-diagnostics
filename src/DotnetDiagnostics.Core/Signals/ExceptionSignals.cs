using DotnetDiagnostics.Core.Exceptions;

namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Runs the registered exception <see cref="ISignalProvider{TContext}"/>s and returns a ranked, capped
/// set of <see cref="SignalGroup"/>s — the salient "vector" the engine forwards instead of the full
/// exception list. Single aggregation entry point used by both the standard exception stream
/// (<see cref="ExceptionSnapshot"/>, by-type only) and the crash-guard stream
/// (<see cref="CrashGuardSnapshot"/>, which additionally carries managed stacks so the throw-site
/// roll-up applies). Diagnosis-agnostic: it surfaces where exceptions concentrate (by type, and by
/// throw-site when stacks were resolved), never why they are thrown.
/// </summary>
public static class ExceptionSignals
{
    private static readonly IReadOnlyList<ISignalProvider<ExceptionSignalContext>> Providers =
        new ISignalProvider<ExceptionSignalContext>[]
        {
            new ExceptionByTypeConcentrationProvider(),
            new ExceptionByThrowSiteProvider(),
        };

    /// <summary>Derives signals from the standard exception stream (by-type only — no throw-site captured).</summary>
    public static IReadOnlyList<SignalGroup> Detect(ExceptionSnapshot snapshot, string handleId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Detect(new ExceptionSignalContext(
            snapshot.TotalExceptions,
            snapshot.ByType,
            handleId,
            ByTypeDrillView: "byType"));
    }

    /// <summary>
    /// Derives signals from the crash-guard stream. Adds the throw-site roll-up by grouping the
    /// retained exception events that carried a resolvable managed stack by
    /// <c>(type, innermost frame)</c> — best-effort, since live EventPipe stack resolution can be empty.
    /// </summary>
    public static IReadOnlyList<SignalGroup> Detect(CrashGuardSnapshot snapshot, string handleId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var withStack = snapshot.Exceptions
            .Where(e => e.ManagedStack is { Count: > 0 })
            .ToArray();

        var throwSites = withStack
            .GroupBy(e => (e.ExceptionType, Site: e.ManagedStack[0]))
            .Select(g => new ExceptionThrowSiteCount(g.Key.ExceptionType, g.Key.Site, g.LongCount()))
            .ToArray();

        return Detect(new ExceptionSignalContext(
            snapshot.TotalExceptions,
            snapshot.ByType,
            handleId,
            ByTypeDrillView: "exceptions",
            ThrowSites: throwSites,
            ThrowSiteSampleTotal: withStack.Length,
            RetainedEventCount: snapshot.Exceptions.Count));
    }

    /// <summary>Runs every registered provider over the context and ranks the union by salience.</summary>
    public static IReadOnlyList<SignalGroup> Detect(ExceptionSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SignalRanker.Rank(Providers.SelectMany(p => p.Detect(context)));
    }
}
