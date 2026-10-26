using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Cli;
using FluentAssertions;
using Microsoft.Extensions.Hosting;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliInvestigationEvidenceTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse(
        "2026-10-26T15:00:00Z",
        CultureInfo.InvariantCulture);

    [Fact]
    public async Task HelloAdvertisesVersionedBoundedCapabilityWithoutChangingProtocolVersion()
    {
        using var input = new StringReader("""{"type":"hello","protocolVersion":2}""" + Environment.NewLine);
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        var exit = await CliStreamingProtocol.RunAsync(
            ["stream", "--protocol", "jsonl"],
            input,
            output,
            error,
            () => new HostBuilder().Build(),
            CancellationToken.None);

        exit.Should().Be(0, error.ToString());
        using var frame = JsonDocument.Parse(output.ToString().Trim());
        frame.RootElement.GetProperty("protocolVersion").GetInt32().Should().Be(2);
        var capability = frame.RootElement.GetProperty("capabilities").GetProperty("investigationEvidence");
        capability.GetProperty("version").GetInt32().Should().Be(1);
        capability.GetProperty("kinds").EnumerateArray().Select(item => item.GetString()).Should()
            .Equal("cpu", "allocation", "off_cpu", "contention", "threadpool", "gc", "requests");
        capability.GetProperty("maxItems").GetInt32().Should().Be(7);
        capability.GetProperty("maxItemBytes").GetInt32().Should().Be(1024 * 1024);
        capability.GetProperty("maxTotalBytes").GetInt32().Should().Be(4 * 1024 * 1024);
    }

    [Theory]
    [InlineData("cpu", "cpu")]
    [InlineData("allocation", "allocation")]
    [InlineData("off_cpu", "off_cpu")]
    [InlineData("contention", "contention")]
    [InlineData("threadpool", "threadpool")]
    [InlineData("gc", "gc")]
    [InlineData("requests", "requests")]
    [InlineData("request-activities", "requests")]
    public void EligibleKindsProduceExplicitIdentityWindowStoreAndLifecycle(
        string collectorKind,
        string normalizedKind)
    {
        var context = new CliInvestigationEvidence();
        var descriptor = context.Create(
            collectorKind,
            4242,
            Start,
            Start,
            Start.AddMinutes(1),
            Start.AddMinutes(1).AddSeconds(6),
            "reported",
            "normal",
            collectorKind == "off_cpu" ? "perf" : "eventpipe",
            "collector",
            ["Retained evidence is bounded."],
            "handle-1",
            Start.AddMinutes(11));

        descriptor.Should().NotBeNull();
        descriptor!.Version.Should().Be(1);
        descriptor.Kind.Should().Be(normalizedKind);
        descriptor.Target.IdentityStatus.Should().Be("verified");
        descriptor.Target.ProcessStartedAt.Should().Be(Start);
        descriptor.Window.Clock.InstanceId.Should().Be(context.ConnectionId);
        descriptor.Window.Clock.Comparison.Should().Be("reported-only");
        descriptor.Retention.Connection.StoreId.Should().Be(context.StoreId);
        descriptor.Retention.Drilldown.Status.Should().Be("available");
        descriptor.Retention.Drilldown.ExpiresNoLaterThan.Should().Be(Start.AddMinutes(11));
        descriptor.Retention.Drilldown.InvalidatedBy.Should().Contain(
            ["expired", "capacity-evicted", "owner-replaced", "connection-closed"]);
        descriptor.Limitations.Should().Contain(limit => limit.Contains("upper bound", StringComparison.Ordinal));
    }

    [Fact]
    public void CompoundAndNonV1KindsAreNotEligible()
    {
        var context = new CliInvestigationEvidence();
        context.Create("gc-activities", 42, Start, Start, Start, Start.AddSeconds(6),
            "observed", "normal", "eventpipe", "collector", [], null, null).Should().BeNull();
        context.Create("kestrel", 42, Start, Start, Start, Start.AddSeconds(6),
            "reported", "normal", "eventpipe", "collector", [], null, null).Should().BeNull();
    }

    [Fact]
    public void IdentityChangeIsExplicitlyUnavailableAndMissingLifetimeIsUnknown()
    {
        var context = new CliInvestigationEvidence();
        var changed = context.Create("cpu", 42, Start, Start.AddSeconds(1), Start, Start.AddSeconds(6),
            "reported", "normal", "eventpipe", "cpu-sampler", [], null, null)!;
        changed.Target.IdentityStatus.Should().Be("changed");
        changed.Availability.Should().Be("unavailable");
        changed.UnavailableReason.Should().Be("target-changed");

        var unknown = context.Create("cpu", 42, null, null, Start, Start.AddSeconds(6),
            "reported", "normal", "eventpipe", "cpu-sampler", [], null, null)!;
        unknown.Target.IdentityStatus.Should().Be("unknown");
        unknown.Availability.Should().Be("available");
        unknown.Retention.Drilldown.Status.Should().Be("unavailable");
        unknown.Retention.Drilldown.UnavailableReason.Should().Be("not-retained");
    }
}
