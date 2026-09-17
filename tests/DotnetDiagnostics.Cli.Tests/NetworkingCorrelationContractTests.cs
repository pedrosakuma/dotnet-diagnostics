using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class NetworkingCorrelationContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualCollectCommand_PreservesLimitationsAndLegacyUnknown(bool legacy)
    {
        var snapshot = NetworkingCorrelationContractFixture.Create(legacy);
        using var services = new ServiceCollection()
            .AddSingleton<INetworkingCollector>(new NetworkingCorrelationContractFixture.Collector(snapshot))
            .AddSingleton<IProcessContextResolver>(new NetworkingCorrelationContractFixture.Resolver())
            .AddSingleton<IDiagnosticHandleStore>(new MemoryDiagnosticHandleStore()).BuildServiceProvider();
        var (exit, json) = await CliGcActivitiesTests.ExecuteAsync(services,
            ["collect", "--kind", "networking", "--duration", "1", "--json"]);
        Assert.Equal(0, exit);
        Assert.Contains(legacy ? "unknown" : "HTTP 1/3", json.GetProperty("summary").GetString(), StringComparison.Ordinal);
        if (!legacy)
            Assert.Equal(2, json.GetProperty("data").GetProperty("correlation").GetProperty("byKind").GetProperty("http")
                .GetProperty("counts").GetProperty("ambiguousStarts").GetInt64());
    }
}
