# DC5 descriptor observer: bounded component investigation

## Scope and conclusion

This investigation follows the [failed authorized repeat](prevalidation-repeat-aa28760.md).
It does not execute another eight-probe suite, the 35-case comparison, a
50,000-offer stand-in, or an EventPipe workload. Both historical attempts
remain unchanged; their missing errno and pin phase remain unknown.

The controlled components prove a real failure mechanism: enumeration does
not retain the target descriptor. Closing an enumerated writer before either
pin causes native `open(O_PATH | O_CLOEXEC)` to return `-1`, errno **2
(`ENOENT`)**. A first `O_PATH` pin keeps the inode alive but does not keep
the original descriptor-table slot populated for the second pin or either
flags read. This is genuine loss of a listed resource, not evidence that the
missing resource was harmless. The unchanged strict contract must mark that
sweep incomplete.

Only bounded causal instrumentation and component seams are implemented.
There is **no success-producing observer workaround**. This demonstrates an
allowed concurrent lifecycle that the current algorithm cannot observe
completely; it does not establish that every possible run fails, or identify
which descriptor disappeared in the historical suites.

## Controlled proofs

All interleavings use explicit callbacks/events, not sleeps or reruns until
green. Production has no manifest-accessible hook or injected syscall result.

| Component | Established result |
| --- | --- |
| Real one-slot fixture, four 512-byte files | The writer runs on another task. At the first file, the observer releases the writer and waits for actual close before its first or second native pin. The next fixture remains blocked until the exception is captured. Both phases preserve errno 2. |
| Direct linked and unlinked regular-file handles | Closing before operation 1/3 preserves native errno 2. Closing before operation 2/4 preserves `DescriptorFlagsFileNotFoundException`, mapped HResult `0x80070002`, **not** raw errno 2. Failed observations dispose their pins. |
| Real descriptor reuse | `dup2` replaces the original slot with a different regular file between pins. `DescriptorIdentityChangedDuringObservation` remains an error, not a retry or successful merge. |
| Injected native open results | Errno 13 (`EACCES`) and 5 (`EIO`) are retained at both pin phases. These are deterministic error-mapping tests, not claims that the host produced permission or I/O failures. |
| Full self/coordinator sweep | A concurrent owned 512-byte output writer closes after the first snapshot. The persisted JSONL retains `[0,3,2,fd]`; the sweep and sticky monitor remain incomplete. Root, coordinator and summary-writer coverage are not omitted. |
| Self enumeration | `Directory.EnumerateFileSystemEntries` exposes the enumerator's directory descriptor while it is alive, and explicit pinning after enumeration fails. However, the production `Directory.GetFiles` filters this directory out. It is **not** evidence that the production failure came from its own enumeration descriptor. |

The original hold-open fixture test remains: it proves observation while each
writer stays open, not concurrent-close handling. The new close-after-pin
tests deliberately retain the opposite, failing outcome.

Direct helper proofs persist the real exception context in the existing
synthetic maximum-width encoding fixture; their other summary fields are
**not measurements**. The full coordinator test instead persists the real
monitor summary. Its retained first general error is
`PathObservationIOException` (the test's exclusive-share writer also blocks
root access), while `nc` separately preserves the real second-pin ENOENT.
Neither that earlier root failure nor unrelated VSTest-host descriptors are
ignored to obtain a clean result. Numeric descriptor IDs are local slots,
not durable/global identities; role 0 covers both coordinator and harness.

## Bounded persisted context

Prevalidation sweep schema `/3` adds optional `nc=[role,operation,error,fd]`,
with the exact domains documented in the
[executor companion](../../design/durable-capture-prevalidation-executor.md).
It retains one descriptor failure independently of the first general error
`ec`; later errors remain counted by `er`. There are no paths, PIDs,
messages, per-descriptor arrays or new suite files.

The original scored encoding stays 854 bytes. The previous extension stays
983 bytes when `nc` is absent; the new worst case is **1,017 including LF**.
The 1,024-byte record, 2,048-record, 2,048-byte authority-frame, output,
cadence/deadline, and 2,506-layout limits remain unchanged. Primary and
secondary failure stages and exact proc-stat cleanup handling are unchanged.
Old `/1` and `/2` context-map hashes remain inspection-only: neither can
authorize a new execution. Fresh binaries, component evidence, context-map
hash, manifest, independent acceptance and authorization bindings are required
for any later actual execution; a fresh suite ID is not permission to retry.

## Negative evidence retained during development

The first test invocation aborted before discovery because a fresh
`MSBuild -t:Restore,Build` evaluation had not loaded newly restored test SDK
imports: the output lacked the test runtimeconfig and copied Azure.Core
dependency. A distinct Build evaluation produced those artifacts.

The first executing targeted run had **21 Passed / 3 Failed**:

1. An initial enumeration-artifact hypothesis expected `GetFiles` to expose
   its directory descriptor. It did not. The corrected proof explicitly
   distinguishes all filesystem entries from the production files-only list.
2. A hand-counted maximum width expected 1,018 bytes; serialization measured
   1,017. The exact assertion was corrected, not the budget.
3. A full quiet sweep did not spontaneously produce a native failure. The
   corrected component controls a real owned writer's close between pins
   rather than assuming ambient descriptor churn.

The next targeted validation passed all 24 cases before the additional
unlinked-file, disposal and malformed-context coverage. All initial logs and
TRX files are retained separately; failures were not overwritten or silently
retried.

## Final validation and preservation

SDK 10.0.201 MSBuild Release builds of DiagnosedBenchmarks and Core.Tests
passed under the shared `flock --close` validation lock. The final targeted
selection passed **29/29**. All DurableCounterSpike tests ran **once** on
the final implementation: **280 Passed, 0 Failed, 0 Skipped**, including
existing cleanup, primary/secondary cause, admission and encoding coverage.
There were 254 tests before these 26 new cases. No new analyzer suppression
or dependency was introduced.

The session's new `descriptor-observer-validation-20260923` folder retains
build/stdout logs, every TRX, a plan-only schema fingerprint (no execution),
and `controlled-native-proof.json`, extracted from the final targeted TRX.
The extraction labels synthetic encoding envelopes versus actual sweeps.

| Evidence | SHA-256 |
| --- | --- |
| Final targeted TRX | `2268c9247e6a08ad0f4cd4aa429fe982882d1579cdec8e4bab914743c0368450` |
| All DurableCounterSpike TRX | `0703a60be525d74ae5b25c6cc8576731ef3f9f6cf64fa4a5b1f83880fb042ee5` |
| Labeled controlled native proof | `79694c47408b52c0e925c34ad42054a2a9458f8b15f6e77ccc5688645087359c` |
| New prevalidation context field map | `f6de1493dea58eb645cd66b2be9d07dfc388e5dbc495090546fdffcaa4ff7e84` |

These are component results, not a real-worker feasibility artifact,
implementation acceptance, authorization, or campaign admission.

## Why a local lock is not a complete fix

Synchronizing known coordinator fixture/control writers with the sweep gate
could prevent their close from overlapping a sweep. That would need to cover
every known owner, preserve all observations, and count lock wait/sweep time
against the existing cadence and deadlines. It would not coordinate native
SQLite open/close/unlink, EventPipe/runtime descriptors, target processes, or
unknown resources. Holding all fixture handles until the end changes the
simultaneous descriptor population and is not a demonstrated remedy either.

Bounded alternatives need a separate technical/protocol disposition:

- Retain a duplicated target open-file description, with flags read through
  that description, rather than re-opening a live numeric slot. On Linux,
  `pidfd_getfd` has permission/ptrace constraints and still cannot recover a
  descriptor closed after enumeration but before duplication. This narrows a
  race; it does not solve the full strict-completeness problem.
- Coordinate **all** descriptor lifecycles with an independently validated
  observer, or collect loss-detecting bounded lifecycle events. Native/runtime
  coverage and drop handling would need proof; this is not a small local fix.
- Stop all observed processes during enumeration/pinning from an external
  observer. This changes workload execution and measurement overhead and
  cannot be implemented as a self-observer lock.
- Define disappearing descriptors as a documented sampled-observation loss.
  This is a **semantic weakening** of the frozen completeness rule and was
  explicitly not implemented.

Therefore actual-worker feasibility remains blocked. Ignoring ENOENT,
changing incomplete to complete, excluding temp/unlinked/native resources,
slowing polling, moving fixtures, or omitting coordinator/harness monitoring
would hide this mechanism rather than resolve it.
