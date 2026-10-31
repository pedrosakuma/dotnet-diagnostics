using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.UseCases;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class ContentionConcentrationEvidenceTests
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UnixEpoch;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("dominant", ContentionConcentrationAvailability.Available, ContentionConcentrationShape.Dominant)]
    [InlineData("diffuse", ContentionConcentrationAvailability.Available, ContentionConcentrationShape.Diffuse)]
    [InlineData("burst", ContentionConcentrationAvailability.Available, ContentionConcentrationShape.ShortBurst)]
    [InlineData("owner-unavailable", ContentionConcentrationAvailability.Available, ContentionConcentrationShape.Dominant)]
    [InlineData("truncated", ContentionConcentrationAvailability.UnsupportedApproximation, ContentionConcentrationShape.TruncatedInventory)]
    [InlineData("healthy", ContentionConcentrationAvailability.InsufficientObservations, ContentionConcentrationShape.BoundedCaptureObservedNoContention)]
    [InlineData("insufficient", ContentionConcentrationAvailability.InsufficientObservations, ContentionConcentrationShape.Unavailable)]
    public void DeterministicShapeMatrix_PreservesBoundedSemantics(
        string scenario,
        ContentionConcentrationAvailability availability,
        ContentionConcentrationShape shape)
    {
        var evidence = ContentionConcentrationEvaluator.Evaluate(Snapshot(scenario));

        evidence.Availability.Should().Be(availability);
        evidence.Shape.Should().Be(shape);
        evidence.Method.Should().Be(new ContentionConcentrationMethod(
            ContentionConcentrationEvaluator.MethodId,
            ContentionConcentrationEvaluator.MethodVersion));
        evidence.IndependenceUnit.Should().Be(ContentionConcentrationEvaluator.IndependenceUnit);
        evidence.Counts.IndependentCaptureUnits.Should().Be(1);
        evidence.OwnerOverlap.Availability.Should().Be(ContentionConcentrationAvailability.NotApplicable);
        evidence.OwnerOverlap.UnavailableFields.Should().ContainSingle(item =>
            item.Reason == "independent_capture_observation_not_provided");
        evidence.Limitations.Should().Contain(item =>
            item.Contains("does not establish cause", StringComparison.Ordinal));

        if (availability == ContentionConcentrationAvailability.Available)
        {
            evidence.Top1WaitDurationShare.Should().NotBeNull();
            evidence.Top3WaitDurationShare.Should().NotBeNull();
            evidence.WaitDurationQuantiles.Availability.Should().Be(ContentionConcentrationAvailability.Available);
        }
        else
        {
            evidence.Top1WaitDurationShare.Should().BeNull();
            evidence.Top3WaitDurationShare.Should().BeNull();
        }
    }

    [Fact]
    public void DominantAndDiffuseShares_UseWaitDurationRatherThanEventCounts()
    {
        var dominant = ContentionConcentrationEvaluator.Evaluate(Snapshot("dominant"));
        var diffuse = ContentionConcentrationEvaluator.Evaluate(Snapshot("diffuse"));

        dominant.Top1WaitDurationShare.Should().BeApproximately(0.8, 1e-12);
        dominant.Top3WaitDurationShare.Should().BeApproximately(1, 1e-12);
        diffuse.Top1WaitDurationShare.Should().BeApproximately(0.2, 1e-12);
        diffuse.Top3WaitDurationShare.Should().BeApproximately(0.6, 1e-12);
        dominant.Counts.ObservedEvents.Should().Be(dominant.Counts.RetainedEvents);
        dominant.Counts.DetectedSourceLossEvents.Should().BeNull();
        dominant.Counts.Should().Be(new ContentionConcentrationCounts(
            Captures: 1,
            ObservedEvents: 10,
            RetainedEvents: 10,
            RetentionDroppedEvents: 0,
            DistinctLocks: 3,
            RetainedLocks: 3,
            TotalWindows: 10,
            ActiveWindows: 10,
            IndependentCaptureUnits: 1,
            DetectedSourceLossEvents: null));
        dominant.WaitDurationQuantiles.Should().BeEquivalentTo(
            new ContentionWaitDurationQuantiles(
                ContentionConcentrationAvailability.Available,
                "milliseconds",
                ContentionQuantileRepresentation.Exact,
                20,
                80,
                100,
                []));
        dominant.UnavailableFields.Should().Contain(item =>
            item.Field == "counts.detectedSourceLossEvents"
            && item.Reason == "source_loss_not_observable");
    }

    [Fact]
    public void KnownSourceLoss_MakesConcentrationUnavailableWithoutReplacingItWithZero()
    {
        var evidence = ContentionConcentrationEvaluator.Evaluate(
            Snapshot("dominant"),
            new ContentionConcentrationEvaluationContext(DetectedSourceLossEvents: 7));

        evidence.Availability.Should().Be(ContentionConcentrationAvailability.LossDetected);
        evidence.Shape.Should().Be(ContentionConcentrationShape.Unavailable);
        evidence.Top1WaitDurationShare.Should().BeNull();
        evidence.Top3WaitDurationShare.Should().BeNull();
        evidence.Counts.DetectedSourceLossEvents.Should().Be(7);
        evidence.WaitDurationQuantiles.Availability.Should().Be(
            ContentionConcentrationAvailability.LossDetected);
    }

    [Fact]
    public void InvalidAndNonfiniteInputs_AreIncompatible()
    {
        var invalidCounts = Snapshot("dominant") with { TotalEvents = 1 };
        var invalid = ContentionConcentrationEvaluator.Evaluate(invalidCounts);
        var nonfinite = ContentionConcentrationEvaluator.Evaluate(
            Snapshot("dominant"),
            new ContentionConcentrationEvaluationContext(WindowSeconds: double.NaN));

        invalid.Availability.Should().Be(ContentionConcentrationAvailability.Incompatible);
        invalid.Top1WaitDurationShare.Should().BeNull();
        nonfinite.Availability.Should().Be(ContentionConcentrationAvailability.Incompatible);
        nonfinite.UnavailableFields.Should().Contain(item =>
            item.Reason == "nonfinite_or_nonpositive_window");
    }

    [Fact]
    public void MissingTimingEvidence_KeepsSharesButDoesNotInventAShape()
    {
        var snapshot = Snapshot("dominant");
        snapshot = snapshot with
        {
            Events = snapshot.Events
                .Select((item, index) => index == 0
                    ? item with
                    {
                        StartedAt = StartedAt.AddSeconds(11),
                        StoppedAt = StartedAt.AddSeconds(11).Add(item.Duration),
                    }
                    : item)
                .ToArray(),
        };

        var evidence = ContentionConcentrationEvaluator.Evaluate(snapshot);

        evidence.Availability.Should().Be(ContentionConcentrationAvailability.Available);
        evidence.Top1WaitDurationShare.Should().NotBeNull();
        evidence.ActiveWindowFraction.Should().BeNull();
        evidence.Shape.Should().Be(ContentionConcentrationShape.Unavailable);
        evidence.UnavailableFields.Should().Contain(item =>
            item.Field == "activeWindowFraction"
            && item.Reason == "event_timestamps_outside_capture_window");
    }

    [Fact]
    public void BoundedTimestampSkew_IsClampedToTheNearestCaptureWindow()
    {
        var snapshot = Snapshot("dominant");
        snapshot = snapshot with
        {
            Events = snapshot.Events
                .Select((item, index) => index == 9
                    ? item with
                    {
                        StartedAt = StartedAt.AddSeconds(9.95),
                        StoppedAt = StartedAt.AddSeconds(10.05),
                    }
                    : item)
                .ToArray(),
        };

        var evidence = ContentionConcentrationEvaluator.Evaluate(snapshot);

        evidence.ActiveWindowFraction.Should().Be(1);
        evidence.Shape.Should().Be(ContentionConcentrationShape.Dominant);
        evidence.UnavailableFields.Should().NotContain(item =>
            item.Field == "activeWindowFraction");
    }

    [Fact]
    public void UnknownLockIdentity_DoesNotBecomeASyntheticDominantLock()
    {
        var snapshot = Snapshot("dominant");
        snapshot = snapshot with
        {
            DistinctMonitors = 0,
            Events = snapshot.Events
                .Select(item => item with
                {
                    LockId = 0,
                    AssociatedObjectId = 0,
                })
                .ToArray(),
        };

        var evidence = ContentionConcentrationEvaluator.Evaluate(snapshot);

        evidence.Availability.Should().Be(
            ContentionConcentrationAvailability.UnsupportedApproximation);
        evidence.Counts.RetainedLocks.Should().Be(0);
        evidence.Top1WaitDurationShare.Should().BeNull();
        evidence.Top3WaitDurationShare.Should().BeNull();
        evidence.Shape.Should().Be(ContentionConcentrationShape.Unavailable);
        evidence.UnavailableFields.Should().Contain(item =>
            item.Reason == "lock_identity_unavailable");
    }

    [Fact]
    public void OwnerOverlap_RequiresCaptureUnitAndKeepsWilsonCounts()
    {
        var available = ContentionConcentrationEvaluator.Evaluate(
            Snapshot("dominant"),
            new ContentionConcentrationEvaluationContext(
                OwnerOverlap: new ContentionOwnerOverlapObservation("capture", 3, 4)));
        var owner = available.OwnerOverlap;

        owner.Availability.Should().Be(ContentionConcentrationAvailability.Available);
        owner.PositiveCaptures.Should().Be(3);
        owner.CaptureCount.Should().Be(4);
        owner.Rate.Should().Be(0.75);
        owner.Interval!.Lower.Should().BeApproximately(0.300641842, 1e-8);
        owner.Interval.Upper.Should().BeApproximately(0.954412739, 1e-8);

        var correlatedEvents = ContentionConcentrationEvaluator.Evaluate(
            Snapshot("dominant"),
            new ContentionConcentrationEvaluationContext(
                OwnerOverlap: new ContentionOwnerOverlapObservation("event", 300, 400)));
        correlatedEvents.OwnerOverlap.Availability.Should().Be(
            ContentionConcentrationAvailability.Incompatible);
        correlatedEvents.OwnerOverlap.Rate.Should().BeNull();

        var nonfinite = ContentionConcentrationEvaluator.Evaluate(
            Snapshot("dominant"),
            new ContentionConcentrationEvaluationContext(
                OwnerOverlap: new ContentionOwnerOverlapObservation(
                    "capture",
                    1,
                    1,
                    double.PositiveInfinity)));
        nonfinite.OwnerOverlap.Availability.Should().Be(
            ContentionConcentrationAvailability.Incompatible);
    }

    [Fact]
    public async Task CoreUseCase_AttachesEvidenceBeforeRegisteringTheHandle()
    {
        var snapshot = Snapshot("dominant");
        var handles = new MemoryDiagnosticHandleStore();

        var result = await EventCollectionUseCases.CollectContentionWithConcentration(
            new FixedCollector(snapshot),
            new FixedResolver(),
            handles,
            snapshot.ProcessId,
            durationSeconds: 10,
            depth: SamplingDepth.Summary);

        result.Data!.Concentration.Should().NotBeNull();
        result.Data.Events.Should().BeEmpty();
        var stored = handles.TryGet<ContentionSnapshot>(result.Handle!);
        stored!.Concentration.Should().NotBeNull();
        stored.Events.Should().HaveCount(snapshot.Events.Count);
    }

    [Fact]
    public void LegacyArtifactWithoutEvidenceBlock_RemainsNullAndIsNotBackfilled()
    {
        var current = Snapshot("dominant") with
        {
            Concentration = ContentionConcentrationEvaluator.Evaluate(Snapshot("dominant")),
        };
        var node = JsonSerializer.SerializeToNode(
            current,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        node.Remove("concentration");

        var legacy = node.Deserialize<ContentionSnapshot>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        legacy.Should().NotBeNull();
        legacy!.Concentration.Should().BeNull();
        legacy.TotalEvents.Should().Be(current.TotalEvents);
        legacy.Events.Should().HaveCount(current.Events.Count);
    }

    private static ContentionSnapshot Snapshot(string scenario)
    {
        var events = scenario switch
        {
            "dominant" => DominantEvents(),
            "owner-unavailable" => DominantEvents(),
            "diffuse" => DiffuseEvents(),
            "burst" => DominantEvents().Select((item, index) => item with
            {
                StartedAt = StartedAt.AddMilliseconds(index * 50),
                StoppedAt = StartedAt.AddMilliseconds(index * 50).Add(item.Duration),
            }).ToArray(),
            "truncated" => DominantEvents(),
            "healthy" => [],
            "insufficient" =>
            [
                Event(0.5, 0, 1),
                Event(5.5, 0, 2),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var totalEvents = scenario == "truncated" ? events.Length + 5 : events.Length;
        var totalWait = TimeSpan.FromMilliseconds(events.Sum(item => item.Duration.TotalMilliseconds));
        return new ContentionSnapshot(
            ProcessId: 42,
            StartedAt,
            Duration,
            TotalEvents: totalEvents,
            DistinctMonitors: events.Select(item => item.LockId).Distinct().Count(),
            TotalContentionDuration: totalWait,
            P50ContentionDuration: events.Length == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(20),
            P95ContentionDuration: events.Length == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(80),
            MaxContentionDuration: events.Length == 0 ? TimeSpan.Zero : events.Max(item => item.Duration),
            Events: events,
            Notes: []);
    }

    private static ContentionEventSample[] DominantEvents()
        =>
        [
            Event(0.5, 100, 1),
            Event(1.5, 100, 1),
            Event(2.5, 100, 1),
            Event(3.5, 100, 1),
            Event(4.5, 100, 1),
            Event(5.5, 100, 1),
            Event(6.5, 100, 1),
            Event(7.5, 100, 1),
            Event(8.5, 100, 2),
            Event(9.5, 100, 3),
        ];

    private static ContentionEventSample[] DiffuseEvents()
        => Enumerable.Range(0, 10)
            .Select(index => Event(index + 0.5, 20, (ulong)(index % 5 + 1)))
            .ToArray();

    private static ContentionEventSample Event(double stoppedAtSeconds, double durationMilliseconds, ulong lockId)
    {
        var stoppedAt = StartedAt.AddSeconds(stoppedAtSeconds);
        var duration = TimeSpan.FromMilliseconds(durationMilliseconds);
        return new(
            stoppedAt - duration,
            stoppedAt,
            duration,
            ContendingThreadId: (int)lockId,
            OwnerManagedThreadId: null,
            LockId: lockId,
            AssociatedObjectId: lockId + 100,
            CallSiteMethod: "Sample.Lock",
            CallSiteModule: "Sample");
    }

    private sealed class FixedCollector(ContentionSnapshot snapshot) : IContentionCollector
    {
        public Task<ContentionSnapshot> CollectAsync(
            int processId,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);
    }

    private sealed class FixedResolver : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(
            int? requestedProcessId,
            CancellationToken cancellationToken)
            => Task.FromResult(new ProcessContextResolution(
                new ProcessContext(
                    requestedProcessId ?? 42,
                    RuntimeFlavor.CoreClr,
                    CanSampleCpu: true,
                    CanCollectGcDump: true,
                    AutoResolved: false,
                    RuntimeVersion: "10.0.0",
                    BindingSource: "explicit"),
                null));
    }
}
