namespace DotnetDiagnostics.Core.Internal;

internal sealed class BoundedPercentileSampler
{
    internal const int ExactSampleCapacity = 4096;

    private readonly List<double> _samples = new(ExactSampleCapacity);
    private readonly Random _random = new(0);

    public long Count { get; private set; }
    public bool IsApproximate { get; private set; }

    public void Add(double value)
    {
        Count++;

        if (_samples.Count < ExactSampleCapacity)
        {
            _samples.Add(value);
            return;
        }

        // Keep exact samples for the first few thousand commands, then switch to a fixed-size
        // reservoir sample so memory stays bounded for busy collection windows. The resulting p95
        // stays exact below the cap and becomes an unbiased approximation above it; MaxMs remains
        // exact because the aggregate tracks it separately.
        IsApproximate = true;
        var replacementIndex = _random.NextInt64(Count);
        if (replacementIndex < ExactSampleCapacity)
        {
            _samples[(int)replacementIndex] = value;
        }
    }

    public double GetPercentile(double percentile)
    {
        if (_samples.Count == 0)
        {
            return 0;
        }

        _samples.Sort();
        var percentileIndex = Math.Max(0, (int)Math.Ceiling(_samples.Count * percentile) - 1);
        return _samples[percentileIndex];
    }

    public double GetPercentile95() => GetPercentile(0.95);
}
