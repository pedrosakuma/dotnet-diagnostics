using System.Diagnostics;
using DotnetDiagnostics.Core.Artifacts;

namespace DotnetDiagnostics.Core.Captures;

internal sealed partial class PortableCaptureStorage
{
    private static readonly HashSet<string> WorkFiles = new(StringComparer.Ordinal)
    {
        "capture.sqlite", "capture.sqlite-journal", "manifest.json", "manifest.json.pending",
        "seal.json", "seal.json.pending", ".lease", "evidence.frames", "validated.frames",
        "strings.index", "strings.data", "records.index"
    };

    internal void SaveImport(PortableImportJournal journal)
    {
        using var admission = SqliteCaptureStore.PortableAdmission(_store.PortableRoot());
        SaveImportLocked(journal);
    }

    internal void SaveImportLocked(PortableImportJournal journal)
    {
        Receipt = Receipt with { Import = journal };
        WriteReceipt(DirectoryPath, Receipt);
    }

    internal void ReduceReservationLocked(long bytes)
    {
        if (bytes < 0 || bytes > Receipt.ReservationBytes - PortableBounds.ReceiptReservation)
            throw CapturePackage.Error(CaptureErrorCode.StorageFailure, "Import reservation transfer is invalid.");
        Receipt = Receipt with { ReservationBytes = Receipt.ReservationBytes - bytes };
        WriteReceipt(DirectoryPath, Receipt);
    }

    internal void CleanImport()
    {
        using var admission = SqliteCaptureStore.PortableAdmission(_store.PortableRoot());
        Receipt.Import?.Worker?.RequireGone();
        Receipt.Import?.ParentIo?.RequireGone();
        DeleteImportWork(DirectoryPath);
        Receipt = Receipt with { ReservationBytes = PortableBounds.ReceiptReservation };
        WriteReceipt(DirectoryPath, Receipt);
    }

    internal static PortableImportResult ReadImportResult(SqliteCaptureStore store, PortableOperationKey key,
        CaptureAccess access, DateTimeOffset now)
    {
        CapturePackage.ValidateId(key.Id);
        CapturePackage.ValidateAccess(access);
        if (key.RequestedUtc.AddHours(24) <= now)
            throw CapturePackage.Error(CaptureErrorCode.InvalidInput, "OperationExpired: import receipt retention has expired.");
        var root = store.PortableRoot();
        if (!Directory.Exists(root)) throw CapturePackage.Error(CaptureErrorCode.NotFound, "Import operation was not found.");
        using var admission = SqliteCaptureStore.PortableAdmission(root);
        CleanupLocked(root, now);
        var path = Path.Combine(root, ".portable", Digest(CapturePackage.Utf8.GetBytes(access.OwnerId + "\0" + key.Id)));
        CapturePackage.RejectLinks(path);
        if (!Directory.Exists(path)) throw CapturePackage.Error(CaptureErrorCode.NotFound, "Import operation was not found.");
        using var lease = TryLease(path) ?? throw CapturePackage.Error(CaptureErrorCode.Busy, "Import is still active.");
        var receipt = ReadReceipt(path);
        if (receipt.OwnerId != access.OwnerId || receipt.Operation != key || receipt.Import is null)
            throw CapturePackage.Error(CaptureErrorCode.NotFound, "Import operation was not found for this owner.");
        return receipt.Import.Result;
    }

    private static PortableExportReceipt ReconcileImport(string root, string directory, PortableExportReceipt receipt)
    {
        var journal = receipt.Import!;
        journal.Worker?.RequireGone();
        journal.ParentIo?.RequireGone();
        if (journal.Terminal) return receipt;
        var watch = Stopwatch.StartNew();
        var entries = journal.Result.Entries.ToArray();
        var failed = false;
        for (var i = 0; i < entries.Length; i++)
        {
            PortableBounds.Check("ControlMilliseconds", watch.ElapsedMilliseconds, 5000);
            if (entries[i].State == PortableEntryState.Published) continue;
            var plan = journal.Plans.SingleOrDefault(plan => plan.EntryId == entries[i].EntryId);
            if (plan is not null)
            {
                CapturePackage.ValidateId(plan.CaptureId);
                var destination = Path.Combine(root, plan.CaptureId);
                CapturePackage.RejectLinks(destination);
                if (Directory.Exists(destination))
                {
                    if (plan.SealHash is null ||
                        !string.Equals(CapturePackage.Hash(Path.Combine(destination, CapturePackage.Seal)), plan.SealHash, StringComparison.Ordinal))
                        throw CapturePackage.Error(CaptureErrorCode.StorageFailure,
                            "PublicationConflict: planned destination cannot be identified; retain operation storage.");
                    var manifest = CapturePackage.ReadManifest(destination, plan.CaptureId);
                    if (manifest.Info.OwnerId != receipt.OwnerId || manifest.Info.State != CaptureState.Sealed)
                        throw CapturePackage.Error(CaptureErrorCode.StorageFailure, "PublicationConflict: owner or state differs.");
                    entries[i] = entries[i] with { State = PortableEntryState.Published, Failure = null };
                    continue;
                }
            }
            var failure = new PortableFailure(CaptureErrorCode.Incomplete, "ImportInterrupted", entries[i].EntryId, null, null, null);
            entries[i] = entries[i] with { State = failed ? PortableEntryState.NotAttempted : PortableEntryState.Failed,
                Mapping = null, Failure = failed ? null : failure };
            failed = true;
        }
        var complete = entries.Length > 0 && entries.All(static entry => entry.State == PortableEntryState.Published);
        var result = journal.Result with { Entries = entries, Complete = complete,
            Failure = complete ? null : new(CaptureErrorCode.Incomplete, "ImportInterrupted", null, null, null, null) };
        receipt = receipt with { Import = journal with { Result = result, Terminal = true, Worker = null, ParentIo = null } };
        WriteReceipt(directory, receipt);
        return receipt;
    }

    private static long ImportWorkBytes(string work)
    {
        long bytes = 0;
        var count = 0;
        var watch = Stopwatch.StartNew();
        Walk(work, 0);
        return bytes;

        void Walk(string directory, int depth)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                CapturePackage.RejectLinks(path);
                PortableBounds.Check("ImportWorkMembers", ++count, 320);
                PortableBounds.Check("ControlMilliseconds", watch.ElapsedMilliseconds, 5000);
                var name = Path.GetFileName(path);
                if (Directory.Exists(path))
                {
                    var valid = depth == 0
                        ? name.Length == 2 && int.TryParse(name, out var entry) && entry is >= 0 and < 16
                        : depth == 1 && name is "source" or "destination" or "analysis";
                    if (!valid) throw CapturePackage.Error(CaptureErrorCode.UnsafePath, "Unexpected import staging directory.");
                    Walk(path, depth + 1);
                }
                else
                {
                    if (depth != 2 || !WorkFiles.Contains(name))
                        throw CapturePackage.Error(CaptureErrorCode.UnsafePath, "Unexpected import staging member.");
                    bytes = checked(bytes + new FileInfo(path).Length);
                    PortableBounds.Check("MaxPrivateBytes", bytes, 2L * 1024 * 1024 * 1024);
                }
            }
        }
    }

    private static void DeleteImportWork(string directory)
    {
        var watch = Stopwatch.StartNew();
        _ = ValidateMembers(directory);
        var work = Path.Combine(directory, "work");
        if (Directory.Exists(work)) Remove(work);
        DeleteArchiveFiles(directory);

        void Remove(string path)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                PortableBounds.Check("ControlMilliseconds", watch.ElapsedMilliseconds, 5000);
                if (Directory.Exists(child)) Remove(child);
                else File.Delete(child);
            }
            Directory.Delete(path);
            PortableBounds.Check("ControlMilliseconds", watch.ElapsedMilliseconds, 5000);
        }
    }
}
