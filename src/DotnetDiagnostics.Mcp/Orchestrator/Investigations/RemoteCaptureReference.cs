using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Captures;
using ModelContextProtocol.Protocol;

namespace DotnetDiagnostics.Mcp.Orchestrator.Investigations;

/// <summary>A capture belongs to its collecting host, not the orchestrator's local store.</summary>
public sealed record RemoteCaptureReference(
    string InvestigationHandleId, string Host, string Tool, string Kind,
    string? CaptureId, IReadOnlyList<RemoteCaptureArtifact> Artifacts,
    CaptureState? State, CaptureQuality? Quality, DiagnosticError? Error)
{
    internal const int MaximumTargets = 16;
    internal static bool ValidSelection(IReadOnlyList<string>? handles)
        => handles is null || handles.Count <= MaximumTargets &&
            handles.All(static handle => !string.IsNullOrWhiteSpace(handle) && handle.Length <= 256);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static RemoteCaptureReference Read(
        InvestigationHandle handle, string kind, CallToolResult? result, string? failure)
    {
        CaptureInfo? capture = null;
        DiagnosticError? error = string.IsNullOrEmpty(failure) ? null : new("RemoteCaptureFailed", failure);
        try
        {
            if (result is not null)
            {
                using var document = result.StructuredContent is { } structured
                    ? JsonDocument.Parse(structured.GetRawText())
                    : JsonDocument.Parse(result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "null");
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (property.Name.Equals("capture", StringComparison.OrdinalIgnoreCase) &&
                            property.Value.ValueKind != JsonValueKind.Null)
                            capture = property.Value.Deserialize<CaptureInfo>(Options);
                        if (property.Name.Equals("error", StringComparison.OrdinalIgnoreCase) &&
                            property.Value.ValueKind != JsonValueKind.Null)
                            error = property.Value.Deserialize<DiagnosticError>(Options) ?? error;
                    }
                }
                if (result.IsError == true)
                    error ??= new("RemoteCaptureFailed", "The collecting host reported a failed operation.");
            }
            if (capture is not null &&
                (!IsId(capture.CaptureId) || capture.Artifacts is null || capture.Artifacts.Count > 64 ||
                 capture.Quality is null || !Enum.IsDefined(capture.State) ||
                 capture.Artifacts.Any(item => item is null || !IsId(item.ArtifactId) ||
                     string.IsNullOrWhiteSpace(item.Kind) || item.Kind.Length > 128)))
            {
                capture = null;
                error = new("RemoteCaptureInvalid", "The collecting host returned invalid or oversized capture metadata.");
            }
        }
        catch (JsonException)
        {
            capture = null;
            error = new("RemoteCaptureInvalid", "The collecting host returned malformed capture metadata.");
        }

        if (capture is null)
            error ??= new("RemoteCaptureMissing", "Persistence was requested but the collecting host returned no capture ID.");
        if (capture is { State: not CaptureState.Sealed })
            error ??= new("RemoteCaptureIncomplete", "The collecting host did not seal the capture; recovery must be requested on that host.");
        if (error?.Message is { Length: > 4096 } || error?.Detail is { Length: > 4096 })
            error = new("RemoteCaptureInvalid", "The collecting host error exceeded 4096 characters; oversized details were omitted.");

        return new(handle.HandleId, handle.TargetDisplayName, "collect_events", kind, capture?.CaptureId,
            capture?.Artifacts.Select(item => new RemoteCaptureArtifact(item.ArtifactId, item.Kind)).ToArray() ?? [],
            capture?.State, capture?.Quality, error);
    }

    private static bool IsId(string? value)
        => value is { Length: 32 } && Guid.TryParseExact(value, "N", out _) &&
           !value.Any(static character => character is >= 'A' and <= 'F');
}

public sealed record RemoteCaptureArtifact(string ArtifactId, string Kind);
