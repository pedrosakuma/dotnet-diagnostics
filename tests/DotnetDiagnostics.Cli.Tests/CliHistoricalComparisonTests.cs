using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Artifacts;
using DotnetDiagnostics.Core.Captures;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.UseCases;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliHistoricalComparisonTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "historical cli", Guid.NewGuid().ToString("N"));
    private sealed record RootProvider(string Root) : IArtifactRootProvider;

    private async Task<HistoricalCaptureReference> Capture(double value)
    {
        var service = new DurableCaptureUseCases(new(new RootProvider(_root)), new MemoryDiagnosticHandleStore(), new());
        var result = await service.CaptureAsync("historical", "counters", CliCaptureRootProvider.CurrentAccess(), _ =>
            Task.FromResult(DiagnosticResult.Ok(new CounterSnapshot(999999, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(7),
                [new("Provider", "value", "value", value, CounterKind.Mean, "bytes")], [], []), "retained")));
        result.Error.Should().BeNull();
        return new(result.Capture!.CaptureId, result.Capture.Artifacts.Single().ArtifactId);
    }

    [Fact]
    public async Task ExplicitReferencesCompareAfterFreshHostWithoutAHandleOrTarget()
    {
        var a = await Capture(12);
        var b = await Capture(15);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await CliHost.RunAsync(Arguments(a, b).Append("--json").ToArray(), stdout, stderr, CancellationToken.None);
        exit.Should().Be(0, stderr.ToString());
        var json = JsonDocument.Parse(stdout.ToString()).RootElement;
        json.GetProperty("schema").GetString().Should().Be(HistoricalComparisonResult.SchemaV1);
        json.GetProperty("metrics")[0].GetProperty("absoluteDelta").GetDecimal().Should().Be(3);
        json.GetProperty("left").GetProperty("reference").GetProperty("captureId").GetString().Should().Be(a.CaptureId);
    }

    [Fact]
    public async Task FreshCliProcessesReopenAndCompareWithoutAnImporter()
    {
        var a = await Capture(20);
        var b = await Capture(14);
        for (var restart = 0; restart < 2; restart++)
        {
            var result = await CliPortableCaptureProcessTests.InvokeAsync(_root, $"historical-{restart}", false, Arguments(a, b));
            result.Exit.Should().Be(0, result.Stderr);
            result.Json.GetProperty("schema").GetString().Should().Be(HistoricalComparisonResult.SchemaV1);
            result.Json.GetProperty("metrics")[0].GetProperty("absoluteDelta").GetDecimal().Should().Be(-6);
        }
    }

    [Fact]
    public async Task SessionInheritsCaptureRootForExplicitComparison()
    {
        var a = await Capture(2);
        var b = await Capture(5);
        var command = "compare " + string.Join(" ", Arguments(a, b).Skip(3)) + " --json";
        using var input = new StringReader(command + "\nexit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliHost.RunAsync(["session", "--capture-root", _root], input, output, error, CancellationToken.None);
        exit.Should().Be(0, error.ToString());
        output.ToString().Should().Contain(HistoricalComparisonResult.SchemaV1).And.Contain(b.CaptureId);
    }

    [Theory]
    [InlineData("--from", "2026-01-01T00:00:00Z")]
    [InlineData("--mode", "dispersion")]
    [InlineData("--pid", "123")]
    [InlineData("--top", "2")]
    [InlineData("--save", "not-written.json")]
    [InlineData("--rank-by", "bytes")]
    [InlineData("--offset", "1")]
    [InlineData("--max-depth", "1")]
    public async Task UnsupportedProjectionAndLiveOptionsFailBeforeStoreReads(string option, string value)
    {
        var reference = new HistoricalCaptureReference(new string('a', 32), new string('b', 32));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await CliHost.RunAsync(Arguments(reference, reference).Concat([option, value]).ToArray(),
            stdout, stderr, CancellationToken.None);
        exit.Should().NotBe(0);
        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public async Task PartialSelectorsFailExplicitly()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliHost.RunAsync(["compare", "--baseline-capture-id", new string('a', 32)],
            output, error, CancellationToken.None);
        exit.Should().NotBe(0);
        error.ToString().Should().Contain("four explicit");
    }

    private string[] Arguments(HistoricalCaptureReference a, HistoricalCaptureReference b) =>
        ["compare", "--capture-root", _root, "--baseline-capture-id", a.CaptureId, "--baseline-artifact-id", a.ArtifactId,
            "--candidate-capture-id", b.CaptureId, "--candidate-artifact-id", b.ArtifactId];

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
