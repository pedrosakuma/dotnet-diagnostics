using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.TestSupport;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Empirically validates that the core diagnostic collectors work unmodified against target
/// processes running older .NET major versions — the claim recorded in
/// docs/research/multi-version-target-support.md. Runs against the multi-targeted
/// <c>samples/MultiVersionSample</c> console app built for each TFM under test.
///
/// Each test skips (does not fail) when either that TFM's build output or its underlying shared
/// runtime isn't present on the host, since this repo doesn't want to force every dev machine / CI
/// runner to install every historical runtime just to build — see
/// <see cref="InstalledRuntimes"/> and <see cref="MultiVersionSampleProcess"/>.
/// </summary>
[Collection("LiveProcess")]
public class CrossVersionTargetTests
{
    [Theory]
    [InlineData("net8.0")]
    [InlineData("net9.0")]
    public async Task Counters_ReturnsSystemRuntimeMetrics_AgainstOlderRuntime(string targetFramework)
    {
        await using var sample = await MultiVersionSampleProcess.StartAsync(targetFramework);

        var collector = new EventPipeCounterCollector();
        var snapshot = await collector.CollectAsync(
            sample.ProcessId,
            TimeSpan.FromSeconds(6),
            providers: ["System.Runtime"],
            intervalSeconds: 1,
            cancellationToken: CancellationToken.None);

        snapshot.Counters.Should().NotBeEmpty(
            $"EventCounters must work against a {targetFramework} target ({sample.RuntimeDescription}) with zero code changes");
        snapshot.Counters.Should().Contain(c => c.Provider == "System.Runtime" && c.Name == "cpu-usage");
    }

    [Theory]
    [InlineData("net8.0")]
    [InlineData("net9.0")]
    public async Task GcEvents_Collect_AgainstOlderRuntime(string targetFramework)
    {
        await using var sample = await MultiVersionSampleProcess.StartAsync(targetFramework);

        var collector = new EventPipeGcCollector();
        var summary = await collector.CollectAsync(sample.ProcessId, TimeSpan.FromSeconds(6));

        summary.Should().NotBeNull(
            $"GC-event collection must work against a {targetFramework} target ({sample.RuntimeDescription}) with zero code changes");
    }

    [Theory]
    [InlineData("net8.0")]
    [InlineData("net9.0")]
    public async Task DumpAndHeapInspect_WorksAgainstOlderRuntime(string targetFramework)
    {
        await using var sample = await MultiVersionSampleProcess.StartAsync(targetFramework);

        var dumpRoot = Path.Combine(Path.GetTempPath(), $"diagnosticsmcp-crossversion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dumpRoot);
        try
        {
            var dumper = new DiagnosticsClientDumper(new TestArtifactRootProvider(dumpRoot));
            var dump = await dumper.WriteDumpAsync(sample.ProcessId, ProcessDumpType.WithHeap, outputDirectory: null, CancellationToken.None);
            File.Exists(dump.FilePath).Should().BeTrue();
            dump.FileSizeBytes.Should().BeGreaterThan(0);

            var inspector = new ClrMdDumpInspector();
            var inspection = await inspector.InspectAsync(
                dump.FilePath,
                new DumpInspectionOptions(TopTypes: 10),
                CancellationToken.None);

            inspection.Heap.TotalBytes.Should().BeGreaterThan(0,
                $"ClrMD must resolve the managed heap of a {targetFramework} dump ({sample.RuntimeDescription}) via its own target-local DAC");
            inspection.TopTypesByBytes.Should().NotBeEmpty();
        }
        finally
        {
            try { Directory.Delete(dumpRoot, recursive: true); } catch { /* best-effort */ }
        }
    }
}
