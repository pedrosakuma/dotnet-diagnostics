using DotnetDiagnostics.Core.Dump;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class ComWrapperAggregationTests
{
    [Fact]
    public void Aggregate_RanksCcwAndRcwTypesByRefCount_AndCountsDisconnectedAndWinRt()
    {
        var comObjectIdentity = new TypeIdentity("MyApp.ComObject")
        {
            ModuleName = "MyApp.dll",
            ModuleVersionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            MetadataToken = 0x02000001,
        };

        var shellIdentity = new TypeIdentity("Shell32.ShellItem")
        {
            ModuleName = "Shell32Interop.dll",
            ModuleVersionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            MetadataToken = 0x02000002,
        };

        var view = ComWrapperAggregation.Aggregate(
            ccwSamples:
            [
                new ComWrapperAggregation.CcwSample("MyApp.ComObject", RefCount: 3, InterfaceCount: 2, comObjectIdentity),
                new ComWrapperAggregation.CcwSample("MyApp.ComObject", RefCount: 1, InterfaceCount: 2, comObjectIdentity),
            ],
            rcwSamples:
            [
                new ComWrapperAggregation.RcwSample("Shell32.ShellItem", RefCount: 2, IsDisconnected: true, IsWinRtObject: false, shellIdentity),
                new ComWrapperAggregation.RcwSample("Shell32.ShellItem", RefCount: 1, IsDisconnected: false, IsWinRtObject: true, shellIdentity),
            ]);

        view.CcwCount.Should().Be(2);
        view.CcwTotalRefCount.Should().Be(4);
        view.TopCcwTypes.Should().ContainSingle();
        view.TopCcwTypes[0].TypeFullName.Should().Be("MyApp.ComObject");
        view.TopCcwTypes[0].Count.Should().Be(2);
        view.TopCcwTypes[0].TotalRefCount.Should().Be(4);
        view.TopCcwTypes[0].MaxRefCount.Should().Be(3);
        view.TopCcwTypes[0].TotalInterfaceCount.Should().Be(4);
        view.TopCcwTypes[0].Identity.Should().Be(comObjectIdentity);

        view.RcwCount.Should().Be(2);
        view.RcwTotalRefCount.Should().Be(3);
        view.RcwDisconnectedCount.Should().Be(1);
        view.RcwWinRtObjectCount.Should().Be(1);
        view.TopRcwTypes.Should().ContainSingle();
        view.TopRcwTypes[0].TypeFullName.Should().Be("Shell32.ShellItem");
        view.TopRcwTypes[0].Count.Should().Be(2);
        view.TopRcwTypes[0].TotalRefCount.Should().Be(3);
        view.TopRcwTypes[0].MaxRefCount.Should().Be(2);
        view.TopRcwTypes[0].DisconnectedCount.Should().Be(1);
        view.TopRcwTypes[0].WinRtObjectCount.Should().Be(1);

        view.Notes.Should().ContainSingle();
        view.Notes[0].Should().Contain("disconnected");
    }

    [Fact]
    public void Aggregate_UnknownTypeNameFallsBackToPlaceholder()
    {
        var view = ComWrapperAggregation.Aggregate(
            ccwSamples: [new ComWrapperAggregation.CcwSample(null, RefCount: 1, InterfaceCount: 1, null)],
            rcwSamples: []);

        view.TopCcwTypes.Should().ContainSingle();
        view.TopCcwTypes[0].TypeFullName.Should().Be("<collected-or-unresolved>");
    }

    [Fact]
    public void Builder_AddRcwCleanupEntry_TracksExactCountEvenWhenSampleIsCapped()
    {
        var builder = new ComWrapperAggregation.Builder();

        for (var i = 0; i < ComWrapperAggregation.MaxCleanupBacklogSample + 10; i++)
        {
            builder.AddRcwCleanupEntry(new ComCleanupBacklogEntry((ulong)i, 0, 0, IsFreeThreaded: false));
        }

        for (var i = 0; i < 5; i++)
        {
            builder.AddSyncBlockCleanupEntry();
        }

        var view = builder.BuildView();

        view.RcwCleanupBacklogCount.Should().Be(ComWrapperAggregation.MaxCleanupBacklogSample + 10);
        view.RcwCleanupBacklogSample.Should().HaveCount(ComWrapperAggregation.MaxCleanupBacklogSample);
        view.SyncBlockCleanupBacklogCount.Should().Be(5);
        view.Notes.Should().Contain(note => note.Contains("truncated"));
    }

    [Fact]
    public void Builder_MatchesAggregateWithoutMaterializingSamples()
    {
        ComWrapperAggregation.CcwSample[] ccwSamples =
        [
            new ComWrapperAggregation.CcwSample("MyApp.Alpha", RefCount: 2, InterfaceCount: 1, null),
            new ComWrapperAggregation.CcwSample("MyApp.Alpha", RefCount: 1, InterfaceCount: 1, null),
        ];
        ComWrapperAggregation.RcwSample[] rcwSamples =
        [
            new ComWrapperAggregation.RcwSample("MyApp.Beta", RefCount: 1, IsDisconnected: false, IsWinRtObject: false, null),
        ];

        var builder = new ComWrapperAggregation.Builder();
        foreach (var sample in ccwSamples)
        {
            builder.AddCcw(sample);
        }

        foreach (var sample in rcwSamples)
        {
            builder.AddRcw(sample);
        }

        var streaming = builder.BuildView();
        var aggregate = ComWrapperAggregation.Aggregate(ccwSamples, rcwSamples);

        streaming.Should().BeEquivalentTo(aggregate);
    }

    [Fact]
    public void Builder_MarkDetectionUnavailable_AddsNoteOnce_AndKeepsPriorCounts()
    {
        var builder = new ComWrapperAggregation.Builder();
        builder.AddCcw(new ComWrapperAggregation.CcwSample("MyApp.ComObject", RefCount: 1, InterfaceCount: 1, null));

        builder.MarkDetectionUnavailable("simulated failure");
        builder.MarkDetectionUnavailable("a second, different failure");

        var view = builder.BuildView();

        view.CcwCount.Should().Be(1);
        view.Notes.Should().ContainSingle(note => note.Contains("simulated failure"));
        view.Notes.Should().NotContain(note => note.Contains("a second, different failure"));
    }
}
