using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Schema;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Contention;
using FluentAssertions;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliContentionConcentrationEvidenceTests
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UnixEpoch;
    private readonly ITestOutputHelper _output;

    public CliContentionConcentrationEvidenceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void HumanAndJsonOutput_PreserveScenarioSpecificEvidence()
    {
        var snapshot = Snapshot();
        var evidence = ContentionConcentrationEvaluator.Evaluate(snapshot);
        snapshot = snapshot with { Concentration = evidence };
        var result = CliCommands.BuildResult(
            DiagnosticResult.Ok(snapshot, "Captured contention."),
            CliCommands.RenderContentionConcentration);

        result.Human.Should().Contain("contention concentration: available, shape=dominant");
        result.Human.Should().Contain("bounded-lock-wait-duration-concentration/v1");
        result.Human.Should().Contain("top-1=80.00%");
        result.Human.Should().Contain("source loss=unavailable");
        result.Human.Should().Contain("owner overlap: not_applicable");
        result.Human.Should().Contain("independent_capture_observation_not_provided");

        var json = JsonSerializer.SerializeToElement(
            result.Envelope,
            result.Envelope.GetType(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var block = json.GetProperty("data").GetProperty("concentration");
        block.GetProperty("availability").GetString().Should().Be("available");
        block.GetProperty("shape").GetString().Should().Be("dominant");
        block.GetProperty("top1WaitDurationShare").GetDouble().Should().BeApproximately(0.8, 1e-12);
        block.GetProperty("ownerOverlap").GetProperty("availability").GetString()
            .Should().Be("not_applicable");
    }

    [Fact]
    public void JsonlCaptureView_CarriesTheSameAdditiveEvidenceBlock()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with
        {
            Concentration = ContentionConcentrationEvaluator.Evaluate(snapshot),
        };
        var result = new CliCommandResult(
            false,
            false,
            DiagnosticResult.Ok(snapshot, "Captured contention."),
            string.Empty);

        var view = CliStreamingCaptureViews.Project(
            result,
            new CliOptions
            {
                Command = "collect",
                Kind = "contention",
                Pid = 42,
                DurationSeconds = 10,
                Top = 10,
            });

        view.ContentionConcentration.Should().BeEquivalentTo(snapshot.Concentration);
        view.Metrics.Should().Contain(metric =>
            metric.Label == "Top-1 wait-duration share"
            && Equals(metric.Value, snapshot.Concentration!.Top1WaitDurationShare));
        var json = JsonSerializer.SerializeToElement(
            view,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.GetProperty("contentionConcentration").GetProperty("method").GetProperty("id")
            .GetString().Should().Be(ContentionConcentrationEvaluator.MethodId);
    }

    [Fact]
    public void LegacyArtifact_HumanAndJsonlKeepEvidenceUnavailableWithoutBackfill()
    {
        var snapshot = Snapshot();
        var result = CliCommands.BuildResult(
            DiagnosticResult.Ok(snapshot, "Captured contention."),
            CliCommands.RenderContentionConcentration);

        result.Human.Should().Contain("artifact predates the additive evidence block");
        snapshot.Concentration.Should().BeNull();

        var view = CliStreamingCaptureViews.Project(
            result,
            new CliOptions
            {
                Command = "collect",
                Kind = "contention",
                Pid = 42,
                DurationSeconds = 10,
                Top = 10,
            });
        view.ContentionConcentration.Should().BeNull();
        view.Notes.Should().Contain(note =>
            note.Contains("values were not backfilled", StringComparison.Ordinal));
    }

    [Fact]
    public void ScenarioSchemaAndRepresentativePayloads_RemainBounded()
    {
        var withoutEvidence = Snapshot();
        var withEvidence = withoutEvidence with
        {
            Concentration = ContentionConcentrationEvaluator.Evaluate(withoutEvidence),
        };
        var truncated = withoutEvidence with
        {
            TotalEvents = withoutEvidence.TotalEvents + 5,
        };
        var noContention = withoutEvidence with
        {
            TotalEvents = 0,
            DistinctMonitors = 0,
            TotalContentionDuration = TimeSpan.Zero,
            P50ContentionDuration = TimeSpan.Zero,
            P95ContentionDuration = TimeSpan.Zero,
            MaxContentionDuration = TimeSpan.Zero,
            Events = [],
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        var schema = options.GetJsonSchemaAsNode(
            typeof(ContentionConcentrationEvidence),
            new JsonSchemaExporterOptions
            {
                TreatNullObliviousAsNonNullable = true,
            });
        var schemaBytes = JsonSerializer.SerializeToUtf8Bytes(schema, options).Length;
        var availableEvidenceBytes = JsonSerializer.SerializeToUtf8Bytes(
            withEvidence.Concentration,
            options).Length;
        var truncatedEvidenceBytes = JsonSerializer.SerializeToUtf8Bytes(
            ContentionConcentrationEvaluator.Evaluate(truncated),
            options).Length;
        var noContentionEvidenceBytes = JsonSerializer.SerializeToUtf8Bytes(
            ContentionConcentrationEvaluator.Evaluate(noContention),
            options).Length;
        var baselineBytes = JsonSerializer.SerializeToUtf8Bytes(withoutEvidence, options).Length;
        var additiveBytes = JsonSerializer.SerializeToUtf8Bytes(withEvidence, options).Length;

        _output.WriteLine($"contention concentration schema: {schemaBytes:N0} bytes");
        _output.WriteLine($"representative available evidence: {availableEvidenceBytes:N0} bytes");
        _output.WriteLine($"representative truncated evidence: {truncatedEvidenceBytes:N0} bytes");
        _output.WriteLine($"representative no-contention evidence: {noContentionEvidenceBytes:N0} bytes");
        _output.WriteLine(
            $"representative contention snapshot: {baselineBytes:N0} -> {additiveBytes:N0} bytes " +
            $"(delta +{additiveBytes - baselineBytes:N0})");

        schemaBytes.Should().BeLessThanOrEqualTo(8_192);
        availableEvidenceBytes.Should().BeLessThanOrEqualTo(4_096);
        truncatedEvidenceBytes.Should().BeLessThanOrEqualTo(4_096);
        noContentionEvidenceBytes.Should().BeLessThanOrEqualTo(4_096);
        additiveBytes.Should().BeGreaterThan(baselineBytes);
        (additiveBytes - baselineBytes).Should().BeLessThanOrEqualTo(4_096);
        additiveBytes.Should().BeLessThanOrEqualTo(16_384);
    }

    private static ContentionSnapshot Snapshot()
    {
        var events = new[]
        {
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
        };
        return new(
            42,
            StartedAt,
            TimeSpan.FromSeconds(10),
            events.Length,
            3,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(100),
            events,
            []);
    }

    private static ContentionEventSample Event(
        double stoppedAtSeconds,
        double durationMilliseconds,
        ulong lockId)
    {
        var stoppedAt = StartedAt.AddSeconds(stoppedAtSeconds);
        var duration = TimeSpan.FromMilliseconds(durationMilliseconds);
        return new(
            stoppedAt - duration,
            stoppedAt,
            duration,
            (int)lockId,
            null,
            lockId,
            lockId + 100,
            "Sample.Lock",
            "Sample");
    }
}
