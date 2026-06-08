using System.Text.Json;
using DotnetDiagnosticsMcp.Core.Comparison;
using DotnetDiagnosticsMcp.Core.Memory;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class ComparableSnapshotSerializationTests
{
    private static ComparableSnapshot Sample() => new(
        Schema: ComparableSnapshot.SchemaV1,
        Kind: "gc-datas",
        Label: "baseline",
        CapturedAt: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        ProcessId: 4242,
        Metrics: new[]
        {
            new MetricValue(
                new MetricDefinition("meanMedianThroughputCostPercent", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Percent, MetricNormalization.None, "%"),
                3.14),
        },
        Rows: new[]
        {
            new ComparableRow(
                new ComparableKey("cpu-sample", "MyApp.dll!MyApp.Worker.Spin", "abc:6", "MyApp.dll", "MyApp.Worker", "Spin"),
                "MyApp.Worker.Spin",
                new[]
                {
                    new MetricValue(
                        new MetricDefinition("exclusivePercent", MetricRole.Primary, BetterDirection.Lower, MetricAggregation.Percent, MetricNormalization.None, "%"),
                        42.5),
                }),
        },
        Provenance: new InvestigationProvenance("host-1")
        {
            Container = new ContainerProvenance(PodName: "pod-3"),
        });

    [Fact]
    public void RoundTrips_PreservingAllFields()
    {
        var original = Sample();

        var json = JsonSerializer.Serialize(original, ComparableSnapshotJsonContext.Default.ComparableSnapshot);
        var restored = JsonSerializer.Deserialize(json, ComparableSnapshotJsonContext.Default.ComparableSnapshot);

        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Enums_SerializeAsStableStrings()
    {
        var json = JsonSerializer.Serialize(Sample(), ComparableSnapshotJsonContext.Default.ComparableSnapshot);

        json.Should().Contain("\"Primary\"");
        json.Should().Contain("\"Lower\"");
        json.Should().Contain("\"Percent\"");
        json.Should().NotContain("\"Role\": 0");
    }

    [Fact]
    public void Json_CarriesSchemaAndOmitsNulls()
    {
        var json = JsonSerializer.Serialize(Sample(), ComparableSnapshotJsonContext.Default.ComparableSnapshot);

        json.Should().Contain(ComparableSnapshot.SchemaV1);
        // ExactId is set on the row key but TypeName-less optional fields stay omitted when null.
        json.Should().NotContain("\"GenericSignature\"");
    }
}
