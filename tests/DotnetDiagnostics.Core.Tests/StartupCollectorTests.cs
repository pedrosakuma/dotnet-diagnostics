using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Startup;
using DotnetDiagnostics.Core.UseCases;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class StartupCollectorTests
{
    [Fact]
    public void StartupCaptureBuffer_CapsRetainedEvents_ButKeepsTotalsAndAggregates()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var buffer = new EventPipeStartupCollector.StartupCaptureBuffer();

        for (var i = 0; i < EventPipeStartupCollector.MaxRetainedTimelineEvents + 3; i++)
        {
            buffer.AddAssembly(new StartupAssemblyLoad(
                startedAt.AddMilliseconds(i),
                "AssemblyLoad",
                i % 2 == 0 ? "Alpha" : "Beta",
                i));
        }

        buffer.AddDiEvent(new StartupDiEvent(
            startedAt.AddSeconds(5),
            "ServiceProviderBuilt",
            10,
            "Alpha.Service",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null));
        buffer.AddDiEvent(new StartupDiEvent(
            startedAt.AddSeconds(8),
            "ServiceResolved",
            10,
            "Alpha.Service",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null));

        buffer.TotalAssemblyLoads.Should().Be(EventPipeStartupCollector.MaxRetainedTimelineEvents + 3);
        buffer.AssemblyLoads.Should().HaveCount(EventPipeStartupCollector.MaxRetainedAssemblyLoads);
        buffer.TotalTimelineEvents.Should().Be(EventPipeStartupCollector.MaxRetainedTimelineEvents + 5);
        buffer.Timeline.Should().HaveCount(EventPipeStartupCollector.MaxRetainedTimelineEvents);
        buffer.TotalDiEvents.Should().Be(2);
        buffer.DiServiceProviderBuiltCount.Should().Be(1);
        buffer.DiServiceResolvedCount.Should().Be(1);
        buffer.ObservedDiActivityDuration.Should().Be(TimeSpan.FromSeconds(3));
        buffer.Truncated.Should().BeTrue();

        buffer.BuildAssemblyAggregates()
            .Should()
            .ContainEquivalentOf(new StartupLoadAggregate("Alpha", 1002, startedAt, startedAt.AddMilliseconds(2002)));
        buffer.BuildAssemblyAggregates()
            .Should()
            .ContainEquivalentOf(new StartupLoadAggregate("Beta", 1001, startedAt.AddMilliseconds(1), startedAt.AddMilliseconds(2001)));
    }

    [Fact]
    public void StartupViews_SurfaceTruncationAndUseIncrementalAggregates()
    {
        var snapshot = new StartupSnapshot(
            ProcessId: 42,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(10),
            TotalAssemblyLoads: 10,
            TotalModuleLoads: 6,
            TotalTimelineEvents: 20,
            TotalDiEvents: 4,
            DiServiceProviderBuiltCount: 1,
            DiServiceProviderDescriptorsCount: 0,
            DiCallSiteBuiltCount: 0,
            DiServiceResolvedCount: 2,
            DiExpressionTreeGeneratedCount: 0,
            DiDynamicMethodBuiltCount: 0,
            DiServiceRealizationFailedCount: 0,
            ObservedDiActivityDuration: TimeSpan.FromMilliseconds(50),
            AssemblyLoads:
            [
                new StartupAssemblyLoad(DateTimeOffset.UtcNow, "AssemblyLoad", "TailOnly", 1),
            ],
            AssemblyAggregates:
            [
                new StartupLoadAggregate("TopAssembly", 10, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ],
            ModuleLoads:
            [
                new StartupModuleLoad(DateTimeOffset.UtcNow, "ModuleLoad", "TailOnly.dll", "/app/TailOnly.dll", 2, 1),
            ],
            ModuleAggregates:
            [
                new StartupLoadAggregate("TopModule.dll", 6, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ],
            DiEvents:
            [
                new StartupDiEvent(DateTimeOffset.UtcNow, "ServiceProviderBuilt", 1, "Svc", null, null, null, null, null, null, null, null, null),
            ],
            Timeline:
            [
                new StartupTimelineEvent(DateTimeOffset.UtcNow, "assembly", "AssemblyLoad", "TailOnly"),
            ],
            Truncated: true,
            Notes: ["bounded"]);

        var summary = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.StartupSnapshot, "summary", snapshot, 5)
            .Result!.Payload.Should().BeOfType<StartupSummaryView>().Subject;
        summary.Truncated.Should().BeTrue();
        summary.TopAssemblies.Should().ContainSingle().Which.Count.Should().Be(10);

        var timeline = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.StartupSnapshot, "timeline", snapshot, 5)
            .Result!.Payload.Should().BeOfType<StartupTimelineView>().Subject;
        timeline.Truncated.Should().BeTrue();
        timeline.TotalEvents.Should().Be(20);
        timeline.Returned.Should().Be(1);
    }

    [Fact]
    public async Task CollectStartup_RejectsDurationAboveCap()
    {
        var result = await EventCollectionUseCases.CollectStartup(
            collector: null!,
            resolver: null!,
            handles: null!,
            processId: 42,
            durationSeconds: EventCollectionUseCases.MaxStartupDurationSeconds + 1,
            depth: SamplingDepth.Summary,
            cancellationToken: CancellationToken.None);

        result.IsError.Should().BeTrue();
        var error = result.Error;
        error.Should().NotBeNull();
        error!.Kind.Should().Be("InvalidArgument");
        error.Message.Should().Contain($"<= {EventCollectionUseCases.MaxStartupDurationSeconds}");
    }
}
