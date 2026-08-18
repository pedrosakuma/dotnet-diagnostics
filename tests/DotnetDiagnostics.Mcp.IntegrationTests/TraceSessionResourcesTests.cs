using System.Text.Json;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.NativeLockContention;
using DotnetDiagnostics.Mcp.Resources;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

/// <summary>
/// Regression coverage for the <c>trace://session/{handle}</c> resource's artifact-unwrapping
/// switch. Issue #855 introduced a new wrapper artifact
/// (<see cref="NativeLockContentionArtifact"/>) for the <c>native-lock-contention-sample</c>
/// handle kind, mirroring the pre-existing <see cref="AllocationSampleArtifact"/> wrapper; a
/// self-review pass caught that <see cref="TraceSessionResources.ReadSession"/> unwrapped only the
/// allocation wrapper and would otherwise have silently fallen through to the "unknown handle"
/// payload for native-lock-contention handles.
/// </summary>
public sealed class TraceSessionResourcesTests
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    [Fact]
    public void ReadSession_NativeLockContentionHandle_ReturnsCallTreePayload_NotUnknown()
    {
        var store = new MemoryDiagnosticHandleStore();
        var trace = new CpuSampleTraceArtifact(
            123,
            StartedAt,
            TimeSpan.FromSeconds(6),
            TotalSamples: 200,
            new CallTreeNode(new SampledFrame("root", "root"), 0, 0, []));
        var sample = new NativeLockContentionSample(
            123,
            StartedAt,
            TimeSpan.FromSeconds(6),
            TotalSampledLockCalls: 200,
            TopContendedCallSites: [],
            ProbedFunctions: ["pthread_mutex_lock", "pthread_mutex_unlock"],
            LibcPath: "/lib/x86_64-linux-gnu/libc.so.6",
            SamplePeriod: 5000,
            SymbolSource: "PdbResolved",
            ContentionEvidence: new NativeContentionEvidence(
                NativeContentionEvidenceLevels.Activity,
                "sampled pthread mutex entry points are lock activity only.",
                SampledLockCallCount: 200));
        var handle = store.Register(
            123,
            "native-lock-contention-sample",
            new NativeLockContentionArtifact(sample, trace),
            TimeSpan.FromMinutes(10));

        var json = TraceSessionResources.ReadSession(store, handle.Id);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("Kind").GetString().Should().Be("native-lock-contention-sample");
        document.RootElement.GetProperty("ProcessId").GetInt32().Should().Be(123);
        document.RootElement.TryGetProperty("Error", out _).Should().BeFalse(
            "a registered native-lock-contention handle must not fall through to the unknown-handle payload");
    }

    [Fact]
    public void ReadSession_AllocationSampleHandle_StillReturnsCallTreePayload()
    {
        var store = new MemoryDiagnosticHandleStore();
        var trace = new CpuSampleTraceArtifact(
            456,
            StartedAt,
            TimeSpan.FromSeconds(6),
            TotalSamples: 50,
            new CallTreeNode(new SampledFrame("root", "root"), 0, 0, []));
        var handle = store.Register(
            456,
            "allocation-sample",
            new AllocationSampleArtifact(
                new AllocationSample(456, StartedAt, TimeSpan.FromSeconds(6), TotalEvents: 10, TotalBytes: 1024, TopByBytes: [], TopByCount: []),
                trace),
            TimeSpan.FromMinutes(10));

        var json = TraceSessionResources.ReadSession(store, handle.Id);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("Kind").GetString().Should().Be("allocation-sample");
        document.RootElement.TryGetProperty("Error", out _).Should().BeFalse();
    }

    [Fact]
    public void ReadSession_UnknownHandle_ReturnsUnknownPayload()
    {
        var store = new MemoryDiagnosticHandleStore();

        var json = TraceSessionResources.ReadSession(store, "does-not-exist");

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("Kind").GetString().Should().Be("unknown");
        document.RootElement.GetProperty("Error").GetString().Should().Contain("unknown or expired");
    }
}
