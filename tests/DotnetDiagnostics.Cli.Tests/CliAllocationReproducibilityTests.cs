using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DotnetDiagnostics.Cli.Tests;

[Collection(nameof(CliStreamingProtocolTests))]
public sealed class CliAllocationReproducibilityTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task SessionQuery_RendersHumanAndJsonFromTheSameCoreResult()
    {
        var store = new MemoryDiagnosticHandleStore();
        var handles = new[]
        {
            Register(store, "a", Artifact(Start, 900, 9, ("A", 500L, 5L), ("B", 300L, 3L))),
            Register(store, "b", Artifact(Start.AddSeconds(10), 1000, 10, ("A", 600L, 6L), ("B", 300L, 3L))),
            Register(store, "c", Artifact(Start.AddSeconds(20), 1100, 11, ("A", 700L, 7L), ("B", 300L, 3L))),
        };
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddSingleton<IDiagnosticHandleStore>(store))
            .Build();

        var outcome = await CliCommands.QuerySession(
            host.Services,
            new CliOptions
            {
                Command = "query",
                Handle = handles[2].Id,
                ComparisonHandles = [handles[0].Id, handles[1].Id],
                View = "allocation-reproducibility",
                Top = 2,
            });

        outcome.IsError.Should().BeFalse();
        outcome.Human.Should().Contain("independence unit: capture");
        outcome.Human.Should().Contain("estimated bytes: median=1,000.00, raw MAD=100.00");
        outcome.Human.Should().Contain("allocated types: pairs=3");
        outcome.Human.Should().Contain("does not prove that a type or call site dominates the workload");
        outcome.Human.Should().Contain("does not verify equivalent request mix, workload intensity or execution conditions");
        outcome.Human.Should().Contain("no universal scenario threshold or causal, severity, regression, health, performance or optimization-priority verdict");
        outcome.Human.Should().Contain("do not mean zero allocation activity");
        var envelope = outcome.Envelope.Should().BeOfType<DiagnosticResult<AllocationReproducibilityResult>>().Subject;
        envelope.Data!.TopK.Should().Be(2);
        envelope.Data.Captures.Should().HaveCount(3);
        JsonSerializer.SerializeToElement(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .GetProperty("data")
            .GetProperty("availability")
            .GetString()
            .Should().Be("available");
    }

    [Fact]
    public async Task JsonlHelloAndQueryExposeAdditiveBoundedContract()
    {
        var store = new MemoryDiagnosticHandleStore();
        var first = Register(store, "first", Artifact(Start, 100, 2, ("A", 75L, 1L), ("B", 25L, 1L)));
        var second = Register(store, "second", Artifact(Start.AddSeconds(10), 120, 3, ("A", 80L, 2L), ("B", 40L, 1L)));

        var frames = await RunAsync(
            store,
            new { type = "hello", protocolVersion = 2 },
            new
            {
                type = "query",
                requestId = "repro",
                handle = second.Id,
                comparisonHandles = new[] { first.Id },
                view = "allocation-reproducibility",
                topN = 2,
            });

        var capability = frames[0].GetProperty("allocationReproducibility");
        capability.GetProperty("version").GetInt32().Should().Be(1);
        capability.GetProperty("minimumCaptures").GetInt32().Should()
            .Be(AllocationReproducibilityBounds.MinimumCaptures);
        capability.GetProperty("maximumCaptures").GetInt32().Should()
            .Be(AllocationReproducibilityBounds.MaximumCaptures);
        capability.GetProperty("maximumTopK").GetInt32().Should()
            .Be(AllocationReproducibilityBounds.MaximumTopK);

        var query = frames[1];
        query.GetProperty("type").GetString().Should().Be("query");
        query.GetProperty("comparisonHandles")[0].GetString().Should().Be(first.Id);
        var data = query.GetProperty("result").GetProperty("data");
        data.GetProperty("schema").GetString().Should().Be(AllocationReproducibilityResult.SchemaV1);
        data.GetProperty("method").GetString().Should().Be(AllocationReproducibilityResult.MethodV1);
        data.GetProperty("independenceUnit").GetString().Should().Be("capture");
        data.GetProperty("allocatedTypeStability").GetProperty("medianMembershipJaccard")
            .GetDouble().Should().Be(1);
        data.GetProperty("allocationStackStability").GetProperty("medianSharedKeySpearman")
            .GetDouble().Should().Be(1);
        data.TryGetProperty("verdict", out _).Should().BeFalse();
    }

    [Fact]
    public async Task JsonlQueryPreservesUnavailableHandleInsteadOfReturningSuccessShapedZero()
    {
        var store = new MemoryDiagnosticHandleStore();
        var current = Register(store, "current", Artifact(Start, 100, 2, ("A", 100L, 2L)));

        var frames = await RunAsync(
            store,
            new { type = "hello", protocolVersion = 2 },
            new
            {
                type = "query",
                requestId = "missing",
                handle = current.Id,
                comparisonHandles = new[] { "missing-handle" },
                view = "allocation-reproducibility",
            });

        var data = frames[1].GetProperty("result").GetProperty("data");
        data.TryGetProperty("availability", out var availability).Should().BeTrue(data.ToString());
        availability.GetString().Should().Be("insufficient_capture_count");
        data.GetProperty("topK").GetInt32().Should().Be(AllocationReproducibilityBounds.DefaultTopK);
        data.GetProperty("estimatedBytes").GetProperty("median").ValueKind.Should().Be(JsonValueKind.Null);
        var unavailable = data.GetProperty("captures").EnumerateArray()
            .Single(capture => capture.GetProperty("handle").GetString() == "missing-handle");
        unavailable.GetProperty("attributionState").GetString().Should().Be("unavailable_handle");
        unavailable.GetProperty("unavailableReason").GetString().Should().Be("handle-unknown");
    }

    [Fact]
    public async Task JsonlQueryRejectsUnboundedOrMisplacedFields()
    {
        var store = new MemoryDiagnosticHandleStore();
        var current = Register(store, "current", Artifact(Start, 100, 2, ("A", 100L, 2L)));
        var cpu = store.Register(
            Environment.ProcessId,
            "cpu-sample",
            Trace(10),
            TimeSpan.FromMinutes(10));
        var tooMany = Enumerable.Range(0, AllocationReproducibilityBounds.MaximumCaptures)
            .Select(index => $"missing-{index}")
            .ToArray();

        var frames = await RunAsync(
            store,
            new { type = "hello", protocolVersion = 2 },
            new
            {
                type = "query",
                requestId = "many",
                handle = current.Id,
                comparisonHandles = tooMany,
                view = "allocation-reproducibility",
            },
            new
            {
                type = "query",
                requestId = "top",
                handle = current.Id,
                view = "allocation-reproducibility",
                topN = AllocationReproducibilityBounds.MaximumTopK + 1,
            },
            new
            {
                type = "query",
                requestId = "wrong-view",
                handle = current.Id,
                comparisonHandles = new[] { current.Id },
                view = "top-methods",
            },
            new
            {
                type = "query",
                requestId = "wrong-kind",
                handle = cpu.Id,
                view = "allocation-reproducibility",
            });

        frames.Skip(1).Should().OnlyContain(frame =>
            frame.GetProperty("type").GetString() == "error"
            && frame.GetProperty("code").GetString() == "invalid_query");
    }

    [Fact]
    public void AllocationSessionViewsAddReproducibilityWithoutChangingCpuViews()
    {
        CliCommands.SessionViewsFor("allocation-sample").Should()
            .Equal([.. CpuSampleQueryDispatcher.SessionViews, "allocation-reproducibility"]);
        CliCommands.SessionViewsFor("cpu-sample").Should().Equal(CpuSampleQueryDispatcher.SessionViews);
    }

    [Fact]
    public void QueryParserAcceptsRepeatableComparisonHandles()
    {
        var options = CliOptions.Parse(
            [
                "query",
                "--handle", "current",
                "--comparison-handle", "first",
                "--comparison-handle", "second",
                "--view", "allocation-reproducibility",
                "--top", "4",
            ],
            out var error);

        error.Should().BeNull();
        options.Should().NotBeNull();
        options!.ComparisonHandles.Should().Equal("first", "second");
        options.Top.Should().Be(4);
    }

    [Fact]
    public async Task SessionQueryRejectsComparisonHandlesOutsideReproducibilityView()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddSingleton<IDiagnosticHandleStore>(
                new MemoryDiagnosticHandleStore()))
            .Build();

        var outcome = await CliCommands.QuerySession(
            host.Services,
            new CliOptions
            {
                Command = "query",
                Handle = "current",
                ComparisonHandles = ["first"],
                View = "call-tree",
            });

        outcome.IsError.Should().BeTrue();
        outcome.Human.Should().Contain("--comparison-handle is supported only");
    }

    [Fact]
    public async Task SessionQueryRejectsWrongKindHandleForReproducibility()
    {
        var store = new MemoryDiagnosticHandleStore();
        var cpu = store.Register(
            Environment.ProcessId,
            "cpu-sample",
            Trace(10),
            TimeSpan.FromMinutes(10));
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddSingleton<IDiagnosticHandleStore>(store))
            .Build();

        var outcome = await CliCommands.QuerySession(
            host.Services,
            new CliOptions
            {
                Command = "query",
                Handle = cpu.Id,
                View = "allocation-reproducibility",
            });

        outcome.IsError.Should().BeTrue();
        outcome.Human.Should().Contain("cpu-sample");
    }

    private static DiagnosticHandle Register(
        IDiagnosticHandleStore store,
        string label,
        AllocationSampleArtifact artifact)
        => store.RegisterWithMetadata(
            artifact.Summary.ProcessId,
            "allocation-sample",
            artifact,
            TimeSpan.FromMinutes(10),
            evictWhenProcessExits: false,
            origin: HandleOrigin.Live,
            producingTool: label);

    private static AllocationSampleArtifact Artifact(
        DateTimeOffset startedAt,
        long bytes,
        long ticks,
        params (string Key, long Bytes, long Ticks)[] rows)
    {
        var types = rows.Select(row => new AllocatedType(
            row.Key,
            row.Bytes,
            row.Ticks,
            HeapKind.Small)).ToArray();
        var sites = rows.Select(row => new AllocationSite(
            new SampledFrame("App.dll", row.Key),
            row.Bytes,
            row.Ticks,
            HeapKind.Small)).ToArray();
        var summary = new AllocationSample(
            Environment.ProcessId,
            startedAt,
            TimeSpan.FromSeconds(3),
            ticks,
            bytes,
            types,
            types)
        {
            TopBySite = sites,
        };
        return new AllocationSampleArtifact(summary, Trace(ticks))
        {
            EstimatedBytesTrace = Trace(bytes),
            Reproducibility = new(
                AllocationReproducibilityCaptureMetadata.SchemaV1,
                "CoreClr",
                "10.0.0",
                "windows",
                "x64",
                AllocationReproducibilityCaptureMetadata.EventPipeBackend,
                "gc-allocation-tick-estimated-bytes",
                "estimated-bytes",
                "capture-total",
                "none",
                3,
                16,
                Start.AddMinutes(-1)),
        };
    }

    private static CpuSampleTraceArtifact Trace(long total)
        => new(
            Environment.ProcessId,
            Start,
            TimeSpan.FromSeconds(3),
            total,
            new CallTreeNode(
                new SampledFrame(string.Empty, "<root>"),
                total,
                0,
                []));

    private static async Task<IReadOnlyList<JsonElement>> RunAsync(
        IDiagnosticHandleStore store,
        params object[] requests)
    {
        using var input = new StringReader(
            string.Join('\n', requests.Select(static request => JsonSerializer.Serialize(request))) + "\n");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var errors = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await CliStreamingProtocol.RunAsync(
            ["stream", "--protocol", "jsonl"],
            input,
            output,
            errors,
            () => new HostBuilder()
                .ConfigureServices(services => services.AddSingleton(store))
                .Build(),
            CancellationToken.None);
        exit.Should().Be(0, errors.ToString());
        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToArray();
    }
}
