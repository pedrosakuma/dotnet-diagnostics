using System.Buffers;

namespace DotnetDiagnostics.Core.Captures;

/// <summary>Bounds both committed JSON bytes and the writer's escaping workspace.</summary>
internal sealed class BoundedSnapshotEncoding(int maxBytes) : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer = [];
    private int _written;

    public void Advance(int count)
    {
        if (count < 0 || count > maxBytes - _written)
            throw new InvalidDataException($"Capture snapshot exceeds maxBytes={maxBytes}.");
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void EnsureCapacity(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        sizeHint = Math.Max(sizeHint, 1);
        // Utf8JsonWriter reserves the worst-case six-byte escape per UTF-16 character,
        // even for strings that ultimately fit. Reservation is bounded independently;
        // Advance still rejects the actual encoded output at maxBytes, never after copying.
        var workspaceLimit = Math.Min(Array.MaxLength, (long)maxBytes * 6 + 4096);
        var required = (long)_written + sizeHint;
        if (required > workspaceLimit)
            throw new InvalidDataException($"Capture snapshot escaping workspace exceeds the maxBytes={maxBytes} budget.");
        if (required <= _buffer.Length) return;
        var capacity = (int)Math.Min(workspaceLimit, Math.Max(required, Math.Max(256L, (long)_buffer.Length * 2)));
        Array.Resize(ref _buffer, capacity);
    }

    internal byte[] ToArray() => _buffer.AsSpan(0, _written).ToArray();
    internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public void Dispose()
    {
        // Snapshots can contain parameter previews, logs, and request strings.
        Array.Clear(_buffer);
        _buffer = [];
    }
}
