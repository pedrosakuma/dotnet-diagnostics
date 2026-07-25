using System.Text;

namespace DotnetDiagnostics.Core.Threads;

internal static class ThreadSnapshotCursorCodec
{
    private const byte Version = 1;
    private const int MaxEncodedLength = 1024;
    private const byte ThreadKind = 1;
    private const byte LockKind = 2;
    private const byte WaiterKind = 3;

    internal readonly record struct ThreadCursor(
        bool BlockedOnly,
        bool UsedFallback,
        int Position,
        int Rank,
        uint LockCount,
        int FrameCount,
        int ManagedThreadId,
        int OriginalIndex);

    internal readonly record struct LockCursor(
        int Position,
        bool IsContended,
        int WaitingThreadCount,
        int RecursionCount,
        ulong ObjectAddress,
        int OriginalIndex);

    internal readonly record struct WaiterCursor(
        int Position,
        ulong ObjectAddress,
        int PreviousWaiterId);

    public static string EncodeThread(string handle, ThreadCursor cursor)
        => Encode(writer =>
        {
            writer.Write(ThreadKind);
            writer.Write(handle);
            writer.Write(cursor.BlockedOnly);
            writer.Write(cursor.UsedFallback);
            writer.Write(cursor.Position);
            writer.Write(cursor.Rank);
            writer.Write(cursor.LockCount);
            writer.Write(cursor.FrameCount);
            writer.Write(cursor.ManagedThreadId);
            writer.Write(cursor.OriginalIndex);
        });

    public static string EncodeLock(string handle, LockCursor cursor)
        => Encode(writer =>
        {
            writer.Write(LockKind);
            writer.Write(handle);
            writer.Write(cursor.Position);
            writer.Write(cursor.IsContended);
            writer.Write(cursor.WaitingThreadCount);
            writer.Write(cursor.RecursionCount);
            writer.Write(cursor.ObjectAddress);
            writer.Write(cursor.OriginalIndex);
        });

    public static string EncodeWaiter(string handle, WaiterCursor cursor)
        => Encode(writer =>
        {
            writer.Write(WaiterKind);
            writer.Write(handle);
            writer.Write(cursor.Position);
            writer.Write(cursor.ObjectAddress);
            writer.Write(cursor.PreviousWaiterId);
        });

    public static bool TryDecodeThread(
        string encoded,
        string expectedHandle,
        bool expectedBlockedOnly,
        bool expectedUsedFallback,
        out ThreadCursor cursor,
        out string error)
    {
        cursor = default;
        if (!TryOpen(encoded, ThreadKind, expectedHandle, out var reader, out error))
        {
            return false;
        }

        using (reader)
        {
            try
            {
                cursor = new ThreadCursor(
                    reader.ReadBoolean(),
                    reader.ReadBoolean(),
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadUInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt32());
                if (!AtEnd(reader))
                {
                    error = "cursor contains trailing data";
                    return false;
                }
                if (cursor.BlockedOnly != expectedBlockedOnly || cursor.UsedFallback != expectedUsedFallback)
                {
                    error = "cursor belongs to a different thread view";
                    return false;
                }
                if (cursor.Position < 1 || cursor.OriginalIndex < 0)
                {
                    error = "cursor contains an invalid thread position";
                    return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException or FormatException)
            {
                error = "cursor payload is truncated";
                return false;
            }
        }
    }

    public static bool TryDecodeLock(
        string encoded,
        string expectedHandle,
        out LockCursor cursor,
        out string error)
    {
        cursor = default;
        if (!TryOpen(encoded, LockKind, expectedHandle, out var reader, out error))
        {
            return false;
        }

        using (reader)
        {
            try
            {
                cursor = new LockCursor(
                    reader.ReadInt32(),
                    reader.ReadBoolean(),
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadUInt64(),
                    reader.ReadInt32());
                if (!AtEnd(reader))
                {
                    error = "cursor contains trailing data";
                    return false;
                }
                if (cursor.Position < 1 || cursor.OriginalIndex < 0)
                {
                    error = "cursor contains an invalid lock position";
                    return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException or FormatException)
            {
                error = "cursor payload is truncated";
                return false;
            }
        }
    }

    public static bool TryDecodeWaiter(
        string encoded,
        string expectedHandle,
        ulong expectedObjectAddress,
        out WaiterCursor cursor,
        out string error)
    {
        cursor = default;
        if (!TryOpen(encoded, WaiterKind, expectedHandle, out var reader, out error))
        {
            return false;
        }

        using (reader)
        {
            try
            {
                cursor = new WaiterCursor(
                    reader.ReadInt32(),
                    reader.ReadUInt64(),
                    reader.ReadInt32());
                if (!AtEnd(reader))
                {
                    error = "cursor contains trailing data";
                    return false;
                }
                if (cursor.ObjectAddress != expectedObjectAddress)
                {
                    error = "cursor belongs to a different lock";
                    return false;
                }
                if (cursor.Position < 1)
                {
                    error = "cursor contains an invalid waiter position";
                    return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException or FormatException)
            {
                error = "cursor payload is truncated";
                return false;
            }
        }
    }

    private static string Encode(Action<BinaryWriter> writePayload)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Version);
            writePayload(writer);
        }
        return Convert.ToBase64String(stream.ToArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryOpen(
        string encoded,
        byte expectedKind,
        string expectedHandle,
        out BinaryReader reader,
        out string error)
    {
        reader = null!;
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > MaxEncodedLength)
        {
            error = "cursor is empty or exceeds the maximum encoded length";
            return false;
        }

        byte[] bytes;
        try
        {
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            error = "cursor is not valid base64url";
            return false;
        }

        var stream = new MemoryStream(bytes, writable: false);
        reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        try
        {
            if (reader.ReadByte() != Version)
            {
                error = "cursor version is unsupported";
                reader.Dispose();
                return false;
            }
            if (reader.ReadByte() != expectedKind)
            {
                error = "cursor belongs to a different pagination kind";
                reader.Dispose();
                return false;
            }
            if (!string.Equals(reader.ReadString(), expectedHandle, StringComparison.Ordinal))
            {
                error = "cursor belongs to a different snapshot handle";
                reader.Dispose();
                return false;
            }
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            error = "cursor payload is truncated";
            reader.Dispose();
            return false;
        }
    }

    private static bool AtEnd(BinaryReader reader)
        => reader.BaseStream.Position == reader.BaseStream.Length;
}
