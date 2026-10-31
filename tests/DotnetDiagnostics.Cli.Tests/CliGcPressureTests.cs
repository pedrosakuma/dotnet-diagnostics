using System.Text;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Gc;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliGcPressureTests
{
    [Fact]
    public async Task HumanJsonAndJsonlExposeTheSameBoundedDecision()
    {
        var path = await WriteCaptureAsync(Capture());
        try
        {
            var human = await RunAsync("gc-pressure", "--file", path);
            var json = await RunAsync("gc-pressure", "--file", path, "--json");
            var jsonl = await RunAsync("gc-pressure", "--file", path, "--jsonl");

            human.ExitCode.Should().Be(0);
            human.Stdout.Should().Contain("selected action  : allocation-investigation");
            human.Stdout.Should().Contain("availability=available");
            human.Stdout.Should().Contain("Temporal co-observation only");
            human.Stdout.Contains("leak", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
            human.Stdout.Contains("caused", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
            human.Stdout.Contains("health score", StringComparison.OrdinalIgnoreCase).Should().BeFalse();

            using var jsonDocument = JsonDocument.Parse(json.Stdout);
            jsonDocument.RootElement.GetProperty("data").GetProperty("nextAction").GetString()
                .Should().Be("allocation-investigation");
            jsonDocument.RootElement.GetProperty("data").GetProperty("identity").GetProperty("captureId").GetString()
                .Should().Be("cli-gc-pressure");

            jsonl.Stdout.TrimEnd().Should().NotContain(Environment.NewLine);
            using var jsonlDocument = JsonDocument.Parse(jsonl.Stdout);
            jsonlDocument.RootElement.GetProperty("data").GetProperty("nextAction").GetString()
                .Should().Be("allocation-investigation");
            json.Stdout.Length.Should().BeLessThan(20_000);
            jsonl.Stdout.Length.Should().BeLessThan(json.Stdout.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LegacyArtifactReturnsExplicitIncompatibility()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gc-pressure-legacy-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{"id":"legacy-capture","windows":[]}""");
        try
        {
            var output = await RunAsync("gc-pressure", "--file", path, "--json");

            output.ExitCode.Should().Be(0);
            using var document = JsonDocument.Parse(output.Stdout);
            var data = document.RootElement.GetProperty("data");
            data.GetProperty("nextAction").GetString().Should().Be("justified-inconclusion");
            data.GetProperty("lohSize").GetProperty("median").GetProperty("availability").GetString()
                .Should().Be("incompatible");
            data.GetProperty("lohSize").GetProperty("median").GetProperty("unavailableReason").GetString()
                .Should().Be("legacy_contract_missing");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UnrelatedSchemaV1ArtifactReturnsExplicitIncompatibility()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gc-pressure-other-v1-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            path,
            """{"schemaVersion":1,"id":"other-v1","windows":[]}""");
        try
        {
            var output = await RunAsync("gc-pressure", "--file", path, "--json");

            output.ExitCode.Should().Be(0);
            using var document = JsonDocument.Parse(output.Stdout);
            var data = document.RootElement.GetProperty("data");
            data.GetProperty("nextAction").GetString().Should().Be("justified-inconclusion");
            data.GetProperty("lohSize").GetProperty("median").GetProperty("availability").GetString()
                .Should().Be("incompatible");
            data.GetProperty("lohSize").GetProperty("median").GetProperty("unavailableReason").GetString()
                .Should().Be("legacy_contract_missing");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MissingWindowContractFieldsCannotDefaultToLosslessExactEvidence()
    {
        var capture = Capture();
        var json = JsonSerializer.SerializeToNode(
            capture,
            GcPressureCharacterizer.CreateJsonOptions())!;
        json["windows"]![1]!["lostOrDropped"] = null;
        json["windows"]![1]!.AsObject().Remove("representation");
        var path = Path.Combine(Path.GetTempPath(), $"gc-pressure-missing-window-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        try
        {
            var output = await RunAsync("gc-pressure", "--file", path, "--json");

            output.ExitCode.Should().Be(0);
            using var document = JsonDocument.Parse(output.Stdout);
            var median = document.RootElement
                .GetProperty("data")
                .GetProperty("lohSize")
                .GetProperty("median");
            median.GetProperty("availability").GetString().Should().Be("incompatible");
            median.GetProperty("unavailableReason").GetString().Should().Be("legacy_contract_missing");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OversizedArtifactIsRejectedBeforeParsing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gc-pressure-large-{Guid.NewGuid():N}.json");
        await File.WriteAllBytesAsync(path, new byte[256 * 1024 + 1]);
        try
        {
            var output = await RunAsync("gc-pressure", "--file", path, "--json");

            output.ExitCode.Should().Be(1);
            output.Stdout.Should().Contain("\"kind\": \"InvalidGcPressureArtifact\"");
            output.Stdout.Should().Contain("input is limited to 262144 bytes");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task JsonAndJsonlAreMutuallyExclusive()
    {
        var output = await RunAsync("gc-pressure", "--file", "missing.json", "--json", "--jsonl");

        output.ExitCode.Should().Be(2);
        output.Stderr.Should().Contain("--json and --jsonl are mutually exclusive");
    }

    private static GcPressureCapture Capture()
        => new(
            GcPressureCharacterizer.CurrentCaptureSchemaVersion,
            "cli-gc-pressure",
            GcPressureScenarioKind.SustainedAllocationPressure,
            "10.0",
            "test-os",
            "x64",
            3,
            [
                Window(0, GcPressureWindowPhase.Baseline, 2, 0, 0.1, 0.2),
                Window(1, GcPressureWindowPhase.Active, 32, 3, 8, 20),
                Window(2, GcPressureWindowPhase.Active, 36, 4, 9, 24),
                Window(3, GcPressureWindowPhase.Active, 34, 3.5, 8.5, 22),
                Window(4, GcPressureWindowPhase.Cooldown, 4, 0, 0.5, 1),
                Window(5, GcPressureWindowPhase.Cooldown, 3, 0, 0.2, 0.5),
            ],
            []);

    private static GcPressureWindow Window(
        int index,
        GcPressureWindowPhase phase,
        double lohMiB,
        double gen2,
        double pause,
        double suspension)
        => new(
            index,
            phase,
            lohMiB * 1024 * 1024,
            gen2,
            pause,
            suspension,
            10,
            0,
            GcPressureRepresentation.Exact);

    private static async Task<string> WriteCaptureAsync(GcPressureCapture capture)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gc-pressure-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(capture, GcPressureCharacterizer.CreateJsonOptions()));
        return path;
    }

    private static async Task<CommandOutput> RunAsync(params string[] args)
    {
        var stdout = new StringWriter(new StringBuilder());
        var stderr = new StringWriter(new StringBuilder());
        var exitCode = await CliHost.RunAsync(
            args,
            stdout,
            stderr,
            CancellationToken.None,
            new CliRuntimeOptions(ForceAnsi: false));
        return new(exitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed record CommandOutput(int ExitCode, string Stdout, string Stderr);
}
