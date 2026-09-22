using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike.AppendFirst;

using DotnetDiagnostics.Core.Tests.DurableCounterSpike;

/// <summary>
/// Binary framing for candidate B (append-first). One frame carries exactly one
/// committed batch. Layout:
/// <list type="bullet">
/// <item>fixed 32-byte header: magic(4) + version(2) + reserved(2) + recordCount(4)
/// + firstSequence(8) + lastSequence(8) + entriesLength(4)</item>
/// <item><c>entriesLength</c> bytes of entries: repeated
/// sequence(8) + payloadLength(4) + payload(payloadLength) per record, in the
/// order the batch was committed</item>
/// <item>32-byte SHA-256 over the exact entries bytes (this is the
/// <c>payloadSha256</c> field from <c>DurableStorageFrameContract</c>)</item>
/// <item>fixed 8-byte commit-footer marker, written and flushed last</item>
/// </list>
/// A frame is only a "committed batch" once its commit-footer marker has been
/// written and durably flushed. Anything short of that (including an
/// otherwise-complete header/entries/checksum with a missing or partial
/// footer) is explicitly excluded from recovery, never fabricated as loss-free.
/// </summary>
internal static class DurableAppendFirstFrame
{
    internal const int HeaderBytes = 4 + 2 + 2 + 4 + 8 + 8 + 4;
    internal const int ChecksumBytes = 32;
    internal const int FooterBytes = 8;
    internal const int TrailerBytes = ChecksumBytes + FooterBytes;

    private static readonly byte[] CommitFooterMarker =
    {
        0x0C, 0x0F, 0xFE, 0xED, 0xF0, 0x0D, 0xCA, 0xFE,
    };

    /// <summary>Builds the full on-disk bytes for one frame from an owned, already-copied batch.</summary>
    internal static byte[] Encode(IReadOnlyList<(long Sequence, byte[] Payload)> ownedRecords)
    {
        if (ownedRecords.Count is < 1 or > DurableStorageFrameContract.MaximumRecordsPerBatch)
        {
            throw new DurableStorageExperimentException(
                "InvalidBatchSize",
                "A frame must carry between 1 and the frozen maximum number of records.");
        }

        var entriesLength = 0;
        foreach (var (_, payload) in ownedRecords)
        {
            if (payload.Length > DurableStorageFrameContract.MaximumRecordBytes)
            {
                throw new DurableStorageExperimentException(
                    "RecordEncodedBytes",
                    "A record payload exceeds the frozen per-record byte limit.");
            }
            entriesLength = checked(entriesLength + 8 + 4 + payload.Length);
        }
        if (entriesLength > DurableStorageFrameContract.MaximumOwnedBytesPerBatch)
        {
            throw new DurableStorageExperimentException(
                "BatchOwnedBytesLimit",
                "A frame's entries exceed the frozen owned-bytes-per-batch limit.");
        }

        var entries = new byte[entriesLength];
        var offset = 0;
        var previousSequence = (long?)null;
        foreach (var (sequence, payload) in ownedRecords)
        {
            if (previousSequence.HasValue && sequence <= previousSequence.Value)
            {
                throw new DurableStorageExperimentException(
                    "InvalidSequenceMembership",
                    "Batch records must have strictly increasing, non-duplicate sequences.");
            }
            previousSequence = sequence;

            BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(offset, 8), sequence);
            offset += 8;
            BinaryPrimitives.WriteInt32LittleEndian(entries.AsSpan(offset, 4), payload.Length);
            offset += 4;
            payload.CopyTo(entries.AsSpan(offset, payload.Length));
            offset += payload.Length;
        }

        var checksum = SHA256.HashData(entries);
        var frame = new byte[HeaderBytes + entriesLength + TrailerBytes];
        var header = frame.AsSpan(0, HeaderBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], DurableStorageFrameContract.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..6], DurableStorageFrameContract.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..8], 0);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..12], ownedRecords.Count);
        BinaryPrimitives.WriteInt64LittleEndian(header[12..20], ownedRecords[0].Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(header[20..28], ownedRecords[^1].Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..32], entriesLength);

        entries.CopyTo(frame.AsSpan(HeaderBytes, entriesLength));
        checksum.CopyTo(frame.AsSpan(HeaderBytes + entriesLength, ChecksumBytes));
        CommitFooterMarker.CopyTo(frame.AsSpan(HeaderBytes + entriesLength + ChecksumBytes, FooterBytes));
        return frame;
    }

    /// <summary>
    /// Splits the header away from the frame so the caller can write the
    /// header+entries+checksum first and the commit-footer marker as a
    /// distinct, final, separately-flushable write. This lets a fault
    /// controller be reached strictly before the footer exists on disk.
    /// </summary>
    internal static (ReadOnlyMemory<byte> BeforeFooter, ReadOnlyMemory<byte> Footer) SplitFooter(byte[] frame)
        => (frame.AsMemory(0, frame.Length - FooterBytes), frame.AsMemory(frame.Length - FooterBytes, FooterBytes));

    /// <summary>Attempts to read the next frame at the current stream position. Never throws on malformed input.</summary>
    internal static DurableAppendFirstFrameScanResult ReadNext(Stream stream)
    {
        var startPosition = stream.Position;
        var header = new byte[HeaderBytes];
        var headerRead = ReadFully(stream, header);
        if (headerRead == 0)
        {
            return DurableAppendFirstFrameScanResult.CleanEnd(startPosition);
        }
        if (headerRead < HeaderBytes)
        {
            return DurableAppendFirstFrameScanResult.Truncated(startPosition, "IncompleteHeader");
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
        var version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4, 2));
        var recordCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
        var firstSequence = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(12, 8));
        var lastSequence = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(20, 8));
        var entriesLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(28, 4));

        if (magic != DurableStorageFrameContract.Magic || version != DurableStorageFrameContract.Version)
        {
            return DurableAppendFirstFrameScanResult.Corrupt(startPosition, "InvalidHeaderMagicOrVersion");
        }
        if (recordCount is < 1 or > DurableStorageFrameContract.MaximumRecordsPerBatch
            || entriesLength < 0 || entriesLength > DurableStorageFrameContract.MaximumOwnedBytesPerBatch)
        {
            return DurableAppendFirstFrameScanResult.Corrupt(startPosition, "InvalidHeaderBounds");
        }

        var entries = new byte[entriesLength];
        var entriesRead = ReadFully(stream, entries);
        if (entriesRead < entriesLength)
        {
            return DurableAppendFirstFrameScanResult.Truncated(startPosition, "IncompleteEntries");
        }

        var trailer = new byte[TrailerBytes];
        var trailerRead = ReadFully(stream, trailer);
        if (trailerRead < TrailerBytes)
        {
            return DurableAppendFirstFrameScanResult.Truncated(startPosition, "IncompleteFooter");
        }

        var storedChecksum = trailer.AsSpan(0, ChecksumBytes);
        var footerMarker = trailer.AsSpan(ChecksumBytes, FooterBytes);
        if (!footerMarker.SequenceEqual(CommitFooterMarker))
        {
            return DurableAppendFirstFrameScanResult.Corrupt(startPosition, "MissingOrInvalidCommitFooter");
        }
        var computedChecksum = SHA256.HashData(entries);
        if (!computedChecksum.AsSpan().SequenceEqual(storedChecksum))
        {
            return DurableAppendFirstFrameScanResult.Corrupt(startPosition, "ChecksumMismatch");
        }

        var records = new List<(long Sequence, byte[] Payload)>(recordCount);
        var offset = 0;
        long? previousSequence = null;
        for (var i = 0; i < recordCount; i++)
        {
            if (offset + 12 > entries.Length)
            {
                return DurableAppendFirstFrameScanResult.Corrupt(startPosition, "InvalidEntryLayout");
            }
            var sequence = BinaryPrimitives.ReadInt64LittleEndian(entries.AsSpan(offset, 8));
            var length = BinaryPrimitives.ReadInt32LittleEndian(entries.AsSpan(offset + 8, 4));
            offset += 12;
            if (length < 0 || length > DurableStorageFrameContract.MaximumRecordBytes || offset + length > entries.Length)
            {
                return DurableAppendFirstFrameScanResult.Corrupt(startPosition, "InvalidEntryLayout");
            }
            if (previousSequence.HasValue && sequence <= previousSequence.Value)
            {
                return DurableAppendFirstFrameScanResult.Corrupt(startPosition, "InvalidSequenceMembership");
            }
            previousSequence = sequence;
            var payload = new byte[length];
            entries.AsSpan(offset, length).CopyTo(payload);
            offset += length;
            records.Add((sequence, payload));
        }
        if (offset != entries.Length
            || records.Count != recordCount
            || records[0].Sequence != firstSequence
            || records[^1].Sequence != lastSequence)
        {
            return DurableAppendFirstFrameScanResult.Corrupt(startPosition, "SequenceMembershipMismatch");
        }

        var consumedBytes = HeaderBytes + entriesLength + TrailerBytes;
        return DurableAppendFirstFrameScanResult.Valid(startPosition, consumedBytes, records);
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }
}

internal enum DurableAppendFirstFrameOutcome
{
    Valid,
    CleanEnd,
    Truncated,
    Corrupt,
}

internal sealed record DurableAppendFirstFrameScanResult(
    DurableAppendFirstFrameOutcome Outcome,
    long StartPosition,
    int ConsumedBytes,
    string? Reason,
    IReadOnlyList<(long Sequence, byte[] Payload)>? Records)
{
    internal static DurableAppendFirstFrameScanResult Valid(
        long startPosition, int consumedBytes, IReadOnlyList<(long Sequence, byte[] Payload)> records)
        => new(DurableAppendFirstFrameOutcome.Valid, startPosition, consumedBytes, null, records);

    internal static DurableAppendFirstFrameScanResult CleanEnd(long startPosition)
        => new(DurableAppendFirstFrameOutcome.CleanEnd, startPosition, 0, null, null);

    internal static DurableAppendFirstFrameScanResult Truncated(long startPosition, string reason)
        => new(DurableAppendFirstFrameOutcome.Truncated, startPosition, 0, reason, null);

    internal static DurableAppendFirstFrameScanResult Corrupt(long startPosition, string reason)
        => new(DurableAppendFirstFrameOutcome.Corrupt, startPosition, 0, reason, null);
}
