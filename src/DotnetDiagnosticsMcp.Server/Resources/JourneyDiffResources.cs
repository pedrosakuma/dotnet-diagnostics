using System.ComponentModel;
using System.Text.Json;
using DotnetDiagnosticsMcp.Core.Comparison;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Server.Tools;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Resources;

/// <summary>
/// Templated Resource that exposes the full <see cref="SnapshotJourneyDiff"/> matrix retained
/// after a compact diff response returned a <c>journey://diff/{{handle}}</c> link.
/// </summary>
[McpServerResourceType]
public sealed class JourneyDiffResources
{
    [McpServerResource(
        UriTemplate = "journey://diff/{handle}",
        Name = "journey-diff",
        Title = "Full journey diff matrix",
        MimeType = "application/json")]
    [Description(
        "Assistant-pull JSON Resource for the full SnapshotJourneyDiff matrix registered after " +
        "query_snapshot(view=\"diff\") or compare_to_baseline returns a compact summary. " +
        "Use this only when the compact verdict/headline/top deltas are insufficient. " +
        "Returns an error contents block when the handle is unknown or expired.")]
    public static string ReadDiff(IDiagnosticHandleStore handles, string handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);

        var diff = handles.TryGet<SnapshotJourneyDiff>(handle);
        if (diff is null)
        {
            return JsonSerializer.Serialize(
                new JourneyDiffResourceErrorPayload(
                    Kind: "unknown",
                    Error: $"Handle '{handle}' is unknown or expired. Re-run the comparison to issue a fresh handle."),
                JourneyDiffResourceJsonContext.Default.JourneyDiffResourceErrorPayload);
        }

        return JsonSerializer.Serialize(diff, JourneyDiffResourceJsonContext.Default.SnapshotJourneyDiff);
    }
}
