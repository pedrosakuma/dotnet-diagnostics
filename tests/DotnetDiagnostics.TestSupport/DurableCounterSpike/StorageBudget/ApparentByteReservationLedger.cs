namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike;

internal readonly record struct ApparentFileIdentity(string Value);

internal readonly record struct ApparentFileRegistration(
    ApparentFileIdentity Identity,
    long Generation,
    Guid LedgerId);

internal readonly record struct ApparentBytePermit(
    ApparentFileIdentity Identity,
    long Generation,
    long PermitId,
    Guid LedgerId);

internal sealed record ApparentByteLedgerSnapshot(
    long Capacity,
    long ChargedBytes,
    int TrackedFiles,
    int OutstandingPermits,
    bool IsFaulted,
    string? FaultCode);

internal sealed class ApparentByteReservationLedger
{
    internal const int MaximumIdentityCharacters = 128;

    private readonly object _gate = new();
    private readonly Dictionary<ApparentFileIdentity, FileEntry> _files = [];
    private readonly Guid _ledgerId = Guid.NewGuid();
    private readonly long _capacity;
    private readonly int _maximumTrackedFiles;
    private readonly int _maximumOutstandingPermits;
    private long _chargedBytes;
    private long _nextGeneration;
    private long _nextPermitId;
    private int _outstandingPermits;
    private string? _faultCode;

    internal ApparentByteReservationLedger(
        long capacity,
        int maximumTrackedFiles,
        int maximumOutstandingPermits)
    {
        if (capacity < 0)
        {
            throw Error("InvalidCapacity", "Capacity cannot be negative.");
        }

        if (maximumTrackedFiles <= 0)
        {
            throw Error("InvalidMetadataLimit", "The tracked-file limit must be positive.");
        }

        if (maximumOutstandingPermits <= 0)
        {
            throw Error("InvalidPermitLimit", "The outstanding-permit limit must be positive.");
        }

        _capacity = capacity;
        _maximumTrackedFiles = maximumTrackedFiles;
        _maximumOutstandingPermits = maximumOutstandingPermits;
    }

    internal ApparentFileRegistration Register(ApparentFileIdentity identity, long observedLength)
    {
        ValidateIdentity(identity);
        ValidateLength(observedLength);

        lock (_gate)
        {
            EnsureHealthy();
            if (_files.ContainsKey(identity))
            {
                throw Error("DuplicateFileIdentity", "The file identity is already registered.");
            }

            if (_files.Count == _maximumTrackedFiles)
            {
                throw Error("TrackedFileLimitExceeded", "The tracked-file metadata limit is exhausted.");
            }

            EnsureAdditionalCapacity(observedLength);
            var generation = Next(ref _nextGeneration, "GenerationOverflow");
            _files.Add(identity, new FileEntry(generation, observedLength));
            _chargedBytes += observedLength;
            return new(identity, generation, _ledgerId);
        }
    }

    internal ApparentBytePermit Reserve(ApparentFileRegistration file, long worstCaseGrowth)
    {
        ValidateLength(worstCaseGrowth);

        lock (_gate)
        {
            EnsureHealthy();
            var entry = GetEntry(file);
            if (entry.ActivePermitId is not null)
            {
                throw Error("FilePermitAlreadyOutstanding", "A file may have only one outstanding reservation.");
            }

            if (_outstandingPermits == _maximumOutstandingPermits)
            {
                throw Error("PermitLimitExceeded", "The outstanding-permit metadata limit is exhausted.");
            }

            _ = CheckedAdd(entry.ObservedLength, worstCaseGrowth);
            EnsureAdditionalCapacity(worstCaseGrowth);
            var permitId = Next(ref _nextPermitId, "PermitIdOverflow");
            entry.ActivePermitId = permitId;
            entry.ReservedGrowth = worstCaseGrowth;
            _chargedBytes += worstCaseGrowth;
            _outstandingPermits++;
            return new(file.Identity, file.Generation, permitId, _ledgerId);
        }
    }

    internal void CompleteConfirmed(ApparentBytePermit permit, long observedLength)
    {
        ValidateLength(observedLength);

        lock (_gate)
        {
            EnsureHealthy();
            var entry = GetActivePermit(permit);
            var ceiling = CheckedAdd(entry.ObservedLength, entry.ReservedGrowth);
            if (observedLength > ceiling)
            {
                _faultCode = "ObservedLengthExceedsReservation";
                throw Error(
                    _faultCode,
                    "A confirmed observation exceeded its reserved ceiling; the ledger can no longer support a quota claim.");
            }

            _chargedBytes -= ceiling - observedLength;
            entry.ObservedLength = observedLength;
            CompletePermit(entry);
        }
    }

    internal void CompleteUnknown(ApparentBytePermit permit)
    {
        lock (_gate)
        {
            EnsureHealthy();
            var entry = GetActivePermit(permit);
            entry.ObservedLength = CheckedAdd(entry.ObservedLength, entry.ReservedGrowth);
            CompletePermit(entry);
        }
    }

    internal void ConfirmObservation(ApparentFileRegistration file, long observedLength)
    {
        ValidateLength(observedLength);

        lock (_gate)
        {
            EnsureHealthy();
            var entry = GetEntry(file);
            if (entry.ActivePermitId is not null)
            {
                throw Error("PermitOutstanding", "Complete the outstanding permit before confirming another observation.");
            }

            if (observedLength > entry.ObservedLength)
            {
                _faultCode = "UnreservedObservedGrowth";
                throw Error(
                    _faultCode,
                    "A confirmed observation grew without a reservation; the ledger can no longer support a quota claim.");
            }

            _chargedBytes -= entry.ObservedLength - observedLength;
            entry.ObservedLength = observedLength;
        }
    }

    internal void RetireConfirmed(ApparentFileRegistration file)
    {
        lock (_gate)
        {
            EnsureHealthy();
            var entry = GetEntry(file);
            if (entry.ActivePermitId is not null)
            {
                throw Error("PermitOutstanding", "Complete the outstanding permit before confirming resource retirement.");
            }

            _chargedBytes -= entry.ObservedLength;
            _files.Remove(file.Identity);
        }
    }

    internal ApparentByteLedgerSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new(
                _capacity,
                _chargedBytes,
                _files.Count,
                _outstandingPermits,
                _faultCode is not null,
                _faultCode);
        }
    }

    private static void ValidateIdentity(ApparentFileIdentity identity)
    {
        if (identity.Value is null || identity.Value.Length > MaximumIdentityCharacters
            || string.IsNullOrWhiteSpace(identity.Value))
        {
            throw Error("InvalidFileIdentity", $"A nonblank file identity of at most {MaximumIdentityCharacters} UTF-16 characters is required.");
        }
    }

    private static void ValidateLength(long length)
    {
        if (length < 0)
        {
            throw Error("InvalidLength", "File lengths and reservations cannot be negative.");
        }
    }

    private void EnsureHealthy()
    {
        if (_faultCode is not null)
        {
            throw Error("LedgerFaulted", $"The ledger is faulted because of '{_faultCode}'.");
        }
    }

    private FileEntry GetEntry(ApparentFileRegistration file)
    {
        if (file.LedgerId != _ledgerId)
        {
            throw Error("ForeignFileToken", "The file token belongs to another ledger.");
        }

        ValidateIdentity(file.Identity);
        if (!_files.TryGetValue(file.Identity, out var entry))
        {
            throw Error("UnknownFileToken", "The file token is not registered.");
        }

        if (entry.Generation != file.Generation)
        {
            throw Error("StaleFileToken", "The file token belongs to an earlier registration generation.");
        }

        return entry;
    }

    private FileEntry GetActivePermit(ApparentBytePermit permit)
    {
        if (permit.LedgerId != _ledgerId)
        {
            throw Error("ForeignPermit", "The permit belongs to another ledger.");
        }

        ValidateIdentity(permit.Identity);
        if (!_files.TryGetValue(permit.Identity, out var entry))
        {
            throw Error("UnknownPermit", "The permit's file is not registered.");
        }

        if (entry.Generation != permit.Generation)
        {
            throw Error("StalePermit", "The permit belongs to an earlier registration generation.");
        }

        if (entry.ActivePermitId != permit.PermitId)
        {
            throw Error(
                permit.PermitId <= entry.LastCompletedPermitId ? "DuplicatePermitCompletion" : "InvalidPermit",
                "The permit is not the active reservation for this file.");
        }

        return entry;
    }

    private void CompletePermit(FileEntry entry)
    {
        entry.LastCompletedPermitId = entry.ActivePermitId!.Value;
        entry.ActivePermitId = null;
        entry.ReservedGrowth = 0;
        _outstandingPermits--;
    }

    private void EnsureAdditionalCapacity(long additionalBytes)
    {
        if (additionalBytes > _capacity - _chargedBytes)
        {
            throw Error("CapacityExceeded", "The requested charge exceeds the apparent-byte capacity.");
        }
    }

    private static long CheckedAdd(long left, long right)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException exception)
        {
            throw new DurableStorageExperimentException(
                "ArithmeticOverflow",
                $"Apparent-byte arithmetic overflowed: {exception.Message}");
        }
    }

    private static long Next(ref long value, string code)
    {
        if (value == long.MaxValue)
        {
            throw Error(code, "The bounded ledger token sequence is exhausted.");
        }

        return ++value;
    }

    private static DurableStorageExperimentException Error(string code, string message) => new(code, message);

    private sealed class FileEntry(long generation, long observedLength)
    {
        internal long Generation { get; } = generation;
        internal long ObservedLength { get; set; } = observedLength;
        internal long ReservedGrowth { get; set; }
        internal long? ActivePermitId { get; set; }
        internal long LastCompletedPermitId { get; set; }
    }
}
