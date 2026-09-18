using System.Text.Json;
using DotnetDiagnostics.BenchmarkDotNet;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetDiagnostics.BenchmarkDotNet.Tests;

public sealed class HttpDestinationContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttributeCollectorJsonAndReportPreserveOptInAndDoNotLeakAuthority(bool include)
    {
        var attribute = new DiagnosticKindAttribute(BenchmarkDiagnosticKind.Activities) { IncludeHttpDestination = include };
        var source = new Collector();
        using var collector = new InProcessDiagnosticCollector(() => new ServiceCollection()
            .AddSingleton<IActivityCollector>(source)
            .AddSingleton<IProcessContextResolver>(new Resolver())
            .AddSingleton<IDiagnosticHandleStore>(new MemoryDiagnosticHandleStore())
            .AddSingleton(new SensitiveDataRedactor(new SecurityOptions { RedactionPatterns = ["secret-backend"] }))
            .BuildServiceProvider());
        var result = await collector.CollectAsync(Environment.ProcessId, attribute.KindList.Single(),
            attribute.DurationSeconds, attribute.IncludeHttpDestination, CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal(include, source.OptedIn);
        using var document = JsonDocument.Parse(result.Json);
        var activity = document.RootElement.GetProperty("Data").GetProperty("Activities")[0];
        if (include) Assert.Equal("redacted", activity.GetProperty("Destination").GetProperty("Availability").GetString());
        else Assert.False(activity.TryGetProperty("Destination", out _));
        Assert.DoesNotContain("secret-backend", result.Json, StringComparison.Ordinal);
        var report = DotnetDiagnosticsReportExporter.BuildMarkdown(
            [new BenchmarkDiagnosticEntry("HTTP", "activities", result.IsError, result.Summary, result.Headline, "activities.json")]);
        Assert.DoesNotContain("secret-backend", report, StringComparison.Ordinal);
        if (include) Assert.Contains("HTTP destination provenance is unknown", report, StringComparison.Ordinal);
    }

    private sealed class Collector : IActivityCollector
    {
        internal bool OptedIn { get; private set; }
        public Task<ActivityCapture> CollectAsync(int processId, TimeSpan duration, IReadOnlyList<string>? sources = null,
            int maxActivities = 200, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<ActivityCapture> CollectAsync(int processId, TimeSpan duration, IReadOnlyList<string>? sources,
            int maxActivities, string? traceId, int maxMatchedActivities, bool includeHttpDestination,
            CancellationToken cancellationToken = default)
        {
            OptedIn = includeHttpDestination;
            var start = DateTimeOffset.UnixEpoch;
            return Task.FromResult(new ActivityCapture(processId, sources, start, duration, 1, 1,
                [new("System.Net.Http", "System.Net.Http.HttpRequestOut", "http", null,
                    "11111111111111111111111111111111", "1111111111111111", null, start, start.AddSeconds(1),
                    TimeSpan.FromSeconds(1), new Dictionary<string, string>())
                {
                    Destination = includeHttpDestination ? new("available", "http", "secret-backend", 8080,
                        "diagnostic-source-http-start") : null,
                }], [], []));
        }
    }

    private sealed class Resolver : IProcessContextResolver
    {
        public Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessContextResolution(new ProcessContext(Environment.ProcessId, RuntimeFlavor.CoreClr,
                true, true, true), null));
    }
}
