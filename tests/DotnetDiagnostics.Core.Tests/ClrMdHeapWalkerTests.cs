using DotnetDiagnostics.Core.Dump;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class ClrMdHeapWalkerTests
{
    [Fact]
    public void AggregateAndRankTypeStats_LoadedCopiesCollectivelyReachTopN()
    {
        var shared = SharedIdentity("/app/first/Shared.dll");
        var copies = new[]
        {
            Stat(shared, bytes: 60, instances: 6, imageBase: 0x1000),
            Stat(shared with { ModulePath = "/app/second/Shared.dll" }, bytes: 60, instances: 6, imageBase: 0x2000),
            Stat(new TypeIdentity("Unrelated") { ModuleName = "Other.dll" }, bytes: 100, instances: 10, imageBase: 0x3000),
        };

        var rankings = ClrMdHeapWalker.AggregateAndRankTypeStats(copies, totalBytes: 220, topN: 1);

        var byBytes = rankings.ByBytes.Should().ContainSingle().Subject;
        byBytes.TypeFullName.Should().Be("Shared.LoadedCopy");
        byBytes.TotalBytes.Should().Be(120);
        byBytes.ModuleImageBase.Should().BeNull();
        byBytes.ModuleImageBases.Should().Equal(0x1000UL, 0x2000UL);

        rankings.ByInstances.Should().ContainSingle()
            .Which.TypeFullName.Should().Be("Shared.LoadedCopy");
    }

    [Fact]
    public void AggregateAndRankTypeStats_DeduplicatesImageBaseBeforeUnrelatedTypeRanking()
    {
        var shared = SharedIdentity("/app/first/Shared.dll");
        var duplicateCopy = Stat(shared, bytes: 60, instances: 6, imageBase: 0x1000);
        var copies = new[]
        {
            duplicateCopy,
            duplicateCopy,
            Stat(shared with { ModulePath = "/app/second/Shared.dll" }, bytes: 60, instances: 6, imageBase: 0x2000),
            Stat(new TypeIdentity("Unrelated.A") { ModuleName = "A.dll" }, bytes: 110, instances: 11, imageBase: 0x3000),
            Stat(new TypeIdentity("Unrelated.B") { ModuleName = "B.dll" }, bytes: 100, instances: 10, imageBase: 0x4000),
        };

        var rankings = ClrMdHeapWalker.AggregateAndRankTypeStats(copies, totalBytes: 330, topN: 2);

        rankings.ByBytes.Select(static stat => stat.TypeFullName)
            .Should().Equal("Shared.LoadedCopy", "Unrelated.A");
        rankings.ByInstances.Select(static stat => stat.TypeFullName)
            .Should().Equal("Shared.LoadedCopy", "Unrelated.A");
        rankings.ByBytes[0].TotalBytes.Should().Be(120);
        rankings.ByBytes[0].InstanceCount.Should().Be(12);
    }

    private static TypeIdentity SharedIdentity(string modulePath)
        => new("Shared.LoadedCopy")
        {
            ModuleName = "Shared.dll",
            ModulePath = modulePath,
            ModuleVersionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            MetadataToken = 0x02000001,
        };

    private static TypeStat Stat(TypeIdentity identity, long bytes, long instances, ulong imageBase)
        => new(
            identity.TypeFullName,
            identity.ModuleName,
            instances,
            bytes,
            0,
            identity)
        {
            ModuleImageBase = imageBase,
            ModuleImageBases = [imageBase],
        };
}
