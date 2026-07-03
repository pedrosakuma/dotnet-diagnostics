namespace DotnetDiagnostics.Core.Findings;

/// <summary>
/// Detects zero or more <see cref="Finding"/>s from a typed context built out of already-collected
/// diagnostic data. Providers are pure functions of their context (no I/O), so they can be re-run
/// cheaply — e.g. inline at collection time and again when a Resource is read for the same handle.
/// </summary>
/// <typeparam name="TContext">The collected-data context this provider inspects.</typeparam>
public interface IFindingProvider<in TContext>
{
    /// <summary>Returns the findings this provider detects in <paramref name="context"/>, or an empty sequence.</summary>
    IEnumerable<Finding> Detect(TContext context);
}
