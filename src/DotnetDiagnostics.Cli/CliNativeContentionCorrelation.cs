using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.NativeLockContention;
using DotnetDiagnostics.Core.OffCpu;

namespace DotnetDiagnostics.Cli;

/// <summary>
/// Session-scoped cross-collector native-synchronization evidence for the <c>session</c> REPL
/// (issue #855). Mirrors <see cref="CliInvestigationDigestFormatter"/>'s pattern exactly: rather
/// than adding a new multi-kind <c>collect</c> verb, the REPL already shares one resolved process
/// and one <see cref="IDiagnosticHandleStore"/> across back-to-back
/// <c>collect --kind native-lock-contention</c> and <c>collect --kind off_cpu</c> invocations; this
/// reuses the same host-neutral <see cref="NativeLockContentionUx.CorrelateBatchEvidence"/> the MCP
/// <c>collect_batch</c> tool calls (<c>CollectBatchSalientEvidence.ApplyNativeContentionEvidence</c>)
/// so the evidence-taxonomy invariant lives in exactly one place.
/// </summary>
internal static class CliNativeContentionCorrelation
{
    /// <summary>Handle kind registered by <c>collect --kind native-lock-contention</c>.</summary>
    internal const string NativeLockContentionKind = "native-lock-contention-sample";

    /// <summary>Handle kind registered by <c>collect --kind off_cpu</c>.</summary>
    internal const string OffCpuKind = "off-cpu-snapshot";

    /// <summary>
    /// Resolves the latest <c>native-lock-contention-sample</c> and <c>off-cpu-snapshot</c> handles
    /// for <paramref name="processId"/> and correlates them when at least one is present. Returns
    /// <see langword="null"/> when neither is present — nothing to surface yet.
    /// </summary>
    internal static NativeContentionEvidence? TryBuild(IDiagnosticHandleStore? store, int processId)
    {
        if (store is null)
        {
            return null;
        }

        var lockHandle = store.TryGetLatestByKind(NativeLockContentionKind, processId);
        var offCpuHandle = store.TryGetLatestByKind(OffCpuKind, processId);
        if (lockHandle is null && offCpuHandle is null)
        {
            return null;
        }

        var lockEvidence = lockHandle is null
            ? null
            : store.TryGet<NativeLockContentionArtifact>(lockHandle.Id)?.Summary.ContentionEvidence;
        var offCpuEvidence = offCpuHandle is null
            ? null
            : store.TryGet<OffCpuSnapshotArtifact>(offCpuHandle.Id)?.NativeContentionEvidence;
        if (lockEvidence is null && offCpuEvidence is null)
        {
            return null;
        }

        return NativeLockContentionUx.CorrelateBatchEvidence(lockEvidence, offCpuEvidence);
    }

    /// <summary>Renders a compact, human-readable line summarizing <paramref name="evidence"/>.</summary>
    internal static string Render(NativeContentionEvidence evidence)
        => $"  → native-contention evidence (native-lock + off-cpu correlated): {evidence.Level} — {evidence.Summary}";
}
