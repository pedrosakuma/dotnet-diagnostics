using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DotnetDiagnostics.Core.Captures;

internal sealed record PortableContentHash(long Bytes, string Sha256, uint Crc32);
internal sealed record PortableZipInput(string Name, PortableContentHash Hash, Func<Stream> Open);
internal sealed record PortableZipMember(string Name, long Offset, PortableContentHash Hash);

/// <summary>Stored-only ZIP v1. Does not use an eager central-directory reader.</summary>
internal static class PortableZip
{
    private static readonly uint[] CrcTable = CreateCrcTable();
    private static readonly string[] CaptureMembers = ["manifest.json", "capture.sqlite", "seal.json"];

    internal static async Task<PortableContentHash> MeasureAsync(Stream source, long maximum, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[PortableBounds.BufferBytes];
        long length = 0;
        var crc = uint.MaxValue;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(0, PortableBounds.ReadSize(maximum - length)), token).ConfigureAwait(false);
                if (read == 0) break;
                length = checked(length + read);
                PortableBounds.Check("MemberBytes", length, maximum);
                hash.AppendData(buffer, 0, read);
                crc = UpdateCrc(crc, buffer.AsSpan(0, read));
            }
            return new(length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), ~crc);
        }
        finally { Array.Clear(buffer); }
    }

    internal static PortableContentHash Measure(ReadOnlySpan<byte> bytes) =>
        new(bytes.Length, PortableCaptureStorage.Digest(bytes), ~UpdateCrc(uint.MaxValue, bytes));

    internal static long Size(IReadOnlyList<PortableZipInput> members, PortableCaptureOptions options)
    {
        PortableBounds.Check("MaxMembers", members.Count, 2 + 3L * options.MaxEntries);
        long raw = 22, content = 0, directory = 0, headers = 22;
        foreach (var member in members)
        {
            ValidateName(member.Name);
            var nameBytes = member.Name.Length;
            content = checked(content + member.Hash.Bytes);
            directory += 46 + nameBytes;
            headers += 76 + 2 * nameBytes;
            raw = checked(raw + 76 + 2 * nameBytes + member.Hash.Bytes);
        }
        PortableBounds.Check("MaxUncompressedBytes", content, options.MaxUncompressedBytes);
        PortableBounds.Check("MaxArchiveBytes", raw, options.MaxArchiveBytes);
        PortableBounds.Check("CentralDirectoryBytes", directory, 32 * 1024);
        PortableBounds.Check("ZipHeaderBytes", headers, 64 * 1024);
        return raw;
    }

    internal static async Task WriteAsync(Stream output, IReadOnlyList<PortableZipInput> members,
        PortableCaptureOptions options, CancellationToken token)
    {
        var expected = Size(members, options);
        var offsets = new uint[members.Count];
        var buffer = new byte[PortableBounds.BufferBytes];
        long written = 0;
        try
        {
            for (var i = 0; i < members.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var member = members[i];
                offsets[i] = checked((uint)written);
                var header = new byte[30 + member.Name.Length];
                U32(header, 0, 0x04034b50);
                U16(header, 4, 20); U16(header, 6, 0x0800); U16(header, 12, 0x0021);
                U32(header, 14, member.Hash.Crc32);
                U32(header, 18, checked((uint)member.Hash.Bytes)); U32(header, 22, checked((uint)member.Hash.Bytes));
                U16(header, 26, checked((ushort)member.Name.Length));
                Encoding.ASCII.GetBytes(member.Name, header.AsSpan(30));
                await WriteCountedAsync(output, header, written, options, token).ConfigureAwait(false);
                written += header.Length;
                using var input = member.Open();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long consumed = 0;
                var crc = uint.MaxValue;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var read = await input.ReadAsync(buffer.AsMemory(0, PortableBounds.ReadSize(member.Hash.Bytes - consumed)), token).ConfigureAwait(false);
                    if (read == 0) break;
                    consumed = checked(consumed + read);
                    PortableBounds.Check("MemberBytes", consumed, member.Hash.Bytes);
                    hash.AppendData(buffer, 0, read);
                    crc = UpdateCrc(crc, buffer.AsSpan(0, read));
                    await WriteCountedAsync(output, buffer.AsMemory(0, read), written, options, token).ConfigureAwait(false);
                    written += read;
                }
                if (consumed != member.Hash.Bytes || ~crc != member.Hash.Crc32 ||
                    !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), member.Hash.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw Corrupt("Source member changed while exporting.");
            }
            var centralStart = checked((uint)written);
            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                var header = new byte[46 + member.Name.Length];
                U32(header, 0, 0x02014b50);
                U16(header, 4, 0x0314); U16(header, 6, 20); U16(header, 8, 0x0800); U16(header, 14, 0x0021);
                U32(header, 16, member.Hash.Crc32);
                U32(header, 20, checked((uint)member.Hash.Bytes)); U32(header, 24, checked((uint)member.Hash.Bytes));
                U16(header, 28, checked((ushort)member.Name.Length));
                U32(header, 38, 0x81800000); U32(header, 42, offsets[i]);
                Encoding.ASCII.GetBytes(member.Name, header.AsSpan(46));
                await WriteCountedAsync(output, header, written, options, token).ConfigureAwait(false);
                written += header.Length;
            }
            var end = new byte[22];
            U32(end, 0, 0x06054b50); U16(end, 8, checked((ushort)members.Count)); U16(end, 10, checked((ushort)members.Count));
            U32(end, 12, checked((uint)(written - centralStart))); U32(end, 16, centralStart);
            await WriteCountedAsync(output, end, written, options, token).ConfigureAwait(false);
            if (written + end.Length != expected) throw Corrupt("Archive size accounting disagrees.");
        }
        finally { Array.Clear(buffer); }
    }

    internal static async Task<IReadOnlyList<PortableZipMember>> InspectAsync(Stream input,
        PortableCaptureOptions options, CancellationToken token)
    {
        if (!input.CanSeek || !input.CanRead) throw new ArgumentException("ZIP preflight requires private seekable input.", nameof(input));
        var length = input.Length;
        PortableBounds.Check("MaxArchiveBytes", length, options.MaxArchiveBytes);
        if (length < 22) throw Corrupt("ZIP end record is missing.");
        var end = new byte[22];
        input.Position = length - 22;
        await input.ReadExactlyAsync(end, token).ConfigureAwait(false);
        var count = R16(end, 10);
        var directoryBytes = R32(end, 12);
        var directoryStart = R32(end, 16);
        if (R32(end, 0) != 0x06054b50 || R16(end, 4) != 0 || R16(end, 6) != 0 ||
            R16(end, 8) != count || R16(end, 20) != 0 || count < 5 || (count - 2) % 3 != 0 ||
            (long)directoryStart + directoryBytes != length - 22)
            throw Corrupt("Unsupported ZIP end record, layout or member count.");
        PortableBounds.Check("MaxMembers", count, 2 + 3L * options.MaxEntries);
        PortableBounds.Check("CentralDirectoryBytes", directoryBytes, 32 * 1024);
        // Only now, after bounded EOCD preflight, allocate a directory inventory.
        var entries = new List<PortableZipMember>(count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        long centralPosition = directoryStart, localPosition = 0, total = 0, headerBytes = 22;
        var central = new byte[46];
        var local = new byte[30];
        var nameBuffer = new byte[128];
        var buffer = new byte[PortableBounds.BufferBytes];
        try
        {
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (centralPosition + central.Length > length - 22) throw Corrupt("Central header exceeds its declared range.");
                input.Position = centralPosition;
                await input.ReadExactlyAsync(central, token).ConfigureAwait(false);
                var nameBytes = R16(central, 28);
                PortableBounds.Check("MemberNameBytes", nameBytes, 128);
                if (R32(central, 0) != 0x02014b50 || R16(central, 6) is not (10 or 20) || R16(central, 8) != 0x0800 ||
                    R16(central, 10) != 0 || R16(central, 30) != 0 || R16(central, 32) != 0 ||
                    R16(central, 34) != 0 || R32(central, 20) != R32(central, 24) ||
                    R32(central, 42) != localPosition || (R32(central, 38) & 0x410) != 0 ||
                    (R32(central, 38) >> 16 & 0xf000) is not (0 or 0x8000) ||
                    centralPosition + 46 + nameBytes > length - 22)
                    throw Corrupt("Unsupported ZIP member header.");
                await input.ReadExactlyAsync(nameBuffer.AsMemory(0, nameBytes), token).ConfigureAwait(false);
                var name = Encoding.ASCII.GetString(nameBuffer, 0, nameBytes);
                ValidateName(name);
                if (!names.Add(name)) throw Corrupt("Duplicate ZIP member.");
                if (i == 0 && name != "bundle.json" || i == 1 && name != "bundle.seal.json" ||
                    i >= 2 && !name.EndsWith("/" + CaptureMembers[(i - 2) % 3], StringComparison.Ordinal))
                    throw Corrupt("ZIP member order is invalid.");
                var size = R32(central, 24);
                var maximum = i == 0 ? options.MaxIndexBytes : i == 1 ? 1024L :
                    (i - 2) % 3 == 1 ? 256L * 1024 * 1024 : 128 * 1024;
                PortableBounds.Check("MemberBytes", size, maximum);
                total = checked(total + size);
                PortableBounds.Check("MaxUncompressedBytes", total, options.MaxUncompressedBytes);
                headerBytes += 76 + 2 * nameBytes;
                PortableBounds.Check("ZipHeaderBytes", headerBytes, 64 * 1024);
                input.Position = localPosition;
                await input.ReadExactlyAsync(local, token).ConfigureAwait(false);
                if (R32(local, 0) != 0x04034b50 || R16(local, 4) != R16(central, 6) || R16(local, 6) != 0x0800 ||
                    R16(local, 8) != 0 || R16(local, 26) != nameBytes || R16(local, 28) != 0 ||
                    R32(local, 14) != R32(central, 16) || R32(local, 18) != size || R32(local, 22) != size)
                    throw Corrupt("Local and central ZIP headers disagree.");
                await input.ReadExactlyAsync(nameBuffer.AsMemory(0, nameBytes), token).ConfigureAwait(false);
                if (Encoding.ASCII.GetString(nameBuffer, 0, nameBytes) != name) throw Corrupt("ZIP names disagree.");
                var dataStart = input.Position;
                if (dataStart + size > directoryStart) throw Corrupt("ZIP content overlaps the directory.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var crc = uint.MaxValue;
                long remaining = size;
                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var wanted = (int)Math.Min(buffer.Length, remaining);
                    await input.ReadExactlyAsync(buffer.AsMemory(0, wanted), token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, wanted);
                    crc = UpdateCrc(crc, buffer.AsSpan(0, wanted));
                    remaining -= wanted;
                }
                if (~crc != R32(central, 16)) throw Corrupt("ZIP CRC mismatch.");
                entries.Add(new(name, dataStart, new(size, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), ~crc)));
                localPosition = dataStart + size;
                centralPosition += 46 + nameBytes;
            }
            if (localPosition != directoryStart || centralPosition != length - 22 || input.Length != length)
                throw Corrupt("ZIP contains gaps, trailing data or a changed length.");
            for (var i = 2; i < entries.Count; i += 3)
            {
                var prefix = entries[i].Name[..41];
                if (!entries[i + 1].Name.StartsWith(prefix, StringComparison.Ordinal) ||
                    !entries[i + 2].Name.StartsWith(prefix, StringComparison.Ordinal))
                    throw Corrupt("Capture member groups disagree.");
            }
            return entries.AsReadOnly();
        }
        finally { Array.Clear(buffer); }
    }

    private static async ValueTask WriteCountedAsync(Stream output, ReadOnlyMemory<byte> bytes, long written,
        PortableCaptureOptions options, CancellationToken token)
    {
        PortableBounds.Check("MaxArchiveBytes", checked(written + bytes.Length), options.MaxArchiveBytes);
        token.ThrowIfCancellationRequested();
        await output.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static void ValidateName(string name)
    {
        if (name is "bundle.json" or "bundle.seal.json") return;
        if (name.Length > 128 || name.Length < 42 || !name.StartsWith("entries/", StringComparison.Ordinal) ||
            name[40] != '/' || !CaptureMembers.Contains(name[41..], StringComparer.Ordinal))
            throw CapturePackage.Error(CaptureErrorCode.UnsafePath, "Unsupported ZIP member name.");
        CapturePackage.ValidateId(name.Substring(8, 32));
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320;
            table[i] = value;
        }
        return table;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 255];
        return crc;
    }

    private static CaptureStoreException Corrupt(string message) => CapturePackage.Error(CaptureErrorCode.CorruptPackage, message);
    private static ushort R16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint R32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void U16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void U32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
