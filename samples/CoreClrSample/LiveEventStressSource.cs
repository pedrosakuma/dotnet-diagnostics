using System.Diagnostics.Tracing;

[EventSource(Name = "CoreClrSample.LiveEventStress")]
internal sealed class LiveEventStressSource : EventSource
{
    public static LiveEventStressSource Log { get; } = new();

    [Event(1, Level = EventLevel.Informational)]
    public void Generated(long sequence, string payload)
    {
        if (IsEnabled())
        {
            WriteEvent(1, sequence, payload);
        }
    }
}
