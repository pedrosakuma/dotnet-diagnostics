using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;
using Microsoft.Diagnostics.Tracing.Session;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Unit and integration tests for the Windows ETW NativeAOT CPU sampler.
/// Integration tests require Windows with administrative elevation and are skipped otherwise.
/// </summary>
[Collection("LiveProcess")]
public class EtwNativeAotCpuSamplerTests
{
    [Fact]
    public void IsAvailable_ReturnsFalse_OnNonWindows()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // On Windows, this test is not meaningful — skip.
            return;
        }

        var sampler = new EtwNativeAotCpuSampler();
        sampler.IsAvailable().Should().BeFalse("ETW is a Windows-only technology");
    }

    [Fact]
    public void IsAvailable_RespectsElevation_OnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Not on Windows — skip.
            return;
        }

        var sampler = new EtwNativeAotCpuSampler();
        var isElevated = TraceEventSession.IsElevated() == true;
        sampler.IsAvailable().Should().Be(isElevated,
            "IsAvailable should match administrative elevation status");
    }

    [Fact]
    public async Task SampleAsync_ThrowsOnInvalidDuration()
    {
        var sampler = new EtwNativeAotCpuSampler();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sampler.SampleAsync(1, TimeSpan.Zero));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sampler.SampleAsync(1, TimeSpan.FromMinutes(6)));
    }

    [Fact]
    public async Task SampleAsync_ThrowsOnInvalidTopN()
    {
        var sampler = new EtwNativeAotCpuSampler();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sampler.SampleAsync(1, TimeSpan.FromSeconds(1), topN: 0));
    }

    [Fact]
    public async Task SampleAsync_ThrowsWhenNotAvailable()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && TraceEventSession.IsElevated() == true)
        {
            // Elevated on Windows — this test path does not apply.
            return;
        }

        var sampler = new EtwNativeAotCpuSampler();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sampler.SampleAsync(1, TimeSpan.FromSeconds(1)));

        ex.Message.Should().Contain("administrative elevation",
            "error message should mention elevation requirement");
    }

    [Fact]
    public async Task SampleAsync_RespectsCancellation()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || TraceEventSession.IsElevated() != true)
        {
            return;
        }

        var sampler = new EtwNativeAotCpuSampler();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Should cancel promptly rather than hang for the full duration.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sampler.SampleAsync(Environment.ProcessId, TimeSpan.FromSeconds(30), cancellationToken: cts.Token));
    }

    [Fact]
    public void SymbolSource_PdbResolved_ExistsInEnum()
    {
        // Validates that the PdbResolved enum value is available for the ETW path.
        var source = NativeAotSymbolDemangler.SymbolSource.PdbResolved;
        source.Should().NotBe(NativeAotSymbolDemangler.SymbolSource.ElfDemangled,
            "Windows ETW path should use PdbResolved, not ElfDemangled");
        source.Should().NotBe(NativeAotSymbolDemangler.SymbolSource.Unknown);
    }

    [Fact]
    public async Task ErrorMessage_MentionsAdminOrPrivilege()
    {
        // Verifies the error message provides actionable remediation.
        var sampler = new EtwNativeAotCpuSampler();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && TraceEventSession.IsElevated() == true)
        {
            return;
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sampler.SampleAsync(1, TimeSpan.FromSeconds(1)));
        ex.Message.Should().ContainAny("Administrator", "elevation", "SeSystemProfilePrivilege");
    }

    /// <summary>
    /// Live integration test: captures CPU samples from the current process (self-profiling).
    /// Requires Windows + admin. The current process is CoreCLR; CLR method-load and
    /// rundown events must retain the known managed workload name independently of DIA.
    /// Full PDB symbol resolution is validated against NativeAOT targets where symbols
    /// are statically compiled into the binary.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task SampleAsync_CapturesFromCurrentProcess_WhenElevated()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || TraceEventSession.IsElevated() != true)
        {
            // Skip: not Windows or not elevated.
            return;
        }

        var sampler = new EtwNativeAotCpuSampler();
        var pid = Environment.ProcessId;

        using var cts = new CancellationTokenSource();
        var loadTask = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
            BurnManagedCpu(cts.Token);
        }, cts.Token);

        try
        {
            var result = await sampler.SampleAsync(pid, TimeSpan.FromSeconds(7), topN: 50);

            result.Should().NotBeNull();
            result.Summary.ProcessId.Should().Be(pid);
            result.Summary.TotalSamples.Should().BeGreaterThan(0,
                "should have captured at least one CPU sample");
            result.Summary.TopHotspots.Should().NotBeEmpty(
                "should have identified at least one hotspot");
            result.Summary.TopHotspots.Should().Contain(
                hotspot => hotspot.Frame.Method.Contains(nameof(BurnManagedCpu), StringComparison.Ordinal),
                "the workload is JIT-compiled after ETW starts and CLR event-derived names must survive strict native PDB validation. " +
                string.Join(" ", result.Summary.Notes));
            result.Summary.Notes.Should().Contain(note => note.Contains("CLR JIT/loader/rundown", StringComparison.Ordinal));

            result.Artifact.Root.Should().NotBeNull();
            result.Artifact.Root.Children.Should().NotBeEmpty(
                "call tree should have at least one child node");

            result.Artifact.SymbolSource.Should().Be(NativeAotSymbolDemangler.SymbolSource.PdbResolved);
            result.Summary.SymbolSource.Should().Be(result.Artifact.SymbolSource,
                "summary consumers must receive the same symbol provenance as drilldown consumers");
        }
        finally
        {
            cts.Cancel();
            try { await loadTask; } catch (OperationCanceledException) { }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BurnManagedCpu(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Thread.SpinWait(10_000);
        }
    }
}
