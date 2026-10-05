using System.Threading.Channels;

namespace DotnetDiagnostics.Core.Counters;

internal sealed class DiagnosticSessionEventBuffer
{
    private readonly Channel<DiagnosticSessionEvent> _channel;
    private readonly object _publishLock = new();
    private long _sequence;
    private long _droppedObservations;

    public DiagnosticSessionEventBuffer(int capacity)
    {
        _channel = Channel.CreateBounded<DiagnosticSessionEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    public long DroppedObservations => Interlocked.Read(ref _droppedObservations);

    public IAsyncEnumerable<DiagnosticSessionEvent> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public bool TryPublish(Func<long, DiagnosticSessionEvent> createEvent)
    {
        lock (_publishLock)
        {
            var sequence = ++_sequence;
            if (_channel.Writer.TryWrite(createEvent(sequence)))
            {
                return true;
            }

            Interlocked.Increment(ref _droppedObservations);
            return false;
        }
    }

    public void Complete() => _channel.Writer.TryComplete();
}
