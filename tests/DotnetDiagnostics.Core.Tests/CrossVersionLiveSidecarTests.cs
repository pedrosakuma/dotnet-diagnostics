using System.Text.Json;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.Threads;
using FluentAssertions;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>External owned target only; scripts/live-clrmd-compat.py provisions each major.</summary>
[Collection("LiveProcess")]
public sealed class CrossVersionLiveSidecarTests(ITestOutputHelper output)
{
    [LiveCompatibilityFact]
    public async Task LiveHeap_RetainsNamedPopulation()
    {
        var (pid, major) = MultiVersionSampleProcess.ReadLiveCompatibilityTarget();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var heap = await new ClrMdDumpInspector().InspectLiveAsync(pid,
            new DumpInspectionOptions(TopTypes: 200), deadline.Token);
        WriteEvidence(new { heap.Origin, heap.ProcessId, heap.Runtime, heap.TopTypesByInstances });
        heap.Origin.Should().Be(HeapSnapshotOrigin.Live);
        heap.ProcessId.Should().Be(pid);
        AssertMajor(heap.Runtime.Version, major);
        heap.TopTypesByInstances.Should().Contain(t => t.TypeFullName == "RetainedMarker" && t.InstanceCount == 32);
    }

    [LiveCompatibilityFact]
    public async Task LiveThreads_FindNamedGenericFrame()
    {
        var snapshot = await Threads();
        snapshot.Threads.SelectMany(t => t.Frames).Should().Contain(f =>
            f.TypeFullName == "CompatibilityFixture" && f.Identity != null &&
            f.Identity.MethodName == "ClosedGenericHold");
    }

    [LiveCompatibilityFact]
    public async Task LiveAsync_FindsPendingFixture()
    {
        var (pid, major) = MultiVersionSampleProcess.ReadLiveCompatibilityTarget();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var heap = await new ClrMdDumpInspector().InspectLiveAsync(pid, cancellationToken: deadline.Token);
        WriteEvidence(new { heap.Origin, heap.Runtime, heap.AsyncOperations });
        heap.Origin.Should().Be(HeapSnapshotOrigin.Live);
        AssertMajor(heap.Runtime.Version, major);
        heap.AsyncOperations.Should().Contain(op =>
            op.StateMachineTypeFullName.StartsWith("CompatibilityFixture+<PendingAsync>", StringComparison.Ordinal) &&
            op.State >= 0 && op.AwaiterTypeFullName != null &&
            op.AwaiterTypeFullName.Contains("TaskAwaiter", StringComparison.Ordinal));
    }

    [LiveCompatibilityFact]
    public async Task LiveGenerics_ResolvesConcreteInt32()
    {
        var snapshot = await Threads();
        var frame = snapshot.Threads.SelectMany(t => t.Frames).Single(f =>
            f.TypeFullName == "CompatibilityFixture" && f.Identity?.MethodName == "ClosedGenericHold");
        frame.Identity!.ModuleVersionId.Should().NotBeNull("the sidecar mounts the exact target sample");
        var symbol = new SymbolRef(frame.ModuleName!, frame.DisplayName);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var resolved = new ClrMdMethodInstantiationEnricher().Resolve(snapshot.ProcessId,
            [new MethodInstantiationCandidate(symbol, frame.InstructionPointer)],
            new Dictionary<SymbolRef, MethodIdentity> { [symbol] = frame.Identity }, deadline.Token);
        WriteEvidence(resolved);
        var identity = resolved.Should().ContainSingle().Subject.Identity;
        identity.ModuleVersionId.Should().Be(frame.Identity.ModuleVersionId);
        identity.MethodName.Should().Be("ClosedGenericHold");
        identity.GenericTypeArguments!.Method.Should().Equal("System.Int32",
            "shared-canon/unknown is not evidence of the known concrete fixture");
        identity.ClosedSignature.Should().Contain("ClosedGenericHold<System.Int32>");
    }

    private async Task<ThreadSnapshotArtifact> Threads()
    {
        var (pid, major) = MultiVersionSampleProcess.ReadLiveCompatibilityTarget();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var snapshot = await new ClrMdThreadSnapshotInspector().InspectLiveAsync(pid,
            new ThreadSnapshotOptions(MaxFramesPerThread: 32), deadline.Token);
        WriteEvidence(new { snapshot.Origin, snapshot.ProcessId, snapshot.RuntimeVersion, snapshot.Threads });
        snapshot.Origin.Should().Be(ThreadSnapshotOrigin.Live);
        snapshot.ProcessId.Should().Be(pid);
        AssertMajor(snapshot.RuntimeVersion, major);
        return snapshot;
    }

    private static void AssertMajor(string version, int major) => Version.Parse(version).Major.Should().Be(major);

    private void WriteEvidence<T>(T value)
    {
        output.WriteLine($"ClrMD: {typeof(Microsoft.Diagnostics.Runtime.DataTarget).Assembly.FullName}");
        output.WriteLine(JsonSerializer.Serialize(value));
    }
}

public sealed class LiveCompatibilityFactAttribute : FactAttribute
{
    public LiveCompatibilityFactAttribute()
    {
        Timeout = 30_000;
        if (Environment.GetEnvironmentVariable("DOTNET_DIAGNOSTICS_LIVE_COMPAT_MAJOR") is null)
            Skip = "Missing prerequisite: run the owned Linux x64 live ClrMD sidecar compatibility runner.";
    }
}
