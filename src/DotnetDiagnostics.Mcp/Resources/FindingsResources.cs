using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Findings;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Resources;

/// <summary>
/// Templated Resource that exposes the engine-derived <see cref="Finding"/>s for a CPU-sample
/// drill-down handle. Complements the inline findings on the <c>collect_sample</c> envelope: the
/// tool leads with findings at collection time; this Resource lets a client re-pull the current
/// findings for a handle without re-running the sampler. The detectors are re-run over the full
/// merged call tree stored under the handle, so nothing is lost to the inline top-N cap.
/// </summary>
[McpServerResourceType]
public sealed class FindingsResources
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerResource(
        UriTemplate = "findings://cpu-sample/{handle}",
        Name = "cpu-sample-findings",
        Title = "CPU-sample findings",
        MimeType = "application/json")]
    [Description(
        "JSON list of engine-derived, ranked diagnostic findings for a cpu-sample handle registered by " +
        "collect_sample(kind=\"cpu\"). Each finding cross-references the samples into a conclusion " +
        "(pattern, severity, confidence, evidence referencing the handle, suggested fix, next tool) — " +
        "e.g. regex-backtracking. Detectors are re-run over the full call tree so the result matches the " +
        "inline findings on the collect_sample envelope. Empty when nothing is detected; returns an error " +
        "contents block when the handle is unknown or expired.")]
    public static string ReadCpuSampleFindings(
        IDiagnosticHandleStore handles,
        string handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);

        // Restrict to cpu-sample handles: allocation-sample / native-alloc-sample also back a
        // CpuSampleTraceArtifact, but their stack counts are allocation events, not CPU samples —
        // running CPU findings over them would misread bytes/allocs as CPU inclusive samples.
        var lookup = handles.TryGetWithKind(handle);
        if (lookup is not { Kind: "cpu-sample" } found || found.Artifact is not CpuSampleTraceArtifact trace)
        {
            return JsonSerializer.Serialize(
                new FindingsErrorPayload(
                    Kind: "unknown",
                    Error: $"Handle '{handle}' is unknown, expired, or not a cpu-sample handle. Re-run collect_sample(kind=\"cpu\") to issue a fresh handle."),
                SerializerOptions);
        }

        var findings = CpuSampleFindings.Detect(trace, handle);
        return JsonSerializer.Serialize(new FindingsPayload(handle, findings), SerializerOptions);
    }
}

/// <summary>Successful findings Resource payload.</summary>
internal sealed record FindingsPayload(string Handle, IReadOnlyList<Finding> Findings);

/// <summary>Error payload returned when a findings handle is unknown or expired.</summary>
internal sealed record FindingsErrorPayload(string Kind, string Error);
