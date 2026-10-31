using System.Text.Json;
using DotnetDiagnostics.BenchmarkDotNet;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.ProcessDiscovery;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.BenchmarkDotNet.Tests;

public sealed class ContentionConcentrationContractTests
{
    [Fact]
    public async Task InProcessCollector_DoesNotAddCliConcentrationEvidence()
    {
        using var collector = new InProcessDiagnosticCollector(() => new ServiceCollection()
            .AddSingleton<IContentionCollector>(new Collector())
            .AddSingleton<IProcessContextResolver>(new Resolver())
            .AddSingleton<IDiagnosticHandleStore>(new MemoryDiagnosticHandleStore())
            .BuildServiceProvider());

        var result = await collector.CollectAsync(
            Environment.ProcessId,
            "contention",
            durationSeconds: 1,
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        using var document = JsonDocument.Parse(result.Json);
        var data = document.RootElement.GetProperty("Data");
        data.TryGetProperty("Concentration", out _).Should().BeFalse(
            "contention concentration is a Core/CLI vertical slice and must not change BenchmarkDotNet artifacts");
    }

    private sealed class Collector : IContentionCollector
    {
        public Task<ContentionSnapshot> CollectAsync(
            int processId,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ContentionSnapshot(
                processId,
                DateTimeOffset.UnixEpoch,
                duration,
                0,
                0,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                [],
                []));
    }

    private sealed class Resolver : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(
            int? requestedProcessId,
            CancellationToken cancellationToken)
            => Task.FromResult(new ProcessContextResolution(
                new ProcessContext(
                    requestedProcessId ?? Environment.ProcessId,
                    RuntimeFlavor.CoreClr,
                    CanSampleCpu: true,
                    CanCollectGcDump: true,
                    AutoResolved: false),
                null));
    }
}
