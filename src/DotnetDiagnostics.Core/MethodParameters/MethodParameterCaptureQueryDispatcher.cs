using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.MethodParameters;

public static class MethodParameterCaptureQueryDispatcher
{
    public const string SummaryView = "summary";
    public const string EventsView = "events";

    public static IReadOnlyList<string> ViewsFor(string kind)
        => string.Equals(kind, MethodParameterCaptureUseCases.HandleKind, StringComparison.Ordinal)
            ? new[] { SummaryView, EventsView }
            : Array.Empty<string>();

    public static string DefaultViewFor(string kind) => SummaryView;

    public static DiagnosticResult<MethodParameterCaptureQueryResult> Render(
        MethodParameterCaptureArtifact artifact,
        string handle,
        string? view,
        int topN)
    {
        if (topN < 1)
        {
            return DiagnosticResult.Fail<MethodParameterCaptureQueryResult>(
                "Argument 'topN' must be >= 1.",
                new DiagnosticError("InvalidArgument", "Argument 'topN' must be >= 1.", "topN"));
        }

        var effectiveView = string.IsNullOrWhiteSpace(view) ? SummaryView : view.Trim();
        if (!ViewsFor(MethodParameterCaptureUseCases.HandleKind).Contains(effectiveView, StringComparer.OrdinalIgnoreCase))
        {
            return DiagnosticResult.Fail<MethodParameterCaptureQueryResult>(
                $"View '{effectiveView}' is not defined for kind '{MethodParameterCaptureUseCases.HandleKind}'. Allowed: {SummaryView}, {EventsView}.",
                new DiagnosticError("InvalidArgument", $"View '{effectiveView}' is not defined for kind '{MethodParameterCaptureUseCases.HandleKind}'. Allowed: {SummaryView}, {EventsView}.", "view"));
        }

        var result = new MethodParameterCaptureQueryResult(
            MethodParameterCaptureUseCases.HandleKind,
            effectiveView,
            artifact.ProcessId,
            artifact.CapturedAtUtc,
            artifact.RequestedDuration);

        if (string.Equals(effectiveView, SummaryView, StringComparison.OrdinalIgnoreCase))
        {
            result = result with
            {
                Summary = new MethodParameterCaptureSummaryView(
                    artifact.RuntimeFlavor,
                    artifact.RuntimeVersion,
                    artifact.MethodFilters,
                    artifact.ResolvedMethods,
                    (int)Math.Round(artifact.RequestedDuration.TotalSeconds, MidpointRounding.AwayFromZero),
                    artifact.MaxEvents,
                    artifact.PreviewCount,
                    artifact.CaptureCount,
                    artifact.DroppedCount,
                    artifact.TruncatedValueCount,
                    artifact.RedactedValueCount,
                    artifact.ValuesTruncated,
                    artifact.ValuesRedacted,
                    artifact.StopReason),
            };

            return DiagnosticResult.Ok(
                result,
                $"Rendered summary for method-parameter capture handle '{handle}' (pid {artifact.ProcessId}, {artifact.CaptureCount} captured invocation(s)).");
        }

        var slice = artifact.Events.Take(topN).ToArray();
        result = result with
        {
            Events = new MethodParameterCaptureEventsView(slice.Length, artifact.CaptureCount, artifact.PreviewCount, slice),
        };

        return DiagnosticResult.Ok(
            result,
            $"Rendered {slice.Length} method-parameter invocation row(s) from handle '{handle}' (pid {artifact.ProcessId}, total captured {artifact.CaptureCount}).");
    }
}
