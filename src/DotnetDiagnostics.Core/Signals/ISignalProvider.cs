namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Derives zero or more <see cref="SignalGroup"/>s from a typed context built out of already-collected
/// diagnostic data. Providers are pure functions of their context (no I/O), so they can be re-run
/// cheaply — e.g. inline at collection time and again when a Resource is read for the same handle.
/// </summary>
/// <typeparam name="TContext">The collected-data context this provider inspects.</typeparam>
public interface ISignalProvider<in TContext>
{
    /// <summary>Returns the signal groupings this provider derives from <paramref name="context"/>, or an empty sequence.</summary>
    IEnumerable<SignalGroup> Detect(TContext context);
}
