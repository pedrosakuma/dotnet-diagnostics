using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Dump;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Deterministic, process-free coverage for the NativeAOT gcdump guard (issue #471). Requesting the
/// GCHeapSnapshot EventPipe keyword crashes .NET 10 NativeAOT targets (the runtime segfaults and the
/// process exits — reproduced on SDK 10.0.201), so gcdump is deliberately withheld on AOT. These tests
/// pin the two safety seams: the capability gate reports <c>false</c> for AOT, and the collector refuses
/// with <see cref="NotSupportedException"/> before opening a diagnostic session (so a direct caller such
/// as the CLI session REPL can never reach the crash path and kill the target).
/// </summary>
public sealed class GcDumpNativeAotGuardTests
{
    [Theory]
    [InlineData(RuntimeFlavor.CoreClr, true)]
    [InlineData(RuntimeFlavor.NativeAot, false)]
    [InlineData(RuntimeFlavor.Unknown, false)]
    public void ComputeCanCollectGcDump_IsCoreClrOnly(RuntimeFlavor runtime, bool expected)
    {
        CapabilityDetector.ComputeCanCollectGcDump(runtime).Should().Be(
            expected,
            "gcdump is CoreCLR-only — advertising it on NativeAOT would crash the target");
    }

    [Fact]
    public async Task CollectAsync_NativeAot_RefusesBeforeTouchingTheTarget()
    {
        var collector = new GcDumpHeapSnapshotCollector();

        // pid 0 is never a valid diagnostic target; the guard must throw before any socket access,
        // so the invalid pid is never dereferenced.
        var act = async () => await collector.CollectAsync(
            processId: 0,
            new GcDumpOptions { Runtime = RuntimeFlavor.NativeAot },
            CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*NativeAOT*");
    }

    [Fact]
    public void GcDumpOptions_DefaultsToCoreClr_SoExistingCallersAreUnaffected()
    {
        new GcDumpOptions().Runtime.Should().Be(RuntimeFlavor.CoreClr);
    }
}
