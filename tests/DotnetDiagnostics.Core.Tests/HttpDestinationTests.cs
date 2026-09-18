using System.Text.Json;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.DistributedTrace;
using DotnetDiagnostics.Core.Security;

namespace DotnetDiagnostics.Core.Tests;

public sealed class HttpDestinationTests
{
    private const string Trace = "11111111111111111111111111111111";
    private const string OtherTrace = "22222222222222222222222222222222";
    private const string Span = "1111111111111111";
    private static readonly SensitiveDataRedactor Redactor = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactTupleJoinsBothOrdersWithoutChangingNativeEvidence(bool reordered)
    {
        var state = new HttpDestinationCorrelationState(null);
        var original = Activity();
        if (reordered) state.ObserveStop(original);
        state.ObserveStart(Start());
        if (!reordered) state.ObserveStop(original);
        var result = state.Project(original, true, Redactor);
        Assert.Equal(new HttpActivityDestination("available", "http", "backend.test", 8080,
            "diagnostic-source-http-start"), result.Destination);
        Assert.Equal(original, result with { Destination = null });
        Assert.Same(original.Tags, result.Tags);
        Assert.Equal("conflicting-native-host.test", result.Tags["server.address"]);
        Assert.Equal(0, state.Snapshot(true, 1).UnmatchedStarts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DuplicateOrReusedIdentityInvalidatesEvenPreviouslyCompletedPair(bool conflict, bool duplicateStop)
    {
        var state = new HttpDestinationCorrelationState(null);
        var activity = Activity();
        state.ObserveStart(Start());
        state.ObserveStop(activity);
        if (duplicateStop) state.ObserveStop(activity);
        else state.ObserveStart(Start(host: conflict ? "other.test" : "backend.test"));
        Assert.Equal("ambiguous-identity", state.Project(activity, true, Redactor).Destination?.Availability);
        Assert.Equal(duplicateStop ? 1 : 0, state.Snapshot(true, 0).DuplicateStops);
        Assert.Equal(duplicateStop ? 0 : 1, state.Snapshot(true, 0).DuplicateStarts);
        Assert.Equal(conflict ? 1 : 0, state.Snapshot(true, 0).ConflictingStarts);
    }

    [Fact]
    public void MissingStartsOtherTracesAndInvalidIdsNeverGuess()
    {
        var state = new HttpDestinationCorrelationState(null);
        state.ObserveStart(Start(trace: OtherTrace));
        state.ObserveStop(Activity());
        Assert.Equal("missing-start", state.Project(Activity(), true, Redactor).Destination?.Availability);
        state.ObserveStart(Start(span: new string('0', 16)));
        Assert.Equal(1, state.Snapshot(true, 0).InvalidIdentities);
        Assert.Equal(1, state.Snapshot(true, 0).UnmatchedStarts);
        Assert.Equal(1, state.Snapshot(true, 0).UnmatchedStops);
    }

    [Fact]
    public void IdentitySaturationFailsClosedWithoutForgettingTombstones()
    {
        var state = new HttpDestinationCorrelationState(null, identityCap: 1);
        state.ObserveStart(Start());
        state.ObserveStop(Activity());
        state.ObserveStart(Start(trace: OtherTrace));
        state.ObserveStart(Start());
        Assert.Equal("identity-cap", state.Project(Activity(), true, Redactor).Destination?.Availability);
        var facts = state.Snapshot(true, 0);
        Assert.Equal(1, facts.IdentityCapEvents);
        Assert.Equal(1, facts.DuplicateStarts);
        Assert.Equal("identity-cap", facts.Status);
    }

    [Fact]
    public void AuthorityBudgetRetainsIdentityHistoryAndOtherTracesCannotExhaustTargetBudget()
    {
        var state = new HttpDestinationCorrelationState(Trace, identityCap: 2, authorityCap: 1);
        for (var i = 0; i < 20; i++)
        {
            state.ObserveStart(Start(trace: OtherTrace));
            state.ObserveStop(Activity(trace: OtherTrace));
        }
        state.ObserveStart(Start());
        state.ObserveStop(Activity());
        const string second = "2222222222222222";
        state.ObserveStart(Start(span: second));
        state.ObserveStop(Activity(span: second));
        Assert.Equal("available", state.Project(Activity(), true, Redactor).Destination?.Availability);
        Assert.Equal("authority-cap", state.Project(Activity(span: second), true, Redactor).Destination?.Availability);
        state.ObserveStart(Start(span: second));
        Assert.Equal("ambiguous-identity", state.Project(Activity(span: second), true, Redactor).Destination?.Availability);
        var facts = state.Snapshot(true, 1);
        Assert.Equal(40, facts.NonMatchingEvents);
        Assert.Equal(0, facts.IdentityCapEvents);
        Assert.Equal(1, facts.AuthorityCapEvents);
        Assert.Equal(1, facts.DuplicateStarts);
    }

    [Fact]
    public void TransportLossOrIncompleteDrainWithholdsEveryAuthority()
    {
        var state = new HttpDestinationCorrelationState(null);
        state.ObserveStart(Start());
        state.ObserveStop(Activity());
        var result = state.Project(Activity(), false, Redactor).Destination!;
        Assert.Equal("transport-incomplete", result.Availability);
        Assert.Null(result.Host);
        Assert.Equal("transport-incomplete", state.Snapshot(false, 0).Status);
    }

    [Theory]
    [InlineData("http://user:pass@backend.test/path?query#fragment", "80")]
    [InlineData("backend.test/path", "80")]
    [InlineData("backend.test", "0")]
    [InlineData("backend.test", "65536")]
    [InlineData("backend.test", "not-a-port")]
    public void InvalidAuthorityCannotEscapeStructuredFields(string host, string port)
    {
        var state = new HttpDestinationCorrelationState(null);
        var start = Start(host: host);
        start["Port"] = port;
        state.ObserveStart(start);
        state.ObserveStop(Activity());
        Assert.Equal("invalid-authority", state.Project(Activity(), true, Redactor).Destination?.Availability);
        Assert.Equal(1, state.Snapshot(true, 0).InvalidAuthorities);
    }

    [Theory]
    [InlineData("backend\\.test")]
    [InlineData("http://backend\\.test:8080")]
    [InlineData("8080")]
    public void CollectorAndTraceAndDistributedProjectionsRedactAuthorityNotNativeTags(string pattern)
    {
        var redactor = new SensitiveDataRedactor(new SecurityOptions { RedactionPatterns = [pattern] });
        var state = new HttpDestinationCorrelationState(null);
        state.ObserveStart(Start());
        state.ObserveStop(Activity());
        var raw = state.Project(Activity(), true, Redactor);
        var capture = Capture(raw);
        Assert.Equal("redacted", state.Project(Activity(), true, redactor).Destination?.Availability);
        var trace = ActivityTraceProjector.Project(capture, Trace, 10, redactor);
        var distributed = DistributedTraceStitcher.Stitch(Trace, [("pod", capture)], redactor);
        Assert.Equal("redacted", Assert.Single(trace.Spans).Destination?.Availability);
        Assert.Equal("redacted", Assert.Single(distributed.Spans).Destination?.Availability);
        Assert.Equal("redacted", distributed.SlowestHop?.Destination?.Availability);
        Assert.DoesNotContain("backend.test", JsonSerializer.Serialize(trace), StringComparison.Ordinal);
        Assert.Equal("backend.test", raw.Destination?.Host);
        Assert.Same(raw.Tags, Assert.Single(distributed.Spans).Tags);
    }

    [Theory]
    [InlineData("System.Net.Http", true)]
    [InlineData("System.Net.Htt?", true)]
    [InlineData("system.net.*", true)]
    [InlineData("*", true)]
    [InlineData("System.Net.Http.Other", false)]
    [InlineData("Other.*", false)]
    public void ProviderGateHonorsSameSourceWildcards(string filter, bool included)
    {
        var original = EventPipeActivityCollector.BuildProviderArguments([filter])["FilterAndPayloadSpecs"];
        Assert.DoesNotContain("HttpHandlerDiagnosticListener", original, StringComparison.Ordinal);
        var opted = EventPipeActivityCollector.BuildProviderArguments([filter], true)["FilterAndPayloadSpecs"];
        Assert.Equal(included, opted.Contains(EventPipeActivityCollector.HttpDestinationFilter, StringComparison.Ordinal));
        Assert.StartsWith(original, opted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyArtifactsAndCollectorsStayUnknownAndCannotPretendOptInWorked()
    {
        var original = Capture(Activity());
        var roundtrip = JsonSerializer.Deserialize<ActivityCapture>(JsonSerializer.Serialize(original))!;
        Assert.Null(roundtrip.HttpDestinationCorrelation);
        Assert.Null(Assert.Single(roundtrip.Activities).Destination);
        IActivityCollector legacy = new LegacyCollector(original);
        Assert.Same(original, await legacy.CollectAsync(1, TimeSpan.FromSeconds(1), null, 200, null, 200, false));
        await Assert.ThrowsAsync<NotSupportedException>(() => legacy.CollectAsync(1, TimeSpan.FromSeconds(1), null, 200, null, 200, true));
    }

    private static Dictionary<string, string> Start(string trace = Trace, string span = Span, string host = "backend.test") =>
        new() { ["ActivityTraceId"] = trace, ["ActivitySpanId"] = span, ["Host"] = host, ["Port"] = "8080", ["Scheme"] = "http" };

    private static CapturedActivity Activity(string trace = Trace, string span = Span) =>
        new("System.Net.Http", "System.Net.Http.HttpRequestOut", "id", null, trace, span, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1), TimeSpan.FromSeconds(1),
            new Dictionary<string, string> { ["server.address"] = "conflicting-native-host.test" });

    private static ActivityCapture Capture(CapturedActivity activity) =>
        new(1, null, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(2), 1, 1, [activity], [], []);

    private sealed class LegacyCollector(ActivityCapture capture) : IActivityCollector
    {
        public Task<ActivityCapture> CollectAsync(int processId, TimeSpan duration, IReadOnlyList<string>? sources = null,
            int maxActivities = 200, CancellationToken cancellationToken = default) => Task.FromResult(capture);
    }
}
