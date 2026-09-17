namespace DotnetDiagnostics.Core.Dump;

// TraceEvent reads synchronously. Issue cancellable reads on the owned EventPipe stream so
// forced shutdown can quiesce the reader before EventPipeSession.Dispose closes its pipe.
internal sealed class GcDumpFlushReadStream(Stream inner, CancellationToken readCancellation) : Stream
{
    public override int Read(byte[] buffer, int offset, int count) =>
        inner.ReadAsync(buffer, offset, count, readCancellation).GetAwaiter().GetResult();

    // EventPipeSession, not EventPipeEventSource, owns the underlying transport.
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
