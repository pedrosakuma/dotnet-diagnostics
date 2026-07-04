using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.Core.Signals;

/// <summary>
/// Runs the registered allocation <see cref="ISignalProvider{TContext}"/>s and returns a ranked,
/// capped set of <see cref="SignalGroup"/>s — the salient "vector" the engine forwards instead of the
/// full ranked type/site lists. Diagnosis-agnostic: surfaces where allocated bytes concentrate (by
/// type, by call site), never why.
/// </summary>
public static class AllocationSignals
{
    private static readonly IReadOnlyList<ISignalProvider<AllocationSignalContext>> Providers =
        new ISignalProvider<AllocationSignalContext>[]
        {
            new AllocationByTypeConcentrationProvider(),
            new AllocationBySiteConcentrationProvider(),
        };

    /// <summary>Derives signals from the compact allocation-sample summary.</summary>
    public static IReadOnlyList<SignalGroup> Detect(AllocationSample sample, string handleId)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return Detect(new AllocationSignalContext(sample.TotalBytes, sample.TopByBytes, sample.TopBySite, handleId));
    }

    /// <summary>Runs every registered provider over the context and ranks the union by salience.</summary>
    public static IReadOnlyList<SignalGroup> Detect(AllocationSignalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SignalRanker.Rank(Providers.SelectMany(p => p.Detect(context)));
    }
}
