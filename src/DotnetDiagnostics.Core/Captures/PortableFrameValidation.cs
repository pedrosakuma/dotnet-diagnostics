using System.Buffers.Binary;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.UseCases;

namespace DotnetDiagnostics.Core.Captures;

internal sealed record PortableValidatedFrames(long LogicalBytes, long[] TableRows);

internal static class PortableFrameValidation
{
    private static readonly int[] Columns = [6, 2, 3, 9, 9, 3];

    internal static PortableValidatedFrames Validate(Stream input, Stream output, PortableScalarIndex strings,
        Stream recordIndex, CaptureManifest manifest, IReadOnlyDictionary<string, string> mapping,
        CaptureStoreOptions store, PortableCaptureOptions options, long metadataBytes, CancellationToken token)
    {
        var reader = new FrameReader(input, store);
        var info = manifest.Info;
        var descriptors = info.Artifacts.ToArray();
        var positions = descriptors.Select((a, i) => (a.ArtifactId, i)).ToDictionary(static p => p.ArtifactId, static p => p.i, StringComparer.Ordinal);
        var artifactRows = new HashSet<string>(StringComparer.Ordinal);
        var snapshots = new HashSet<string>(StringComparer.Ordinal);
        var streams = new DurableCaptureRecordStreamInfo?[descriptors.Length];
        var persisted = new long[descriptors.Length];
        var graph = new ulong[descriptors.Length];
        var definitions = new HashSet<string>[descriptors.Length];
        for (var i = 0; i < definitions.Length; i++) definitions[i] = new(StringComparer.Ordinal);
        long logical = 0, sourceLogical = 0, tokens = 0, lastRecord = 0, lastFieldRecord = 0, fieldBytes = 0;
        long stackBytes = 0;
        var nextOrdinal = 0;
        long fieldRole = 0, fieldPosition = 0;
        Dictionary<string, CaptureField>? cpuFields = null;
        var rows = new long[6];
        var writeBuffer = new byte[65536];
        Span<byte> recordIndexRow = stackalloc byte[24];
        long total = 0;
        var previousTable = -1;
        while (reader.Read() is { } row)
        {
            token.ThrowIfCancellationRequested();
            var table = row.Table;
            if (table < previousTable) throw Corrupt("Wire.TableOrder");
            previousTable = table;
            PortableBounds.Check("MaxRowsPerTable", ++rows[table], options.MaxRowsPerTable);
            PortableBounds.Check("MaxRowsPerCapture", ++total, options.MaxRowsPerCapture);
            var cells = row.Cells;
            if (table != 4) FinishFields();
            if (table == 0)
            {
                var format = CapturePackage.FormatOf(manifest);
                long[] expected = [format.PackageVersion, format.SchemaVersion, format.RecordVersion,
                    format.IndexVersion, format.WriterVersion, format.RequiredReaderVersion];
                if (rows[0] != 1 || !cells.Select(static c => c.Integer).SequenceEqual(expected)) throw Corrupt("Format.Mismatch");
                cells = [Cell.Number(3), Cell.Number(1), Cell.Number(1), Cell.Number(1), Cell.Number(3), Cell.Number(3)];
            }
            else if (table == 1)
            {
                strings.Add(cells[0].Integer, cells[1].Bytes);
            }
            else if (table == 2)
            {
                var id = cells[0].Text;
                if (!positions.TryGetValue(id, out var ordinal) || !artifactRows.Add(id) ||
                    cells[1].Text != descriptors[ordinal].Kind || cells[2].Text != descriptors[ordinal].Name)
                    throw Corrupt("Data.ArtifactDescriptor");
                cells[0] = Cell.String(mapping[id]);
            }
            else if (table == 3)
            {
                var id = cells[0].Integer;
                if (id <= lastRecord || !positions.TryGetValue(cells[1].Text, out var artifact)) throw Corrupt("Data.Occurrence");
                lastRecord = id;
                if (cells[2].Type != 5 && cells[2].Integer is < 0 or > 3155378975999999999 ||
                    cells[7].Type != 5 && cells[7].Integer < 0) throw Corrupt("Data.TimestampOrDuration");
                Finite(cells[6]);
                var size = checked(128 + Dimension(cells[4]) + Dimension(cells[5]) + Dimension(cells[8]));
                PortableBounds.Check("MaxRecordBytes", size, store.MaxRecordBytes);
                logical = checked(logical + size);
                sourceLogical = checked(sourceLogical + size);
                persisted[artifact]++;
                var category = cells[4].Type == 5 ? null : strings.Get(cells[4].Integer, true).Text;
                var name = cells[5].Type == 5 ? null : strings.Get(cells[5].Integer, true).Text;
                long role = 0;
                if (descriptors[artifact].Kind == "cpu-sample")
                {
                    if (category == CpuReplayStackObservationWriter.DefinitionCategory)
                    {
                        role = 1;
                        if (name is null || definitions[artifact].Contains(name)) throw Corrupt("Data.CpuStackDefinition");
                        PortableBounds.Check("CpuStackDefinitions", definitions[artifact].Count + 1L,
                            CpuReplayStackObservationWriter.MaximumStackDefinitions);
                        stackBytes = checked(stackBytes + 128 + 2L * name.Length + CapturePackage.Utf8.GetByteCount(name));
                        PortableBounds.Check("PortableMetadataBytes", metadataBytes + stackBytes, 4 * 1024 * 1024);
                        definitions[artifact].Add(name);
                    }
                    else if (category == CpuReplayStackObservationWriter.SampleCategory)
                    {
                        role = 2;
                        if (name is null || !definitions[artifact].Contains(name)) throw Corrupt("Data.CpuStackReference");
                    }
                    else if (category?.StartsWith("definition.cpu-stack.", StringComparison.Ordinal) == true ||
                        category?.StartsWith("sample.cpu.eventpipe.stack-ref.", StringComparison.Ordinal) == true)
                        throw CapturePackage.Error(CaptureErrorCode.UnsupportedFormat, "Data.CpuStackRepresentation");
                }
                BinaryPrimitives.WriteInt64LittleEndian(recordIndexRow, id);
                BinaryPrimitives.WriteInt64LittleEndian(recordIndexRow[8..], size);
                BinaryPrimitives.WriteInt64LittleEndian(recordIndexRow[16..], role);
                recordIndex.Position = recordIndex.Length;
                recordIndex.Write(recordIndexRow);
                cells[1] = Cell.String(mapping[cells[1].Text]);
            }
            else if (table == 4)
            {
                var id = cells[0].Integer;
                if (id < lastFieldRecord || id <= 0) throw Corrupt("Data.FieldOrder");
                if (id != lastFieldRecord)
                {
                    FinishFields();
                    lastFieldRecord = id;
                    nextOrdinal = 0;
                    (fieldBytes, fieldPosition, fieldRole) = FindRecord(recordIndex, rows[3], id);
                    if (fieldRole != 0) cpuFields = new(StringComparer.Ordinal);
                }
                if (cells[1].Integer != nextOrdinal++ || nextOrdinal > store.MaxFields) throw Corrupt("Data.FieldOrdinal");
                var kind = cells[3].Integer;
                if (kind is < 0 or > 4 ||
                    (cells[4].Type != 5) != (kind == 1) ||
                    (cells[5].Type != 5) != (kind == 2) ||
                    (cells[6].Type != 5) != (kind == 3) ||
                    (cells[7].Type != 5) != (kind == 4) ||
                    kind == 4 && cells[7].Integer is not (0 or 1)) throw Corrupt("Data.ScalarSlots");
                Finite(cells[6]);
                var size = checked(64 + Dimension(cells[2]) + Dimension(cells[4]) + Dimension(cells[8]));
                fieldBytes = checked(fieldBytes + size);
                PortableBounds.Check("MaxRecordBytes", fieldBytes, store.MaxRecordBytes);
                logical = checked(logical + size);
                sourceLogical = checked(sourceLogical + size);
                if (cpuFields is not null)
                {
                    var fieldName = strings.Get(cells[2].Integer, true).Text!;
                    var field = new CaptureField(fieldName, (CaptureFieldKind)kind,
                        kind == 1 ? strings.Get(cells[4].Integer, true).Text : null,
                        kind == 2 ? cells[5].Integer : null,
                        kind == 3 ? BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(cells[6].Bytes)) : null,
                        kind == 4 ? cells[7].Integer == 1 : null);
                    if (!cpuFields.TryAdd(fieldName, field)) throw Corrupt("Data.CpuStackField");
                }
            }
            else
            {
                var id = cells[0].Text;
                if (!positions.TryGetValue(id, out var artifact) || !snapshots.Add(id) ||
                    cells[1].Integer is < 1 or > int.MaxValue) throw Corrupt("Data.Snapshot");
                var original = new CaptureSnapshot((int)cells[1].Integer, cells[2].Bytes, descriptors[artifact].Kind);
                var mapped = PortableSnapshotImport.ValidateAndMap(info, descriptors[artifact], original, mapping,
                    store, options, metadataBytes + stackBytes, ref tokens, out streams[artifact], out var children);
                foreach (var child in children ?? [])
                {
                    var childIndex = positions[child.ArtifactId];
                    if (persisted[childIndex] > child.Accepted || child.Accepted > info.Quality.Accepted ||
                        child.Offered > info.Quality.Offered) throw Corrupt("Data.CompositionPopulation");
                    graph[positions[child.ParentArtifactId]] |= 1UL << childIndex;
                }
                sourceLogical = checked(sourceLogical + original.Utf8Json.Length);
                logical = checked(logical + mapped.Utf8Json.Length);
                cells[0] = Cell.String(mapping[id]);
                cells[1] = Cell.Number(mapped.Version);
                cells[2] = new(4, mapped.Utf8Json.ToArray());
            }
            PortableBounds.Check("MaxLogicalBytes", sourceLogical, store.MaxLogicalBytes);
            PortableBounds.Check("MaxLogicalBytes", logical, store.MaxLogicalBytes);
            WriteRow(output, table, cells, writeBuffer);
        }
        FinishFields();
        recordIndex.Position = 0;
        for (long i = 0; i < rows[3]; i++)
        {
            token.ThrowIfCancellationRequested();
            recordIndex.ReadExactly(recordIndexRow);
            if (BinaryPrimitives.ReadInt64LittleEndian(recordIndexRow[16..]) != 0)
                throw Corrupt("Data.CpuStackFieldsMissing");
        }
        if (rows[0] != 1 || artifactRows.Count != descriptors.Length || rows[3] != info.Quality.Persisted ||
            !rows.SequenceEqual(reader.Rows) || sourceLogical != reader.LogicalBytes) throw Corrupt("Data.Population");
        for (var i = 0; i < descriptors.Length; i++)
        {
            if (!CaptureArtifactCodec.SupportsKind(descriptors[i].Kind) && descriptors[i].Kind is not ("batch" or "sweep" or "gc-activities"))
                throw CapturePackage.Error(CaptureErrorCode.UnsupportedFormat, "Artifact kind has no portable representation.");
            if (streams[i] is { } stream &&
                (persisted[i] > stream.Accepted || stream.Accepted > info.Quality.Accepted ||
                 stream.Offered > info.Quality.Offered || !stream.Available && persisted[i] != 0))
                throw Corrupt("Data.StreamPopulation");
        }
        ValidateCompositionGraph(graph);
        strings.ValidateAllReferenced(token);
        var summary = new byte[65];
        summary[0] = 5;
        for (var i = 0; i < 6; i++) BinaryPrimitives.WriteInt64LittleEndian(summary.AsSpan(1 + i * 8), rows[i]);
        BinaryPrimitives.WriteInt64LittleEndian(summary.AsSpan(49), logical);
        WriteFrame(output, summary);
        return new(logical, rows);

        long Dimension(Cell cell) => cell.Type == 5 ? 0 : strings.Get(cell.Integer).Cost;
        void FinishFields()
        {
            if (cpuFields is null) return;
            PortableCpuRecordValidation.Validate(fieldRole, cpuFields, options, ref tokens);
            recordIndex.Position = fieldPosition + 16;
            Span<byte> zero = stackalloc byte[8];
            zero.Clear();
            recordIndex.Write(zero);
            cpuFields = null;
            fieldRole = 0;
        }
    }

    internal static void ValidateCompositionGraph(ulong[] graph)
    {
        PortableBounds.Check("MaxArtifacts", graph.Length, 64);
        var marks = new byte[graph.Length];
        for (var i = 0; i < graph.Length; i++) Visit(i);

        void Visit(int node)
        {
            if (marks[node] == 1) throw Corrupt("Data.CompositionCycle");
            if (marks[node] == 2) return;
            marks[node] = 1;
            for (var child = 0; child < graph.Length; child++)
                if ((graph[node] & (1UL << child)) != 0) Visit(child);
            marks[node] = 2;
        }
    }

    private static (long Bytes, long Position, long Role) FindRecord(Stream index, long count, long id)
    {
        Span<byte> bytes = stackalloc byte[24];
        long low = 0, high = count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            index.Position = middle * 24;
            index.ReadExactly(bytes);
            var found = BinaryPrimitives.ReadInt64LittleEndian(bytes);
            if (found == id) return (BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]), middle * 24,
                BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]));
            if (found < id) low = middle + 1;
            else high = middle - 1;
        }
        throw Corrupt("Data.FieldRecord");
    }

    private static void Finite(Cell cell)
    {
        if (cell.Type != 5 && !double.IsFinite(BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(cell.Bytes))))
            throw Corrupt("Data.NonFinite");
    }

    private sealed record Cell(byte Type, byte[] Bytes)
    {
        internal long Integer => Type == 1 ? BinaryPrimitives.ReadInt64LittleEndian(Bytes) : throw Corrupt("Data.StorageClass");
        internal string Text => Type == 3 ? CapturePackage.Utf8.GetString(Bytes) : throw Corrupt("Data.StorageClass");
        internal static Cell String(string text) => new(3, CapturePackage.Utf8.GetBytes(text));
        internal static Cell Number(long value) => new(1, BitConverter.GetBytes(value));
    }

    private sealed record Row(int Table, Cell[] Cells);

    private sealed class FrameReader(Stream input, CaptureStoreOptions options)
    {
        private readonly byte[] _buffer = new byte[65536];
        private long _wire;
        internal long[] Rows { get; } = new long[6];
        internal long LogicalBytes { get; private set; }

        internal Row? Read()
        {
            var frame = Next();
            if (frame.Length == 65 && frame[0] == 5)
            {
                for (var i = 0; i < 6; i++) Rows[i] = BinaryPrimitives.ReadInt64LittleEndian(frame[(1 + i * 8)..]);
                LogicalBytes = BinaryPrimitives.ReadInt64LittleEndian(frame[49..]);
                if (input.ReadByte() != -1) throw Corrupt("Wire.Trailing");
                return null;
            }
            if (frame.Length != 3 || frame[0] != 1 || frame[1] > 5 || frame[2] != Columns[frame[1]]) throw Corrupt("Wire.Row");
            var table = frame[1];
            var cells = new Cell[Columns[table]];
            for (var column = 0; column < cells.Length; column++)
            {
                frame = Next();
                if (frame.Length != 6 || frame[0] != 2) throw Corrupt("Wire.Cell");
                var type = frame[1];
                var length = BinaryPrimitives.ReadInt32LittleEndian(frame[2..]);
                var expected = table switch
                {
                    0 => 1, 1 => column == 0 ? 1 : 3, 2 => 3,
                    3 => column == 1 ? 3 : column == 6 ? 2 : 1,
                    4 => column == 6 ? 2 : 1,
                    _ => column == 0 ? 3 : column == 1 ? 1 : 4
                };
                var nullable = table == 3 && column >= 2 || table == 4 && column >= 4;
                if (type != expected && !(type == 5 && nullable) || length < 0 ||
                    type == 5 && length != 0 || type is 1 or 2 && length != 8) throw Corrupt("Wire.StorageClass");
                PortableBounds.Check("CellBytes", length, type == 4 ? options.MaxSnapshotBytes : options.MaxRecordBytes);
                var bytes = new byte[length];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    frame = Next();
                    if (frame.Length < 2 || frame[0] != 3 || frame.Length - 1 > bytes.Length - offset) throw Corrupt("Wire.Value");
                    frame[1..].CopyTo(bytes.AsSpan(offset));
                    offset += frame.Length - 1;
                }
                if (type == 3) _ = CapturePackage.Utf8.GetCharCount(bytes);
                cells[column] = new(type, bytes);
            }
            frame = Next();
            if (frame.Length != 1 || frame[0] != 4) throw Corrupt("Wire.RowEnd");
            return new(table, cells);
        }

        private ReadOnlySpan<byte> Next()
        {
            Span<byte> header = stackalloc byte[4];
            input.ReadExactly(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 1 or > 65536) throw Corrupt("Wire.FrameBytes");
            _wire = checked(_wire + 4 + length);
            PortableBounds.Check("WorkerWireBytes", _wire, 512L * 1024 * 1024);
            input.ReadExactly(_buffer.AsSpan(0, length));
            return _buffer.AsSpan(0, length);
        }
    }

    private static void WriteRow(Stream output, int table, Cell[] cells, byte[] chunk)
    {
        WriteFrame(output, [1, (byte)table, (byte)cells.Length]);
        Span<byte> header = stackalloc byte[6];
        chunk[0] = 3;
        foreach (var cell in cells)
        {
            header[0] = 2;
            header[1] = cell.Type;
            BinaryPrimitives.WriteInt32LittleEndian(header[2..], cell.Bytes.Length);
            WriteFrame(output, header);
            for (var offset = 0; offset < cell.Bytes.Length;)
            {
                var length = Math.Min(chunk.Length - 1, cell.Bytes.Length - offset);
                cell.Bytes.AsSpan(offset, length).CopyTo(chunk.AsSpan(1));
                WriteFrame(output, chunk.AsSpan(0, length + 1));
                offset += length;
            }
        }
        WriteFrame(output, [4]);
    }

    private static void WriteFrame(Stream output, ReadOnlySpan<byte> bytes)
    {
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        output.Write(header);
        output.Write(bytes);
    }

    private static CaptureStoreException Corrupt(string reason) => CapturePackage.Error(CaptureErrorCode.CorruptPackage, reason);
}
