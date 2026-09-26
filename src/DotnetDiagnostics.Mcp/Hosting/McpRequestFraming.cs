using System.Text.Json;

namespace DotnetDiagnostics.Mcp.Hosting;

/// <summary>Byte admission before the SDK materializes JSON-RPC messages or tool arguments.</summary>
internal static class McpRequestFraming
{
    internal const int MaximumFrameBytes = 1024 * 1024;
    internal const int MaximumTransferRequestBytes = 64 * 1024;
    internal const int CopyBufferBytes = 64 * 1024;

    internal static void Validate(ReadOnlySpan<byte> frame)
    {
        if (frame.Length > MaximumFrameBytes)
            throw new InvalidDataException("MCP request exceeds the 1 MiB transport frame limit.");
        if (frame.Length <= MaximumTransferRequestBytes)
            return;

        // A token scan allocates no argument strings/arrays. Recognize the containing
        // tool/action without mistaking similarly named data in another tool for a transfer.
        var reader = new Utf8JsonReader(frame, new JsonReaderOptions { MaxDepth = 64 });
        var inParameters = false;
        var inArguments = false;
        var getBytes = false;
        var captureAction = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (reader.CurrentDepth == 1) inParameters = false;
                if (reader.CurrentDepth == 2) inArguments = false;
            }
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            if (reader.CurrentDepth == 1 && reader.ValueTextEquals("params"u8))
                inParameters = reader.Read() && reader.TokenType == JsonTokenType.StartObject;
            else if (inParameters && reader.CurrentDepth == 2 && reader.ValueTextEquals("name"u8))
                getBytes |= reader.Read() && reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("get_bytes"u8);
            else if (inParameters && reader.CurrentDepth == 2 && reader.ValueTextEquals("arguments"u8))
                inArguments = reader.Read() && reader.TokenType == JsonTokenType.StartObject;
            else if (inArguments && reader.CurrentDepth == 3 && reader.ValueTextEquals("captureAction"u8))
                captureAction = true;
        }
        if (getBytes && captureAction)
            throw new InvalidDataException("Capture requests exceed the 64 KiB encoded request limit.");
    }
}

/// <summary>Bounds complete newline-delimited messages before the SDK's line reader sees them.</summary>
internal sealed class BoundedMcpInputStream(Stream input) : Stream
{
    private readonly byte[] _buffer = new byte[McpRequestFraming.CopyBufferBytes];
    private readonly MemoryStream _frame = new(McpRequestFraming.CopyBufferBytes);
    private int _offset;
    private int _count;
    private bool _eof;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        if (_frame.Position == _frame.Length)
        {
            _frame.SetLength(0);
            while (true)
            {
                if (_offset == _count && !_eof)
                {
                    _count = await input.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                    _offset = 0;
                    _eof = _count == 0;
                }
                if (_eof) break;
                var newline = Array.IndexOf(_buffer, (byte)'\n', _offset, _count - _offset);
                var length = newline < 0 ? _count - _offset : newline - _offset + 1;
                if (_frame.Length + length > McpRequestFraming.MaximumFrameBytes)
                    throw new InvalidDataException("MCP request exceeds the 1 MiB transport frame limit.");
                _frame.Write(_buffer, _offset, length);
                _offset += length;
                if (newline >= 0) break;
            }
            McpRequestFraming.Validate(_frame.GetBuffer().AsSpan(0, checked((int)_frame.Length)));
            _frame.Position = 0;
        }
        return _frame.Read(buffer.Span);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _frame.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Applies the same pre-parser limits to length-delimited and chunked HTTP bodies.</summary>
internal sealed class McpRequestFramingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp") || !HttpMethods.IsPost(context.Request.Method))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (context.Request.ContentLength > McpRequestFraming.MaximumFrameBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        using var frame = new MemoryStream(McpRequestFraming.CopyBufferBytes);
        var buffer = new byte[McpRequestFraming.CopyBufferBytes];
        try
        {
            while (true)
            {
                var remaining = McpRequestFraming.MaximumFrameBytes - checked((int)frame.Length);
                var count = await context.Request.Body.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)),
                    context.RequestAborted).ConfigureAwait(false);
                if (count == 0) break;
                if (count > remaining)
                    throw new InvalidDataException("MCP request exceeds the transport frame limit.");
                frame.Write(buffer, 0, count);
            }
            McpRequestFraming.Validate(frame.GetBuffer().AsSpan(0, checked((int)frame.Length)));
        }
        catch (InvalidDataException)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        catch (JsonException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var original = context.Request.Body;
        frame.Position = 0;
        context.Request.Body = frame;
        try { await next(context).ConfigureAwait(false); }
        finally { context.Request.Body = original; }
    }
}
