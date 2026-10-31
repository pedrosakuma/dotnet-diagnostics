using System.Globalization;
using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;
using Microsoft.Extensions.Hosting;

namespace DotnetDiagnostics.Cli.Tests;

[Collection(nameof(CliStreamingProtocolTests))]
public sealed class CliCpuHotspotComparisonTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        nameof(CliCpuHotspotComparisonTests),
        Guid.NewGuid().ToString("N"));

    public CliCpuHotspotComparisonTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Compare_CpuHotspot_RendersCaptureEvidenceWithoutVerdict()
    {
        var baseline = Write("baseline.json", CaptureSet("baseline", [30, 31, 29, 30]));
        var candidate = Write("candidate.json", CaptureSet("candidate", [60, 61, 59, 60]));

        var (exit, stdout, stderr) = await RunCliAsync("compare", baseline, candidate);

        exit.Should().Be(0);
        stderr.Should().Contain("SAFETY warning [moderate] compare:");
        stdout.Should().Contain("availability=available; no performance or causal verdict");
        stdout.Should().Contain("valid-independent-complete=4");
        stdout.Should().Contain("selected-inclusive=30");
        stdout.Should().Contain("unresolved-share=5%");
        stdout.Should().Contain("baseline selected share");
        stdout.Should().Contain("paired selected-share deltas");
        stdout.Should().Contain("samples are correlated within each capture");
        stdout.ToLowerInvariant().Should().NotContain("regression");
        stdout.ToLowerInvariant().Should().NotContain("improvement");
    }

    [Fact]
    public async Task Compare_CpuHotspot_JsonAndSaveUseCoreResult()
    {
        var baseline = Write("baseline.json", CaptureSet("baseline", [30, 31, 29, 30]));
        var candidate = Write("candidate.json", CaptureSet("candidate", [60, 61, 59, 60]));
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
            .Should().Be(CpuHotspotComparisonResult.SchemaV1);
        output.RootElement.GetProperty("availability").GetString().Should().Be("available");
        output.RootElement.GetProperty("baselineCaptures").GetArrayLength().Should().Be(4);
        output.RootElement.GetProperty("pairedShareDeltas").GetArrayLength().Should().Be(4);
        output.RootElement.TryGetProperty("verdict", out _).Should().BeFalse();
        File.Exists(saved).Should().BeTrue();
        using var savedDocument = JsonDocument.Parse(File.ReadAllText(saved));
        savedDocument.RootElement.GetProperty("provenance")
            .GetProperty("orderedInputSha256").GetString().Should().HaveLength(64);
    }

    [Fact]
    public async Task Stream_CpuHotspotComparison_AdvertisesAndReturnsExplicitStates()
    {
        var baseline = CaptureSet("baseline", [30, 31, 29, 30]);
        var candidate = CaptureSet("candidate", [60, 61, 59, 60]);

        var frames = await RunStreamAsync(
            new { type = "hello", protocolVersion = 2 },
            new
            {
                type = "compare",
                requestId = "cpu-hotspot-1",
                kind = "cpu-hotspot",
                baseline,
                candidate,
            });

        var capability = frames[0].GetProperty("cpuHotspotComparison");
        capability.GetProperty("version").GetInt32().Should().Be(1);
        capability.GetProperty("inputSchema").GetString()
            .Should().Be(CpuHotspotCaptureSet.SchemaV1);
        capability.GetProperty("maxCapturesPerSide").GetInt32()
            .Should().Be(CpuHotspotComparison.MaximumCapturesPerSide);
        var comparison = frames[1];
        comparison.GetProperty("type").GetString().Should().Be("comparison");
        comparison.GetProperty("kind").GetString().Should().Be("cpu-hotspot");
        comparison.GetProperty("result").GetProperty("availability").GetString()
            .Should().Be("available");
        comparison.GetProperty("result").TryGetProperty("verdict", out _).Should().BeFalse();

        candidate = candidate with
        {
            Identity = candidate.Identity with
            {
                Workload = candidate.Identity.Workload with { OfferedLoad = 101 },
            },
        };
        frames = await RunStreamAsync(
            new { type = "hello", protocolVersion = 2 },
            new
            {
                type = "compare",
                requestId = "cpu-hotspot-incompatible",
                kind = "cpu-hotspot",
                baseline,
                candidate,
            });

        var incompatible = frames[1].GetProperty("result");
        incompatible.GetProperty("availability").GetString().Should().Be("incompatible");
        incompatible.GetProperty("unavailableReason").GetString()
            .Should().Be("compatibility_identity_mismatch");
        incompatible.GetProperty("pairedShareDeltas").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Compare_CpuHotspot_RejectsModeAndMixedSchemas()
    {
        var baseline = Write("baseline.json", CaptureSet("baseline", [30, 31, 29, 30]));
        var candidate = Write("candidate.json", CaptureSet("candidate", [60, 61, 59, 60]));
        var snapshot = Path.Combine(_root, "snapshot.json");
        await File.WriteAllTextAsync(snapshot, """{"schema":"dotnet-diagnostics/comparable-snapshot/v1"}""");

        var modeResult = await RunCliAsync("compare", baseline, candidate, "--mode", "trend", "--json");
        var mixedResult = await RunCliAsync("compare", baseline, snapshot, "--json");

        modeResult.Exit.Should().Be(1);
        modeResult.Stdout.Should().Contain("fixed paired-capture semantics");
        mixedResult.Exit.Should().Be(1);
        mixedResult.Stdout.Should().Contain("Both CPU-hotspot inputs");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Write(string fileName, CpuHotspotCaptureSet value)
    {
        var path = Path.Combine(_root, fileName);
        using var stream = File.Create(path);
        JsonSerializer.Serialize(
            stream,
            value,
            CpuHotspotComparisonJsonContext.Default.CpuHotspotCaptureSet);
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

    private static CpuHotspotCaptureSet CaptureSet(string axisValue, int[] selectedSamples)
        => new(
            CpuHotspotCaptureSet.SchemaV1,
            axisValue,
            new(
                new("CoreCLR", "same-major-minor", "linux", "x64"),
                new(
                    CpuSampleBackend.EventPipeSampleProfiler.ToString(),
                    "1",
                    CpuSampleEvidenceKind.StackFrequencyWithHeuristicWaits),
                new(
                    "cpu-hotspot-inclusive-share",
                    "sample-share",
                    "inclusive-stack-membership",
                    "retained-total-samples",
                    "none",
                    "selected",
                    CpuHotspotRepresentation.Exact),
                new(5, "1ms", "top-16", "none"),
                new("steady-load", "1", "route=/work", 100, "same-node"),
                new("capture", "samples-within-capture", "pairKey"),
                new("build", axisValue)),
            selectedSamples.Select((samples, index) =>
            {
                var pairOrder = index % 2 == 0
                    ? CpuHotspotPairOrder.BaselineFirst
                    : CpuHotspotPairOrder.CandidateFirst;
                var startedAt = DateTimeOffset.UnixEpoch.AddMinutes((index + 1) * 2);
                if ((string.Equals(axisValue, "candidate", StringComparison.Ordinal)
                        && pairOrder == CpuHotspotPairOrder.BaselineFirst)
                    || (!string.Equals(axisValue, "candidate", StringComparison.Ordinal)
                        && pairOrder == CpuHotspotPairOrder.CandidateFirst))
                {
                    startedAt = startedAt.AddSeconds(6);
                }
                var methods = new[]
                {
                    new CpuHotspotMethodObservation("selected", "Selected", samples),
                    new CpuHotspotMethodObservation("other", "Other", 70 - samples),
                    new CpuHotspotMethodObservation("third", "Third", 10),
                };
                return new CpuHotspotCaptureObservation(
                    $"capture-{index + 1}",
                    $"pair-{index + 1}",
                    pairOrder,
                    $"run-{index + 1}",
                    new(
                        CpuHotspotCaptureProfile.SchemaV1,
                        1000 + index,
                        startedAt,
                        5,
                        100,
                        5,
                        CpuSampleBackend.EventPipeSampleProfiler,
                        CpuSampleEvidenceKind.StackFrequencyWithHeuristicWaits,
                        CpuHotspotRepresentation.Exact,
                        methods.Length,
                        0,
                        methods));
            }).ToArray());
}
