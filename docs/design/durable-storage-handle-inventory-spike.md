# Durable storage handle inventory spike

**Status:** Experimental TestSupport component only. Linux is the currently
declared implementation scope because the frozen durable-storage experiment is
Linux-based. Other operating systems return the typed `UnsupportedPlatform`
failure; this is not a claim that equivalent implementations are impossible.

## Native evidence and identity

The inventory calls libc `statx(2)` with `AT_EMPTY_PATH` and an empty pathname,
so identity, apparent length, type, and link count are read from the same
already-open file descriptor. The implementation was checked against:

- [`statx(2)` in the Linux man-pages project](https://man7.org/linux/man-pages/man2/statx.2.html),
  which specifies libc's signature, `AT_EMPTY_PATH`, result-mask semantics,
  and the meanings of `stx_ino`, `stx_size`, `stx_mode`, `stx_nlink`, and the
  containing-device fields; and
- [Linux UAPI `include/uapi/linux/stat.h`](https://github.com/torvalds/linux/blob/master/include/uapi/linux/stat.h),
  which fixes `struct statx` at `0x100` bytes and labels the decoded offsets:
  mask `0x00`, link count `0x10`, mode `0x1c`, inode `0x20`, size `0x28`,
  containing-device major `0x88`, and minor `0x8c`. It also defines the
  requested `STATX_TYPE`, `STATX_NLINK`, `STATX_INO`, and `STATX_SIZE` bits.

The bounded ledger token is
`linux:<device-major>:<device-minor>:<inode>`, formatted with invariant-culture
fixed-width hexadecimal. It is 40 UTF-16 characters, below the accepted
ledger's 128-character limit. Device plus inode is used only while at least one
supplied handle reference remains pinned by the inventory lease. No identity
or inode-reuse claim is made after lease disposal.

Missing libc/statx support, syscall failure, missing result-mask fields,
non-regular files, and unrepresentable lengths are explicit typed failures.
Paths, timestamps, content hashes, and numeric file descriptors are not used
as identities.

## Bounds, publication, and ownership

The caller supplies capacity, maximum supplied handles, maximum distinct
tracked files, and the existing ledger's permit metadata limit. Enumeration
checks the handle count before retaining or observing the next item; it never
materializes an unbounded enumerable. Each successfully accepted supplied
`SafeFileHandle`, including an alias, receives its own `DangerousAddRef`.
References remain held until the disposable inventory lease is released.
Cleanup releases only references actually acquired and never closes a
caller-owned handle. A reference acquired before concurrent owner disposal
keeps that native handle alive for the observation.

Observations are deduplicated by Linux device plus inode. Two handles for the
same file are charged once; distinct files with identical contents remain
distinct. Conflicting lengths or link counts for one identity fail rather than
using last-write-wins. The accepted `ApparentByteReservationLedger` is
constructed privately and each newly observed identity is registered in it;
private partial state is discarded on any later failure. Source and output
lengths use that one ledger, and the lease publishes only an immutable
observation view and immutable ledger snapshot after enumeration, observation,
registration, and all cap checks succeed. It does not expose the mutable ledger.
Lease disposal is idempotent, and published state rejects use after disposal.

The precondition is that the caller has established exclusive or otherwise
quiescent mutation ownership for all supplied files. `statx(2)` is a metadata
snapshot and does not prove absence of concurrent change, ABA, or inventory
completeness.

## Evidence and deliberate limits

Linux tests use real regular files and establish duplicate-open
deduplication, distinct identical-content identities, rename stability while a
handle is held, exact byte/handle/distinct-file limits, source/output aggregate
charging, invalid and disposed handle rejection, enumeration/observation
failure cleanup, concurrent owner disposal after reference acquisition,
idempotent lease release, and an unlinked held file retaining its observed
identity and length. Native-result failure seams supplement but do not replace
those Linux tests. Linux-only tests use declared xUnit skips on other systems.

Cleanup assertions dispose the caller's handles and then check that their
procfs descriptor links no longer refer to the uniquely named test files.
They do not require descriptor numbers to remain unused by parallel tests.
Temporarily omitting failure-path reference release made the enumeration,
capacity and handle/file-limit regressions fail; the release was restored.
The default factory also rejects unsupported platforms for empty inventories,
before any native observation would otherwise occur.

This component inventories only caller-provided handles. It does not recursively
discover package files, establish root containment, prove that all relevant
handles were supplied, define already-followed symlink policy, or establish a
package hardlink policy. Link count zero records an unlinked-held observation;
it does not prove actual deletion or resource release while pins remain, and
directory absence never releases ledger charge.

Also unverified are identity reuse after release, native SQLite growth,
callback-time growth, complete filesystem or physical-block quotas, process
handoff, crash reconstruction, and producer/adapter wiring. This spike does not
justify an optimistic 256 MiB guarantee or open any campaign gate.
