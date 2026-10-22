using System.Text.Json;
using System.Globalization;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Requests;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class RequestActivityCaptureTests
{
    internal const string Trace = "1234567890abcdef1234567890abcdef";
    internal const string RequestSpan = "1111111111111111";
    internal static readonly DateTimeOffset Start = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
    private static readonly RequestActivityOptions Options = new(10, ["Sample"]);

    internal static EventPipeInFlightRequestCollector.RequestEvent Request(string span = RequestSpan,
        string trace = Trace, string path = "/sample")
    {
        var arguments = new Dictionary<string, string>
        {
            ["ActivityTraceId"] = trace,
            ["ActivitySpanId"] = span,
            ["Path"] = path,
            ["Method"] = "GET",
            ["ActivityStartTime"] = Start.AddSeconds(1).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        EventPipeInFlightRequestCollector.TryCreateEvent(true, arguments, Start.UtcDateTime, out var e, strictIdentity: true)
            .Should().BeTrue();
        return e;
    }

    internal static CapturedActivity Activity(string span = "2222222222222222", string? parent = RequestSpan, string trace = Trace)
        => new("Sample", "Completed", span, parent, trace, span, parent,
            Start.AddSeconds(2), Start.AddSeconds(3), TimeSpan.FromSeconds(1), new Dictionary<string, string> { ["secret"] = "not-transported" });

    internal static RequestActivityCapture Close(RequestActivityState state, string completion = "normal",
        long? lost = 0, string lifetime = "known", DateTimeOffset? start = null)
        => state.Close("capture-one", 42, Start.AddDays(-1), Start, Options, start ?? Start,
            Start.AddSeconds(10), completion, lost, lifetime);

    [Fact]
    public void DistinctRequestsSharingTraceDoNotBecomeEachOthersChildren()
    {
        var state = new RequestActivityState();
        state.ObserveRequest(Request());
        state.ObserveRequest(Request("aaaaaaaaaaaaaaaa"));
        state.ObserveActivity(Activity());
        state.ObserveActivity(Activity("3333333333333333", "aaaaaaaaaaaaaaaa"));
        state.ObserveActivity(Activity("4444444444444444", "2222222222222222"));
        state.ObserveActivity(Activity("5555555555555555", trace: new string('a', 32)));
        var capture = Close(state);
        var selected = capture.Requests.Single(r => r.SpanId == RequestSpan);
        var query = capture.QueryRelated(capture.CaptureId, selected.RowId);
        query.Rows.Should().HaveCount(3);
        query.Rows.Single(r => r.Activity.SpanId == "2222222222222222").Should().Match<RelatedActivity>(r => r.Relationship == "direct-child" && r.Verified);
        query.Rows.Single(r => r.Activity.SpanId == "4444444444444444").Should().Match<RelatedActivity>(r => r.Relationship == "observed-descendant" && r.Verified);
        query.Rows.Single(r => r.Activity.SpanId == "3333333333333333").Should().Match<RelatedActivity>(r => r.Relationship == "same-trace-only" && !r.Verified);
        JsonSerializer.Serialize(query).Should().NotContain("not-transported").And.NotContain("\"Tags\"");
        var other = capture.QueryRelated(capture.CaptureId, capture.Requests.Single(r => r != selected).RowId);
        other.Rows.Count(r => r.Verified).Should().Be(1);
    }

    [Theory]
    [InlineData("cancelled", 0L, "known")]
    [InlineData("early", 0L, "known")]
    [InlineData("source-failure", null, "known")]
    [InlineData("normal", 1L, "known")]
    [InlineData("normal", 0L, "unknown")]
    [InlineData("normal", 0L, "changed")]
    public void IncompleteQualityPreservesStructuralBasisButWithholdsVerification(string completion, long? loss, string lifetime)
    {
        var state = new RequestActivityState();
        state.ObserveRequest(Request());
        state.ObserveActivity(Activity());
        var capture = Close(state, completion, loss, lifetime);
        var row = capture.QueryRelated(capture.CaptureId, capture.Requests[0].RowId).Rows.Single();
        row.Relationship.Should().Be("direct-child");
        row.Verified.Should().BeFalse();
        row.VerificationWithheldReason.Should().NotBeNullOrEmpty();
        capture.Quality.LifetimeStatus.Should().Be(lifetime);
    }

    [Theory]
    [InlineData("0000000000000000")]
    [InlineData("not-a-span")]
    [InlineData("AAAAAAAAAAAAAAAA")]
    [InlineData(null)]
    [InlineData("9999999999999999")]
    public void MissingMalformedOrUnobservedParentNeverVerifies(string? parent)
    {
        var state = new RequestActivityState();
        state.ObserveRequest(Request());
        state.ObserveActivity(Activity(parent: parent));
        var capture = Close(state);
        var row = capture.QueryRelated(capture.CaptureId, capture.Requests[0].RowId).Rows.Single();
        row.Verified.Should().BeFalse();
        row.Relationship.Should().Be("same-trace-only");
        row.VerificationWithheldReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void DuplicatesIncludingDroppedRowsBlockVerification()
    {
        var state = new RequestActivityState();
        state.ObserveRequest(Request());
        state.ObserveActivity(Activity());
        for (var i = 1; i < RequestActivityCapture.MaxActivities; i++)
            state.ObserveActivity(Activity(i.ToString("x16", CultureInfo.InvariantCulture), "eeeeeeeeeeeeeeee"));
        state.ObserveActivity(Activity());
        var capture = Close(state);
        capture.RetainedActivities.Should().Be(200);
        capture.Quality.DroppedActivities.Should().Be(1);
        capture.Quality.DuplicateIdentities.Should().Be(1);
        var query = capture.QueryRelated(capture.CaptureId, capture.Requests[0].RowId, 1);
        query.TotalRows.Should().Be(200);
        query.OmittedRows.Should().Be(199);
        query.Rows.Single().Should().Match<RelatedActivity>(r => r.Relationship == "direct-child" && !r.Verified);
    }

    [Fact]
    public void MissingIntermediateCycleAndOutOfWindowDoNotFabricateVerifiedChains()
    {
        var state = new RequestActivityState();
        state.ObserveRequest(Request());
        state.ObserveActivity(Activity("2222222222222222", "3333333333333333"));
        state.ObserveActivity(Activity("3333333333333333", "2222222222222222"));
        state.ObserveActivity(Activity("4444444444444444", "5555555555555555"));
        state.ObserveActivity(Activity("6666666666666666") with { StartedAt = Start.AddSeconds(-1) });
        var capture = Close(state);
        capture.QueryRelated(capture.CaptureId, capture.Requests[0].RowId).Rows.Should().OnlyContain(r => !r.Verified);
    }

    [Fact]
    public void IdentityGuardAndBothInsertionCapsStayBoundedAndVisible()
    {
        var state = new RequestActivityState();
        state.ObserveRequest(Request());
        state.ObserveActivity(Activity());
        for (var i = 1; i <= 5000; i++)
        {
            state.ObserveRequest(Request(i.ToString("x16", CultureInfo.InvariantCulture), new string('b', 32)));
            state.ObserveActivity(Activity(i.ToString("x16", CultureInfo.InvariantCulture), trace: new string('b', 32)));
        }
        var capture = Close(state);
        capture.Requests.Should().HaveCount(100);
        capture.RetainedActivities.Should().Be(200);
        capture.Quality.DroppedRequests.Should().Be(4901);
        capture.Quality.DroppedActivities.Should().Be(4801);
        capture.Quality.IdentityCapacityReached.Should().BeTrue();
        capture.Quality.RetainedIdentities.Should().Be(4096);
        capture.Quality.AccountedRetainedBytes.Should().BeLessThan(RequestActivityCapture.MaxAccountedRetainedBytes);
        var related = capture.QueryRelated(capture.CaptureId, capture.Requests[0].RowId);
        related.Rows.Single().Verified.Should().BeFalse();
        related.Rows.Single().VerificationWithheldReason.Should().Contain("MaxIdentities");
    }

    [Fact]
    public void ExactRowCaptureAndQueryBoundsAreRequired()
    {
        var state = new RequestActivityState();
        state.ObserveRequest(Request());
        var capture = Close(state);
        var mismatch = () => capture.QueryRequests("another-capture");
        mismatch.Should().Throw<ArgumentException>();
        var missing = () => capture.QueryRelated(capture.CaptureId, "not-retained");
        missing.Should().Throw<ArgumentException>();
        var tooMany = () => capture.QueryRequests(capture.CaptureId, 101);
        tooMany.Should().Throw<ArgumentOutOfRangeException>();
        state.ObserveRequest(new(Trace + "/" + RequestSpan, false, null));
        Close(state).Requests.Should().BeEmpty();
    }

    [Fact]
    public void PrivacyAndTextBoundsApplyBeforeRetention()
    {
        var state = new RequestActivityState();
        state.ObserveRequest(Request(path: "/sample?access_token=not-retained"));
        state.ObserveActivity(Activity() with { OperationName = "Password=hunter2" });
        state.ObserveActivity(Activity("3333333333333333") with { OperationName = new string('x', 257) });
        var capture = Close(state);
        var json = JsonSerializer.Serialize(capture.QueryRelated(capture.CaptureId, capture.Requests[0].RowId));
        json.Should().NotContain("hunter2").And.NotContain("not-retained");
        capture.Requests[0].Path.Should().Be("/sample");
        capture.Quality.TruncatedFields.Should().Be(1);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("Foo/Stop")]
    [InlineData("Foo\nBar")]
    [InlineData("Foo:Bar")]
    [InlineData("")]
    public void InvalidSourceFiltersAreRejectedRatherThanBroadened(string source)
    {
        var act = () => new RequestActivityOptions(10, [source]).Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MaximumTextCardinalityAndSerializedProjectionStayWithinBudgets()
    {
        var state = new RequestActivityState();
        for (var i = 1; i <= 100; i++)
            state.ObserveRequest(Request(i.ToString("x16", CultureInfo.InvariantCulture), path: new string('\u4e00', 1024)) with
            {
                PendingRequest = new(Trace, i.ToString("x16", CultureInfo.InvariantCulture), new string('\u4e00', 1024), new string('\u4e00', 256), Start),
            });
        for (var i = 101; i < 5000; i++)
            state.ObserveActivity(Activity(i.ToString("x16", CultureInfo.InvariantCulture)) with
            {
                SourceName = new string('\u4e00', 256),
                OperationName = new string('\u4e00', 256),
            });
        var capture = Close(state);
        capture.Quality.AccountedRetainedBytes.Should().BeLessThan(RequestActivityCapture.MaxAccountedRetainedBytes);
        JsonSerializer.SerializeToUtf8Bytes(capture.QueryRequests(capture.CaptureId)).Length.Should().BeLessThan(1024 * 1024);
        JsonSerializer.SerializeToUtf8Bytes(capture.QueryRelated(capture.CaptureId, capture.Requests[0].RowId)).Length.Should().BeLessThan(1024 * 1024);
    }

    [Fact]
    public void JointSubscriptionOmitsTagsAndLegacyIdentifiersWithoutChangingStandaloneDefaults()
    {
        EventPipeInFlightRequestCollector.BuildFilterSpec().Should().Contain("ActivityId=*Activity.Id");
        EventPipeInFlightRequestCollector.BuildFilterSpec(false).Should().NotContain("ActivityId=*Activity.Id")
            .And.Contain("ActivityTraceId=*Activity.TraceId");
        EventPipeActivityCollector.BuildProviderArguments(["Sample"])["FilterAndPayloadSpecs"].Should().Contain("Tags=");
        EventPipeActivityCollector.BuildProviderArguments(["Sample"], includeTags: false)["FilterAndPayloadSpecs"]
            .Should().NotContain("Tags=").And.Contain("[AS]Sample/Stop");
    }

    [Fact]
    public void UnknownHeaderAndParseErrorsWithholdVerificationWithoutClaimingMismatch()
    {
        var state = new RequestActivityState { ParseErrors = 1 };
        state.ObserveRequest(Request());
        state.ObserveActivity(Activity());
        var capture = state.Close("capture", 42, null, Start, Options, null, Start.AddSeconds(10), "normal", 0, "unknown");
        var row = capture.QueryRelated("capture", capture.Requests[0].RowId).Rows.Single();
        row.Relationship.Should().Be("direct-child");
        row.Verified.Should().BeFalse();
        row.VerificationWithheldReason.Should().Contain("unknown");
        capture.ProcessStartedAt.Should().BeNull();
        capture.ObservationStart.Should().BeNull();
    }

    [Theory]
    [InlineData(null, RequestSpan)]
    [InlineData("bad-trace", RequestSpan)]
    [InlineData(Trace, null)]
    [InlineData(Trace, "0000000000000000")]
    public void LegacyFallbackCannotEnterJointRequestIdentity(string? trace, string? span)
    {
        var arguments = new Dictionary<string, string>
        {
            ["ActivityId"] = "legacy-request", ["ActivityTraceId"] = trace ?? "", ["ActivitySpanId"] = span ?? "",
            ["ActivityStartTime"] = Start.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        EventPipeInFlightRequestCollector.TryCreateEvent(true, arguments, Start.UtcDateTime, out _, strictIdentity: true).Should().BeFalse();
        EventPipeInFlightRequestCollector.TryCreateEvent(true, arguments, Start.UtcDateTime, out _, strictIdentity: false).Should().BeTrue();
    }

    [Fact]
    public void OversizedFiltersAndMissingRequestTimestampAreRejected()
    {
        var source = () => new RequestActivityOptions(10, [new string('a', 201)]).Validate();
        source.Should().Throw<ArgumentException>();
        var arguments = new Dictionary<string, string> { ["ActivityTraceId"] = Trace, ["ActivitySpanId"] = RequestSpan };
        EventPipeInFlightRequestCollector.TryCreateEvent(true, arguments, Start.UtcDateTime, out _, strictIdentity: true).Should().BeFalse();
    }
}
