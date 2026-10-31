using System.Globalization;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core.Comparison;
using FluentAssertions;
using Microsoft.Extensions.Hosting;

namespace DotnetDiagnostics.Cli.Tests;

[Collection(nameof(CliStreamingProtocolTests))]
public sealed class CliRequestLatencyComparisonTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        nameof(CliRequestLatencyComparisonTests),
        Guid.NewGuid().ToString("N"));

    public CliRequestLatencyComparisonTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Compare_RequestLatency_RendersHumanEvidenceWithoutVerdict()
    {
        var baseline = Write("baseline.json", WindowSet("baseline", "build-a", 10));
        var candidate = Write("candidate.json", WindowSet("candidate", "build-b", 30));

        var (exit, stdout, stderr) = await RunCliAsync("compare", baseline, candidate);

        exit.Should().Be(0);
        stderr.Should().Contain("SAFETY warning [moderate] compare:");
        stdout.Should().Contain("availability=available; no performance verdict");
        stdout.Should().Contain("completed source=64, retained=64");
        stdout.Should().Contain("valid-independent-complete=8");
        stdout.Should().Contain("order-mismatch=0");
        stdout.Should().Contain("below-minimum=0");
        stdout.Should().Contain("p50");
        stdout.Should().Contain("p95");
        stdout.Should().Contain("p99");
        stdout.Should().Contain("unavailable values are not zero");
        stdout.Should().NotContain("regression");
        stdout.Should().NotContain("improvement");
    }

    [Fact]
    public async Task Compare_RequestLatency_JsonAndSaveUseCoreResult()
    {
        var baseline = Write("baseline.json", WindowSet("baseline", "build-a", 10));
        var candidate = Write("candidate.json", WindowSet("candidate", "build-b", 30));
        var saved = Path.Combine(_root, "comparison.json");

        var (exit, stdout, _) = await RunCliAsync(
            "compare",
            baseline,
            candidate,
            "--json",
            "--save",
            saved);

        exit.Should().Be(0);
        using var output = JsonDocument.Parse(stdout);
        output.RootElement.GetProperty("schema").GetString()
            .Should().Be(RequestLatencyComparisonResult.SchemaV1);
        output.RootElement.GetProperty("availability").GetString().Should().Be("available");
        output.RootElement.GetProperty("quantiles").GetArrayLength().Should().Be(3);
        output.RootElement.TryGetProperty("verdict", out _).Should().BeFalse();
        File.Exists(saved).Should().BeTrue();
        using var savedDocument = JsonDocument.Parse(File.ReadAllText(saved));
        savedDocument.RootElement.GetProperty("provenance")
            .GetProperty("seedSha256").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Stream_RequestLatencyComparison_AdvertisesAndReturnsCoreResult()
    {
        var baseline = WindowSet("baseline", "build-a", 10);
        var candidate = WindowSet("candidate", "build-b", 30);

        var frames = await RunStreamAsync(
            new { type = "hello", protocolVersion = 2 },
            new
            {
                type = "compare",
                requestId = "latency-1",
                kind = "request-latency",
                baseline,
                candidate,
            });

        var capability = frames[0].GetProperty("requestLatencyComparison");
        capability.GetProperty("version").GetInt32().Should().Be(1);
        capability.GetProperty("messageType").GetString().Should().Be("compare");
        capability.GetProperty("inputSchema").GetString()
            .Should().Be(RequestLatencyWindowSet.SchemaV1);
        var comparison = frames[1];
        comparison.GetProperty("type").GetString().Should().Be("comparison");
        comparison.GetProperty("requestId").GetString().Should().Be("latency-1");
        comparison.GetProperty("kind").GetString().Should().Be("request-latency");
        comparison.GetProperty("result").GetProperty("availability").GetString()
            .Should().Be("available");
        comparison.GetProperty("result").TryGetProperty("verdict", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Stream_RequestLatencyComparison_ReturnsExplicitCompatibilityFailure()
    {
        var baseline = WindowSet("baseline", "build-a", 10);
        var candidate = WindowSet("candidate", "build-b", 30);
        candidate = candidate with
        {
            Identity = candidate.Identity with
            {
                Quantity = candidate.Identity.Quantity with { Filters = "endpoint=/different" },
            },
        };

        var frames = await RunStreamAsync(
            new { type = "hello", protocolVersion = 2 },
            new
            {
                type = "compare",
                requestId = "latency-incompatible",
                kind = "request-latency",
                baseline,
                candidate,
            });

        var result = frames[1].GetProperty("result");
        result.GetProperty("availability").GetString().Should().Be("incompatible");
        result.GetProperty("unavailableReason").GetString()
            .Should().Be("compatibility_identity_mismatch");
        result.GetProperty("identityMismatches")[0].GetProperty("field").GetString()
            .Should().Be("quantity.filters");
        result.GetProperty("quantiles")[0].GetProperty("absoluteDeltaMilliseconds")
            .ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Stream_RequestLatencyComparison_RejectsOversizedWindowSet()
    {
        var baseline = WindowSet("baseline", "build-a", 10);
        baseline = baseline with
        {
            Identity = baseline.Identity with
            {
                Quantity = baseline.Identity.Quantity with
                {
                    Filters = new string(
                        'x',
                        RequestLatencyComparison.MaximumSerializedWindowSetBytes),
                },
            },
        };

        var frames = await RunStreamAsync(
            new { type = "hello", protocolVersion = 2 },
            new
            {
                type = "compare",
                requestId = "latency-oversized",
                kind = "request-latency",
                baseline,
                candidate = WindowSet("candidate", "build-b", 30),
            });

        frames[1].GetProperty("type").GetString().Should().Be("error");
        frames[1].GetProperty("requestId").GetString().Should().Be("latency-oversized");
        frames[1].GetProperty("code").GetString().Should().Be("invalid_comparison");
        frames[1].GetProperty("message").GetString().Should().Contain("262144-byte limit");
    }

    [Fact]
    public async Task Compare_NonObjectRoots_ReturnStructuredInvalidSnapshot()
    {
        var baseline = Path.Combine(_root, "baseline-array.json");
        var candidate = Path.Combine(_root, "candidate-array.json");
        await File.WriteAllTextAsync(baseline, "[]");
        await File.WriteAllTextAsync(candidate, "[]");

        var (exit, stdout, _) = await RunCliAsync(
            "compare",
            baseline,
            candidate,
            "--json");

        exit.Should().Be(1);
        using var output = JsonDocument.Parse(stdout);
        output.RootElement.GetProperty("error").GetProperty("kind").GetString()
            .Should().Be("InvalidSnapshot");
    }

    [Fact]
    public async Task Compare_SaveFailure_ReturnsOutputWriteFailure()
    {
        var baseline = Write("baseline.json", WindowSet("baseline", "build-a", 10));
        var candidate = Write("candidate.json", WindowSet("candidate", "build-b", 30));

        var (exit, stdout, _) = await RunCliAsync(
            "compare",
            baseline,
            candidate,
            "--json",
            "--save",
            _root);

        exit.Should().Be(1);
        using var output = JsonDocument.Parse(stdout);
        output.RootElement.GetProperty("error").GetProperty("kind").GetString()
            .Should().Be("OutputWriteFailure");
        stdout.Should().NotContain("InvalidRequestLatencyEvidence");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Write(string fileName, RequestLatencyWindowSet value)
    {
        var path = Path.Combine(_root, fileName);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(
            stream,
            value,
            RequestLatencyComparisonJsonContext.Default.RequestLatencyWindowSet);
        return path;
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunCliAsync(
        params string[] args)
    {
        var stdout = new StringWriter(new StringBuilder(), CultureInfo.InvariantCulture);
        var stderr = new StringWriter(new StringBuilder(), CultureInfo.InvariantCulture);
        var exit = await CliHost.RunAsync(args, stdout, stderr, CancellationToken.None);
        return (exit, stdout.ToString().Trim(), stderr.ToString().Trim());
    }

    private static async Task<IReadOnlyList<JsonElement>> RunStreamAsync(
        params object[] requests)
    {
        using var input = new StringReader(
            string.Join(
                '\n',
                requests.Select(request => JsonSerializer.Serialize(
                    request,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)))) + "\n");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await CliStreamingProtocol.RunAsync(
            ["stream", "--protocol", "jsonl"],
            input,
            output,
            errors,
            static () => new HostBuilder().Build(),
            CancellationToken.None);
        exit.Should().Be(0, errors.ToString());
        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToArray();
    }

    private static RequestLatencyWindowSet WindowSet(
        string id,
        string axisValue,
        double offset)
        => new(
            RequestLatencyWindowSet.SchemaV1,
            id,
            new(
                new("CoreCLR", "major-compatible", "windows", "x64", $"{id}-run"),
                new("eventpipe", "1"),
                new(
                    "request-latency",
                    "ms",
                    "completed-request-duration",
                    "per-completed-request",
                    "endpoint=/checkout;method=GET",
                    RequestLatencyRepresentation.Exact),
                new(
                    "8-requests",
                    "non-overlapping-sequential-windows",
                    "Microsoft.AspNetCore.Hosting",
                    "activity-stop",
                    "retain-all",
                    "none"),
                new(
                    "checkout",
                    "1",
                    "endpoint=/checkout;method=GET",
                    "1",
                    "single-process"),
                new(
                    "bounded_time_window",
                    "request-event-within-window-pair",
                    "pairKey"),
                new("build", axisValue)),
            Enumerable.Range(0, RequestLatencyComparison.MinimumCompleteWindowPairs)
                .Select(window => new RequestLatencyWindow(
                    $"window-{window + 1}",
                    $"pair-{window + 1}",
                    window % 2 == 0
                        ? RequestLatencyPairOrder.BaselineFirst
                        : RequestLatencyPairOrder.CandidateFirst,
                    RequestLatencyWindowOutcome.Complete,
                    RequestLatencyComparison.MinimumCompletedRequestsPerWindow,
                    ErrorCount: 0,
                    CensoredCount: 0,
                    MissingCompletionCount: 0,
                    LostOrDropped: 0,
                    RequestLatencyRepresentation.Exact,
                    Enumerable.Range(
                            0,
                            RequestLatencyComparison.MinimumCompletedRequestsPerWindow)
                        .Select(sample => offset + window * 0.1 + sample * 0.01)
                        .ToArray()))
                .ToArray());
}
