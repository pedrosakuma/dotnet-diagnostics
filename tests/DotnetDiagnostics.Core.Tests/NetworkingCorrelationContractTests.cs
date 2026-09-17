using System.Text.Json;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.Tests;

public sealed class NetworkingCorrelationContractTests
{
    [Theory]
    [InlineData("summary", false)]
    [InlineData("byOperation", false)]
    [InlineData("queue", false)]
    [InlineData("tls", false)]
    [InlineData("dns", false)]
    [InlineData("summary", true)]
    [InlineData("byOperation", true)]
    [InlineData("queue", true)]
    [InlineData("tls", true)]
    [InlineData("dns", true)]
    public void AllQueries_PreserveCorrelation(string view, bool legacy)
    {
        var snapshot = NetworkingCorrelationContractFixture.Create(legacy);
        var result = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.NetworkingSnapshot, view, snapshot, 1);
        var json = JsonSerializer.SerializeToElement(result.Result!.Payload);
        if (legacy)
        {
            Assert.Equal(JsonValueKind.Null, json.GetProperty("Correlation").ValueKind);
            return;
        }
        Assert.Equal(2, json.GetProperty("Correlation").GetProperty("ByKind").GetProperty("http")
            .GetProperty("Counts").GetProperty("ambiguousStarts").GetInt64());
        Assert.True(json.GetProperty("Correlation").GetProperty("ByKind").GetProperty("tls").GetProperty("HasLimitations").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedUseCase_AndLegacyJson_DoNotInventCompleteCoverage(bool legacy)
    {
        var snapshot = NetworkingCorrelationContractFixture.Create(legacy);
        var result = await EventCollectionUseCases.CollectNetworking(new NetworkingCorrelationContractFixture.Collector(snapshot),
            new NetworkingCorrelationContractFixture.Resolver(), new MemoryDiagnosticHandleStore(),
            Environment.ProcessId, 1);
        Assert.False(result.IsError);
        Assert.Equal(snapshot.Correlation, result.Data!.Correlation);
        var roundTrip = JsonSerializer.Deserialize<NetworkingSnapshot>(JsonSerializer.Serialize(result.Data))!;
        if (!legacy)
            Assert.Equal(2, roundTrip.Correlation!.Http.AmbiguousStarts);
        Assert.Contains(legacy ? "unknown" : "HTTP 1/3", result.Summary, StringComparison.Ordinal);
        if (legacy)
        {
            var json = JsonSerializer.SerializeToNode(snapshot)!.AsObject();
            json.Remove("Correlation");
            Assert.Null(json.Deserialize<NetworkingSnapshot>()!.Correlation);
        }
    }
}
