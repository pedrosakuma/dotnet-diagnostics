using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.ProcessDiscovery;

namespace DotnetDiagnostics.Core.MethodParameters;

public sealed record MethodFilter(
    string ModuleName,
    string TypeName,
    string MethodName)
{
    public int? GenericArity { get; init; }

    public IReadOnlyList<string>? Signature { get; init; }

    public string? ModuleVersionId { get; init; }
}

public sealed record ResolvedMethodIdentity(
    string ModuleName,
    string ModuleVersionId,
    string TypeName,
    string MethodName,
    int GenericArity,
    int MetadataToken,
    IReadOnlyList<string> Signature);

public sealed record CapturedParameterValue(
    string Name,
    string TypeName,
    string Value,
    bool Redacted,
    bool Truncated)
{
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record MethodParameterInvocation(
    int Sequence,
    DateTimeOffset TimestampUtc,
    ResolvedMethodIdentity Method,
    IReadOnlyList<CapturedParameterValue> Parameters);

public sealed record MethodParameterCaptureSample(
    string RuntimeFlavor,
    string RuntimeVersion,
    IReadOnlyList<MethodFilter> MethodFilters,
    int DurationSeconds,
    int MaxEvents,
    int PreviewCount,
    int CaptureCount,
    int DroppedCount,
    int TruncatedValueCount,
    int RedactedValueCount,
    bool ValuesTruncated,
    bool ValuesRedacted,
    string StopReason,
    IReadOnlyList<MethodParameterInvocation> Events);

public sealed record MethodParameterCaptureArtifact(
    int ProcessId,
    DateTimeOffset CapturedAtUtc,
    TimeSpan RequestedDuration,
    string RuntimeFlavor,
    string RuntimeVersion,
    IReadOnlyList<MethodFilter> MethodFilters,
    IReadOnlyList<ResolvedMethodIdentity> ResolvedMethods,
    int MaxEvents,
    int PreviewCount,
    int CaptureCount,
    int DroppedCount,
    int TruncatedValueCount,
    int RedactedValueCount,
    bool ValuesTruncated,
    bool ValuesRedacted,
    string StopReason,
    IReadOnlyList<MethodParameterInvocation> Events)
{
    private const int PreviewCharacterLimit = 256;

    public MethodParameterCaptureSample ToPreviewSample()
        => new(
            RuntimeFlavor,
            RuntimeVersion,
            MethodFilters,
            (int)Math.Round(RequestedDuration.TotalSeconds, MidpointRounding.AwayFromZero),
            MaxEvents,
            PreviewCount,
            CaptureCount,
            DroppedCount,
            TruncatedValueCount,
            RedactedValueCount,
            ValuesTruncated,
            ValuesRedacted,
            StopReason,
            Events.Take(PreviewCount).Select(CreatePreviewInvocation).ToArray());

    private static MethodParameterInvocation CreatePreviewInvocation(MethodParameterInvocation invocation)
        => invocation with
        {
            Parameters = invocation.Parameters.Select(CreatePreviewParameter).ToArray(),
        };

    private static CapturedParameterValue CreatePreviewParameter(CapturedParameterValue parameter)
    {
        if (parameter.Value.Length <= PreviewCharacterLimit)
        {
            return parameter;
        }

        var notes = parameter.Notes.Contains("preview-cap", StringComparer.Ordinal)
            ? parameter.Notes
            : parameter.Notes.Concat(["preview-cap"]).ToArray();

        return parameter with
        {
            Value = parameter.Value[..PreviewCharacterLimit],
            Truncated = true,
            Notes = notes,
        };
    }
}

public sealed record MethodParameterCaptureQueryResult(
    string Kind,
    string View,
    int ProcessId,
    DateTimeOffset CapturedAtUtc,
    TimeSpan RequestedDuration)
{
    public MethodParameterCaptureSummaryView? Summary { get; init; }

    public MethodParameterCaptureEventsView? Events { get; init; }
}

public sealed record MethodParameterCaptureSummaryView(
    string RuntimeFlavor,
    string RuntimeVersion,
    IReadOnlyList<MethodFilter> MethodFilters,
    IReadOnlyList<ResolvedMethodIdentity> ResolvedMethods,
    int DurationSeconds,
    int MaxEvents,
    int PreviewCount,
    int CaptureCount,
    int DroppedCount,
    int TruncatedValueCount,
    int RedactedValueCount,
    bool ValuesTruncated,
    bool ValuesRedacted,
    string StopReason);

public sealed record MethodParameterCaptureEventsView(
    int ReturnedCount,
    int CaptureCount,
    int PreviewCount,
    IReadOnlyList<MethodParameterInvocation> Events);

public sealed record MethodParameterCaptureRequest(
    IReadOnlyList<MethodFilter> MethodFilters,
    TimeSpan Duration,
    int MaxEvents,
    int PreviewCount,
    string RuntimeVersion,
    ProcessContext? ProcessContext);

internal sealed record ResolvedMethodBinding(
    MethodFilter Filter,
    string ModulePath,
    ResolvedMethodIdentity Identity,
    MethodDescription PayloadDescription);

internal sealed class MethodDescription
{
    public string ModuleName { get; init; } = string.Empty;

    public string TypeName { get; init; } = string.Empty;

    public string MethodName { get; init; } = string.Empty;
}

internal sealed class StartCapturePayload
{
    public Guid RequestId { get; init; }

    public TimeSpan Duration { get; init; }

    public CaptureParametersConfiguration Configuration { get; init; } = new();
}

internal sealed class StopCapturePayload
{
    public Guid RequestId { get; init; }
}

internal sealed class CaptureParametersConfiguration
{
    [JsonPropertyName("methods")]
    public MethodDescription[] Methods { get; init; } = Array.Empty<MethodDescription>();

    [JsonPropertyName("useDebuggerDisplayAttribute")]
    public bool UseDebuggerDisplayAttribute { get; init; }

    [JsonPropertyName("captureLimit")]
    public int? CaptureLimit { get; init; }
}
