using System.Collections.Immutable;
using DotnetDiagnosticsMcp.Core.Dump;
using FluentAssertions;
using Microsoft.Diagnostics.Runtime;

namespace DotnetDiagnosticsMcp.Core.Tests;

public sealed class GcHandleAggregationTests
{
    [Fact]
    public void Aggregate_BucketsPublicKinds_AndNotesInternalKinds()
    {
        var alphaIdentity = new TypeIdentity("MyApp.Alpha")
        {
            ModuleName = "MyApp.dll",
            ModuleVersionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            MetadataToken = 0x02000001,
        };

        var betaIdentity = new TypeIdentity("MyApp.Beta")
        {
            ModuleName = "MyApp.dll",
            ModuleVersionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            MetadataToken = 0x02000002,
        };

        var view = GcHandleAggregation.Aggregate(
        [
            new GcHandleAggregation.GcHandleSample(ClrHandleKind.Strong, "MyApp.Alpha", 128, alphaIdentity),
            new GcHandleAggregation.GcHandleSample(ClrHandleKind.Strong, "MyApp.Alpha", 128, alphaIdentity),
            new GcHandleAggregation.GcHandleSample(ClrHandleKind.WeakShort, "MyApp.Beta", 64, betaIdentity),
            new GcHandleAggregation.GcHandleSample(ClrHandleKind.WeakLong, null, 0, null),
            new GcHandleAggregation.GcHandleSample(ClrHandleKind.Pinned, "System.Byte[]", 1024, null),
            new GcHandleAggregation.GcHandleSample(ClrHandleKind.Dependent, "MyApp.Node", 32, null),
            new GcHandleAggregation.GcHandleSample(ClrHandleKind.AsyncPinned, "System.Byte[]", 256, null),
            new GcHandleAggregation.GcHandleSample(ClrHandleKind.RefCounted, "MyApp.RefCounted", 48, null),
            new GcHandleAggregation.GcHandleSample(ClrHandleKind.SizedRef, "MyApp.SizedRef", 96, null),
        ]);

        view.TotalHandles.Should().Be(9);
        view.ByKind.Select(static bucket => bucket.Kind).ToArray()
            .Should().Equal("Pinned", "Normal", "Weak", "WeakTrackResurrection", "Dependent", "AsyncPinned");

        view.ByKind.Single(bucket => bucket.Kind == "Normal")
            .Should().BeEquivalentTo(new GcHandleBucket(
                Kind: "Normal",
                Count: 2,
                RetainedBytes: 256,
                TopTypes: ImmutableArray.Create(new GcHandleTypeStat("MyApp.Alpha", 2, 256, alphaIdentity))));

        var resurrected = view.ByKind.Single(bucket => bucket.Kind == "WeakTrackResurrection").TopTypes.Single();
        resurrected.TypeFullName.Should().Be("<collected-or-unresolved>");
        resurrected.Count.Should().Be(1);
        resurrected.RetainedBytes.Should().Be(0);
        resurrected.Identity.Should().BeNull();

        view.Notes.Should().ContainSingle();
        view.Notes[0].Should().Contain("RefCounted");
        view.Notes[0].Should().Contain("SizedRef");
        view.Notes[0].Should().Contain("TotalHandles");
    }

    [Fact]
    public void Aggregate_SeparatesSameTypeNameWhenTypeIdentityDiffers()
    {
        var firstIdentity = new TypeIdentity("Shared.Name")
        {
            ModuleName = "One.dll",
            ModuleVersionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            MetadataToken = 0x02000001,
        };

        var secondIdentity = new TypeIdentity("Shared.Name")
        {
            ModuleName = "Two.dll",
            ModuleVersionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            MetadataToken = 0x02000001,
        };

        var view = GcHandleAggregation.Aggregate(
        [
            new GcHandleAggregation.GcHandleSample(ClrHandleKind.Strong, "Shared.Name", 10, firstIdentity),
            new GcHandleAggregation.GcHandleSample(ClrHandleKind.Strong, "Shared.Name", 20, secondIdentity),
        ]);

        var normalBucket = view.ByKind.Single(bucket => bucket.Kind == "Normal");
        normalBucket.Count.Should().Be(2);
        normalBucket.TopTypes.Should().HaveCount(2);
        normalBucket.TopTypes.Should().Contain(stat => stat.Identity == firstIdentity && stat.Count == 1 && stat.RetainedBytes == 10);
        normalBucket.TopTypes.Should().Contain(stat => stat.Identity == secondIdentity && stat.Count == 1 && stat.RetainedBytes == 20);
    }
}
