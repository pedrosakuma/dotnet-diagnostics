using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DotnetDiagnostics.Core.Artifacts;
using Microsoft.Data.Sqlite;

namespace DotnetDiagnostics.Core.Captures;

internal sealed record CaptureManifest(
    CaptureInfo Info, int PackageVersion, int SchemaVersion, int RecordVersion,
    int IndexVersion, int WriterVersion, int ReaderVersion, string[]? RequiredFeatures, long ReservationBytes);

internal sealed record CaptureSeal(string ManifestHash, string DatabaseHash);

internal static class CapturePackage
{
    internal const string Database = "capture.sqlite";
    internal const string Manifest = "manifest.json";
    internal const string Seal = "seal.json";
    internal const string Lease = ".lease";
    internal const int MetadataLimit = 128 * 1024;
    internal const string RecoveryIdentityFeature = "recovery-artifact-identity-v1";
    internal static readonly CaptureFormatVersions CurrentFormat = new(2, 1, 1, 1, 2, 2);
    private static readonly CaptureFormatVersions PreviousFormat = new(1, 1, 1, 1, 1, 1);
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HashSet<string> Members = new(StringComparer.Ordinal)
    {
        Database, Database + "-wal", Database + "-shm", Manifest, Seal, Lease,
        Manifest + ".pending", Seal + ".pending"
    };

    internal static CaptureStoreException Error(CaptureErrorCode code, string message, Exception? cause = null)
        => new(code, message, cause);

    internal static CaptureStoreException Translate(Exception ex) => ex switch
    {
        CaptureStoreException known => known,
        SqliteException { SqliteErrorCode: 13 } => Error(CaptureErrorCode.CapacityExceeded, "SQLite main-file or filesystem capacity exhausted.", ex),
        SqliteException { SqliteErrorCode: 11 or 26 } => Error(CaptureErrorCode.CorruptPackage, "SQLite evidence is corrupt or is not a database.", ex),
        JsonException => Error(CaptureErrorCode.CorruptPackage, "Package metadata is invalid JSON.", ex),
        ArtifactPathException => Error(CaptureErrorCode.UnsafePath, "Artifact path validation failed.", ex),
        FileNotFoundException or DirectoryNotFoundException => Error(CaptureErrorCode.NotFound, "Capture package or required member is missing.", ex),
        _ => Error(CaptureErrorCode.StorageFailure, "Capture storage operation failed; retain evidence and inspect the inner error.", ex)
    };

    internal static void ValidateId(string id)
    {
        if (id is null || id.Length != 32 || !Guid.TryParseExact(id, "N", out _) ||
            id.Any(static c => c is >= 'A' and <= 'F'))
            throw Error(CaptureErrorCode.InvalidInput, "An ID must be a lowercase GUID in N format.");
    }

    internal static void ValidateAccess(CaptureAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        ValidateText(access.OwnerId, 1024, "OwnerId");
        if (string.IsNullOrWhiteSpace(access.OwnerId))
            throw Error(CaptureErrorCode.InvalidInput, "OwnerId must not be blank.");
    }

    internal static void Authorize(CaptureInfo info, CaptureAccess access)
    {
        ValidateAccess(access);
        if (!access.AllOwners && !string.Equals(info.OwnerId, access.OwnerId, StringComparison.Ordinal))
            throw Error(CaptureErrorCode.Forbidden, "The current caller does not own this capture.");
    }

    internal static void ValidateText(string? text, int maxBytes, string name)
    {
        try
        {
            if (text is not null && (text.Length > maxBytes || Utf8.GetByteCount(text) > maxBytes))
                throw Error(CaptureErrorCode.InvalidInput, $"{name} exceeds its UTF-8 bound.");
        }
        catch (EncoderFallbackException ex)
        {
            throw Error(CaptureErrorCode.InvalidInput, $"{name} contains invalid Unicode.", ex);
        }
    }

    internal static void RejectLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current) || new FileInfo(current).LinkTarget is not null) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Error(CaptureErrorCode.UnsafePath, "Symlink/reparse-point paths are not supported for captures.");
            current = Path.GetDirectoryName(current);
        }
    }

    internal static void ValidateMembers(string directory)
    {
        RejectLinks(directory);
        var count = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if (++count > Members.Count || !Members.Contains(Path.GetFileName(path)) ||
                (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw Error(CaptureErrorCode.UnsafePath, "Capture contains an unsupported member, directory, or link.");
        }
    }

    internal static FileStream AcquireLease(string directory, bool exclusive)
    {
        var path = Path.Combine(directory, Lease);
        RejectLinks(path);
        try
        {
            return new FileStream(path, FileMode.Open, exclusive ? FileAccess.ReadWrite : FileAccess.Read,
                exclusive ? FileShare.None : FileShare.Read);
        }
        catch (IOException ex) when (File.Exists(path))
        {
            throw Error(CaptureErrorCode.Busy, "Capture is leased by another reader, writer, delete, or recovery operation.", ex);
        }
    }

    internal static T ReadJson<T>(string path)
    {
        RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > MetadataLimit)
            throw Error(CaptureErrorCode.CorruptPackage, "Package metadata exceeds the format bound.");
        return JsonSerializer.Deserialize(stream, JsonType<T>()) ??
            throw Error(CaptureErrorCode.CorruptPackage, "Package metadata is null.");
    }

    internal static void WriteJson<T>(string directory, string name, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonType<T>());
        if (bytes.Length > MetadataLimit)
            throw Error(CaptureErrorCode.CapacityExceeded, "Package metadata exceeds the format bound.");
        var target = Path.Combine(directory, name);
        var pending = target + ".pending";
        RejectLinks(target);
        RejectLinks(pending);
        using (var stream = SafeArtifactPath.CreateRestrictedFile(pending))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(pending, target, overwrite: true);
    }

    private static JsonTypeInfo<T> JsonType<T>() =>
        (JsonTypeInfo<T>?)CaptureJsonContext.Default.GetTypeInfo(typeof(T)) ??
        throw new InvalidOperationException("Capture metadata requires an explicitly source-generated format type.");

    internal static CaptureManifest ReadManifest(string directory, string id)
    {
        var manifest = ReadJson<CaptureManifest>(Path.Combine(directory, Manifest));
        var format = FormatOf(manifest);
        var features = manifest.RequiredFeatures;
        var supportedFeatures = format == PreviousFormat
            ? features is { Length: 1 } && features[0] == "normalized-scalars-v1"
            : features is { Length: 2 or 3 } && features.Contains("normalized-scalars-v1", StringComparer.Ordinal) &&
              features.Contains("artifact-provenance-v1", StringComparer.Ordinal) &&
              (features.Length == 2 || features.Contains(RecoveryIdentityFeature, StringComparer.Ordinal));
        if (!IsSupportedFormat(format) || !supportedFeatures)
            throw Error(CaptureErrorCode.UnsupportedFormat, "Unsupported package/schema/record/index/writer/reader version or required feature.");
        if (manifest.Info is null || manifest.Info.CaptureId != id || manifest.Info.Artifacts is null ||
            manifest.Info.Artifacts.Count > 64 || manifest.Info.Quality is null ||
            !Enum.IsDefined(manifest.Info.State) || manifest.ReservationBytes is < 64 * 1024 or > 512 * 1024 * 1024)
            throw Error(CaptureErrorCode.CorruptPackage, "Invalid capture identity or metadata.");
        ValidateAccess(new CaptureAccess(manifest.Info.OwnerId));
        ValidateText(manifest.Info.Name, 1024, nameof(manifest.Info.Name));
        ValidateText(manifest.Info.GroupId, 1024, nameof(manifest.Info.GroupId));
        var quality = manifest.Info.Quality;
        if (manifest.Info.Name is null || quality.Offered < 0 || quality.Accepted < 0 ||
            quality.Persisted < 0 || quality.RecordRejected < 0 || quality.QueueRejected < 0 ||
            quality.StorageRejected < 0 || quality.Pending < 0 || quality.SourceRejected < 0 ||
            quality.SnapshotRejected < 0 || quality.Accepted > quality.Offered || quality.Persisted > quality.Accepted ||
            quality.Offered - quality.Persisted - quality.RecordRejected - quality.QueueRejected - quality.StorageRejected != quality.Pending ||
            manifest.Info.State == CaptureState.Sealed && quality.Pending != 0)
            throw Error(CaptureErrorCode.CorruptPackage, "Invalid capture quality populations or name.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var hasRecoveryIdentity = features!.Contains(RecoveryIdentityFeature, StringComparer.Ordinal);
        var aliases = 0;
        foreach (var artifact in manifest.Info.Artifacts)
        {
            if (artifact is null || artifact.Name is null || string.IsNullOrWhiteSpace(artifact.Kind))
                throw Error(CaptureErrorCode.CorruptPackage, "Invalid artifact metadata.");
            ValidateId(artifact.ArtifactId);
            ValidateText(artifact.Kind, 1024, nameof(artifact.Kind));
            ValidateText(artifact.Name, 1024, nameof(artifact.Name));
            if (artifact.Provenance is not null)
            {
                if (format == PreviousFormat)
                    throw Error(CaptureErrorCode.UnsupportedFormat, "Artifact provenance requires the v2 package representation.");
                ValidateProvenance(artifact.Provenance);
            }
            if (!ids.Add(artifact.ArtifactId))
                throw Error(CaptureErrorCode.CorruptPackage, "Duplicate or ambiguous artifact identity.");
            if (artifact.SourceArtifactId is { } sourceId)
            {
                if (!hasRecoveryIdentity)
                    throw Error(CaptureErrorCode.UnsupportedFormat, "Recovery artifact identity requires its declared versioned feature.");
                ValidateId(sourceId);
                if (!ids.Add(sourceId))
                    throw Error(CaptureErrorCode.CorruptPackage, "Recovery source identity collides with another current or source artifact identity.");
                aliases++;
            }
        }
        if (hasRecoveryIdentity && (aliases == 0 || manifest.Info.DerivedFrom is null))
            throw Error(CaptureErrorCode.CorruptPackage, "Recovery identity feature requires derived capture metadata and actual source identities.");
        return manifest;
    }

    internal static CaptureFormatVersions FormatOf(CaptureManifest manifest) => new(
        manifest.PackageVersion, manifest.SchemaVersion, manifest.RecordVersion,
        manifest.IndexVersion, manifest.WriterVersion, manifest.ReaderVersion);

    private static bool IsSupportedFormat(CaptureFormatVersions format) =>
        format == CurrentFormat || format == PreviousFormat;

    internal static void ValidateProvenance(CaptureArtifactProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        if (provenance.ProcessId <= 0 || provenance.Duration < TimeSpan.Zero)
            throw Error(CaptureErrorCode.InvalidInput, "Provenance process ID must be positive and duration nonnegative when supplied.");
        ValidateText(provenance.ProducingTool, 1024, nameof(provenance.ProducingTool));
        ValidateText(provenance.OriginalHandleOrigin, 1024, nameof(provenance.OriginalHandleOrigin));
        ValidateText(provenance.RuntimeName, 1024, nameof(provenance.RuntimeName));
        ValidateText(provenance.RuntimeVersion, 1024, nameof(provenance.RuntimeVersion));
    }

    internal static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static long PackageBytes(string directory)
    {
        ValidateMembers(directory);
        long size = 0;
        foreach (var file in Directory.EnumerateFiles(directory))
            size = checked(size + new FileInfo(file).Length);
        return size;
    }

    internal static SqliteConnection Connect(string directory, bool immutable)
    {
        var path = Path.Combine(directory, Database);
        RejectLinks(path);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = immutable ? new Uri(path).AbsoluteUri + "?immutable=1" : path,
            Mode = immutable ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 1
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF;");
            if (immutable) Execute(connection, "PRAGMA query_only=ON;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static void ValidateDatabase(SqliteConnection connection, CaptureFormatVersions? expectedFormat = null)
    {
        try { ValidateDatabaseCore(connection, expectedFormat); }
        catch (SqliteException ex)
        {
            throw Error(CaptureErrorCode.CorruptPackage, "Required SQLite schema is missing or corrupt.", ex);
        }
    }

    private static void ValidateDatabaseCore(SqliteConnection connection, CaptureFormatVersions? expectedFormat)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT package,schema_version,record_version,index_version,writer_version,reader_version FROM format;";
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                throw Error(CaptureErrorCode.CorruptPackage, "SQLite format descriptor is missing.");
            var format = new CaptureFormatVersions(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5));
            if (!IsSupportedFormat(format))
                throw Error(CaptureErrorCode.UnsupportedFormat, "SQLite format versions are unsupported.");
            if (reader.Read() || expectedFormat is not null && format != expectedFormat)
                throw Error(CaptureErrorCode.CorruptPackage, "SQLite and manifest format descriptors disagree or are duplicated.");
        }
        command.CommandText = "PRAGMA quick_check;";
        if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
            throw Error(CaptureErrorCode.CorruptPackage, "SQLite quick_check rejected this package.");
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='index' AND name IN ('ix_occurrence_artifact','ix_occurrence_time','ix_occurrence_thread','ix_occurrence_category','ix_occurrence_name','ix_fields_name');";
        if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 6)
            throw Error(CaptureErrorCode.CorruptPackage, "Required occurrence indexes are missing.");
        command.CommandText = """
            SELECT a.id,a.kind,a.name,o.id,o.timestamp_ticks,o.thread_id,o.category_id,o.name_id,
            o.numeric_value,o.duration_ns,o.unit_id,f.ordinal,f.name_id,f.kind,f.string_id,
            f.int_value,f.double_value,f.bool_value,f.unit_id,s.value,p.version,p.json
            FROM artifacts a,occurrences o,fields f,strings s,snapshots p WHERE 0;
            """;
        using var shape = command.ExecuteReader();
    }

    internal const string Schema = """
        CREATE TABLE format(package INTEGER NOT NULL,schema_version INTEGER NOT NULL,record_version INTEGER NOT NULL,index_version INTEGER NOT NULL,writer_version INTEGER NOT NULL,reader_version INTEGER NOT NULL);
        INSERT INTO format VALUES(2,1,1,1,2,2);
        CREATE TABLE strings(id INTEGER PRIMARY KEY,value TEXT NOT NULL UNIQUE);
        CREATE TABLE artifacts(id TEXT PRIMARY KEY,kind TEXT NOT NULL,name TEXT NOT NULL);
        CREATE TABLE occurrences(
          id INTEGER PRIMARY KEY,artifact_id TEXT NOT NULL REFERENCES artifacts(id),
          timestamp_ticks INTEGER,thread_id INTEGER,category_id INTEGER REFERENCES strings(id),
          name_id INTEGER REFERENCES strings(id),numeric_value REAL,duration_ns INTEGER,unit_id INTEGER REFERENCES strings(id));
        CREATE TABLE fields(
          record_id INTEGER NOT NULL REFERENCES occurrences(id),ordinal INTEGER NOT NULL,
          name_id INTEGER NOT NULL REFERENCES strings(id),kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 4),
          string_id INTEGER REFERENCES strings(id),int_value INTEGER,double_value REAL,bool_value INTEGER,
          unit_id INTEGER REFERENCES strings(id),PRIMARY KEY(record_id,ordinal));
        CREATE TABLE snapshots(artifact_id TEXT PRIMARY KEY REFERENCES artifacts(id),version INTEGER NOT NULL,json BLOB NOT NULL);
        CREATE INDEX ix_occurrence_artifact ON occurrences(artifact_id,id);
        CREATE INDEX ix_occurrence_time ON occurrences(artifact_id,timestamp_ticks,id);
        CREATE INDEX ix_occurrence_thread ON occurrences(artifact_id,thread_id,id);
        CREATE INDEX ix_occurrence_category ON occurrences(artifact_id,category_id,id);
        CREATE INDEX ix_occurrence_name ON occurrences(artifact_id,name_id,id);
        CREATE INDEX ix_fields_name ON fields(name_id,record_id);
        """;
}
