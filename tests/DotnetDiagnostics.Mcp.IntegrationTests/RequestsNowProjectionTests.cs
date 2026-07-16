using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

public sealed class RequestsNowProjectionTests
{
    [Fact]
    public async Task InspectProcess_RequestsNow_PreservesTruncationNotes()
    {
        const string note = "Dropped 3 request thread snapshot capture(s) after reaching SnapshotQueueCapacity=256; request rows and counts are incomplete lower bounds once the cap is hit because omitted requests are removed from the result.";
        var collector = new StubRequestsNowCollector(new RequestsNowSnapshot(
            ProcessId: 42,
            CapturedAt: DateTimeOffset.UtcNow,
            Window: TimeSpan.FromSeconds(2),
            Requests: [])
        {
            Notes = [note],
        });

        var result = await InspectProcessTool.InspectProcess(
            discovery: null!,
            resolver: ToolGuardTests.EchoResolver(),
            detector: null!,
            containerCollector: null!,
            memoryCollector: null!,
            runtimeConfigInspector: null!,
            resourcesCollector: null!,
            requestsNowCollector: collector,
            counterCollector: null!,
            principalAccessor: TestPrincipalAccessors.WithScopes("ptrace"),
            preflightInspector: null!,
            view: InspectProcessTool.RequestsNowView,
            processId: 42,
            cancellationToken: CancellationToken.None);

        result.Data.Should().NotBeNull();
        result.Data!.RequestsNow.Should().BeEmpty();
        result.Data.Notes.Should().Equal(note);
        result.Summary.Should().Contain(note);
    }

    private sealed class StubRequestsNowCollector(RequestsNowSnapshot snapshot) : IRequestsNowCollector
    {
        public Task<RequestsNowSnapshot> CollectAsync(
            int processId,
            TimeSpan window,
            int topFrames,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }
}
