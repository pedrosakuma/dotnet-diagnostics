using DotnetDiagnostics.Core.Exceptions;

namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Already-collected exception data a <see cref="ISignalProvider{TContext}"/> groups over. Two
/// fidelities are carried because the two exception collectors expose different dimensions: the
/// standard exception stream (<see cref="Exceptions.EventPipeExceptionCollector"/>) captures only
/// <see cref="ByType"/> (exact, always available); the crash-guard stream additionally resolves a
/// managed stack per event, so its <see cref="ThrowSites"/> add the neutral <i>throw-site</i>
/// dimension (best-effort — live EventPipe stack resolution can come back empty).
/// </summary>
/// <param name="TotalExceptions">Exact total exceptions observed in the window.</param>
/// <param name="ByType">Exact per-type counts (both collectors).</param>
/// <param name="HandleId">Drill-down handle the snapshot was registered under, referenced by every bucket.</param>
/// <param name="ByTypeDrillView">The <c>query_snapshot</c> view that drills the by-type grouping for this snapshot kind (e.g. <c>byType</c> for the exception stream, <c>exceptions</c> for crash-guard).</param>
/// <param name="ThrowSites">
/// Per <c>(type, throw-site frame)</c> counts, available only when managed stacks were resolved
/// (crash-guard path). <c>null</c> on the standard exception stream, which carries no stack.
/// </param>
/// <param name="ThrowSiteSampleTotal">
/// Number of exception events that carried a resolvable stack and were folded into
/// <see cref="ThrowSites"/>. May be less than <see cref="TotalExceptions"/> because the retained
/// event list is capped and some events resolve no stack — throw-site shares are relative to this.
/// </param>
/// <param name="RetainedEventCount">
/// Number of retained exception events available behind the handle (the crash-guard
/// <c>exceptions</c> drilldown source). Used to size the throw-site drilldown's <c>topN</c> so it is
/// guaranteed to return the events behind the dominant bucket. <c>0</c> on the standard stream.
/// </param>
public sealed record ExceptionSignalContext(
    long TotalExceptions,
    IReadOnlyList<ExceptionCount> ByType,
    string HandleId,
    string ByTypeDrillView,
    IReadOnlyList<ExceptionThrowSiteCount>? ThrowSites = null,
    long ThrowSiteSampleTotal = 0,
    int RetainedEventCount = 0);

/// <summary>One <c>(exception type, throw-site frame)</c> grouping with its observed count.</summary>
/// <param name="ExceptionType">The exception type.</param>
/// <param name="ThrowSite">The innermost (throw) managed frame the exception originated from.</param>
/// <param name="Count">How many retained events fell into this grouping.</param>
public sealed record ExceptionThrowSiteCount(
    string ExceptionType,
    string ThrowSite,
    long Count);
