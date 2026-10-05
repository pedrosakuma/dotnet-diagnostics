using System.Threading.Channels;

namespace DotnetDiagnostics.Core.Counters;

internal sealed class CounterObservationBuffer
{
    private readonly Channel<CounterObservation> _channel;
    private long _sequence;
    private long _droppedObservations;

    public CounterObservationBuffer(int capacity)
    {
        _channel = Channel.CreateBounded<CounterObservation>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
    }

    public long DroppedObservations => Interlocked.Read(ref _droppedObservations);

    public IAsyncEnumerable<CounterObservation> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public bool TryPublish(DateTimeOffset timestamp, CounterValue counter)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        if (_channel.Writer.TryWrite(new CounterObservation(sequence, timestamp, counter)))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedObservations);
        return false;
    }

    public void Complete() => _channel.Writer.TryComplete();
}
