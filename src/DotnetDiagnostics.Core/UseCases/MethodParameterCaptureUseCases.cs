using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.MethodParameters;
using DotnetDiagnostics.Core.ProcessDiscovery;
using static DotnetDiagnostics.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnostics.Core.UseCases;

public static class MethodParameterCaptureUseCases
{
    private static readonly TimeSpan MethodParameterHandleTtl = TimeSpan.FromMinutes(10);
    public const string HandleKind = "method-params-capture";

    public static async Task<DiagnosticResult<MethodParameterCaptureSample>> CollectAsync(
        IMethodParameterCaptureCollector collector,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        int? processId,
        int durationSeconds,
        int maxEvents,
        int previewCount,
        IReadOnlyList<MethodFilter>? methods,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1 || durationSeconds > 30)
        {
            return InvalidArg<MethodParameterCaptureSample>(nameof(durationSeconds), "must be between 1 and 30");
        }

        if (maxEvents < 1 || maxEvents > 500)
        {
            return InvalidArg<MethodParameterCaptureSample>(nameof(maxEvents), "must be between 1 and 500");
        }

        if (previewCount < 1 || previewCount > 25)
        {
            return InvalidArg<MethodParameterCaptureSample>(nameof(previewCount), "must be between 1 and 25");
        }

        if (methods is null || methods.Count == 0 || methods.Count > 10)
        {
            return InvalidArg<MethodParameterCaptureSample>(nameof(methods), "must contain between 1 and 10 method filters");
        }

        var invalidFilter = methods.FirstOrDefault(filter =>
            string.IsNullOrWhiteSpace(filter.ModuleName) ||
            string.IsNullOrWhiteSpace(filter.TypeName) ||
            string.IsNullOrWhiteSpace(filter.MethodName));
        if (invalidFilter is not null)
        {
            return InvalidArg<MethodParameterCaptureSample>(nameof(methods), "requires moduleName, typeName, and methodName for every filter");
        }

        var resolved = await ResolveContextAsync<MethodParameterCaptureSample>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null)
        {
            return resolved.Failure;
        }

        var request = new MethodParameterCaptureRequest(
            methods,
            TimeSpan.FromSeconds(durationSeconds),
            maxEvents,
            previewCount,
            resolved.Context?.RuntimeVersion ?? string.Empty,
            resolved.Context);

        var capture = await collector.CollectAsync(resolved.ProcessId, request, cancellationToken).ConfigureAwait(false);
        if (capture.IsError || capture.Data is null)
        {
            return WithContext(new DiagnosticResult<MethodParameterCaptureSample>(capture.Summary, capture.Hints, capture.Error), resolved.Context)
                with { Cancelled = capture.Cancelled };
        }

        var artifact = capture.Data;
        var handle = handles.Register(resolved.ProcessId, HandleKind, artifact, MethodParameterHandleTtl, evictWhenProcessExits: true, origin: HandleOrigin.Live);
        var preview = artifact.ToPreviewSample();
        var summary = $"Captured {preview.CaptureCount} method invocation(s) over {preview.DurationSeconds}s on {preview.RuntimeFlavor} {preview.RuntimeVersion} for {preview.MethodFilters.Count} method filter(s). " +
                      $"Retained {preview.CaptureCount}/{preview.MaxEvents} event(s) (dropped {preview.DroppedCount}). Values truncated: {preview.TruncatedValueCount}, redacted: {preview.RedactedValueCount}. " +
                      $"Inline preview shows first {Math.Min(preview.PreviewCount, preview.CaptureCount)} invocation(s); handle '{handle.Id}' retains the full bounded capture for ~10 minutes.";
        var hints = new[]
        {
            new NextActionHint("query_snapshot", "Inspect the summary projection for the retained method-parameter capture handle.", new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "summary" }) { Priority = NextActionHintPriority.High },
            new NextActionHint("query_snapshot", "Read the retained invocation rows from the method-parameter capture handle.", new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "events", ["topN"] = preview.MaxEvents, ["includeSensitiveValues"] = true }) { Priority = NextActionHintPriority.High },
        };

        return WithContext(
            DiagnosticResult.OkWithHandle(preview, summary, handle.Id, handle.ExpiresAt, hints) with { Cancelled = capture.Cancelled },
            resolved.Context);
    }

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string message)
        => DiagnosticResult.Fail<T>($"Argument '{parameterName}' {message}.", new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {message}.", parameterName));
}
