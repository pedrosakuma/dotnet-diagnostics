using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Cli;
using System.Globalization;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class NetworkingCorrelationContractTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "absent")]
    [InlineData(false, "zero")]
    [InlineData(false, "positive")]
    [InlineData(false, "failed")]
    [InlineData(false, "partial")]
    [InlineData(false, "reservoir")]
    [InlineData(false, "unpaired")]
    [InlineData(false, "incomplete")]
    [InlineData(false, "loss")]
    [InlineData(false, "loss-empty")]
    [InlineData(false, "unknown-loss")]
    [InlineData(false, "invalid-queue")]
    [InlineData(false, "legacy")]
    [InlineData(false, "legacy-counts")]
    public async Task ActualCollectCommand_PreservesLimitationsAndLegacyUnknown(bool legacy, string? scenario)
    {
        var snapshot = scenario is null ? NetworkingCorrelationContractFixture.Create(legacy)
            : NetworkingCorrelationContractFixture.CreateLatencyScenario(scenario);
        using var services = new ServiceCollection()
            .AddSingleton<INetworkingCollector>(new NetworkingCorrelationContractFixture.Collector(snapshot))
            .AddSingleton<IProcessContextResolver>(new NetworkingCorrelationContractFixture.Resolver())
            .AddSingleton<IDiagnosticHandleStore>(new MemoryDiagnosticHandleStore()).BuildServiceProvider();
        var (exit, json) = await CliGcActivitiesTests.ExecuteAsync(services,
            ["collect", "--kind", "networking", "--duration", "1", "--json"]);
        Assert.Equal(0, exit);
        if (scenario is not null)
        {
            NetworkingCorrelationContractFixture.AssertLatencyScenario(scenario, json.GetProperty("data"),
                json.GetProperty("summary").GetString());
            Assert.True(CliCommandExecution.TryPrepareOneShot(["collect", "--kind", "networking", "--duration", "1"],
                out var prepared, out _));
            using var stdout = new StringWriter(CultureInfo.InvariantCulture);
            using var stderr = new StringWriter(CultureInfo.InvariantCulture);
            var human = await CliCommandExecution.ExecuteAsync(services, prepared!, stdout, stderr,
                new CliExecutionOptions(CliExecutionContext.OneShot, AnsiEnabled: false, ShowProgress: false), CancellationToken.None);
            Assert.Equal(0, human.ExitCode);
            Assert.Empty(stderr.ToString());
            NetworkingCorrelationContractFixture.AssertLatencyScenario(scenario, json.GetProperty("data"), stdout.ToString());
            return;
        }
        Assert.Contains(legacy ? "transport loss are unknown" : "completion=early", json.GetProperty("summary").GetString(), StringComparison.Ordinal);
        var hasQuality = json.GetProperty("data").TryGetProperty("captureQuality", out var quality);
        if (legacy) Assert.False(hasQuality);
        else
        {
            Assert.True(hasQuality);
            Assert.Equal("early", quality.GetProperty("completion").GetString());
            Assert.Equal(7, quality.GetProperty("eventsLost").GetInt64());
            Assert.Equal(1, quality.GetProperty("parseErrors").GetInt64());
        }
        Assert.Contains(legacy ? "unknown" : "HTTP 1/3", json.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Contains(legacy ? "Latency population/outcomes are unknown" : "v2 includes failed completions",
            json.GetProperty("summary").GetString(), StringComparison.Ordinal);
        if (!legacy)
        {
            Assert.Equal(2, json.GetProperty("data").GetProperty("correlation").GetProperty("byKind").GetProperty("http")
                .GetProperty("counts").GetProperty("ambiguousStarts").GetInt64());
            foreach (var kind in new[] { "http", "dns", "tls" })
            {
                var counts = json.GetProperty("data").GetProperty("correlation").GetProperty("byKind").GetProperty(kind).GetProperty("counts");
                Assert.Equal(2, counts.GetProperty("latencyPopulationVersion").GetInt64());
                Assert.Equal(1, counts.GetProperty("pairedFailed").GetInt64());
                Assert.Equal(0, counts.GetProperty("pairedWithoutFailure").GetInt64());
            }
        }
    }
}
