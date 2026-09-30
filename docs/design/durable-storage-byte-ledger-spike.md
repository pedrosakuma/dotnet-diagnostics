# Durable storage apparent-byte ledger spike

**Status:** Experimental TestSupport component only. It is not registered with
either adapter or a runner, and the 256 MiB campaign gate remains closed.

## API and invariants

`ApparentByteReservationLedger` is a small, lock-protected accounting component.
Its constructor requires an apparent-byte capacity, a tracked-file metadata
limit, and an outstanding-permit limit. Those metadata limits are component
configuration, not new frozen campaign settings.

Callers register an explicit `ApparentFileIdentity` token with a trusted
observed length. The token is opaque to the ledger: it is neither a path nor a
raw operating-system handle. A future inventory/native boundary must discover
stable file IDs and resolve aliases before calling this API.
Tokens are limited to 128 UTF-16 characters, so a bounded entry count cannot
retain unbounded identity strings. This is a component metadata constraint,
not a path-length limit or a new frozen campaign setting.

The ledger atomically charges:

- each file's trusted observed length or retained conservative ceiling; and
- each outstanding reservation's worst-case growth.

There is at most one outstanding reservation per file. This intentionally
bounds permit state and simplifies stale-token rejection, but future writer
integration must serialize growth reservations for each real file. Registrations
and permits carry ledger ownership plus monotonic generations/IDs. Foreign,
stale, duplicate, negative, overflowing, and metadata-exhausting operations
fail with typed `DurableStorageExperimentException` codes without changing
charges. No unbounded completed-token tombstone collection is retained.

`CompleteConfirmed` replaces the old observed length plus reservation with a
trusted post-I/O observation. `CompleteUnknown` conservatively converts the
entire reserved ceiling into observed charge. There is deliberately no
`IDisposable` lease that could release bytes after a possibly committed write.
Confirmed shrink and `RetireConfirmed` release charge only after the active
permit has completed. `CompleteUnknown` completes the permit without releasing
its reserved ceiling; retirement still requires separate confirmation of actual
resource release. Merely closing a named
file is not retirement. An unlinked but open file remains charged until the
caller has established actual resource release.

If a confirmed post-I/O length exceeds its reserved ceiling, or trusted
re-inventory discovers unreserved growth, the ledger faults. It retains its
last accounted charges, refuses further mutation, and cannot support a
successful quota claim. The faulted snapshot can be below the newly reported
length and must not be read as a bound on actual bytes. It does not invent
headroom or translate a filesystem
outcome into a batch `Committed`, `Failed`, or `Unknown` result; transaction
classification remains outside this component.

## Caller trust boundary

A "confirmed" observation means the caller has trustworthy identity and length
evidence for the same live file generation. A directory scan alone cannot find
an unlinked-open file and is therefore insufficient to release that charge.
Pure managed synthetic identity tests do not establish hard-link/symlink alias
handling, unlink/close semantics, inode or file-ID reuse behavior, native
SQLite auxiliary-file coverage, crash reconstruction, or any physical storage
bound. Actual identity discovery and identity-reuse policy remain deferred.

## Proposed runner obligations (not implemented)

The proposed integration schedule is:

1. The package-writer process owns the package and writes its own manifest and
   seal while all canonical/query growth is covered by reservations.
2. A recovery successor starts only after confirmed original-process exit or
   equivalent quiescence.
3. The successor performs a fresh external inventory before reconstructing
   ledger state or writing recovery output.
4. Harness evidence and control output live outside the package-writer's
   accounted package and have a separately established containment policy.

This schedule is a proposal for later runner/native integration, not runtime
implementation or evidence. No host changes, process termination, registry or
writer wiring, support-window change, protocol-v2 schema change, or campaign
execution is included. The component proves only arithmetic, concurrency, and
token-state invariants under explicit caller confirmations; it cannot establish
the accepted protocol's 256 MiB physical aggregate ceiling.
