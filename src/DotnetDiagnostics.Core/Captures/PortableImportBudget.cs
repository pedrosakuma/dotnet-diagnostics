using DotnetDiagnostics.Core.Artifacts;

namespace DotnetDiagnostics.Core.Captures;

internal sealed class PortableImportBudget(PortableCaptureStorage storage)
{
    private long _reserved = storage.Receipt.ReservationBytes - PortableBounds.ReceiptReservation;

    internal void Reserve(long bytes)
    {
        var next = checked(_reserved + bytes);
        storage.Reserve(next);
        _reserved = next;
    }

    internal Stream Create(string path, long maximum, bool reserved = false)
    {
        var initial = reserved ? maximum : Math.Min(maximum, 1024 * 1024);
        if (!reserved) Reserve(initial);
        using (SafeArtifactPath.CreateRestrictedFile(path)) { }
        CapturePackage.RejectLinks(path);
        return new ChargedStream(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 1), maximum, this, initial);
    }

    internal void PublishedLocked(long reservation)
    {
        storage.ReduceReservationLocked(reservation);
        _reserved -= reservation;
    }

    private sealed class ChargedStream(FileStream file, long maximum, PortableImportBudget budget, long initial) : Stream
    {
        private long _charged = initial;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => file.Length;
        public override long Position { get => file.Position; set => file.Position = value; }
        public override void Flush() => file.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => file.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => file.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => file.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            file.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => file.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Charge(buffer.Length);
            file.Write(buffer);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Charge(buffer.Length);
            return file.WriteAsync(buffer, cancellationToken);
        }
        private void Charge(int bytes)
        {
            var end = checked(file.Position + bytes);
            PortableBounds.Check("ImportFileBytes", end, maximum);
            if (end <= _charged) return;
            var next = Math.Min(maximum, checked((end + 1024 * 1024 - 1) / (1024 * 1024) * (1024 * 1024)));
            budget.Reserve(next - _charged);
            _charged = next;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) file.Dispose();
            base.Dispose(disposing);
        }
    }
}
