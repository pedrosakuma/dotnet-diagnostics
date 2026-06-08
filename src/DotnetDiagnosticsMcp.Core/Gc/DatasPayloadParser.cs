using System.Buffers.Binary;

namespace DotnetDiagnosticsMcp.Core.Gc;

/// <summary>
/// Decodes the little-endian packed payloads of the three DATAS <c>GCDynamicEvent</c> sub-events.
/// Layouts are version 1 (a <c>uint16</c> version prefix). Parsing never throws on a bad payload:
/// callers inspect the returned <see cref="DatasParseOutcome"/> and aggregate counts.
/// </summary>
public static class DatasPayloadParser
{
    public const string SampleEventName = "SizeAdaptationSample";
    public const string TuningEventName = "SizeAdaptationTuning";
    public const string FullGcTuningEventName = "SizeAdaptationFullGCTuning";

    public const ushort SupportedVersion = 1;
    public const int SampleLength = 38;
    public const int TuningLength = 56;
    public const int FullGcTuningLength = 44;

    public static DatasParseOutcome TryParseSample(
        ReadOnlySpan<byte> p, DateTimeOffset timestamp, out DatasSampleEvent? ev, out bool extraBytes)
    {
        ev = null;
        var outcome = Validate(p, SampleLength, out extraBytes);
        if (outcome != DatasParseOutcome.Decoded) return outcome;

        ev = new DatasSampleEvent(
            timestamp,
            GcIndex: BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(2, 8)),
            ElapsedBetweenGcsUs: BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(10, 4)),
            GcPauseTimeUs: BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(14, 4)),
            SohMslWaitUs: BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(18, 4)),
            UohMslWaitUs: BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(22, 4)),
            TotalSohStableSize: BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(26, 8)),
            Gen0BudgetPerHeap: BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(34, 4)));
        return DatasParseOutcome.Decoded;
    }

    public static DatasParseOutcome TryParseTuning(
        ReadOnlySpan<byte> p, DateTimeOffset timestamp, out DatasTuningEvent? ev, out bool extraBytes)
    {
        ev = null;
        var outcome = Validate(p, TuningLength, out extraBytes);
        if (outcome != DatasParseOutcome.Decoded) return outcome;

        ev = new DatasTuningEvent(
            timestamp,
            NewHeapCount: BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(2, 2)),
            MaxHeapCount: BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(4, 2)),
            MinHeapCount: BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(6, 2)),
            GcIndex: BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(8, 8)),
            TotalSohStableSize: BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(16, 8)),
            MedianThroughputCostPercent: BinaryPrimitives.ReadSingleLittleEndian(p.Slice(24, 4)),
            TcpToConsider: BinaryPrimitives.ReadSingleLittleEndian(p.Slice(28, 4)),
            CurrentAroundTargetAccumulation: BinaryPrimitives.ReadSingleLittleEndian(p.Slice(32, 4)),
            RecordedTcpCount: BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(36, 2)),
            RecordedTcpSlope: BinaryPrimitives.ReadSingleLittleEndian(p.Slice(38, 4)),
            NumGcsSinceLastChange: BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(42, 4)),
            AggFactor: p[46],
            ChangeDecision: BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(47, 2)),
            AdjustmentReason: BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(49, 2)),
            HeapCountChangeFreqFactor: BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(51, 2)),
            HeapCountFreqReason: BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(53, 2)),
            AdjustMetric: p[55]);
        return DatasParseOutcome.Decoded;
    }

    public static DatasParseOutcome TryParseFullGcTuning(
        ReadOnlySpan<byte> p, DateTimeOffset timestamp, out DatasFullGcTuningEvent? ev, out bool extraBytes)
    {
        ev = null;
        var outcome = Validate(p, FullGcTuningLength, out extraBytes);
        if (outcome != DatasParseOutcome.Decoded) return outcome;

        ev = new DatasFullGcTuningEvent(
            timestamp,
            NewHeapCount: BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(2, 2)),
            GcIndex: BinaryPrimitives.ReadUInt64LittleEndian(p.Slice(4, 8)),
            MedianGen2Tcp: BinaryPrimitives.ReadSingleLittleEndian(p.Slice(12, 4)),
            NumGen2sSinceLastChange: BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(16, 4)),
            Gen2Sample0Age: BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(20, 4)),
            Gen2Sample0Percent: BinaryPrimitives.ReadSingleLittleEndian(p.Slice(24, 4)),
            Gen2Sample1Age: BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(28, 4)),
            Gen2Sample1Percent: BinaryPrimitives.ReadSingleLittleEndian(p.Slice(32, 4)),
            Gen2Sample2Age: BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(36, 4)),
            Gen2Sample2Percent: BinaryPrimitives.ReadSingleLittleEndian(p.Slice(40, 4)));
        return DatasParseOutcome.Decoded;
    }

    private static DatasParseOutcome Validate(ReadOnlySpan<byte> p, int expectedLength, out bool extraBytes)
    {
        extraBytes = false;
        if (p.Length < 2)
        {
            return DatasParseOutcome.Malformed;
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(0, 2));
        if (version != SupportedVersion)
        {
            return DatasParseOutcome.UnsupportedVersion;
        }

        if (p.Length < expectedLength)
        {
            return DatasParseOutcome.Malformed;
        }

        extraBytes = p.Length > expectedLength;
        return DatasParseOutcome.Decoded;
    }
}

/// <summary>Result of attempting to decode a single DATAS payload.</summary>
public enum DatasParseOutcome
{
    Decoded,
    Malformed,
    UnsupportedVersion,
}
