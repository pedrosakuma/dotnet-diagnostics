using System.Text.Json.Nodes;
using DotnetDiagnostics.Core.Counters;
using FluentAssertions;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

public sealed class CounterEvidenceProjectionTests
{
    [Fact]
    public void LegacyV1_PreservesExistingShapeAndDefaults()
    {
        var snapshot = Snapshot();

        var projection = BlindedDiagnosticToolGateway.ProjectCounterEvidence(
            snapshot,
            CounterEvidenceContractVersion.LegacyV1);

        projection.Truncated.Should().BeFalse();
        projection.Evidence.ToJsonString().Should().Be(
            """{"counters":[{"name":"requests","displayName":"Requests","value":12,"unit":"items","kind":"Sum","maximumObserved":20}],"notes":["bounded"],"omittedCounterCount":0}""");
        var defaultGateway = new BlindedDiagnosticToolGateway(0, new AgentHarnessBudget());
        defaultGateway.Tools.Should().BeSameAs(BlindedDiagnosticToolGateway.ToolDefinitions);
    }

    [Fact]
    public void ProspectiveV2_ExportsPerSampleMetadataWithoutDerivingRatesOrTrends()
    {
        var projection = BlindedDiagnosticToolGateway.ProjectCounterEvidence(
            Snapshot(),
            CounterEvidenceContractVersion.ProspectiveV2);

        var evidence = projection.Evidence;
        evidence["counterEvidenceContract"]!.GetValue<string>().Should().Be("prospective-v2");
        evidence["counterSemantics"]!["timeSeriesAvailable"]!.GetValue<bool>().Should().BeFalse();
        evidence["counterSemantics"]!["sampleAlignmentAvailable"]!.GetValue<bool>().Should().BeFalse();
        evidence["counterSemantics"]!["ratesDerivedByHarness"]!.GetValue<bool>().Should().BeFalse();
        var counter = evidence["counters"]!.AsArray().Single()!.AsObject();
        counter["lastSample"]!["value"]!.GetValue<double>().Should().Be(12);
        counter["lastSample"]!["actualIntervalSec"]!.GetValue<double>().Should().Be(2);
        counter["lastSample"]!["displayRateTimeScaleSeconds"]!.GetValue<double>().Should().Be(1);
        counter["maximumRawSample"]!["value"]!.GetValue<double>().Should().Be(20);
        counter["maximumRawSample"]!["actualIntervalSec"]!.GetValue<double>().Should().Be(4);
        counter["maximumRawSample"]!["displayRateTimeScaleSeconds"]!.GetValue<double>().Should().Be(60);
        evidence.ToJsonString().Should().NotContain("ratePer");
        evidence.ToJsonString().Should().NotContain("growth");
    }

    [Fact]
    public void ProspectiveV2_ReportsUnavailableAndInvalidMetadataAsBoundedJson()
    {
        var last = new CounterValue(
            "System.Runtime",
            "invalid",
            "Invalid",
            double.PositiveInfinity,
            CounterKind.Sum)
        {
            IntervalSec = double.NaN,
            DisplayRateTimeScale = TimeSpan.Zero,
        };
        var snapshot = new CounterSnapshot(
            1,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(2),
            [last],
            [],
            []);

        var projection = BlindedDiagnosticToolGateway.ProjectCounterEvidence(
            snapshot,
            CounterEvidenceContractVersion.ProspectiveV2);

        var counter = projection.Evidence["counters"]!.AsArray().Single()!.AsObject();
        var lastSample = counter["lastSample"]!.AsObject();
        lastSample["available"]!.GetValue<bool>().Should().BeTrue();
        lastSample["value"].Should().BeNull();
        lastSample["actualIntervalSec"].Should().BeNull();
        lastSample["displayRateTimeScaleSeconds"].Should().BeNull();
        lastSample["metadataIssues"]!.AsArray().Select(value => value!.GetValue<string>())
            .Should().Equal("value-non-finite", "actualIntervalSec-invalid", "displayRateTimeScale-invalid");
        var maximum = counter["maximumRawSample"]!.AsObject();
        maximum["available"]!.GetValue<bool>().Should().BeFalse();
        maximum["value"].Should().BeNull();
        maximum["actualIntervalSec"].Should().BeNull();
        maximum["displayRateTimeScaleSeconds"].Should().BeNull();
        projection.Evidence.ToJsonString().Should().NotContain("Infinity").And.NotContain("NaN");
    }

    [Fact]
    public void ProspectiveV2_KeepsSystemRuntimeFilterSortAndCap()
    {
        var counters = Enumerable.Range(0, 65)
            .Select(index => new CounterValue(
                "System.Runtime",
                $"counter-{64 - index:D2}",
                $"Counter {index}",
                index,
                CounterKind.Mean))
            .Append(new CounterValue("Private.Provider", "secret", "Secret", 1, CounterKind.Mean))
            .ToArray();
        var snapshot = new CounterSnapshot(
            1,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(2),
            counters,
            [],
            []);

        var projection = BlindedDiagnosticToolGateway.ProjectCounterEvidence(
            snapshot,
            CounterEvidenceContractVersion.ProspectiveV2);

        var projected = projection.Evidence["counters"]!.AsArray();
        projected.Should().HaveCount(60);
        projected.Select(value => value!["name"]!.GetValue<string>())
            .Should().BeInAscendingOrder(StringComparer.Ordinal);
        projection.Evidence.ToJsonString().Should().NotContain("secret").And.NotContain("Private.Provider");
        projection.Evidence["omittedCounterCount"]!.GetValue<int>().Should().Be(6);
        projection.Truncated.Should().BeTrue();
    }

    private static CounterSnapshot Snapshot()
    {
        var last = new CounterValue(
            "System.Runtime",
            "requests",
            "Requests",
            12,
            CounterKind.Sum,
            "items")
        {
            IntervalSec = 2,
            DisplayRateTimeScale = TimeSpan.FromSeconds(1),
        };
        var maximum = last with
        {
            Value = 20,
            IntervalSec = 4,
            DisplayRateTimeScale = TimeSpan.FromMinutes(1),
        };
        return new CounterSnapshot(
            1,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromSeconds(6),
            [last],
            [],
            ["bounded"])
        {
            MaxCounters = [maximum],
        };
    }
}
