using System.Buffers.Binary;
using DotnetDiagnostics.Core.Gc;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class DatasPayloadParserTests
{
    private static readonly DateTimeOffset Ts = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryParseSample_DecodesAllFields()
    {
        var p = new byte[DatasPayloadParser.SampleLength];
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(0, 2), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(2, 8), 42UL);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(10, 4), 1000U);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(14, 4), 250U);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(18, 4), 5U);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(22, 4), 7U);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(26, 8), 123_456UL);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(34, 4), 99U);

        var outcome = DatasPayloadParser.TryParseSample(p, Ts, out var ev, out var extra);

        outcome.Should().Be(DatasParseOutcome.Decoded);
        extra.Should().BeFalse();
        ev!.GcIndex.Should().Be(42UL);
        ev.ElapsedBetweenGcsUs.Should().Be(1000U);
        ev.GcPauseTimeUs.Should().Be(250U);
        ev.SohMslWaitUs.Should().Be(5U);
        ev.UohMslWaitUs.Should().Be(7U);
        ev.TotalSohStableSize.Should().Be(123_456UL);
        ev.Gen0BudgetPerHeap.Should().Be(99U);
        ev.ThroughputCostPercent.Should().BeApproximately(250.0 * 100.0 / 1250.0, 1e-9);
    }

    [Fact]
    public void TryParseSample_ZeroElapsedAndPause_ThroughputCostIsZero()
    {
        var p = new byte[DatasPayloadParser.SampleLength];
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(0, 2), 1);
        // all other fields left at zero

        var outcome = DatasPayloadParser.TryParseSample(p, Ts, out var ev, out _);

        outcome.Should().Be(DatasParseOutcome.Decoded);
        ev!.ThroughputCostPercent.Should().Be(0);
    }

    [Fact]
    public void TryParseTuning_DecodesAllFields()
    {
        var p = new byte[DatasPayloadParser.TuningLength];
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(0, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2, 2), 8);   // new
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4, 2), 16);  // max
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6, 2), 2);   // min
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(8, 8), 100UL);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(16, 8), 9_000UL);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(24, 4), 3.5f);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(28, 4), 4.0f);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(32, 4), 1.25f);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(36, 2), 6);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(38, 4), -0.5f);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(42, 4), 12U);
        p[46] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(47, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(49, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(51, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(53, 2), 4);
        p[55] = 5;

        var outcome = DatasPayloadParser.TryParseTuning(p, Ts, out var ev, out var extra);

        outcome.Should().Be(DatasParseOutcome.Decoded);
        extra.Should().BeFalse();
        ev!.NewHeapCount.Should().Be(8);
        ev.MaxHeapCount.Should().Be(16);
        ev.MinHeapCount.Should().Be(2);
        ev.GcIndex.Should().Be(100UL);
        ev.TotalSohStableSize.Should().Be(9_000UL);
        ev.MedianThroughputCostPercent.Should().Be(3.5f);
        ev.TcpToConsider.Should().Be(4.0f);
        ev.CurrentAroundTargetAccumulation.Should().Be(1.25f);
        ev.RecordedTcpCount.Should().Be(6);
        ev.RecordedTcpSlope.Should().Be(-0.5f);
        ev.NumGcsSinceLastChange.Should().Be(12U);
        ev.AggFactor.Should().Be(3);
        ev.ChangeDecision.Should().Be(1);
        ev.AdjustmentReason.Should().Be(2);
        ev.HeapCountChangeFreqFactor.Should().Be(3);
        ev.HeapCountFreqReason.Should().Be(4);
        ev.AdjustMetric.Should().Be(5);
    }

    [Fact]
    public void TryParseFullGcTuning_DecodesAllFields()
    {
        var p = new byte[DatasPayloadParser.FullGcTuningLength];
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(0, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2, 2), 10);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(4, 8), 200UL);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(12, 4), 2.5f);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(16, 4), 4U);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(20, 4), 1U);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(24, 4), 1.1f);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(28, 4), 2U);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(32, 4), 2.2f);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(36, 4), 3U);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(40, 4), 3.3f);

        var outcome = DatasPayloadParser.TryParseFullGcTuning(p, Ts, out var ev, out _);

        outcome.Should().Be(DatasParseOutcome.Decoded);
        ev!.NewHeapCount.Should().Be(10);
        ev.GcIndex.Should().Be(200UL);
        ev.MedianGen2Tcp.Should().Be(2.5f);
        ev.NumGen2sSinceLastChange.Should().Be(4U);
        ev.Gen2Sample0Age.Should().Be(1U);
        ev.Gen2Sample0Percent.Should().Be(1.1f);
        ev.Gen2Sample1Age.Should().Be(2U);
        ev.Gen2Sample1Percent.Should().Be(2.2f);
        ev.Gen2Sample2Age.Should().Be(3U);
        ev.Gen2Sample2Percent.Should().Be(3.3f);
    }

    [Fact]
    public void TryParse_TooShort_IsMalformed()
    {
        var outcome = DatasPayloadParser.TryParseSample(new byte[] { 1 }, Ts, out var ev, out _);
        outcome.Should().Be(DatasParseOutcome.Malformed);
        ev.Should().BeNull();
    }

    [Fact]
    public void TryParse_ShorterThanExpectedButVersioned_IsMalformed()
    {
        var p = new byte[DatasPayloadParser.SampleLength - 1];
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(0, 2), 1);

        var outcome = DatasPayloadParser.TryParseSample(p, Ts, out var ev, out _);

        outcome.Should().Be(DatasParseOutcome.Malformed);
        ev.Should().BeNull();
    }

    [Fact]
    public void TryParse_WrongVersion_IsUnsupportedVersion()
    {
        var p = new byte[DatasPayloadParser.SampleLength];
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(0, 2), 2);

        var outcome = DatasPayloadParser.TryParseSample(p, Ts, out var ev, out _);

        outcome.Should().Be(DatasParseOutcome.UnsupportedVersion);
        ev.Should().BeNull();
    }

    [Fact]
    public void TryParse_LongerThanExpected_DecodesAndFlagsExtraBytes()
    {
        var p = new byte[DatasPayloadParser.SampleLength + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(0, 2), 1);

        var outcome = DatasPayloadParser.TryParseSample(p, Ts, out var ev, out var extra);

        outcome.Should().Be(DatasParseOutcome.Decoded);
        extra.Should().BeTrue();
        ev.Should().NotBeNull();
    }
}
