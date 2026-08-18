using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.NativeLockContention;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Cli;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliNativeContentionEvidenceTests
{
    [Fact]
    public void BuildResult_PreservesOffCpuNativeContentionEvidence_InHumanAndJsonEnvelope()
    {
        var evidence = new NativeContentionEvidence(
            NativeContentionEvidenceLevels.ConfirmedBlocking,
            "closed futex",
            NativeSyncSpanCount: 1,
            ClosedNativeSyncSpanCount: 1,
            NativeSyncOffCpuMicros: 25_000,
            ClosedNativeSyncOffCpuMicros: 25_000);
        var snapshot = new OffCpuSnapshot(
            ProcessId: 42,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(1),
            TotalOffCpuMicros: 25_000,
            DistinctThreads: 1,
            TopBlockingStacks:
            [
                new OffCpuStackHotspot(
                    "pthread_mutex_lock",
                    25_000,
                    1,
                    "S",
                    [new OffCpuFrame("libc.so.6", "pthread_mutex_lock")],
                    SyscallBreakdown: [new OffCpuSyscallAttribution("futex", 1, 25_000)],
                    NativeContentionEvidence: evidence),
            ],
            SchedSwitches: 1,
            SymbolSource: "test",
            NativeContentionEvidence: evidence);
        var result = DiagnosticResult.Ok(
            snapshot,
            "Native sync blocking evidence: confirmed-blocking (1 closed / 0 censored span(s), 25.0 ms closed).");

        var cli = CliCommands.BuildResult(result, static (_, _) => { });

        cli.Human.Should().Contain("Native sync blocking evidence: confirmed-blocking");
        var json = JsonSerializer.SerializeToElement(cli.Envelope, cli.Envelope.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.GetProperty("data").GetProperty("nativeContentionEvidence").GetProperty("level").GetString()
            .Should().Be(NativeContentionEvidenceLevels.ConfirmedBlocking);
    }
}
