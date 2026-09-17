using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Gc;

namespace DotnetDiagnostics.Core.Collection;

/// <summary>Shared transport-neutral GC overlay handle validation.</summary>
public static class GcCorrelationHandles
{
    public static string? Resolve(IDiagnosticHandleStore store, HandleLookup activity, string? gcHandle,
        out GcSummary? gc)
    {
        ArgumentNullException.ThrowIfNull(store);
        gc = null;
        if (string.IsNullOrWhiteSpace(gcHandle)) return "gc-overlay requires a GC handle.";
        var candidate = store.TryGetWithKind(gcHandle);
        if (candidate is not { } found) return "GC handle is unknown or expired.";
        if (found.Kind != CollectionHandleKinds.GcEvents || found.Artifact is not GcSummary summary)
            return "GC handle must reference gc-events.";
        if (activity.Artifact is not ActivityCapture capture) return "gc-overlay requires an activities handle.";
        if (found.Handle.Origin != activity.Handle.Origin) return "Activity and GC handle origins differ.";
        if (found.Handle.ProcessId != activity.Handle.ProcessId || capture.ProcessId != summary.ProcessId)
            return "Activity and GC process IDs differ.";
        if (GcActivityCorrelator.Validate(capture, summary) is { } error) return error;
        gc = summary;
        return null;
    }
}
