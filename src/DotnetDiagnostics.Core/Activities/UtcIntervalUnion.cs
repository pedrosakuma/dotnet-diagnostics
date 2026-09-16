namespace DotnetDiagnostics.Core.Activities;

/// <summary>Union of completed child intervals clipped to a parent, using absolute UTC instants.</summary>
internal static class UtcIntervalUnion
{
    internal static (long CoveredTicks, int Clipped, int Disjoint) Measure(
        DateTimeOffset parentStart,
        DateTimeOffset parentStop,
        IEnumerable<(DateTimeOffset Start, DateTimeOffset Stop)> children)
    {
        var intervals = new List<(long Start, long Stop)>();
        var clipped = 0;
        var disjoint = 0;
        foreach (var (start, stop) in children)
        {
            if (stop < start)
            {
                continue;
            }

            var left = Math.Max(parentStart.UtcTicks, start.UtcTicks);
            var right = Math.Min(parentStop.UtcTicks, stop.UtcTicks);
            if (left != start.UtcTicks || right != stop.UtcTicks)
            {
                clipped++;
            }

            if (right <= left)
            {
                if (stop > start || start < parentStart || stop > parentStop)
                {
                    disjoint++;
                }
                continue;
            }

            intervals.Add((left, right));
        }

        intervals.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        long covered = 0;
        long mergedStop = long.MinValue;
        foreach (var (start, stop) in intervals)
        {
            covered += Math.Max(0, stop - Math.Max(start, mergedStop));
            mergedStop = Math.Max(mergedStop, stop);
        }

        return (covered, clipped, disjoint);
    }
}
