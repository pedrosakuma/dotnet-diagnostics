using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace DotnetDiagnostics.Core.Captures;

public sealed partial class PortableCaptureUseCases
{
    // Internal post-validation operations, not an alternate archive admission path.
    internal sealed class ImportPublication(SqliteCaptureStore store, string root, PortableCaptureStorage storage,
        PortableImportBudget budget, CaptureAccess access, PortableImportResult initial)
    {
        internal PortableImportResult Result { get; set; } = initial;
        private PortableImportPlan[] _plans = [];
        private bool _published;

        internal void SaveProgress() => storage.SaveImport(new(Result, _plans, false));

        internal async Task PrepareAsync(IReadOnlyList<PortableImportEntry> descriptors,
            IReadOnlyList<PortableEntryMapping> mappings, AuthorizePortableImport authorize, CancellationToken token)
        {
            await authorize(descriptors, PortableAuthorizationPhase.Prepare, null, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _plans = mappings.Select(static mapping => new PortableImportPlan(mapping.EntryId, mapping.LocalCaptureId, null)).ToArray();
            Result = Result with { Entries = mappings.Select(static mapping =>
                new PortableEntryResult(mapping.EntryId, PortableEntryState.Pending, mapping, null)).ToArray() };
            SaveProgress();
        }

        internal async Task PublishAsync(string destination, CaptureInfo info, int index, long reservation,
            IReadOnlyList<PortableImportEntry> descriptors, AuthorizePortableImport authorize, CancellationToken token)
        {
            var entry = Result.Entries[index];
            var captureId = entry.Mapping!.LocalCaptureId;
            _plans[index] = _plans[index] with { SealHash = CapturePackage.Hash(Path.Combine(destination, CapturePackage.Seal)) };
            SaveProgress();
            using (SqliteCaptureStore.PortableAdmission(root))
            {
                token.ThrowIfCancellationRequested();
                store.CheckImportPublication(captureId, info, access);
                await authorize(descriptors, PortableAuthorizationPhase.Publish, entry.EntryId, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                store.CheckImportPublication(captureId, info, access);
                Directory.Move(destination, Path.Combine(root, captureId));
                _published = true;
                var outcomes = Result.Entries.ToArray();
                outcomes[index] = outcomes[index] with { State = PortableEntryState.Published };
                Result = Result with { Entries = outcomes };
                storage.SaveImportLocked(new(Result, _plans, false));
                budget.PublishedLocked(reservation);
            }
        }

        internal PortableImportResult Complete()
        {
            Result = Result with { Complete = true };
            storage.SaveImport(new(Result, _plans, true));
            storage.CleanImport();
            return Result;
        }

        internal PortableImportResult Fail(Exception error, int index, bool callerCancelled, bool ioOutstanding)
        {
            var cancelled = error is OperationCanceledException && callerCancelled;
            var known = error as CaptureStoreException ?? CapturePackage.Error(
                cancelled ? CaptureErrorCode.Incomplete : error is OperationCanceledException ? CaptureErrorCode.CapacityExceeded :
                error is NotSupportedException ? CaptureErrorCode.UnsupportedFormat :
                error is JsonException or InvalidDataException or ArgumentException or OverflowException ? CaptureErrorCode.CorruptPackage :
                CaptureErrorCode.StorageFailure, cancelled ? "Cancelled: caller cancelled the operation." :
                    error is OperationCanceledException ? "OperationDeadline: whole-operation budget exhausted." : "ImportValidationOrStorageFailure", error);
            var entries = Result.Entries.ToArray();
            for (var i = 0; i < entries.Length; i++)
                if (entries[i].State != PortableEntryState.Published)
                    entries[i] = entries[i] with { State = i == index ? cancelled ? PortableEntryState.Cancelled : PortableEntryState.Failed :
                        PortableEntryState.NotAttempted, Mapping = null,
                        Failure = i == index ? Failure(known, entries[i].EntryId) : null };
            Result = Result with { Complete = false, Cancelled = cancelled, Entries = entries, Failure = Failure(known, null) };
            try
            {
                storage.SaveImport(new(Result, _plans, true, ioOutstanding ? storage.Receipt.Import?.Worker : null,
                    ioOutstanding ? storage.Receipt.Import?.ParentIo : null));
                storage.CleanImport();
            }
            catch (Exception cleanup) when (cleanup is CaptureStoreException or IOException or UnauthorizedAccessException)
            {
                throw CapturePackage.Error(CaptureErrorCode.StorageFailure,
                    $"ImportFinalizationFailed: operation {Result.OperationId}; query GetImportResultAsync before assuming no publication.",
                    new AggregateException(error, cleanup));
            }
            if (!_published) ExceptionDispatchInfo.Capture(cancelled ? error : known).Throw();
            return Result;
        }
    }
}
