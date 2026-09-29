using System.Net;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core.Captures;

namespace DotnetDiagnostics.Core.Tests;

public sealed class HistoricalAcceptanceDiagnosticsTests
{
    [Fact]
    public async Task AcceptanceSelectsExistingRouteWithoutChangingGlobalDefaults()
    {
        var diagnostics = new HttpReadinessDiagnostics();
        var options = HistoricalComparisonAcceptanceTests.OwnedSampleOptions(diagnostics);
        Assert.Equal("/", new LiveSampleOptions().ReadinessPath);
        Assert.Null(new LiveSampleOptions().HttpDiagnostics);
        Assert.Equal("/weatherforecast", options.ReadinessPath);
        Assert.Equal(TimeSpan.FromSeconds(30), options.HttpTimeout);
        Assert.True(options.WaitForHttpReady);
        Assert.Same(diagnostics, options.HttpDiagnostics);
        using var handler = new RouteHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1") };
        await DiagnosticReadiness.WaitForHttpReadyAsync(http, options.HttpTimeout, options.ReadinessPath,
            TimeProvider.System, diagnostics: diagnostics);
        Assert.Equal("/weatherforecast", handler.Path);
        Assert.Equal(200, diagnostics.LastStatusCode);
        Assert.Equal("Succeeded", diagnostics.Completion);
    }

    [Fact]
    public async Task OnlyFailuresAreReportedAndOriginalExceptionIsPreserved()
    {
        var lines = new List<string>();
        await HistoricalAcceptanceDiagnostics.RunAsync(() => Task.CompletedTask, lines.Add);
        Assert.Empty(lines);
        var original = new CaptureStoreException(CaptureErrorCode.CapacityExceeded, "private-message");
        original.Data["WorkerGapTicks"] = 100001L;
        original.Data["WorkerGapLimitTicks"] = 100000L;
        original.Data["WorkerProtocolPhase"] = "Receiving";
        var thrown = await Assert.ThrowsAsync<CaptureStoreException>(() =>
            HistoricalAcceptanceDiagnostics.RunAsync(() => Task.FromException(original), lines.Add));
        Assert.Same(original, thrown);
        var line = Assert.Single(lines);
        Assert.DoesNotContain("private-message", line);
        using var json = JsonDocument.Parse(line);
        var fields = json.RootElement.GetProperty("chain")[0].GetProperty("fields");
        Assert.Equal(100001L, fields.GetProperty("WorkerGapTicks").GetInt64());
        Assert.Equal("Receiving", fields.GetProperty("WorkerProtocolPhase").GetString());
    }

    [Fact]
    public async Task ReporterFailureDoesNotReplaceOriginalAcceptanceFailure()
    {
        var original = new InvalidOperationException("original");
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HistoricalAcceptanceDiagnostics.RunAsync(() => Task.FromException(original), _ => throw new IOException("reporter")));
        Assert.Same(original, thrown);
    }

    [Theory]
    [InlineData("Bearer do-not-record")]
    [InlineData("password=do-not-record")]
    [InlineData("secret=do-not-record")]
    [InlineData("/private/do-not-record")]
    [InlineData("https://example.invalid/?sig=do-not-record")]
    public void FiniteStateAllowlistRedactsUnknownStringsInsteadOfPersistingPatterns(string sensitive)
    {
        var error = new InvalidOperationException(sensitive);
        error.Data["WorkerProtocolPhase"] = sensitive;
        error.Data["WorkerPollStage"] = new string('x', 10000) + sensitive;
        error.Data["WorkerGapTicks"] = sensitive;
        error.Data["UnknownField"] = sensitive;
        error.Data["WorkerSenderStatus"] = new UnsafeScalar();
        var text = HistoricalAcceptanceDiagnostics.Format(error);
        Assert.DoesNotContain("do-not-record", text);
        Assert.DoesNotContain("UnknownField", text);
        Assert.DoesNotContain(new string('x', 64), text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(4, json.RootElement.GetProperty("rejectedValues").GetInt32());
        Assert.Empty(json.RootElement.GetProperty("chain")[0].GetProperty("fields").EnumerateObject());
    }

    [Fact]
    public void DepthFieldsBytesAndLinesAreBoundedWithExplicitTruncation()
    {
        Exception? error = null;
        for (var depth = 0; depth < 20; depth++)
        {
            error = new InvalidOperationException("never emitted", error);
            foreach (var field in new[] { "WorkerLastValidSampleTicks", "WorkerCurrentTicks", "WorkerGapTicks",
                "WorkerGapLimitTicks", "WorkerPollStartedTicks", "WorkerLastCompletedPollDurationTicks",
                "WorkerMetricsStartedTicks", "WorkerMetricsFinishedTicks", "WorkerGcPauseDeltaTicks",
                "WorkerGcCountDelta", "WorkerSupervisorThreadCpuDeltaTicks" })
                error.Data[field] = long.MaxValue;
            error.Data["WorkerProtocolPhase"] = "Receiving";
            error.Data["WorkerPollStage"] = "Record";
            error.Data["WorkerSenderStatus"] = "RanToCompletion";
            error.Data["WorkerReceiverStatus"] = "WaitingForActivation";
        }
        var text = HistoricalAcceptanceDiagnostics.Format(error!);
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        Assert.Equal(HistoricalAcceptanceDiagnostics.MaximumDepth, root.GetProperty("chain").GetArrayLength());
        Assert.Equal(HistoricalAcceptanceDiagnostics.MaximumFields,
            root.GetProperty("chain").EnumerateArray().Sum(e => e.GetProperty("fields").EnumerateObject().Count()));
        Assert.True(root.GetProperty("chainTruncated").GetBoolean());
        Assert.True(root.GetProperty("fieldsTruncated").GetBoolean());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.True(Encoding.UTF8.GetByteCount(text) <= HistoricalAcceptanceDiagnostics.MaximumBytes);
        Assert.Single(text.Split('\n'));
    }

    [Fact]
    public void InnerExceptionFieldsAndOnlyScalarReadinessEvidenceAreRetained()
    {
        var inner = new InvalidOperationException("inner payload");
        inner.Data["WorkerGapTicks"] = -1L;
        var diagnostics = new HttpReadinessDiagnostics();
        diagnostics.Response(503);
        diagnostics.DeadlineExpired();
        using var json = JsonDocument.Parse(HistoricalAcceptanceDiagnostics.Format(new InvalidOperationException("outer payload", inner), diagnostics));
        Assert.Equal(-1L, json.RootElement.GetProperty("chain")[1].GetProperty("fields").GetProperty("WorkerGapTicks").GetInt64());
        Assert.Equal(503, json.RootElement.GetProperty("readiness").GetProperty("LastStatusCode").GetInt32());
        Assert.Equal("DeadlineExpired", json.RootElement.GetProperty("readiness").GetProperty("Completion").GetString());
        Assert.Equal("not-retained", json.RootElement.GetProperty("sampleStreams").GetString());
        diagnostics.Reset();
        Assert.Null(diagnostics.LastStatusCode);
        Assert.Equal("NotStarted", diagnostics.LastProbeOutcome);
        Assert.Equal("Pending", diagnostics.Completion);
    }

    private sealed class UnsafeScalar
    {
        public override string ToString() => throw new InvalidOperationException("Must not inspect arbitrary values.");
    }

    private sealed class RouteHandler : HttpMessageHandler
    {
        public string? Path { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(Path == "/weatherforecast" ? HttpStatusCode.OK : HttpStatusCode.NotFound));
        }
    }
}
