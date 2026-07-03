using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Signals;
using ModelContextProtocol.Server;

namespace DotnetDiagnostics.Mcp.Resources;

/// <summary>
/// Templated Resource that exposes the engine-derived <see cref="SignalGroup"/>s (the salient
/// diagnosis-agnostic "vector") for a CPU-sample drill-down handle. Complements the inline signals on
/// the <c>collect_sample</c> envelope: the tool leads with signals at collection time; this Resource
/// lets a client re-pull the current signals for a handle without re-running the sampler. Providers
/// are re-run over the full merged call tree stored under the handle, so the namespace roll-up is
/// faithful and nothing is lost to the inline top-N cap.
/// </summary>
[McpServerResourceType]
public sealed class SignalsResources
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerResource(
        UriTemplate = "signals://cpu-sample/{handle}",
        Name = "cpu-sample-signals",
        Title = "CPU-sample signals",
        MimeType = "application/json")]
    [Description(
        "JSON list of engine-derived, salient signal groupings (a diagnosis-agnostic \"vector\") for a " +
        "cpu-sample handle registered by collect_sample(kind=\"cpu\"). Each grouping describes where a " +
        "signal concentrates (signal id, one-line summary, salience in [0,1], and buckets each " +
        "referencing the handle) — e.g. cpu.self-time.concentration and cpu.self-time.by-namespace. It " +
        "says where the CPU is spent, never what the bug is; the consumer draws the conclusion and " +
        "drills in. Detectors are re-run over the full call tree so the result matches (and is richer " +
        "than) the inline signals on the collect_sample envelope. Empty when nothing is salient; " +
        "returns an error contents block when the handle is unknown or expired.")]
    public static string ReadCpuSampleSignals(
        IDiagnosticHandleStore handles,
        string handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);

        // Restrict to cpu-sample handles: allocation-sample / native-alloc-sample also back a
        // CpuSampleTraceArtifact, but their stack counts are allocation events, not CPU samples —
        // running CPU signal grouping over them would misread bytes/allocs as CPU self-time.
        var lookup = handles.TryGetWithKind(handle);
        if (lookup is not { Kind: "cpu-sample" } found || found.Artifact is not CpuSampleTraceArtifact trace)
        {
            return JsonSerializer.Serialize(
                new SignalsErrorPayload(
                    Kind: "unknown",
                    Error: $"Handle '{handle}' is unknown, expired, or not a cpu-sample handle. Re-run collect_sample(kind=\"cpu\") to issue a fresh handle."),
                SerializerOptions);
        }

        var signals = CpuSampleSignals.Detect(trace, handle);
        return JsonSerializer.Serialize(new SignalsPayload(handle, signals), SerializerOptions);
    }
}

/// <summary>Successful signals Resource payload.</summary>
internal sealed record SignalsPayload(string Handle, IReadOnlyList<SignalGroup> Signals);

/// <summary>Error payload returned when a signals handle is unknown or expired.</summary>
internal sealed record SignalsErrorPayload(string Kind, string Error);
