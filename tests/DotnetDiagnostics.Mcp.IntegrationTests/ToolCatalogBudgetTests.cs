using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Evidence;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit.Abstractions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

[Collection(DiagnosticIntegrationGroup.Name)]
public sealed class ToolCatalogBudgetTests : IClassFixture<ToolCatalogBudgetTests.FullCatalogFactory>
{
    // Baseline after the Phase 16 proxy-delegation/scope-authorization batch was
    // 220,804 bytes. Issue #773 deliberately adds one static resolved-risk summary
    // per tool, conditional-safety flags, compact result fields, and the reserved
    // acknowledgement schema only where high/critical resolution is possible:
    // 258,367 bytes. 270,000 left ~4.5% headroom while continuing to catch
    // accidental catalog growth. Issues #828 (collect_sample(kind="cpu-efficiency"))
    // and #830 (collect_sample(kind="native-lock-contention")) each add a new kind
    // discriminator value, parameter, and description to the already-large
    // collect_sample schema; combined, the measured catalog is 271,316 bytes.
    // 280,000 restores ~3% headroom above that measured baseline.
    // Issue #986: base d522603 measures 279,976 bytes; bounded CrashGuard
    // observation facts add 1,072 output-schema bytes (no prose), reaching
    // 281,048. The explicit 282,000 ceiling preserves all eight diagnostic
    // fields with 952 bytes of headroom; it does not restore 3% headroom.
    // Issue #1120: base 5be1063 (pre-thread-statics) measures 281,866 bytes —
    // only 134 bytes of headroom remained under 282,000. The new
    // query_snapshot(view="thread-statics")/collect_thread_snapshot output
    // model was trimmed to the issue's literal spec (managed thread id, field
    // name, IsInitialized, truncated value preview only — no OS thread id,
    // no per-field type name/address/truncation flag), and the shared
    // typeFullName parameter description was shortened rather than grown.
    // Even after that trim the structural output-schema cost of one new
    // nested per-thread/per-field array is irreducible prose-free growth,
    // measuring 282,450 bytes. 283,000 restores ~550 bytes of headroom.
    // Issue #1077: base 282,687 bytes. Never returning a sampled percentage without
    // its count adds the nullable SignalBucket.SampleCount and
    // CollectBatchInvestigationDigest.CpuTotalSamples output-schema properties
    // (no prose), measuring 283,702 bytes. All other sample counts/denominators
    // were added to summaries and handle-store query results, which are not in
    // tools/list. 284,000 keeps ~300 bytes of headroom.
    // Issue #1192: the fixed-key EvidenceQuality.Observed output contract reaches
    // 284,392 bytes on Linux and Windows. The approved 285,000 ceiling preserves
    // these typed facts with 608 bytes of headroom; tool counts remain 17/13.
    // Issue #1323: the Core-owned ThreadPoolBacklogEvidence record is exposed
    // through the existing CounterSnapshot output path. The maximal catalog is
    // 286,452 bytes and the default catalog is 253,284 bytes; 287,000 preserves
    // the typed contract with 548 bytes of headroom and adds no tool.
    private const int MaximumCatalogBytes = 287_000;

    private readonly FullCatalogFactory _factory;
    private readonly ITestOutputHelper _output;

    public ToolCatalogBudgetTests(FullCatalogFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task FullCatalog_StaysWithinSerializedByteBudget()
    {
        await using var client = await ConnectAsync();
        var result = await client.ListToolsAsync(
            new ListToolsRequestParams(),
            cancellationToken: CancellationToken.None);

        var measurement = ToolCatalogMeasurement.Measure(result);
        _output.WriteLine(measurement.ToReport());

        measurement.Tools.Should().HaveCount(17,
            "the budget must measure the maximal shipping surface, including orchestrator and Azure discovery");
        measurement.SerializedBytes.Should().BeLessThanOrEqualTo(MaximumCatalogBytes,
            "tools/list growth consumes model context on every client catalog refresh; update the measurement report and justify any budget increase");
    }

    [Fact]
    public async Task FullCatalog_OutputPropertiesUseClientCompatibleObjectSchemas()
    {
        await using var client = await ConnectAsync();
        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);

        tools.Should().HaveCount(17);
        foreach (var tool in tools)
        {
            var schema = tool.ReturnJsonSchema!.Value;
            foreach (var property in schema.GetProperty("properties").EnumerateObject())
            {
                property.Value.ValueKind.Should().Be(JsonValueKind.Object,
                    $"the TypeScript SDK 1.32.1 protocol decoder requires an object for {tool.Name}.{property.Name}");
            }
        }

        foreach (var name in new[] { "query_snapshot", "get_bytes", "inspect_heap", "compare_to_baseline" })
        {
            var data = tools.Single(tool => tool.Name == name)
                .ReturnJsonSchema!.Value.GetProperty("properties").GetProperty("data");
            data.EnumerateObject().Should().BeEmpty(
                $"{name}.data must still accept any JSON value, including null and non-object values");
        }
    }

    [Fact]
    public void ContentionProjection_PreservesTheExistingMcpWireShape()
    {
        var schema = McpJsonUtilities.DefaultOptions.GetJsonSchemaAsNode(
            typeof(CollectEventsContentionSnapshot),
            new JsonSchemaExporterOptions
            {
                TreatNullObliviousAsNonNullable = true,
            });
        var properties = schema["properties"]!.AsObject();

        properties.Should().ContainKey("processId");
        properties.Should().ContainKey("events");
        properties.Should().ContainKey("notes");
        properties.Should().NotContainKey("concentration",
            "issue #1319 is Core/CLI evidence and must not expand the MCP catalog");
    }

    [Fact]
    public async Task ProspectiveStatisticalEvidenceContract_MeasuresCatalogSchemaAndPayloadCost()
    {
        await using var client = await ConnectAsync();
        var result = await client.ListToolsAsync(
            new ListToolsRequestParams(),
            cancellationToken: CancellationToken.None);
        var current = ToolCatalogMeasurement.Measure(result);

        var schema = McpJsonUtilities.DefaultOptions.GetJsonSchemaAsNode(
            typeof(ProspectiveStatisticalEvidence),
            new JsonSchemaExporterOptions
            {
                TreatNullObliviousAsNonNullable = true,
            });
        var schemaBytes = SerializedLength(schema);
        var projected = ProjectStatisticalEvidenceOntoExistingTools(result, schema);
        var payloads = RepresentativeStatisticalEvidence.Payloads
            .Select(payload => new
            {
                payload.Availability,
                Bytes = JsonSerializer.SerializeToUtf8Bytes(
                    payload,
                    McpJsonUtilities.DefaultOptions).Length,
            })
            .ToArray();
        var availablePayloadBytes = payloads.Single(static payload =>
            payload.Availability == ProspectiveEvidenceAvailability.Available).Bytes;
        var unavailablePayloadBytes = payloads
            .Where(static payload =>
                    payload.Availability != ProspectiveEvidenceAvailability.Available)
            .Select(static payload => payload.Bytes)
            .ToArray();

        _output.WriteLine(
            $"prospective statistical evidence schema: {schemaBytes:N0} bytes");
        _output.WriteLine(
            $"prospective maximal catalog: {projected.MaximalBytes:N0} bytes " +
            $"(delta +{projected.MaximalBytes - current.SerializedBytes:N0})");
        _output.WriteLine(
            $"prospective default catalog: {projected.DefaultBytes:N0} bytes " +
            $"(delta +{projected.DefaultBytes - current.DefaultCatalogBytes:N0})");
        foreach (var payload in payloads)
        {
            _output.WriteLine(
                $"representative {payload.Availability} payload: {payload.Bytes:N0} bytes");
        }

        projected.MaximalToolCount.Should().Be(17,
            "statistical evidence must reuse existing tools rather than add an MCP tool");
        projected.DefaultToolCount.Should().Be(13);
        projected.MaximalBytes.Should().BeGreaterThan(current.SerializedBytes,
            "typed result records consume tools/list schema bytes even before any payload is returned");
        projected.DefaultBytes.Should().BeGreaterThan(current.DefaultCatalogBytes);
        schemaBytes.Should().BeLessThanOrEqualTo(8_192,
            "a representative single-record schema should remain bounded enough to evaluate explicitly");
        payloads.Should().OnlyContain(static payload => payload.Bytes <= 4_096,
            "one bounded evidence record should remain small relative to collector result payloads");

        var contract = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "docs",
            "design",
            "statistical-evidence-contract.md"));
        var budgetReport = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "docs",
            "research",
            "mcp-tool-catalog-budget.md"));
        foreach (var measuredBytes in new[]
                 {
                     current.SerializedBytes,
                     current.DefaultCatalogBytes,
                     schemaBytes,
                     projected.MaximalBytes,
                     projected.DefaultBytes,
                     projected.MaximalBytes - current.SerializedBytes,
                     availablePayloadBytes,
                 })
        {
            var formatted = measuredBytes.ToString("N0", CultureInfo.InvariantCulture);
            contract.Should().Contain(formatted,
                "the design contract must carry the executable schema/catalog/payload measurement");
            budgetReport.Should().Contain(formatted,
                "the catalog budget report must carry the executable prospective measurement");
        }

        var unavailableRange = string.Create(
            CultureInfo.InvariantCulture,
            $"{unavailablePayloadBytes.Min():N0}–{unavailablePayloadBytes.Max():N0}");
        contract.Should().Contain(unavailableRange);
        budgetReport.Should().Contain(unavailableRange);
    }

    [Fact]
    public async Task ThreadPoolBacklogEvidence_MeasuresShippingSchemaAndPayloadCost()
    {
        await using var client = await ConnectAsync();
        var result = await client.ListToolsAsync(
            new ListToolsRequestParams(),
            cancellationToken: CancellationToken.None);
        var current = ToolCatalogMeasurement.Measure(result);
        var schema = McpJsonUtilities.DefaultOptions.GetJsonSchemaAsNode(
            typeof(ThreadPoolBacklogEvidence),
            new JsonSchemaExporterOptions
            {
                TreatNullObliviousAsNonNullable = true,
            });
        var schemaBytes = SerializedLength(schema);
        var windows = Enumerable.Range(0, 4)
            .Select(index => new ThreadPoolBacklogWindow(
                DateTimeOffset.UnixEpoch.AddSeconds(index),
                1,
                10 + index))
            .ToArray();
        var request = new ThreadPoolBacklogEvidenceRequest(
            "capture-1",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(4),
            new ThreadPoolBacklogProtocol(10),
            windows,
            windows,
            new ThreadPoolBacklogReason(
                null,
                ThreadPoolEvidence.Missing,
                null,
                null));
        var available = ThreadPoolBacklogAnalyzer.Evaluate(request);
        var unavailable = ThreadPoolBacklogAnalyzer.Evaluate(request with
        {
            EventPipeEventsLost = 1,
        });
        var availablePayloadBytes = JsonSerializer.SerializeToUtf8Bytes(
            available,
            McpJsonUtilities.DefaultOptions).Length;
        var unavailablePayloadBytes = JsonSerializer.SerializeToUtf8Bytes(
            unavailable,
            McpJsonUtilities.DefaultOptions).Length;

        _output.WriteLine($"ThreadPool backlog evidence schema: {schemaBytes:N0} bytes");
        _output.WriteLine($"ThreadPool backlog available payload: {availablePayloadBytes:N0} bytes");
        _output.WriteLine($"ThreadPool backlog unavailable payload: {unavailablePayloadBytes:N0} bytes");

        current.Tools.Should().HaveCount(17);
        schemaBytes.Should().BeLessThanOrEqualTo(4_096);
        availablePayloadBytes.Should().BeLessThanOrEqualTo(4_096);
        unavailablePayloadBytes.Should().BeLessThanOrEqualTo(4_096);

        var budgetReport = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "docs",
            "research",
            "mcp-tool-catalog-budget.md"));
        foreach (var measuredBytes in new[]
                 {
                     current.SerializedBytes,
                     current.DefaultCatalogBytes,
                     schemaBytes,
                     availablePayloadBytes,
                     unavailablePayloadBytes,
                 })
        {
            budgetReport.Should().Contain(
                measuredBytes.ToString("N0", CultureInfo.InvariantCulture),
                "the catalog budget report must carry the executable ThreadPool backlog measurement");
        }
    }

    [Fact]
    public async Task RequestLatencyComparison_MeasuresExistingPlacementAndPayloadCost()
    {
        await using var client = await ConnectAsync();
        var result = await client.ListToolsAsync(
            new ListToolsRequestParams(),
            cancellationToken: CancellationToken.None);
        var current = ToolCatalogMeasurement.Measure(result);
        var resultSchema = McpJsonUtilities.DefaultOptions.GetJsonSchemaAsNode(
            typeof(RequestLatencyComparisonResult),
            new JsonSchemaExporterOptions
            {
                TreatNullObliviousAsNonNullable = true,
            });
        var resultSchemaBytes = SerializedLength(resultSchema);
        var actualCatalog = JsonSerializer.SerializeToNode(
            result,
            McpJsonUtilities.DefaultOptions)?.AsObject()
            ?? throw new InvalidOperationException("Could not serialize the MCP tool catalog.");
        var actualCompare = actualCatalog["tools"]!.AsArray()
            .Select(tool => tool!.AsObject())
            .Single(tool => tool["name"]!.GetValue<string>() == "compare_to_baseline");
        var currentDataSchema = actualCompare["outputSchema"]!["properties"]!["data"]!.AsObject();
        var projected = actualCatalog.DeepClone().AsObject();
        var projectedCompare = projected["tools"]!.AsArray()
            .Select(tool => tool!.AsObject())
            .Single(tool => tool["name"]!.GetValue<string>() == "compare_to_baseline");
        projectedCompare["outputSchema"]!["properties"]!["data"] = resultSchema.DeepClone();
        var typedCatalogBytes = SerializedLength(projected);
        var availableBytes = JsonSerializer.SerializeToUtf8Bytes(
            RepresentativeRequestLatencyComparison.Available,
            McpJsonUtilities.DefaultOptions).Length;
        var incompatibleBytes = JsonSerializer.SerializeToUtf8Bytes(
            RepresentativeRequestLatencyComparison.Incompatible,
            McpJsonUtilities.DefaultOptions).Length;

        _output.WriteLine(
            $"request-latency current maximal catalog: {current.SerializedBytes:N0} bytes");
        _output.WriteLine(
            $"request-latency current default catalog: {current.DefaultCatalogBytes:N0} bytes");
        _output.WriteLine(
            "request-latency existing compare_to_baseline output-schema delta: +0 bytes");
        _output.WriteLine(
            $"request-latency typed result schema: {resultSchemaBytes:N0} bytes");
        _output.WriteLine(
            $"request-latency typed catalog projection: {typedCatalogBytes:N0} bytes " +
            $"(delta +{typedCatalogBytes - current.SerializedBytes:N0})");
        _output.WriteLine(
            $"request-latency available payload: {availableBytes:N0} bytes");
        _output.WriteLine(
            $"request-latency incompatible payload: {incompatibleBytes:N0} bytes");

        result.Tools.Should().HaveCount(17);
        currentDataSchema.Should().BeEmpty(
            "the existing compare_to_baseline data envelope accepts the Core result without adding a typed tools/list schema");
        typedCatalogBytes.Should().BeGreaterThan(current.SerializedBytes,
            "the avoided typed projection cost must be measured rather than assumed");
        availableBytes.Should().BeLessThanOrEqualTo(16 * 1024);
        incompatibleBytes.Should().BeLessThanOrEqualTo(16 * 1024);

        var budgetReport = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "docs",
            "research",
            "mcp-tool-catalog-budget.md"));
        foreach (var measuredBytes in new[]
                 {
                     current.SerializedBytes,
                     current.DefaultCatalogBytes,
                     resultSchemaBytes,
                     typedCatalogBytes,
                     typedCatalogBytes - current.SerializedBytes,
                     availableBytes,
                     incompatibleBytes,
                 })
        {
            budgetReport.Should().Contain(
                measuredBytes.ToString("N0", CultureInfo.InvariantCulture));
        }
        budgetReport.Should().Contain(
            "existing `compare_to_baseline` output-schema delta is **0 bytes**");
    }

    [Fact]
    public async Task CpuHotspotComparison_MeasuresExistingPlacementAndPayloadCost()
    {
        await using var client = await ConnectAsync();
        var result = await client.ListToolsAsync(
            new ListToolsRequestParams(),
            cancellationToken: CancellationToken.None);
        var current = ToolCatalogMeasurement.Measure(result);
        var resultSchema = McpJsonUtilities.DefaultOptions.GetJsonSchemaAsNode(
            typeof(CpuHotspotComparisonResult),
            new JsonSchemaExporterOptions
            {
                TreatNullObliviousAsNonNullable = true,
            });
        var resultSchemaBytes = SerializedLength(resultSchema);
        var actualCatalog = JsonSerializer.SerializeToNode(
            result,
            McpJsonUtilities.DefaultOptions)?.AsObject()
            ?? throw new InvalidOperationException("Could not serialize the MCP tool catalog.");
        var actualCompare = actualCatalog["tools"]!.AsArray()
            .Select(tool => tool!.AsObject())
            .Single(tool => tool["name"]!.GetValue<string>() == "compare_to_baseline");
        var currentDataSchema = actualCompare["outputSchema"]!["properties"]!["data"]!.AsObject();
        var projected = actualCatalog.DeepClone().AsObject();
        var projectedCompare = projected["tools"]!.AsArray()
            .Select(tool => tool!.AsObject())
            .Single(tool => tool["name"]!.GetValue<string>() == "compare_to_baseline");
        projectedCompare["outputSchema"]!["properties"]!["data"] = resultSchema.DeepClone();
        var typedCatalogBytes = SerializedLength(projected);
        var availableBytes = JsonSerializer.SerializeToUtf8Bytes(
            RepresentativeCpuHotspotComparison.Available,
            McpJsonUtilities.DefaultOptions).Length;
        var incompatibleBytes = JsonSerializer.SerializeToUtf8Bytes(
            RepresentativeCpuHotspotComparison.Incompatible,
            McpJsonUtilities.DefaultOptions).Length;
        var degradedBytes = JsonSerializer.SerializeToUtf8Bytes(
            RepresentativeCpuHotspotComparison.Degraded,
            McpJsonUtilities.DefaultOptions).Length;

        _output.WriteLine(
            $"CPU-hotspot current maximal catalog: {current.SerializedBytes:N0} bytes");
        _output.WriteLine(
            $"CPU-hotspot current default catalog: {current.DefaultCatalogBytes:N0} bytes");
        _output.WriteLine(
            "CPU-hotspot existing compare_to_baseline output-schema delta: +0 bytes");
        _output.WriteLine(
            $"CPU-hotspot typed result schema: {resultSchemaBytes:N0} bytes");
        _output.WriteLine(
            $"CPU-hotspot typed catalog projection: {typedCatalogBytes:N0} bytes " +
            $"(delta +{typedCatalogBytes - current.SerializedBytes:N0})");
        _output.WriteLine(
            $"CPU-hotspot available payload: {availableBytes:N0} bytes");
        _output.WriteLine(
            $"CPU-hotspot incompatible payload: {incompatibleBytes:N0} bytes");
        _output.WriteLine(
            $"CPU-hotspot degraded payload: {degradedBytes:N0} bytes");

        result.Tools.Should().HaveCount(17);
        currentDataSchema.Should().BeEmpty(
            "the existing compare_to_baseline data envelope accepts the Core result without adding a typed tools/list schema");
        typedCatalogBytes.Should().BeGreaterThan(current.SerializedBytes,
            "the avoided typed projection cost must be measured rather than assumed");
        availableBytes.Should().BeLessThanOrEqualTo(24 * 1024);
        incompatibleBytes.Should().BeLessThanOrEqualTo(24 * 1024);
        degradedBytes.Should().BeLessThanOrEqualTo(24 * 1024);

        var budgetReport = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "docs",
            "research",
            "mcp-tool-catalog-budget.md"));
        foreach (var measuredBytes in new[]
                 {
                     current.SerializedBytes,
                     current.DefaultCatalogBytes,
                     resultSchemaBytes,
                     typedCatalogBytes,
                     typedCatalogBytes - current.SerializedBytes,
                     availableBytes,
                     incompatibleBytes,
                     degradedBytes,
                 })
        {
            budgetReport.Should().Contain(
                measuredBytes.ToString("N0", CultureInfo.InvariantCulture));
        }
        budgetReport.Should().Contain(
            "CPU-hotspot existing `compare_to_baseline` output-schema delta is **0 bytes**");
    }

    [Fact]
    public async Task RetainedGrowthEvidence_MeasuresCoreSchemaPayloadAndZeroCatalogDelta()
    {
        await using var client = await ConnectAsync();
        var result = await client.ListToolsAsync(
            new ListToolsRequestParams(),
            cancellationToken: CancellationToken.None);
        var clientTools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);
        var current = ToolCatalogMeasurement.Measure(result);
        var schema = McpJsonUtilities.DefaultOptions.GetJsonSchemaAsNode(
            typeof(RetainedGrowthEvidenceResult),
            new JsonSchemaExporterOptions
            {
                TreatNullObliviousAsNonNullable = true,
            });
        var schemaBytes = SerializedLength(schema);
        var payload = RepresentativeRetainedGrowthEvidence();
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            McpJsonUtilities.DefaultOptions).Length;
        var querySnapshotData = clientTools.Single(tool => tool.Name == "query_snapshot")
            .ReturnJsonSchema!.Value.GetProperty("properties").GetProperty("data");

        _output.WriteLine($"retained-growth result schema: {schemaBytes:N0} bytes");
        _output.WriteLine($"retained-growth representative payload: {payloadBytes:N0} bytes");
        _output.WriteLine(
            $"retained-growth shipping catalog: {current.SerializedBytes:N0} maximal / " +
            $"{current.DefaultCatalogBytes:N0} default bytes (delta +0 / +0)");

        result.Tools.Should().HaveCount(17,
            "objective retained growth reuses query_snapshot data and must not add an MCP tool");
        querySnapshotData.EnumerateObject().Should().BeEmpty(
            "query_snapshot.data remains intentionally unconstrained, so this Core/CLI result adds zero tools/list schema bytes");
        current.SerializedBytes.Should().BeLessThanOrEqualTo(MaximumCatalogBytes);
        schemaBytes.Should().BeLessThanOrEqualTo(20_000);
        payloadBytes.Should().BeLessThanOrEqualTo(16_384);

        var budgetReport = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "docs",
            "research",
            "mcp-tool-catalog-budget.md"));
        foreach (var measuredBytes in new[]
                 {
                     current.SerializedBytes,
                     current.DefaultCatalogBytes,
                     schemaBytes,
                     payloadBytes,
                 })
        {
            budgetReport.Should().Contain(
                measuredBytes.ToString("N0", CultureInfo.InvariantCulture),
                "the catalog budget report must carry the executable retained-growth measurement");
        }
        budgetReport.Should().Contain("delta is **0 bytes**");
    }

    [Fact]
    public void StatisticalMethodRegistry_DefinesTheDesignOnlyFoundation()
    {
        var registryPath = Path.Combine(
            FindRepoRoot(),
            "docs",
            "design",
            "statistical-evidence-method-registry.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(registryPath));
        var root = document.RootElement;

        root.GetProperty("schema").GetString().Should()
            .Be("dotnet-diagnostics/statistical-evidence-method-registry/v1");
        root.GetProperty("status").GetString().Should().Be("design-only");
        root.GetProperty("eventLevelDefault").GetString().Should()
            .Be("rejected_without_demonstration");
        root.GetProperty("availabilityStates").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().Equal(
                "available",
                "insufficient_observations",
                "incompatible",
                "loss_detected",
                "unsupported_approximation",
                "not_applicable");
        root.GetProperty("independenceUnits").EnumerateArray()
            .Select(static item => item.GetString())
            .Should().Equal(
                "event",
                "bounded_time_window",
                "capture",
                "process_run",
                "environment_allocation");

        var methods = root.GetProperty("methods").EnumerateArray().ToArray();
        methods.Select(static method => method.GetProperty("id").GetString())
            .Should().Equal(
                "window-median",
                "paired-delta",
                "median-absolute-deviation",
                "wilson-score-rate",
                "theil-sen-slope",
                "percentile-bootstrap-interval");
        methods.Should().OnlyContain(static method =>
            method.GetProperty("version").GetInt32() == 1
            && method.GetProperty("minimumInputCount").GetInt32() > 0
            && !method.GetProperty("emitsVerdict").GetBoolean()
            && method.GetProperty("allowedIndependenceUnits").GetArrayLength() > 0
            && method.GetProperty("produces").GetArrayLength() > 0);
    }

    [Fact]
    public async Task AttachTool_ToolsListAdvertisesExternalDockerProfileFlow()
    {
        await using var client = await ConnectAsync();
        var tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);

        var attach = tools.Single(tool => tool.Name == "attach_to_pod");
        attach.Title.Should().StartWith("Attach to an orchestrated target");
        attach.Title.Should().Contain("external Docker/MCP profile");
        attach.Description.Should().StartWith("Attaches to an orchestrated diagnostic target");
        attach.Description.Should().Contain("list_orchestrator(kind=\"external-profiles\")");
        attach.Description.Should().Contain("existing attach_to_pod tool");
        attach.Description.Should().Contain("no Kubernetes Pod or ephemeral container is required");
        attach.Description.Should().Contain("retained for backward compatibility");

        var properties = attach.JsonSchema.GetProperty("properties");
        var profileDescription = properties.GetProperty("profileName").GetProperty("description").GetString();
        profileDescription.Should().Contain("external Docker sidecars");
        profileDescription.Should().Contain("list_orchestrator(kind=\"external-profiles\")");
        profileDescription.Should().Contain("attach_to_pod(profileName=\"sidecar\")");

        var podDescription = properties.GetProperty("podName").GetProperty("description").GetString();
        podDescription.Should().StartWith("Kubernetes Pod mode only");

        var list = tools.Single(tool => tool.Name == "list_orchestrator");
        list.Description.Should().Contain("external Docker/MCP profiles");
        list.Description.Should().Contain("attach_to_pod(profileName=\"returned-name\")");
    }

    [Fact]
    public void ProsePartition_PreservesSchemaPropertiesNamedTitleOrDescription()
    {
        var schema = JsonNode.Parse(
            """
            {
              "title": "Root annotation",
              "description": "Root prose",
              "type": "object",
              "properties": {
                "title": {
                  "type": "string",
                  "description": "Description of the title property"
                },
                "description": {
                  "type": "string",
                  "title": "Description field"
                }
              }
            }
            """)!;

        ToolMeasurement.RemoveProseAnnotations(schema);

        var root = schema.AsObject();
        root.Should().NotContainKey("title");
        root.Should().NotContainKey("description");

        var properties = root["properties"]!.AsObject();
        properties.Should().ContainKey("title");
        properties.Should().ContainKey("description");
        properties["title"]!.AsObject().Should().NotContainKey("description");
        properties["description"]!.AsObject().Should().NotContainKey("title");
    }

    private async Task<McpClient> ConnectAsync()
    {
        var httpClient = _factory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", FullCatalogFactory.Token);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {FullCatalogFactory.Token}",
                },
            },
            httpClient,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(
            transport,
            cancellationToken: CancellationToken.None);
    }

    private static ProjectedCatalogMeasurement ProjectStatisticalEvidenceOntoExistingTools(
        ListToolsResult result,
        JsonNode statisticalEvidenceSchema)
    {
        var maximal = JsonSerializer.SerializeToNode(
            result,
            McpJsonUtilities.DefaultOptions)?.AsObject()
            ?? throw new InvalidOperationException("Could not serialize the MCP tool catalog.");
        var optionalToolNames = new HashSet<string>(
            ["attach_to_pod", "detach_from_pod", "list_orchestrator", "discover_azure"],
            StringComparer.Ordinal);
        var defaultCatalog = JsonSerializer.SerializeToNode(
            new ListToolsResult
            {
                Tools = result.Tools
                    .Where(tool => !optionalToolNames.Contains(tool.Name))
                    .ToArray(),
            },
            McpJsonUtilities.DefaultOptions)?.AsObject()
            ?? throw new InvalidOperationException("Could not serialize the default MCP tool catalog.");

        ApplyStatisticalEvidenceSchema(maximal, statisticalEvidenceSchema);
        ApplyStatisticalEvidenceSchema(defaultCatalog, statisticalEvidenceSchema);
        var tools = maximal["tools"]!.AsArray();
        var defaultTools = defaultCatalog["tools"]!.AsArray();

        return new ProjectedCatalogMeasurement(
            MaximalBytes: SerializedLength(maximal),
            DefaultBytes: SerializedLength(defaultCatalog),
            MaximalToolCount: tools.Count,
            DefaultToolCount: defaultTools.Count);
    }

    private static void ApplyStatisticalEvidenceSchema(
        JsonObject catalog,
        JsonNode statisticalEvidenceSchema)
    {
        var tools = catalog["tools"]?.AsArray()
            ?? throw new InvalidOperationException("The MCP tool catalog has no tools array.");
        var hostTools = new HashSet<string>(
            ["collect_events", "collect_sample", "compare_to_baseline"],
            StringComparer.Ordinal);

        foreach (var tool in tools)
        {
            var toolObject = tool?.AsObject()
                ?? throw new InvalidOperationException("The MCP tool catalog contains a non-object tool.");
            var name = toolObject["name"]?.GetValue<string>()
                ?? throw new InvalidOperationException("An MCP tool has no name.");
            if (!hostTools.Contains(name))
            {
                continue;
            }

            var outputProperties = toolObject["outputSchema"]?["properties"]?.AsObject()
                ?? throw new InvalidOperationException($"Tool '{name}' has no output-schema properties.");
            outputProperties["statisticalEvidence"] = statisticalEvidenceSchema.DeepClone();
        }
    }

    private static int SerializedLength(JsonNode node)
        => Encoding.UTF8.GetByteCount(node.ToJsonString(McpJsonUtilities.DefaultOptions));

    private static RetainedGrowthEvidenceResult RepresentativeRetainedGrowthEvidence()
    {
        var target = new TypeIdentity("Example.RetainedNode") { ModuleName = "Example.dll" };
        var quality = new EvidenceQuality(
            EvidenceQuality.SchemaV1,
            [
                new EvidenceLimitation(
                    EvidenceLimitationCategory.MechanismUnobservable,
                    "eventpipe-loss",
                    null,
                    "The gcdump transport does not expose an exact loss count."),
            ],
            new EvidenceConclusionPolicy(
                EvidenceConclusionSupport.Supported,
                EvidenceConclusionSupport.Inconclusive,
                EvidenceConclusionSupport.Inconclusive));
        var attempts = Enumerable.Range(1, 3)
            .Select(index =>
            {
                var capturedAt = new DateTimeOffset(2026, 1, index, 0, 0, 0, TimeSpan.Zero);
                var baseline = RetainedGrowthSnapshot(
                    processId: 1_000 + index,
                    capturedAt,
                    target,
                    bytes: 1_000_000 + (index * 10_000),
                    instances: 10_000 + (index * 100),
                    quality);
                var current = RetainedGrowthSnapshot(
                    processId: 1_000 + index,
                    capturedAt.AddMinutes(1),
                    target,
                    bytes: baseline.TopTypesByBytes[0].TotalBytes + 640_000 + (index * 1_000),
                    instances: baseline.TopTypesByBytes[0].InstanceCount + 20_000,
                    quality);
                return new RetainedGrowthEvidenceAttempt(
                    $"run-{index}",
                    RetainedGrowthAttemptOutcome.Completed,
                    $"baseline-{index}",
                    baseline,
                    $"current-{index}",
                    current)
                {
                    ObservedGen2Collections = 2,
                    Allocation = new RetainedGrowthAllocationObservation(
                        11_000_000 + (index * 100_000),
                        1_000 + index,
                        LostOrDropped: 0),
                };
            })
            .ToArray();
        return RetainedGrowthEvidence.Build(target, attempts);
    }

    private static HeapSnapshotArtifact RetainedGrowthSnapshot(
        int processId,
        DateTimeOffset capturedAt,
        TypeIdentity target,
        long bytes,
        long instances,
        EvidenceQuality quality)
    {
        var stat = new TypeStat(
            target.TypeFullName,
            target.ModuleName,
            instances,
            bytes,
            TotalBytesPercent: 100,
            Identity: target);
        return new HeapSnapshotArtifact(
            HeapSnapshotOrigin.GcDump,
            processId,
            capturedAt,
            TimeSpan.FromSeconds(1),
            new DumpRuntimeInfo("CoreCLR", "10.0.0", "x64", IsServerGC: false, HeapCount: 1),
            new DumpHeapSummary(bytes, 0, 0, bytes, 0, 0, bytes),
            [stat],
            [stat])
        {
            Quality = quality,
        };
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotnetDiagnostics.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    public sealed class FullCatalogFactory : WebApplicationFactory<DotnetDiagnostics.Mcp.Program>
    {
        public const string Token = "catalog-budget-test-token-do-not-use-in-prod";

        private readonly string? _previousToken;
        private readonly string? _previousOrchestratorEnabled;
        private readonly string? _previousAzureDiscoveryEnabled;

        public FullCatalogFactory()
        {
            _previousToken = Environment.GetEnvironmentVariable("MCP_BEARER_TOKEN");
            _previousOrchestratorEnabled = Environment.GetEnvironmentVariable("Orchestrator__Enabled");
            _previousAzureDiscoveryEnabled = Environment.GetEnvironmentVariable("AzureDiscovery__Enabled");

            Environment.SetEnvironmentVariable("MCP_BEARER_TOKEN", Token);
            Environment.SetEnvironmentVariable("Orchestrator__Enabled", "true");
            Environment.SetEnvironmentVariable("AzureDiscovery__Enabled", "true");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable("MCP_BEARER_TOKEN", _previousToken);
            Environment.SetEnvironmentVariable("Orchestrator__Enabled", _previousOrchestratorEnabled);
            Environment.SetEnvironmentVariable("AzureDiscovery__Enabled", _previousAzureDiscoveryEnabled);
        }
    }
}

internal sealed record ProjectedCatalogMeasurement(
    int MaximalBytes,
    int DefaultBytes,
    int MaximalToolCount,
    int DefaultToolCount);

[JsonConverter(typeof(JsonStringEnumConverter<ProspectiveEvidenceAvailability>))]
internal enum ProspectiveEvidenceAvailability
{
    [JsonStringEnumMemberName("available")]
    Available,

    [JsonStringEnumMemberName("insufficient_observations")]
    InsufficientObservations,

    [JsonStringEnumMemberName("incompatible")]
    Incompatible,

    [JsonStringEnumMemberName("loss_detected")]
    LossDetected,

    [JsonStringEnumMemberName("unsupported_approximation")]
    UnsupportedApproximation,

    [JsonStringEnumMemberName("not_applicable")]
    NotApplicable,
}

[JsonConverter(typeof(JsonStringEnumConverter<ProspectiveIndependenceUnit>))]
internal enum ProspectiveIndependenceUnit
{
    [JsonStringEnumMemberName("bounded_time_window")]
    BoundedTimeWindow,

    [JsonStringEnumMemberName("capture")]
    Capture,

    [JsonStringEnumMemberName("process_run")]
    ProcessRun,

    [JsonStringEnumMemberName("environment_allocation")]
    EnvironmentAllocation,
}

internal sealed record ProspectiveObservedQuantity(
    string Id,
    string Unit,
    string Aggregation,
    string Normalization);

internal sealed record ProspectiveEvidenceDelta(
    double? BaselineEstimate,
    double? CandidateEstimate,
    double? Absolute,
    double? Relative);

internal sealed record ProspectiveEvidenceInterval(
    double ConfidenceLevel,
    double Lower,
    double Upper);

internal sealed record ProspectiveEvidenceCounts(
    int Observations,
    int Windows,
    int Captures,
    int Repetitions,
    int IndependenceUnits,
    int ExcludedNonFinite,
    int LostOrDropped);

internal sealed record ProspectiveEvidenceMethod(
    string Id,
    int Version,
    IReadOnlyDictionary<string, string> Parameters);

internal sealed record ProspectiveCompatibilityIdentity(
    string Runtime,
    string Backend,
    string Unit,
    string CaptureSettingsFingerprint,
    string WorkloadFingerprint,
    string ComparisonAxis);

internal sealed record ProspectiveUnavailableField(
    string Field,
    string Reason);

internal sealed record ProspectiveStatisticalEvidence(
    string Schema,
    ProspectiveEvidenceAvailability Availability,
    ProspectiveObservedQuantity Quantity,
    double? Estimate,
    ProspectiveEvidenceDelta? Delta,
    ProspectiveEvidenceInterval? Interval,
    ProspectiveEvidenceCounts Counts,
    ProspectiveEvidenceMethod Method,
    ProspectiveIndependenceUnit IndependenceUnit,
    ProspectiveCompatibilityIdentity Compatibility,
    IReadOnlyList<ProspectiveUnavailableField> UnavailableFields,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Limitations);

internal static class RepresentativeStatisticalEvidence
{
    internal static IReadOnlyList<ProspectiveStatisticalEvidence> Payloads { get; } =
    [
        Create(ProspectiveEvidenceAvailability.Available, estimate: 12.5,
            delta: new(10, 12.5, 2.5, 0.25),
            interval: new(0.95, 1.1, 3.8),
            limitations: ["Interval describes resampling variability, not a causal verdict."]),
        Create(ProspectiveEvidenceAvailability.InsufficientObservations,
            unavailableFields:
            [
                new("estimate", "minimum_independence_units_not_met"),
                new("interval", "minimum_independence_units_not_met"),
            ],
            limitations: ["Need at least two independent process runs; found one."]),
        Create(ProspectiveEvidenceAvailability.Incompatible,
            unavailableFields:
            [
                new("delta", "capture_settings_mismatch"),
                new("interval", "capture_settings_mismatch"),
            ],
            limitations: ["Capture settings fingerprints differ; observations were not pooled."]),
        Create(ProspectiveEvidenceAvailability.LossDetected,
            unavailableFields:
            [
                new("estimate", "source_loss_detected"),
                new("interval", "source_loss_detected"),
            ],
            limitations: ["Detected source loss invalidates population-level interval estimation."]),
        Create(ProspectiveEvidenceAvailability.UnsupportedApproximation,
            unavailableFields:
            [
                new("estimate", "reservoir_approximation_not_admitted"),
                new("interval", "reservoir_approximation_not_admitted"),
            ],
            limitations: ["The retained reservoir approximation is not admitted by this method."]),
        Create(ProspectiveEvidenceAvailability.NotApplicable,
            unavailableFields:
            [
                new("delta", "baseline_or_candidate_not_applicable"),
                new("interval", "parent_estimate_not_applicable"),
            ],
            limitations: ["A paired delta requires both baseline and candidate observations."]),
    ];

    private static ProspectiveStatisticalEvidence Create(
        ProspectiveEvidenceAvailability availability,
        double? estimate = null,
        ProspectiveEvidenceDelta? delta = null,
        ProspectiveEvidenceInterval? interval = null,
        IReadOnlyList<ProspectiveUnavailableField>? unavailableFields = null,
        IReadOnlyList<string>? limitations = null)
        => new(
            Schema: "dotnet-diagnostics/statistical-evidence/v1",
            availability,
            new("request-latency", "ms", "window_median", "per_request"),
            estimate,
            delta,
            interval,
            new(
                Observations: availability == ProspectiveEvidenceAvailability.Available ? 1200 : 0,
                Windows: availability == ProspectiveEvidenceAvailability.Available ? 12 : 1,
                Captures: availability == ProspectiveEvidenceAvailability.Available ? 6 : 1,
                Repetitions: availability == ProspectiveEvidenceAvailability.Available ? 3 : 1,
                IndependenceUnits: availability == ProspectiveEvidenceAvailability.Available ? 3 : 1,
                ExcludedNonFinite: 0,
                LostOrDropped: availability == ProspectiveEvidenceAvailability.LossDetected ? 17 : 0),
            new(
                "paired-delta",
                1,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["estimate"] = "median",
                    ["interval"] = "percentile-bootstrap-interval/v1",
                    ["resamples"] = "2000",
                }),
            ProspectiveIndependenceUnit.ProcessRun,
            new(
                "coreclr/10",
                "eventpipe",
                "ms",
                "sha256:capture-settings",
                "sha256:workload",
                "baseline_vs_candidate"),
            unavailableFields ?? [],
            ["Process runs are independent under the scenario protocol."],
            limitations ?? []);
}

internal static class RepresentativeRequestLatencyComparison
{
    internal static RequestLatencyComparisonResult Available { get; } =
        RequestLatencyComparison.Compare(
            WindowSet("baseline", "build-a", 10),
            WindowSet("candidate", "build-b", 30));

    internal static RequestLatencyComparisonResult Incompatible { get; } =
        RequestLatencyComparison.Compare(
            WindowSet("baseline", "build-a", 10),
            WindowSet("candidate", "build-b", 30) with
            {
                Identity = WindowSet("candidate", "build-b", 30).Identity with
                {
                    Quantity = WindowSet("candidate", "build-b", 30).Identity.Quantity with
                    {
                        Filters = "endpoint=/different",
                    },
                },
            });

    private static RequestLatencyWindowSet WindowSet(
        string id,
        string axisValue,
        double offset)
        => new(
            RequestLatencyWindowSet.SchemaV1,
            id,
            new(
                new("CoreCLR", "major-compatible", "linux", "x64", $"{id}-run"),
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

internal static class RepresentativeCpuHotspotComparison
{
    internal static CpuHotspotComparisonResult Available { get; } =
        CpuHotspotComparison.Compare(
            CaptureSet("baseline", [30, 31, 29, 30]),
            CaptureSet("candidate", [60, 61, 59, 60]));

    internal static CpuHotspotComparisonResult Incompatible { get; } =
        CpuHotspotComparison.Compare(
            CaptureSet("baseline", [30, 31, 29, 30]),
            CaptureSet("candidate", [60, 61, 59, 60]) with
            {
                Identity = CaptureSet("candidate", [60, 61, 59, 60]).Identity with
                {
                    Workload = CaptureSet("candidate", [60, 61, 59, 60]).Identity.Workload with
                    {
                        OfferedLoad = 101,
                    },
                },
            });

    internal static CpuHotspotComparisonResult Degraded { get; } =
        CpuHotspotComparison.Compare(
            CaptureSet("baseline", [30, 31, 29, 30]),
            CaptureSet("candidate", [60, 61, 59, 60], unresolvedSamples: 40));

    private static CpuHotspotCaptureSet CaptureSet(
        string axisValue,
        int[] selectedSamples,
        int unresolvedSamples = 5)
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
                        unresolvedSamples,
                        CpuSampleBackend.EventPipeSampleProfiler,
                        CpuSampleEvidenceKind.StackFrequencyWithHeuristicWaits,
                        CpuHotspotRepresentation.Exact,
                        methods.Length,
                        0,
                        methods));
            }).ToArray());
}

internal sealed record ToolCatalogMeasurement(
    int SerializedBytes,
    int EstimatedTokens,
    int DefaultToolCount,
    int DefaultCatalogBytes,
    int DefaultCatalogEstimatedTokens,
    int CatalogOverheadBytes,
    IReadOnlyList<ToolMeasurement> Tools)
{
    private static readonly HashSet<string> OptionalToolNames = new(StringComparer.Ordinal)
    {
        "attach_to_pod",
        "detach_from_pod",
        "list_orchestrator",
        "discover_azure",
    };

    public static ToolCatalogMeasurement Measure(ListToolsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(
            result,
            McpJsonUtilities.DefaultOptions).Length;
        var tools = result.Tools
            .Select(ToolMeasurement.Measure)
            .OrderByDescending(tool => tool.SerializedBytes)
            .ToArray();
        var catalogOverheadBytes = serializedBytes - tools.Sum(tool => tool.SerializedBytes);
        var defaultTools = result.Tools
            .Where(tool => !OptionalToolNames.Contains(tool.Name))
            .ToArray();
        var defaultCatalogBytes = JsonSerializer.SerializeToUtf8Bytes(
            new ListToolsResult
            {
                Tools = defaultTools,
            },
            McpJsonUtilities.DefaultOptions).Length;

        return new ToolCatalogMeasurement(
            serializedBytes,
            EstimateTokens(serializedBytes),
            defaultTools.Length,
            defaultCatalogBytes,
            EstimateTokens(defaultCatalogBytes),
            catalogOverheadBytes,
            tools);
    }

    public string ToReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"tools/list result: {SerializedBytes:N0} bytes (~{EstimatedTokens:N0} tokens at 4 UTF-8 bytes/token)");
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"default {DefaultToolCount:N0}-tool subset: {DefaultCatalogBytes:N0} bytes (~{DefaultCatalogEstimatedTokens:N0} tokens)");
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"catalog framing: {CatalogOverheadBytes:N0} bytes");
        builder.AppendLine(
            "tool | total bytes | input schema bytes* | output schema bytes* | " +
            "prose bytes | schema-structure bytes | other metadata bytes");
        foreach (var tool in Tools)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"{tool.Name} | {tool.SerializedBytes:N0} | {tool.InputSchemaBytes:N0} | " +
                $"{tool.OutputSchemaBytes:N0} | {tool.ProseBytes:N0} | " +
                $"{tool.SchemaStructureBytes:N0} | {tool.OtherMetadataBytes:N0}");
        }

        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"all tools | {Tools.Sum(tool => tool.SerializedBytes):N0} | " +
            $"{Tools.Sum(tool => tool.InputSchemaBytes):N0} | " +
            $"{Tools.Sum(tool => tool.OutputSchemaBytes):N0} | " +
            $"{Tools.Sum(tool => tool.ProseBytes):N0} | " +
            $"{Tools.Sum(tool => tool.SchemaStructureBytes):N0} | " +
            $"{Tools.Sum(tool => tool.OtherMetadataBytes):N0}");
        builder.AppendLine("* input/output schema byte columns include descriptions and therefore overlap the prose column");
        return builder.ToString();
    }

    private static int EstimateTokens(int bytes) => (bytes + 3) / 4;
}

internal sealed record ToolMeasurement(
    string Name,
    int SerializedBytes,
    int InputSchemaBytes,
    int OutputSchemaBytes,
    int ProseBytes,
    int SchemaStructureBytes,
    int OtherMetadataBytes)
{
    public static ToolMeasurement Measure(Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(
            tool,
            McpJsonUtilities.DefaultOptions).Length;
        var full = JsonSerializer.SerializeToNode(
            tool,
            McpJsonUtilities.DefaultOptions)?.AsObject()
            ?? throw new InvalidOperationException($"Could not serialize tool '{tool.Name}'.");

        var nodeBytes = SerializedLength(full);
        if (nodeBytes != serializedBytes)
        {
            throw new InvalidOperationException(
                $"Tool '{tool.Name}' JSON-node serialization changed its byte length " +
                $"({nodeBytes} != {serializedBytes}); the catalog decomposition would no longer be exact.");
        }

        var withoutProse = full.DeepClone().AsObject();
        RemoveProseAnnotations(withoutProse);

        var withoutProseOrSchemas = withoutProse.DeepClone().AsObject();
        withoutProseOrSchemas.Remove("inputSchema");
        withoutProseOrSchemas.Remove("outputSchema");

        var withoutProseBytes = SerializedLength(withoutProse);
        var otherMetadataBytes = SerializedLength(withoutProseOrSchemas);

        return new ToolMeasurement(
            tool.Name,
            serializedBytes,
            InputSchemaBytes: SerializedLength(full["inputSchema"]!),
            OutputSchemaBytes: SerializedLength(full["outputSchema"]!),
            ProseBytes: serializedBytes - withoutProseBytes,
            SchemaStructureBytes: withoutProseBytes - otherMetadataBytes,
            OtherMetadataBytes: otherMetadataBytes);
    }

    private static int SerializedLength(JsonNode node)
        => Encoding.UTF8.GetByteCount(node.ToJsonString(McpJsonUtilities.DefaultOptions));

    internal static void RemoveProseAnnotations(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            RemoveStringAnnotation(obj, "title");
            RemoveStringAnnotation(obj, "description");
            foreach (var property in obj.ToArray())
            {
                if (property.Value is not null)
                {
                    RemoveProseAnnotations(property.Value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    RemoveProseAnnotations(item);
                }
            }
        }
    }

    private static void RemoveStringAnnotation(JsonObject obj, string propertyName)
    {
        if (obj[propertyName] is JsonValue value &&
            value.TryGetValue<string>(out _))
        {
            obj.Remove(propertyName);
        }
    }
}
