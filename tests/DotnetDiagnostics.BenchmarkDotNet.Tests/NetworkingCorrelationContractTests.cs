using System.Text.Json;
using DotnetDiagnostics.BenchmarkDotNet;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.BenchmarkDotNet.Tests;

public sealed class NetworkingCorrelationContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualCollectorAndReport_PreserveLimitationsAndLegacyUnknown(bool legacy)
    {
        var snapshot = NetworkingCorrelationContractFixture.Create(legacy);
        using var collector = new InProcessDiagnosticCollector(() => new ServiceCollection()
            .AddSingleton<INetworkingCollector>(new NetworkingCorrelationContractFixture.Collector(snapshot))
            .AddSingleton<IProcessContextResolver>(new NetworkingCorrelationContractFixture.Resolver())
            .AddSingleton<IDiagnosticHandleStore>(new MemoryDiagnosticHandleStore()).BuildServiceProvider());
        var capture = await collector.CollectAsync(Environment.ProcessId, "networking", 1, CancellationToken.None);
        Assert.False(capture.IsError);
        var expected = legacy ? "unknown" : "HTTP 1/3";
        Assert.Contains(expected, capture.Headline, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(capture.Json);
        var hasQuality = json.RootElement.GetProperty("Data").TryGetProperty("CaptureQuality", out var quality);
        if (legacy) Assert.False(hasQuality);
        else
        {
            Assert.True(hasQuality);
            Assert.Equal("early", quality.GetProperty("Completion").GetString());
            Assert.Equal(7, quality.GetProperty("EventsLost").GetInt64());
            Assert.Equal(1, quality.GetProperty("ParseErrors").GetInt64());
        }
        if (!legacy)
            Assert.Equal(2, json.RootElement.GetProperty("Data").GetProperty("Correlation").GetProperty("ByKind").GetProperty("http")
                .GetProperty("Counts").GetProperty("ambiguousStarts").GetInt64());
        var report = DotnetDiagnosticsReportExporter.BuildMarkdown(
            [new BenchmarkDiagnosticEntry("Loopback", "networking", capture.IsError, capture.Summary,
                capture.Headline, "networking.json")]);
        Assert.Contains(expected, report, StringComparison.Ordinal);
        Assert.Contains(legacy ? "transport loss are unknown" : "completion=early", report, StringComparison.Ordinal);
    }
}
