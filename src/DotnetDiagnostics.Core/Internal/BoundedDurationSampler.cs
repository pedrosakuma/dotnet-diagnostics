namespace DotnetDiagnostics.Core.Internal;

internal sealed class BoundedDurationSampler
{
    private readonly BoundedPercentileSampler _samplesMs = new();

    public long Count => _samplesMs.Count;
    public bool IsApproximate => _samplesMs.IsApproximate;
    public TimeSpan Max { get; private set; }

    public void Add(TimeSpan duration)
    {
        var clamped = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        _samplesMs.Add(clamped.TotalMilliseconds);
        if (clamped > Max)
        {
            Max = clamped;
        }
    }

    public TimeSpan GetPercentile(double percentile)
        => TimeSpan.FromMilliseconds(_samplesMs.GetPercentile(percentile));
}
