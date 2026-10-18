using System.Threading.Channels;

namespace DotnetDiagnostics.Core.Counters;

internal sealed class DiagnosticSessionEventBuffer
{
    private readonly Channel<DiagnosticSessionEvent> _channel;
    private readonly long _retainedBytesCapacity;
    private readonly object _publishLock = new();
    private long _sequence;
    private long _droppedObservations;
    private long _retainedBytes;

    public DiagnosticSessionEventBuffer(int capacity, long retainedBytesCapacity = long.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedBytesCapacity);
        _retainedBytesCapacity = retainedBytesCapacity;
        _channel = Channel.CreateBounded<DiagnosticSessionEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    public long DroppedObservations => Interlocked.Read(ref _droppedObservations);

    public async IAsyncEnumerable<DiagnosticSessionEvent> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var sessionEvent in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                yield return sessionEvent;
            }
            finally
            {
                Interlocked.Add(ref _retainedBytes, -sessionEvent.RetainedSizeBytes);
            }
        }
    }

    public bool TryPublish(Func<long, DiagnosticSessionEvent> createEvent)
    {
        lock (_publishLock)
        {
            var sequence = ++_sequence;
            var sessionEvent = createEvent(sequence);
            var retainedBytes = sessionEvent.RetainedSizeBytes;
            if (retainedBytes <= _retainedBytesCapacity
                && Interlocked.Read(ref _retainedBytes) + retainedBytes <= _retainedBytesCapacity)
            {
                Interlocked.Add(ref _retainedBytes, retainedBytes);
                if (_channel.Writer.TryWrite(sessionEvent))
                {
                    return true;
                }

                Interlocked.Add(ref _retainedBytes, -retainedBytes);
            }

            Interlocked.Increment(ref _droppedObservations);
            return false;
        }
    }

    public void Complete() => _channel.Writer.TryComplete();
}
