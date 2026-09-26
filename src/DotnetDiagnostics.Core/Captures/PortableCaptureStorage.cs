using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DotnetDiagnostics.Core.Artifacts;

namespace DotnetDiagnostics.Core.Captures;

internal sealed partial class PortableCaptureStorage : IDisposable
{
    private static readonly HashSet<string> Members = new(StringComparer.Ordinal)
    {
        ".lease", "receipt.json", "receipt.pending", "bundle.pending", "bundle.ddcapture"
    };
    private readonly SqliteCaptureStore _store;
    private readonly FileStream _lease;
    internal string DirectoryPath { get; }
    internal PortableExportReceipt Receipt { get; private set; }
    internal string ArchivePath => Path.Combine(DirectoryPath, "bundle.ddcapture");
    internal bool Reused { get; }

    private PortableCaptureStorage(SqliteCaptureStore store, string path, FileStream lease,
        PortableExportReceipt receipt, bool reused)
    {
        _store = store;
        DirectoryPath = path;
        _lease = lease;
        Receipt = receipt;
        Reused = reused;
    }

    internal static PortableCaptureStorage Begin(SqliteCaptureStore store, PortableOperationKey key,
        CaptureAccess access, string fingerprint, DateTimeOffset now, PortableImportJournal? import = null)
    {
        CapturePackage.ValidateId(key.Id);
        if (key.RequestedUtc > now.AddMinutes(5) || key.RequestedUtc <= now.AddHours(-24))
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "OperationExpired: operation time is outside its supported lifetime.");
        var root = store.PortableRoot();
        if (!Directory.Exists(root))
            throw CapturePackage.Error(CaptureErrorCode.NotFound, "Capture store does not exist.");
        using var admission = SqliteCaptureStore.PortableAdmission(root);
        CleanupLocked(root, now);
        var portable = SafeArtifactPath.ResolveCaptureDirectory(root, ".portable");
        var name = Digest(CapturePackage.Utf8.GetBytes(access.OwnerId + "\0" + key.Id));
        var path = Path.Combine(portable, name);
        CapturePackage.RejectLinks(path);
        var directories = Directories(root);
        var active = 0;
        var ownerActive = false;
        var bundleIds = new HashSet<string>(StringComparer.Ordinal);
        var watch = Stopwatch.StartNew();
        foreach (var directory in directories)
        {
            PortableBounds.Check("ControlMilliseconds", watch.ElapsedMilliseconds, 5000);
            var receipt = ReadReceipt(directory);
            bundleIds.Add(receipt.BundleId);
            using var probe = TryLease(directory);
            if (probe is not null) continue;
            active++;
            ownerActive |= string.Equals(receipt.OwnerId, access.OwnerId, StringComparison.Ordinal);
        }
        if (active >= 2 || ownerActive)
            throw CapturePackage.Error(CaptureErrorCode.Busy, "Portable operation slots are occupied (two per store, one per owner).");
        if (Directory.Exists(path))
        {
            var receipt = ReadReceipt(path);
            if (receipt.OwnerId != access.OwnerId || receipt.Operation != key || receipt.Fingerprint != fingerprint)
                throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "OperationConflict: operation key already identifies another request.");
            if ((receipt.Import is null) != (import is null))
                throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "OperationConflict: operation kind differs.");
            if (import is null && (receipt.Result is null || receipt.BytesExpireUtc <= now || !File.Exists(Path.Combine(path, "bundle.ddcapture"))))
                throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "TransferExpired: staged export is unavailable; use a new operation key.");
            return ReuseUnderAdmission(store, path, receipt, import is not null);
        }
        if (key.RequestedUtc < now.AddMinutes(-5))
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "OperationExpired: first use must be within five minutes.");
        PortableBounds.Check("MaxReceipts", directories.Count + 1L, 64);
        store.CheckPortableAdmission(root, PortableBounds.ReceiptReservation);
        path = SafeArtifactPath.ResolveCaptureDirectory(portable, name);
        using (SafeArtifactPath.CreateRestrictedFile(Path.Combine(path, ".lease"))) { }
        string bundleId;
        do { bundleId = Guid.NewGuid().ToString("N"); } while (bundleIds.Contains(bundleId));
        var created = new PortableExportReceipt(access.OwnerId, key, fingerprint, bundleId,
            now.ToUniversalTime(), now.AddHours(1), PortableBounds.ReceiptReservation, null, import);
        WriteReceipt(path, created);
        return new(store, path, TryLease(path) ??
            throw CapturePackage.Error(CaptureErrorCode.Busy, "Portable lease could not be acquired."), created, reused: false);
    }

    internal static PortableCaptureStorage ReuseUnderAdmission(SqliteCaptureStore store, string path,
        PortableExportReceipt receipt, bool import)
    {
        var lease = TryLease(path) ?? throw CapturePackage.Error(CaptureErrorCode.Busy, "Portable retry is already active.");
        try
        {
            if (import)
            {
                // Cleanup may have seen this lease active immediately before its owner
                // exited. Reconcile after acquisition, not just during the earlier scan.
                receipt = ReconcileAndCleanImport(store.PortableRoot(), path, receipt);
            }
            return new(store, path, lease, receipt, reused: true);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static PortableExportReceipt ReconcileAndCleanImport(string root, string path, PortableExportReceipt receipt)
    {
        receipt = ReconcileImport(root, path, receipt);
        DeleteImportWork(path);
        receipt = receipt with { ReservationBytes = PortableBounds.ReceiptReservation };
        WriteReceipt(path, receipt);
        return receipt;
    }

    internal void Reserve(long archiveBytes)
    {
        var root = _store.PortableRoot();
        using var admission = SqliteCaptureStore.PortableAdmission(root);
        var reservation = checked(archiveBytes + PortableBounds.ReceiptReservation);
        PortableBounds.Check("MaxPrivateBytes", reservation, 2L * 1024 * 1024 * 1024);
        _store.CheckPortableAdmission(root, Math.Max(0, reservation - Receipt.ReservationBytes));
        Receipt = Receipt with { ReservationBytes = reservation };
        WriteReceipt(DirectoryPath, Receipt);
    }

    internal void Complete(PortableExportResult result)
    {
        using var admission = SqliteCaptureStore.PortableAdmission(_store.PortableRoot());
        Receipt = Receipt with { Result = result };
        WriteReceipt(DirectoryPath, Receipt);
    }

    internal void LimitRetention(DateTimeOffset expires)
    {
        using var admission = SqliteCaptureStore.PortableAdmission(_store.PortableRoot());
        Receipt = Receipt with { BytesExpireUtc = expires < Receipt.BytesExpireUtc ? expires : Receipt.BytesExpireUtc };
        WriteReceipt(DirectoryPath, Receipt);
    }

    internal void SaveTransfer(bool upload, long receivedBytes)
    {
        using var admission = SqliteCaptureStore.PortableAdmission(_store.PortableRoot());
        Receipt = Receipt with { Transfer = new(upload, receivedBytes) };
        WriteReceipt(DirectoryPath, Receipt);
    }

    internal void Abandon()
    {
        using var admission = SqliteCaptureStore.PortableAdmission(_store.PortableRoot());
        try
        {
            DeleteArchiveFiles(DirectoryPath);
            Receipt = Receipt with { ReservationBytes = PortableBounds.ReceiptReservation, BytesExpireUtc = DateTimeOffset.MinValue };
            WriteReceipt(DirectoryPath, Receipt);
        }
        catch (IOException ex)
        {
            throw CapturePackage.Error(CaptureErrorCode.StorageFailure, "CleanupFailed: export staging remains charged to the store.", ex);
        }
    }

    internal static long AccountedBytes(string root)
    {
        long bytes = 0;
        var calls = Path.Combine(root, ".portable-calls");
        CapturePackage.RejectLinks(calls);
        if (File.Exists(calls))
        {
            PortableBounds.Check("PortableCallControlBytes", new FileInfo(calls).Length, 16);
            bytes = 16;
        }
        var watch = Stopwatch.StartNew();
        foreach (var directory in Directories(root))
        {
            PortableBounds.Check("ControlMilliseconds", watch.ElapsedMilliseconds, 5000);
            var actual = ValidateMembers(directory);
            var receiptPath = Path.Combine(directory, "receipt.json");
            // Incomplete initialization/cleanup still consumes capacity; never silently ignore it.
            var reservation = File.Exists(receiptPath) ? ReadReceipt(directory).ReservationBytes : 2L * 1024 * 1024 * 1024;
            bytes = checked(bytes + Math.Max(actual, reservation));
        }
        PortableBounds.Check("ControlMilliseconds", watch.ElapsedMilliseconds, 5000);
        return bytes;
    }

    internal static void Cleanup(SqliteCaptureStore store, DateTimeOffset now)
    {
        var root = store.PortableRoot();
        if (!Directory.Exists(root)) return;
        using var admission = SqliteCaptureStore.PortableAdmission(root);
        CleanupLocked(root, now);
    }

    private static void CleanupLocked(string root, DateTimeOffset now)
    {
        var watch = Stopwatch.StartNew();
        foreach (var directory in Directories(root))
        {
            PortableBounds.Check("ControlMilliseconds", watch.ElapsedMilliseconds, 5000);
            if (!File.Exists(Path.Combine(directory, "receipt.json")))
            {
                CleanupUninitialized(directory);
                continue;
            }
            var receipt = ReadReceipt(directory);
            using (var lease = TryLease(directory))
            {
                if (lease is null) continue;
                // A host-held transfer has no restart capability. Its durable import
                // receipt survives, but byte staging is never adopted by a new host.
                if (receipt.Transfer is not null && receipt.Import is null)
                {
                    DeleteArchiveFiles(directory);
                    receipt = receipt with { BytesExpireUtc = DateTimeOffset.MinValue,
                        ReservationBytes = PortableBounds.ReceiptReservation };
                    WriteReceipt(directory, receipt);
                }
                if (receipt.Import is not null)
                {
                    receipt = ReconcileAndCleanImport(root, directory, receipt);
                    if (receipt.Operation.RequestedUtc.AddHours(24) > now) continue;
                }
                if (receipt.Result is not null && receipt.Operation.RequestedUtc.AddHours(24) > now &&
                    receipt.BytesExpireUtc > now) continue;
                try
                {
                    DeleteArchiveFiles(directory);
                    if (receipt.Operation.RequestedUtc.AddHours(24) > now)
                    {
                        WriteReceipt(directory, receipt with
                        {
                            ReservationBytes = PortableBounds.ReceiptReservation,
                            BytesExpireUtc = receipt.Result is null ? DateTimeOffset.MinValue : receipt.BytesExpireUtc
                        });
                        continue;
                    }
                    File.Delete(Path.Combine(directory, "receipt.pending"));
                    File.Delete(Path.Combine(directory, "receipt.json"));
                }
                catch (IOException ex)
                {
                    throw CapturePackage.Error(CaptureErrorCode.StorageFailure, "CleanupFailed: expired export remains reserved.", ex);
                }
            }
            File.Delete(Path.Combine(directory, ".lease"));
            Directory.Delete(directory);
        }
    }

    private static void CleanupUninitialized(string directory)
    {
        _ = ValidateMembers(directory);
        var leasePath = Path.Combine(directory, ".lease");
        using (var lease = File.Exists(leasePath) ? TryLease(directory) : null)
        {
            if (lease is null && File.Exists(leasePath)) return;
            if (File.Exists(Path.Combine(directory, "bundle.pending")) || File.Exists(Path.Combine(directory, "bundle.ddcapture")))
                throw CapturePackage.Error(CaptureErrorCode.StorageFailure,
                    "CleanupFailed: orphan archive has no receipt; retain it for operator inspection.");
            File.Delete(Path.Combine(directory, "receipt.pending"));
        }
        File.Delete(leasePath);
        Directory.Delete(directory);
    }

    private static void DeleteArchiveFiles(string directory)
    {
        _ = ValidateMembers(directory);
        File.Delete(Path.Combine(directory, "bundle.pending"));
        File.Delete(Path.Combine(directory, "bundle.ddcapture"));
    }

    private static List<string> Directories(string root)
    {
        var portable = Path.Combine(root, ".portable");
        CapturePackage.RejectLinks(portable);
        var result = new List<string>();
        if (!Directory.Exists(portable)) return result;
        foreach (var path in Directory.EnumerateFileSystemEntries(portable))
        {
            PortableBounds.Check("MaxReceipts", result.Count + 1L, 64);
            CapturePackage.RejectLinks(path);
            var name = Path.GetFileName(path);
            if (!Directory.Exists(path) || name.Length != 64 || name.Any(static c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
                throw CapturePackage.Error(CaptureErrorCode.UnsafePath, "Portable control namespace contains an unexpected entry.");
            result.Add(path);
        }
        return result;
    }

    private static long ValidateMembers(string directory)
    {
        long bytes = 0;
        var count = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            CapturePackage.RejectLinks(path);
            if (Path.GetFileName(path) == "work" && Directory.Exists(path))
            {
                bytes = checked(bytes + ImportWorkBytes(path));
                continue;
            }
            if (++count > Members.Count || !Members.Contains(Path.GetFileName(path)) || Directory.Exists(path))
                throw CapturePackage.Error(CaptureErrorCode.UnsafePath, "Portable operation contains an unexpected member.");
            bytes = checked(bytes + new FileInfo(path).Length);
        }
        PortableBounds.Check("MaxPrivateBytes", bytes, 2L * 1024 * 1024 * 1024);
        return bytes;
    }

    private static FileStream? TryLease(string directory)
    {
        var path = Path.Combine(directory, ".lease");
        CapturePackage.RejectLinks(path);
        try { return new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) when (File.Exists(path)) { return null; }
    }

    private static PortableExportReceipt ReadReceipt(string directory)
    {
        _ = ValidateMembers(directory);
        using var stream = new FileStream(Path.Combine(directory, "receipt.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        PortableBounds.Check("MaxReceiptBytes", stream.Length, PortableBounds.ReceiptBytes);
        var receipt = JsonSerializer.Deserialize(stream, PortableCaptureJson.Default.PortableExportReceipt) ??
            throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Export receipt is null.");
        if (receipt.ReservationBytes < PortableBounds.ReceiptReservation || receipt.ReservationBytes > 2L * 1024 * 1024 * 1024)
            throw CapturePackage.Error(CaptureErrorCode.CorruptPackage, "Export receipt reservation is invalid.");
        return receipt;
    }

    private static void WriteReceipt(string directory, PortableExportReceipt receipt)
    {
        var watch = Stopwatch.StartNew();
        var bytes = PortableCaptureJson.Encode(receipt, PortableCaptureJson.Default.PortableExportReceipt, PortableBounds.ReceiptBytes);
        var pending = Path.Combine(directory, "receipt.pending");
        CapturePackage.RejectLinks(pending);
        File.Delete(pending);
        using (var stream = SafeArtifactPath.CreateRestrictedFile(pending))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(pending, Path.Combine(directory, "receipt.json"), overwrite: true);
        PortableBounds.Check("ControlMilliseconds", watch.ElapsedMilliseconds, 5000);
    }

    internal static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public void Dispose() => _lease.Dispose();
}
