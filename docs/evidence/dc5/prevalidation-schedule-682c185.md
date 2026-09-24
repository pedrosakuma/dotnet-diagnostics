# DC5 scheduled-cohort correction: seven covered probes, geometry incomplete

The next revision-7 suite covered the first seven probes, including all live
and fault-recovery probes, but stopped on its final geometry fixture. It
remains partial/unsealed and cannot supply all-eight campaign readiness.

Implementation: `682c185d9cbf03d76fa4548a12abc055487b09ab` (#1035).
Suite: `pv-unified-v7-03`. Manifest SHA-256:
`b22034fb2da7f9e2339bf10bb842822ef6845f9cdcf7b9c22d814febacce821a`.
Invocation: 2026-09-24 14:02:31.110-14:05:52.803 UTC, exit 3.

Read-only typed replay verified the first seven actual coverage, source and
recovery predicates. This is verification of retained evidence, not another
workload execution or a replacement for the missing final probe.
`PV-GEOMETRY` failed with `DescriptorOnlyIdentityLimitExceeded`, stage
`harness`. Raw evidence records 33 descriptor-only identities, including the
32 observed unlinked fixture identities. The extra historical FD/path was
not recorded and remains unknown.

## Controlled fixture defects

A deterministic native component case demonstrated that opening a new linked
file after pathname traversal can legitimately add a 33rd descriptor-only
identity while 32 unlinked fixture handles are already held. The monitor is
correct to fail that observation; later closing the new handle does not
clear the sticky failure. This proves a mechanism, not the identity of the
historical extra descriptor.

Fixture construction now creates all rooted padding and proof/result/coverage/
cleanup slots first, then awaits the authoritative
`geometry-rooted-fixtures-complete` sweep at 539 rooted and zero descriptor-only
identities. Only afterward does it open the 32 existing memfd fixtures.
The final authoritative observation still requires exactly 539 + 32 = 571.
No monitor pause, identity exclusion, cap increase or accounting redefinition
is introduced.

Component execution then exposed a separate history-fixture counting defect:
directory-inclusive traversal had been confused with the required file
population. The corrected validation independently bounds 64 slots and four
immutable, correctly sized files per slot. Malformed or oversized slots,
files and writable fixture files remain rejected.

## Validation and retention

The narrow correction passed 21 targeted and 574 full DurableCounterSpike
component cases; actual EventPipe was explicitly excluded. Tests include
steady maximum geometry, hard overflow at 33, cleanup, construction failure/
cancellation and strict completed-context handling. Independent review found
no significant issue.

Local proofs, logs, TRX and `actual-snapshot.json` are under session
`f10a11bb-805c-4e83-8621-74c5363b7de5/files/geometry-component/`.
Preservation verified 6,379 protected files unchanged, including the original
clean executed source, binaries and failed suite. The snapshot is not a seal.

No real suite was repeated for this component correction. Revision-7 protocol
hashes, workloads, timings, resource limits and readiness requirements remain
unchanged. The next prospective attempt requires its own committed build,
reviewed input identities and parent authorization. No 35-case comparison,
recommendation or product backend decision is claimed.
