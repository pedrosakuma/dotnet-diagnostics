using DotnetDiagnostics.Core.Dump;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public class GcDumpTypeAggregatorTests
{
    [Fact]
    public void Project_RanksTypesByBytesAndInstances_AndComputesPercent()
    {
        var agg = new GcDumpTypeAggregator();
        agg.RegisterType(1, "System.String");
        agg.RegisterType(2, "System.Byte[]");
        agg.RegisterType(3, "MyApp.Tiny");

        // 100 small strings, 2 huge byte arrays, 1 tiny object.
        for (var i = 0; i < 100; i++)
        {
            agg.AddNode(1, 40);
        }
        agg.AddNode(2, 4000);
        agg.AddNode(2, 4000);
        agg.AddNode(3, 24);

        agg.TotalBytes.Should().Be(40 * 100 + 8000 + 24);
        agg.NodeCount.Should().Be(103);

        var (byBytes, byInstances) = agg.Project(snapshotTopTypes: 200);

        byBytes.Should().NotBeEmpty();
        byBytes[0].TypeFullName.Should().Be("System.Byte[]");
        byBytes[0].TotalBytes.Should().Be(8000);
        byBytes[0].InstanceCount.Should().Be(2);
        byBytes[0].TotalBytesPercent.Should().BeApproximately(66.0, 1.0);

        byInstances[0].TypeFullName.Should().Be("System.String");
        byInstances[0].InstanceCount.Should().Be(100);
    }

    [Fact]
    public void ToTypeStat_UnknownType_FallsBackToHexId()
    {
        var agg = new GcDumpTypeAggregator();
        agg.AddNode(0xABCD, 16);

        var (byBytes, _) = agg.Project(10);

        byBytes.Should().ContainSingle();
        byBytes[0].TypeFullName.Should().Be("0xABCD");
    }

    [Fact]
    public void Project_HonoursTopTypesCap()
    {
        var agg = new GcDumpTypeAggregator();
        for (ulong t = 0; t < 50; t++)
        {
            agg.RegisterType(t, $"Type{t}");
            agg.AddNode(t, t + 1);
        }

        var (byBytes, _) = agg.Project(snapshotTopTypes: 5);

        byBytes.Should().HaveCount(5);
    }
}
