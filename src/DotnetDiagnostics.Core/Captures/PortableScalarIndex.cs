using System.Buffers.Binary;

namespace DotnetDiagnostics.Core.Captures;

/// <summary>Sorted fixed-width private scratch rows; cardinality does not become retained host metadata.</summary>
internal sealed class PortableScalarIndex(Stream rows, Stream values) : IDisposable
{
    private const int RowBytes = 40;
    private long _count;
    private long _last;

    internal void Add(long id, ReadOnlySpan<byte> text)
    {
        if (id <= _last) throw Invalid();
        var units = CapturePackage.Utf8.GetCharCount(text);
        Span<byte> row = stackalloc byte[RowBytes];
        row.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(row, id);
        BinaryPrimitives.WriteInt64LittleEndian(row[8..], values.Length);
        BinaryPrimitives.WriteInt32LittleEndian(row[16..], text.Length);
        BinaryPrimitives.WriteInt64LittleEndian(row[24..], checked(24 + units * 2L + text.Length));
        values.Position = values.Length;
        values.Write(text);
        rows.Position = _count * RowBytes;
        rows.Write(row);
        _count++;
        _last = id;
    }

    internal (long Cost, string? Text) Get(long id, bool materialize = false)
    {
        Span<byte> row = stackalloc byte[RowBytes];
        var location = Find(id, row);
        if (location < 0) throw Invalid();
        rows.Position = location + 32;
        rows.WriteByte(1);
        string? text = null;
        if (materialize)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(row[16..]);
            var bytes = new byte[length];
            values.Position = BinaryPrimitives.ReadInt64LittleEndian(row[8..]);
            values.ReadExactly(bytes);
            text = CapturePackage.Utf8.GetString(bytes);
        }
        return (BinaryPrimitives.ReadInt64LittleEndian(row[24..]), text);
    }

    internal void ValidateAllReferenced(CancellationToken token)
    {
        Span<byte> row = stackalloc byte[RowBytes];
        rows.Position = 0;
        for (long i = 0; i < _count; i++)
        {
            token.ThrowIfCancellationRequested();
            rows.ReadExactly(row);
            if (row[32] != 1) throw Invalid();
        }
    }

    private long Find(long id, Span<byte> row)
    {
        if (id <= 0) throw Invalid();
        long low = 0, high = _count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            rows.Position = middle * RowBytes;
            rows.ReadExactly(row);
            var current = BinaryPrimitives.ReadInt64LittleEndian(row);
            if (current == id) return middle * RowBytes;
            if (current < id) low = middle + 1;
            else high = middle - 1;
        }
        return -1;
    }

    public void Dispose()
    {
        rows.Dispose();
        values.Dispose();
    }

    private static CaptureStoreException Invalid() =>
        CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Data.StringReference: invalid, duplicate or unused scalar dimension.");
}
