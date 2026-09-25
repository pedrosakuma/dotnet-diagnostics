using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DotnetDiagnostics.Core.Captures;

internal sealed record SqliteAdmissionRequest(string Executable, string SqliteLibrary, string PrivateDirectory,
    string Database, CaptureFormatVersions Format, IReadOnlyList<CaptureArtifactInfo> Artifacts, long Persisted)
{
    internal Action<int>? BeforeInput { get; init; }
    internal Action? AfterExit { get; init; }
    internal bool IncludeUsage { get; init; }
}

internal sealed record SqliteAdmissionLimits
{
    internal CaptureStoreOptions Store { get; init; } = new();
    internal int RowsPerTable { get; init; } = 2_000_000;
    internal int RowsPerCapture { get; init; } = 4_000_000;
    internal long VmInstructions { get; init; } = 200_000_000;
    internal int TokensPerSnapshot { get; init; } = 2_000_000;
    internal int TokensPerCapture { get; init; } = 8_000_000;
    internal CaptureWorkerLimits Worker { get; init; } = new();
    internal void Validate()
    {
        Store.Validate();
        Worker.Validate();
        if (RowsPerTable is < 1 or > 2_000_000 || RowsPerCapture is < 1 or > 4_000_000 ||
            VmInstructions is < 1000 or > 200_000_000 || TokensPerSnapshot is < 1 or > 2_000_000 ||
            TokensPerCapture is < 1 or > 8_000_000)
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "SQLite admission ceilings can only be reduced.");
    }
}

/// <summary>Only schema/scalar validation. Evidence remains provisional; this is NOT a valid capture/import result.</summary>
internal sealed record SqliteAdmissionResult(IReadOnlyList<long> TableRows, long LogicalBytes, long VmInstructions,
    long WireBytes, int LandlockAbi, long PeakObservedRss, TimeSpan MaximumObservationGap)
{
    internal TimeSpan CpuTime { get; init; }
    internal TimeSpan WallTime { get; init; }
}

internal static partial class IsolatedCaptureWorker
{
    internal static Task<SqliteAdmissionResult> AdmitSqliteAsync(SqliteAdmissionRequest request, Stream evidence,
        SqliteAdmissionLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(evidence);
        limits ??= new();
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw Unsupported("WorkerPlatformUnavailable");
        if (!evidence.CanWrite) throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Provisional evidence requires writable output.");
        ArgumentNullException.ThrowIfNull(request.Artifacts);
        if (request.Artifacts.Count > limits.Store.MaxArtifacts) throw Limit("Artifacts");
        var artifacts = new CaptureArtifactInfo[request.Artifacts.Count];
        for (var i = 0; i < artifacts.Length; i++) artifacts[i] = request.Artifacts[i];
        request = request with { Artifacts = Array.AsReadOnly(artifacts) };
        ValidateAdmissionPaths(request);
        var payload = EncodeAdmissionRequest(request, limits);
        return Task.Run(() =>
        {
            using var lease = new FileStream(request.Database, FileMode.Open, FileAccess.Read, FileShare.Read);
            ValidateSqliteHeader(lease, limits.Store.MaxDatabaseBytes);
            var nonce = Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo(request.Executable)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = request.PrivateDirectory
            };
            start.Environment.Clear();
            foreach (var argument in new[] { nonce, request.SqliteLibrary, request.PrivateDirectory,
                new Uri(request.Database).AbsoluteUri + "?immutable=1", request.Database, "--admit" })
                start.ArgumentList.Add(argument);
            ProtocolOutcome<SqliteAdmissionResult> result;
            try
            {
                result = RunProtocol(start, nonce, limits.Worker, payload,
                    (stream, token) => SqliteAdmissionWire.ReadAsync(stream, evidence, request, limits, token), cancellationToken,
                    request.BeforeInput, request.AfterExit);
            }
            catch (IOException ex)
            {
                throw CapturePackage.Error(CaptureErrorCode.StorageFailure, "Admission evidence I/O failed; discard the provisional prefix.", ex);
            }
            return result.Result with { LandlockAbi = result.Abi, PeakObservedRss = result.PeakRss,
                MaximumObservationGap = result.MaximumGap, WallTime = result.WallTime };
        }, CancellationToken.None);
    }

    private static void ValidateAdmissionPaths(SqliteAdmissionRequest request)
    {
        foreach (var path in new[] { request.Executable, request.SqliteLibrary, request.PrivateDirectory, request.Database })
        {
            if (!Path.IsPathFullyQualified(path) || path.Length > 2048 || path.Contains('\0'))
                throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "Admission requires bounded absolute staging/asset paths.");
            CapturePackage.RejectLinks(path);
            if (!File.Exists(path) && !Directory.Exists(path)) throw Unsupported("WorkerAssetUnavailable");
        }
        var relative = Path.GetRelativePath(request.PrivateDirectory, request.Database);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw CapturePackage.Error(CaptureErrorCode.UnsafePath, "Database must belong to private owned staging.");
        if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(request.PrivateDirectory) &
            (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
             UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw Unsupported("WorkerStagingNotPrivate");
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(request.Database + suffix))
                throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Header.ExternalJournal: admission requires the complete main file alone.");
    }

    internal static void ValidateSqliteHeader(Stream input, long maximum)
    {
        if (input.Length > maximum) throw Limit("DatabaseBytes");
        if (input.Length < 100) throw CorruptAdmission("Header.Truncated");
        Span<byte> header = stackalloc byte[100];
        input.ReadExactly(header);
        var encoded = BinaryPrimitives.ReadUInt16BigEndian(header[16..]);
        var pageSize = encoded == 1 ? 65536 : encoded;
        var pages = BinaryPrimitives.ReadUInt32BigEndian(header[28..]);
        if (!header[..16].SequenceEqual("SQLite format 3\0"u8) || pageSize < 512 ||
            (pageSize & (pageSize - 1)) != 0 || pages == 0 || (long)pages * pageSize != input.Length ||
            header[18] is not (1 or 2) || header[19] is not (1 or 2) ||
            header[20] != 0 || header[21] != 64 || header[22] != 32 || header[23] != 32 ||
            BinaryPrimitives.ReadUInt32BigEndian(header[56..]) != 1 ||
            header.Slice(72, 20).IndexOfAnyExcept((byte)0) != -1 ||
            BinaryPrimitives.ReadUInt32BigEndian(header[24..]) != BinaryPrimitives.ReadUInt32BigEndian(header[92..]))
            throw CorruptAdmission("Header.GeometryOrEncoding");
    }

    private static byte[] EncodeAdmissionRequest(SqliteAdmissionRequest request, SqliteAdmissionLimits limits)
    {
        if (!CapturePackage.IsSupportedFormat(request.Format))
            throw Unsupported("Format.Unsupported");
        if (request.Persisted < 0) throw CorruptAdmission("Data.PersistedPopulation");
        if (request.Artifacts.Count > limits.Store.MaxArtifacts) throw Limit("Artifacts");
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory, CapturePackage.Utf8, leaveOpen: true);
        writer.Write("GO\n"u8);
        writer.Write(0);
        writer.Write(request.IncludeUsage ? 2 : 1);
        foreach (var axis in new[] { request.Format.PackageVersion, request.Format.SchemaVersion,
            request.Format.RecordVersion, request.Format.IndexVersion, request.Format.WriterVersion, request.Format.RequiredReaderVersion })
            writer.Write(axis);
        writer.Write(request.Persisted);
        writer.Write(limits.RowsPerTable); writer.Write(limits.RowsPerCapture);
        writer.Write(limits.Store.MaxRecordBytes); writer.Write(limits.Store.MaxFields);
        writer.Write(limits.Store.MaxSnapshotBytes); writer.Write(limits.Store.MaxArtifacts);
        writer.Write(limits.Store.MaxLogicalBytes); writer.Write(limits.VmInstructions);
        writer.Write(request.Artifacts.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in request.Artifacts)
        {
            CapturePackage.ValidateId(artifact.ArtifactId);
            if (!ids.Add(artifact.ArtifactId)) throw CorruptAdmission("Data.ArtifactDescriptor");
            CapturePackage.ValidateText(artifact.Kind, 1024, nameof(artifact.Kind));
            CapturePackage.ValidateText(artifact.Name, 1024, nameof(artifact.Name));
            if (string.IsNullOrWhiteSpace(artifact.Kind) || artifact.Name is null) throw CorruptAdmission("Data.ArtifactDescriptor");
            foreach (var value in new[] { artifact.ArtifactId, artifact.Kind, artifact.Name })
            {
                var bytes = CapturePackage.Utf8.GetBytes(value);
                if (memory.Length + 4 + bytes.Length > 128 * 1024 + 7) throw Limit("RequestBytes");
                writer.Write(bytes.Length); writer.Write(bytes);
            }
        }
        var payload = memory.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(3), payload.Length - 7);
        return payload;
    }

    internal static CaptureStoreException CorruptAdmission(string reason) =>
        CapturePackage.Error(CaptureErrorCode.CorruptPackage, reason + ": SQLite structure/scalar admission failed; discard provisional evidence.");
}

internal static class SqliteAdmissionWire
{
    private static readonly int[] Columns = [6, 2, 3, 9, 9, 3];

    internal static async Task<SqliteAdmissionResult> ReadAsync(Stream source, Stream evidence,
        SqliteAdmissionRequest request, SqliteAdmissionLimits limits, CancellationToken token)
    {
        var prefix = new byte[4];
        var buffer = new byte[65536];
        var rows = new long[6];
        long wire = 0, total = 0, tokens = 0;
        var table = -1;
        var previousTable = -1;
        var column = 0;
        var remaining = 0;
        var idBytes = new byte[32];
        var idOffset = -1;
        var artifactIds = request.Artifacts.Select(static a => a.ArtifactId).ToHashSet(StringComparer.Ordinal);
        var seenArtifacts = new HashSet<string>(StringComparer.Ordinal);
        byte[]? snapshot = null;
        var snapshotOffset = 0;
        while (true)
        {
            await Exact(source, prefix, token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
            if (length is < 1 or > 65536) throw IsolatedCaptureWorker.Limit("WorkerFrameBytes");
            wire = checked(wire + 4 + length);
            if (wire > 512L * 1024 * 1024) throw IsolatedCaptureWorker.Limit("WorkerWireBytes");
            await Exact(source, buffer.AsMemory(0, (int)length), token).ConfigureAwait(false);
            var tag = buffer[0];
            if (tag == 127)
            {
                if (length > 128) throw IsolatedCaptureWorker.CorruptAdmission("Wire.ErrorFrame");
                for (var i = 1; i < length; i++)
                    if (buffer[i] > 127) throw IsolatedCaptureWorker.CorruptAdmission("Wire.ErrorReason");
                var reason = CapturePackage.Utf8.GetString(buffer, 1, (int)length - 1);
                if (reason.Length == 0 || reason.Any(static c => !char.IsAsciiLetterOrDigit(c) && c != '.'))
                    throw IsolatedCaptureWorker.CorruptAdmission("Wire.ErrorReason");
                if (reason.StartsWith("Limit.", StringComparison.Ordinal)) throw IsolatedCaptureWorker.Limit(reason);
                if (reason is "Format.Unsupported" || reason.EndsWith("Unavailable", StringComparison.Ordinal))
                    throw IsolatedCaptureWorker.Unsupported(reason);
                throw IsolatedCaptureWorker.CorruptAdmission(reason);
            }
            if (tag == 1)
            {
                if (length != 3 || table != -1 || buffer[1] > 5 || buffer[1] < previousTable ||
                    buffer[2] != Columns[buffer[1]]) throw IsolatedCaptureWorker.CorruptAdmission("Wire.Row");
                table = buffer[1]; previousTable = table; column = 0;
                if (++rows[table] > limits.RowsPerTable || ++total > limits.RowsPerCapture)
                    throw IsolatedCaptureWorker.Limit("WorkerRows");
                if (table is 2 or 5 && rows[table] > limits.Store.MaxArtifacts)
                    throw IsolatedCaptureWorker.Limit("Artifacts");
            }
            else if (tag == 2)
            {
                if (length != 6 || table < 0 || remaining != 0 || column >= Columns[table])
                    throw IsolatedCaptureWorker.CorruptAdmission("Wire.Cell");
                var type = buffer[1];
                var size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(2));
                if (type is < 1 or > 5 || type == 5 && size != 0 || type is 1 or 2 && size != 8)
                    throw IsolatedCaptureWorker.CorruptAdmission("Wire.StorageClass");
                var expected = ExpectedType(table, column);
                var nullable = table == 3 && column >= 2 || table == 4 && column >= 4;
                if (type != expected && !(nullable && type == 5))
                    throw IsolatedCaptureWorker.CorruptAdmission("Wire.StorageClass");
                var maximum = type == 4 ? limits.Store.MaxSnapshotBytes : table == 2 && column > 0 ? 1024 :
                    type == 3 && (table == 2 || table == 3 || table == 5) ? 32 : limits.Store.MaxRecordBytes;
                if (size > maximum) throw IsolatedCaptureWorker.Limit(type == 4 ? "SnapshotBytes" : "CellBytes");
                if (type == 4 && size == 0) throw IsolatedCaptureWorker.CorruptAdmission("Snapshot.Empty");
                var identifier = table is 2 or 5 && column == 0 || table == 3 && column == 1;
                if (identifier && size != 32) throw IsolatedCaptureWorker.CorruptAdmission("Wire.ArtifactId");
                idOffset = identifier ? 0 : -1;
                remaining = (int)size;
                if (type == 4)
                {
                    snapshot = new byte[remaining];
                    snapshotOffset = 0;
                }
                if (remaining == 0) column++;
            }
            else if (tag == 3)
            {
                var count = (int)length - 1;
                if (table < 0 || count == 0 || count > remaining) throw IsolatedCaptureWorker.CorruptAdmission("Wire.CellChunk");
                if (snapshot is not null)
                {
                    buffer.AsSpan(1, count).CopyTo(snapshot.AsSpan(snapshotOffset));
                    snapshotOffset += count;
                }
                if (idOffset >= 0)
                {
                    buffer.AsSpan(1, count).CopyTo(idBytes.AsSpan(idOffset));
                    idOffset += count;
                }
                remaining -= count;
                if (remaining == 0)
                {
                    column++;
                    if (idOffset >= 0)
                    {
                        if (idBytes.Any(static b => b is not (>= (byte)'0' and <= (byte)'9') and not (>= (byte)'a' and <= (byte)'f')))
                            throw IsolatedCaptureWorker.CorruptAdmission("Wire.ArtifactId");
                        var id = CapturePackage.Utf8.GetString(idBytes);
                        if (!artifactIds.Contains(id) || table == 2 && !seenArtifacts.Add(id))
                            throw IsolatedCaptureWorker.CorruptAdmission("Wire.ArtifactId");
                        idOffset = -1;
                    }
                    if (snapshot is not null)
                    {
                        tokens += CountSnapshotTokens(snapshot, limits.TokensPerSnapshot);
                        if (tokens > limits.TokensPerCapture) throw IsolatedCaptureWorker.Limit("TokensPerCapture");
                        snapshot = null;
                    }
                }
            }
            else if (tag == 4)
            {
                if (length != 1 || table < 0 || remaining != 0 || column != Columns[table])
                    throw IsolatedCaptureWorker.CorruptAdmission("Wire.RowEnd");
                table = -1;
            }
            else if (tag == 5)
            {
                if (length != (request.IncludeUsage ? 73 : 65) || table != -1 || rows[0] != 1 || rows[2] != request.Artifacts.Count ||
                    rows[3] != request.Persisted) throw IsolatedCaptureWorker.CorruptAdmission("Wire.Completion");
                for (var i = 0; i < 6; i++)
                    if (BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(1 + i * 8)) != rows[i])
                        throw IsolatedCaptureWorker.CorruptAdmission("Wire.Population");
                var logical = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(49));
                var work = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(57));
                if (logical < 0 || logical > limits.Store.MaxLogicalBytes || work < 0 || work > limits.VmInstructions)
                    throw IsolatedCaptureWorker.CorruptAdmission("Wire.Budget");
                var cpu = request.IncludeUsage ? BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(65)) : 0;
                if (cpu < 0 || cpu > limits.Worker.CpuTime.Ticks / 10) throw IsolatedCaptureWorker.Limit("WorkerCpuTime");
                BinaryPrimitives.WriteUInt32LittleEndian(prefix, 65);
                await evidence.WriteAsync(prefix, token).ConfigureAwait(false);
                await evidence.WriteAsync(buffer.AsMemory(0, 65), token).ConfigureAwait(false);
                if (await source.ReadAsync(prefix.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
                    throw IsolatedCaptureWorker.CorruptAdmission("Wire.Trailing");
                await evidence.FlushAsync(token).ConfigureAwait(false);
                return new(Array.AsReadOnly(rows), logical, work, wire, 0, 0, TimeSpan.Zero)
                    { CpuTime = TimeSpan.FromTicks(cpu * 10) };
            }
            else throw IsolatedCaptureWorker.CorruptAdmission("Wire.Tag");
            await evidence.WriteAsync(prefix, token).ConfigureAwait(false);
            await evidence.WriteAsync(buffer.AsMemory(0, (int)length), token).ConfigureAwait(false);
        }
    }

    private static int ExpectedType(int table, int column) => table switch
    {
        0 => 1,
        1 => column == 0 ? 1 : 3,
        2 => 3,
        3 => column == 1 ? 3 : column == 6 ? 2 : 1,
        4 => column == 6 ? 2 : 1,
        _ => column == 0 ? 3 : column == 1 ? 1 : 4
    };

    private static async Task Exact(Stream source, Memory<byte> bytes, CancellationToken token)
    {
        try { await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false); }
        catch (EndOfStreamException) { throw IsolatedCaptureWorker.CorruptAdmission("Wire.Truncated"); }
    }

    private static long CountSnapshotTokens(ReadOnlySpan<byte> json, int maximum)
    {
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 64 });
            long count = 0;
            while (reader.Read())
                if (++count > maximum) throw IsolatedCaptureWorker.Limit("TokensPerSnapshot");
            if (count == 0) throw IsolatedCaptureWorker.CorruptAdmission("Snapshot.Json");
            return count;
        }
        catch (JsonException ex)
        {
            throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Snapshot.JsonDepthOrSyntax", ex);
        }
    }
}
