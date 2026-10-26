using System.Globalization;
using System.Text;
using DotnetDiagnostics.Core.Dump;

namespace DotnetDiagnostics.Core.Tests.HeapFragmentationSpike;

internal enum HeapLayoutRangeKind
{
    Used,
    Free,
}

internal enum HeapLayoutEvidenceState
{
    Complete,
    Partial,
    Indeterminate,
}

internal sealed record HeapLayoutRange(ulong Start, long Length, HeapLayoutRangeKind Kind);

internal sealed record HeapLayoutObservation(
    string Name,
    ulong SegmentStart,
    long SegmentLength,
    HeapLayoutEvidenceState EvidenceState,
    IReadOnlyList<HeapLayoutRange> Ranges,
    long PinnedRootCount = 0);

internal sealed record HeapLayoutFixture(
    HeapLayoutObservation Observation,
    IReadOnlyList<ulong>? GroundTruthPinnedObjectAddresses = null);

internal sealed record ProportionalHeapStrip(
    int Width,
    int UsedCells,
    int FreeCells,
    int UnknownCells,
    string Cells,
    long UsedBytes,
    long FreeBytes,
    long UnknownBytes);

internal sealed record FreeRangeProjection(ulong Start, long Length);

internal sealed record DetailedFreeRangeProjection(
    int RetentionCapacity,
    int RetentionStorageSlots,
    int PeakRetainedRangeCount,
    long TopKSelectionComparisonCount,
    long TopKSelectionComparisonUpperBound,
    long ObservedRangeCount,
    int RetainedRangeCount,
    long OmittedRangeCount,
    long LargestObservedFreeBlockBytes,
    IReadOnlyList<FreeRangeProjection> TopFreeRanges);

internal sealed record HeapLayoutAccounting(
    HeapLayoutEvidenceState EvidenceState,
    long SegmentBytes,
    long AccountedBytes,
    long UnknownBytes,
    string State);

internal sealed record PinningProjection(
    long PinnedRootCount,
    long? PinnedBytes,
    string? Concentration,
    string Limitation);

internal sealed record HeapLayoutConclusionContract(
    bool SupportsObjectLiveness,
    bool SupportsRetainedObjectSize,
    string Limitation);

internal sealed record HeapFragmentationEvidenceComparison(
    string Fixture,
    SegmentStat CurrentAggregate,
    ProportionalHeapStrip Proportional,
    DetailedFreeRangeProjection Detailed,
    HeapLayoutAccounting Accounting,
    PinningProjection Pinning,
    HeapLayoutConclusionContract Conclusions)
{
    internal string Render()
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"fixture: {Fixture}");
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"current aggregate: used={CurrentAggregate.UsedBytes}, free={CurrentAggregate.FreeBytes}, " +
            $"freeObjects={CurrentAggregate.FreeObjectCount}, freePercent={CurrentAggregate.FreePercent:0.##}");
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"proportional: [{Proportional.Cells}] used={Proportional.UsedBytes}, " +
            $"free={Proportional.FreeBytes}, unknown={Proportional.UnknownBytes}");
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"detailed: largestFree={Detailed.LargestObservedFreeBlockBytes}, " +
            $"ranges observed={Detailed.ObservedRangeCount}, retained={Detailed.RetainedRangeCount}, " +
            $"omitted={Detailed.OmittedRangeCount}, " +
            $"topKComparisons={Detailed.TopKSelectionComparisonCount}/" +
            $"{Detailed.TopKSelectionComparisonUpperBound}");
        foreach (var range in Detailed.TopFreeRanges)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"  free 0x{range.Start:x}+{range.Length}");
        }

        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"accounting: state={Accounting.State}, accounted={Accounting.AccountedBytes}, " +
            $"unknown={Accounting.UnknownBytes}");
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"pinning: roots={Pinning.PinnedRootCount}, bytes={FormatUnavailable(Pinning.PinnedBytes)}, " +
            $"concentration={Pinning.Concentration ?? "unavailable"}");
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"claims: objectLiveness={FormatSupport(Conclusions.SupportsObjectLiveness)}, " +
            $"retainedObjectSize={FormatSupport(Conclusions.SupportsRetainedObjectSize)}");
        return builder.ToString();
    }

    private static string FormatUnavailable(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";

    private static string FormatSupport(bool supported) => supported ? "supported" : "unavailable";
}

internal static class HeapFragmentationEvidenceHarness
{
    internal static HeapFragmentationEvidenceComparison Compare(
        HeapLayoutObservation observation,
        int topK,
        int stripWidth = 20)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentOutOfRangeException.ThrowIfNegative(topK);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stripWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(observation.SegmentLength);

        var segmentEnd = checked(observation.SegmentStart + (ulong)observation.SegmentLength);
        var retainer = new BoundedFreeRangeRetainer(topK);
        long usedBytes = 0;
        long freeBytes = 0;
        long objectCount = 0;
        long freeObjectCount = 0;
        var previousEnd = observation.SegmentStart;

        foreach (var range in observation.Ranges)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(range.Length);
            var rangeEnd = checked(range.Start + (ulong)range.Length);
            if (range.Start < previousEnd || range.Start < observation.SegmentStart || rangeEnd > segmentEnd)
            {
                throw new ArgumentException(
                    "Ranges must be address-ordered, non-overlapping, and contained by the segment.",
                    nameof(observation));
            }

            previousEnd = rangeEnd;
            if (range.Kind == HeapLayoutRangeKind.Free)
            {
                freeBytes = checked(freeBytes + range.Length);
                freeObjectCount++;
                retainer.Observe(new FreeRangeProjection(range.Start, range.Length));
            }
            else
            {
                usedBytes = checked(usedBytes + range.Length);
                objectCount++;
            }
        }

        var accountedBytes = checked(usedBytes + freeBytes);
        if (accountedBytes > observation.SegmentLength)
        {
            throw new ArgumentException("Observed ranges exceed the segment length.", nameof(observation));
        }

        var unknownBytes = observation.SegmentLength - accountedBytes;
        var aggregate = new SegmentStat(
            LogicalHeap: 0,
            Kind: "Generation2",
            Generation: "Gen2",
            Start: observation.SegmentStart,
            End: segmentEnd,
            Length: observation.SegmentLength,
            CommittedBytes: observation.SegmentLength,
            ReservedBytes: 0,
            UsedBytes: usedBytes,
            FreeBytes: freeBytes,
            ObjectCount: objectCount,
            FreeObjectCount: freeObjectCount)
        {
            FreePercent = Math.Round(100.0 * freeBytes / observation.SegmentLength, 2),
        };
        var strip = BuildStrip(stripWidth, usedBytes, freeBytes, unknownBytes);
        var retained = retainer.Snapshot();
        var detailed = new DetailedFreeRangeProjection(
            topK,
            retainer.StorageSlotCount,
            retainer.PeakRetainedCount,
            retainer.SelectionComparisonCount,
            checked(retainer.ObservedCount * topK),
            retainer.ObservedCount,
            retained.Count,
            retainer.ObservedCount - retained.Count,
            retainer.LargestObserved?.Length ?? 0,
            retained);
        var accountingState = observation.EvidenceState switch
        {
            HeapLayoutEvidenceState.Complete when unknownBytes == 0 => "complete",
            HeapLayoutEvidenceState.Partial => "partial",
            _ => "indeterminate",
        };

        return new HeapFragmentationEvidenceComparison(
            observation.Name,
            aggregate,
            strip,
            detailed,
            new HeapLayoutAccounting(
                observation.EvidenceState,
                observation.SegmentLength,
                accountedBytes,
                unknownBytes,
                accountingState),
            new PinningProjection(
                observation.PinnedRootCount,
                PinnedBytes: null,
                Concentration: null,
                "Pinned-root counts are not correlated with segment addresses; pinned bytes and concentration are unavailable."),
            new HeapLayoutConclusionContract(
                SupportsObjectLiveness: false,
                SupportsRetainedObjectSize: false,
                "Free-object layout does not establish object liveness or retained object size."));
    }

    private static ProportionalHeapStrip BuildStrip(
        int width,
        long usedBytes,
        long freeBytes,
        long unknownBytes)
    {
        var totalBytes = checked(usedBytes + freeBytes + unknownBytes);
        var values = new[] { usedBytes, freeBytes, unknownBytes };
        var cells = new int[values.Length];
        var remainders = new decimal[values.Length];
        var allocated = 0;

        for (var index = 0; index < values.Length; index++)
        {
            var exact = (decimal)values[index] * width / totalBytes;
            cells[index] = (int)decimal.Floor(exact);
            remainders[index] = exact - cells[index];
            allocated += cells[index];
        }

        while (allocated < width)
        {
            var winner = 0;
            for (var index = 1; index < remainders.Length; index++)
            {
                if (remainders[index] > remainders[winner])
                {
                    winner = index;
                }
            }

            cells[winner]++;
            remainders[winner] = -1;
            allocated++;
        }

        var rendered = string.Concat(
            new string('U', cells[0]),
            new string('F', cells[1]),
            new string('?', cells[2]));
        return new ProportionalHeapStrip(
            width,
            cells[0],
            cells[1],
            cells[2],
            rendered,
            usedBytes,
            freeBytes,
            unknownBytes);
    }
}

internal sealed class BoundedFreeRangeRetainer
{
    private readonly FreeRangeProjection[] _retained;
    private int _count;

    internal BoundedFreeRangeRetainer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _retained = new FreeRangeProjection[capacity];
    }

    internal int StorageSlotCount => _retained.Length;
    internal int PeakRetainedCount { get; private set; }
    internal long ObservedCount { get; private set; }
    internal long SelectionComparisonCount { get; private set; }
    internal FreeRangeProjection? LargestObserved { get; private set; }

    internal void Observe(FreeRangeProjection range)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(range.Length);
        ObservedCount++;

        if (LargestObserved is null || IsBetter(range, LargestObserved))
        {
            LargestObserved = range;
        }

        if (_retained.Length == 0)
        {
            return;
        }

        if (_count < _retained.Length)
        {
            _retained[_count] = range;
            BubbleTowardFront(_count);
            _count++;
            PeakRetainedCount = Math.Max(PeakRetainedCount, _count);
            return;
        }

        if (!IsBetterForRetention(range, _retained[_count - 1]))
        {
            return;
        }

        _retained[_count - 1] = range;
        BubbleTowardFront(_count - 1);
    }

    internal IReadOnlyList<FreeRangeProjection> Snapshot()
    {
        var result = new FreeRangeProjection[_count];
        Array.Copy(_retained, result, _count);
        return result;
    }

    private void BubbleTowardFront(int index)
    {
        while (index > 0 && IsBetterForRetention(_retained[index], _retained[index - 1]))
        {
            (_retained[index], _retained[index - 1]) = (_retained[index - 1], _retained[index]);
            index--;
        }
    }

    private bool IsBetterForRetention(FreeRangeProjection candidate, FreeRangeProjection existing)
    {
        SelectionComparisonCount++;
        return IsBetter(candidate, existing);
    }

    private static bool IsBetter(FreeRangeProjection candidate, FreeRangeProjection existing) =>
        candidate.Length > existing.Length
        || (candidate.Length == existing.Length && candidate.Start < existing.Start);
}

internal static class KnownHeapLayoutFixtures
{
    private const ulong Start = 0x1000;

    internal static HeapLayoutFixture NonFragmented()
    {
        var ranges = new List<HeapLayoutRange>(11);
        var address = Start;
        for (var index = 0; index < 10; index++)
        {
            ranges.Add(new HeapLayoutRange(address, 80, HeapLayoutRangeKind.Used));
            address += 80;
        }

        ranges.Add(new HeapLayoutRange(address, 200, HeapLayoutRangeKind.Free));
        return new HeapLayoutFixture(new HeapLayoutObservation(
            "non-fragmented-free-tail-control",
            Start,
            1_000,
            HeapLayoutEvidenceState.Complete,
            ranges));
    }

    internal static HeapLayoutFixture Fragmented()
    {
        var ranges = new List<HeapLayoutRange>(20);
        var address = Start;
        for (var index = 0; index < 10; index++)
        {
            ranges.Add(new HeapLayoutRange(address, 80, HeapLayoutRangeKind.Used));
            address += 80;
            ranges.Add(new HeapLayoutRange(address, 20, HeapLayoutRangeKind.Free));
            address += 20;
        }

        return new HeapLayoutFixture(new HeapLayoutObservation(
            "many-small-holes",
            Start,
            1_000,
            HeapLayoutEvidenceState.Complete,
            ranges));
    }

    internal static HeapLayoutFixture Partial()
    {
        return new HeapLayoutFixture(new HeapLayoutObservation(
            "partial-walk",
            Start,
            1_000,
            HeapLayoutEvidenceState.Partial,
            [
                new HeapLayoutRange(Start, 400, HeapLayoutRangeKind.Used),
                new HeapLayoutRange(Start + 400, 100, HeapLayoutRangeKind.Free),
                new HeapLayoutRange(Start + 500, 100, HeapLayoutRangeKind.Used),
                new HeapLayoutRange(Start + 600, 50, HeapLayoutRangeKind.Free),
            ]));
    }

    internal static HeapLayoutFixture PinnedConcentrated()
    {
        return Pinned(
            "pinned-concentrated-ground-truth",
            [Start + 8, Start + 16, Start + 24, Start + 32]);
    }

    internal static HeapLayoutFixture PinnedDispersed()
    {
        return Pinned(
            "pinned-dispersed-ground-truth",
            [Start + 8, Start + 208, Start + 408, Start + 608]);
    }

    internal static HeapLayoutObservation HighHoleCount(int holeCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(holeCount);
        var ranges = new List<HeapLayoutRange>(holeCount * 2);
        var address = Start;
        for (var index = 0; index < holeCount; index++)
        {
            ranges.Add(new HeapLayoutRange(address, 1, HeapLayoutRangeKind.Used));
            address++;
            ranges.Add(new HeapLayoutRange(address, index + 1L, HeapLayoutRangeKind.Free));
            address += (ulong)(index + 1L);
        }

        return new HeapLayoutObservation(
            $"high-hole-count-{holeCount}",
            Start,
            checked((long)(address - Start)),
            HeapLayoutEvidenceState.Complete,
            ranges);
    }

    internal static HeapLayoutObservation CurrentLiveContractIndeterminate()
    {
        return Partial().Observation with
        {
            Name = "current-live-contract-without-completion-signal",
            EvidenceState = HeapLayoutEvidenceState.Indeterminate,
        };
    }

    private static HeapLayoutFixture Pinned(string name, IReadOnlyList<ulong> groundTruth)
    {
        var observation = Fragmented().Observation with
        {
            Name = name,
            PinnedRootCount = groundTruth.Count,
        };
        return new HeapLayoutFixture(observation, groundTruth);
    }
}
