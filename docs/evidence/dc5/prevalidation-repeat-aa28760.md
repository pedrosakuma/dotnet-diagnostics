# DC5 corrected prevalidation repeat: descriptor observation unavailable

## Result

The prospectively authorized suite `pv-repeat-20260923` ran once on
2026-09-23, from 20:37:34.457 to 20:37:36.419 UTC, and returned exit code 3.
Admission passed, but the first entry stopped during synthetic history-fixture
preparation, **before any workload harness launched**. The other seven entries
were not run. No third suite or retry was started.

Unlike the [original attempt](prevalidation-765e56c.md), this attempt retained
the primary failure code and stage:

| Evidence level | Primary failure | Stage | First secondary failure and stage |
| --- | --- | --- | --- |
| PV-O1-A coverage | `DescriptorObservationUnavailable` | `fixture-preparation` | `DescriptorObservationUnavailable`, `entry-cleanup`, count 1 |
| Suite report | `DescriptorObservationUnavailable` | `fixture-preparation` | `PrevalidationOwnedCleanupOrFreezeIncomplete`, `suite-finalization`, count 1 |

The suite status is `partial-unsealed:Finalization`. That status and the
secondary failures do not replace the preserved primary cause.

| Probe | Outcome | Attempts |
| --- | --- | --- |
| PV-O1-A | Incomplete during fixture preparation; no workload harness launched | 1 |
| PV-O1-B | Not run: suite stopped | 0 |
| PV-LIVE-E | Not run: suite stopped | 0 |
| PV-LIVE-A | Not run: suite stopped | 0 |
| PV-LIVE-B | Not run: suite stopped | 0 |
| PV-F3-A | Not run: suite stopped | 0 |
| PV-F3-B | Not run: suite stopped | 0 |
| PV-GEOMETRY | Not run: suite stopped | 0 |

## What is established, and what is not

The sole emission of `DescriptorObservationUnavailable` in this build is
`LinuxProcessDescriptorObserver.OpenPathHandle`: its native
`open(O_PATH | O_CLOEXEC)` call failed while attempting to pin a process
descriptor. This establishes the failing operation class, not the underlying
OS reason.

The coherent-observation path opens a descriptor twice, before comparing
identity, flags and target. The retained code does not identify which opening
failed, the particular descriptor, or its errno. The errno was present only
in the exception message, which is not part of the bounded serialized code.
Concurrent descriptor closure or reuse remains a hypothesis, not a confirmed
diagnosis. In particular, the report does not assert `ENOENT`, a permission
failure, or the cause of the original attempt.

The nine first-entry sweep records show:

- Periodics 2 through 5 are incomplete, each with one error and
  `ec: DescriptorObservationUnavailable`.
- All threshold alarm fields are null. Observed maxima are 266 current-context
  identities, 179,810 suite bytes, and a 175.7396 ms completion gap.
  These are non-atomic observations, not instantaneous upper bounds.
- Later fixture-complete and cleanup observations are individually complete.
  They correctly do not erase the earlier sticky incomplete state.
- The completed fixture inventory is 63 slots, four 512-byte files per slot,
  or 129,024 bytes. These are synthetic files, not capture packages.
- Exactly one attempt receipt and no harness request, ownership journal,
  source-worker descriptor or worker result exist.

The `/2` cleanup record confirms quiescence, no unconfirmed identities, no
errors and zero additional errors. No real workload process had been launched.
The coordinator exited before the preservation snapshot.

`prevalidation-inspect` returned exit 0 and
`partial-unsealed-not-readiness-evidence`, with eight outcomes, zero sealed
artifacts, the preserved primary code/stage and
`campaignAdmissionGranted: false`. Inspection success is not experiment
success. **No SQLite versus append-first workload comparison was performed.**

## Correction and authorization provenance

Implementation commit: `aa287605980e0b360e4ba5d6ea61338379bf0187`, draft #1027.
The [prospective repeat scope](../../design/durable-capture-prevalidation-repeat.md)
records the maintainer's 2026-09-23 authorization for one additional suite
after reviewed corrections, not a reset of the original attempt.

Independent correction review covered primary/secondary stage attribution,
bounded error retention, legacy inspection compatibility, and precise
cleanup proc-stat disappearance handling. Parent validation preserved a
254-Passed/0-Failed/0-Skipped TRX, including deterministic proc-stat
open-before-reap/read-after-reap coverage under SDK 10.0.201 and runtime
10.0.12. The earlier historical cleanup IOException's errno remains unknown;
that controlled proof did not reconstruct it.

Fresh committed binaries and prerequisites were resolved and independently
checked before the new manifest-bound acceptance and authorization. Actual
admission returned exit 0. The prerequisite's
`descriptorOnlyCampaignFeasibilityEstablished` remains false.

The unchanged signal-stage identity reader retains a documented, fail-closed
causal-attribution limitation: an unreadable proc-stat may be reported as
`OwnedProcessIdentityMismatch`. No observation in this repeat is attributed
to that path.

## Preservation

Both attempts remain separate. After this repeat, all original 268 evidence
files and 78 original binary inventory entries were rechecked unchanged.
The new partial snapshot contains 268 files, with no workload evidence and
no protocol seal. Private paths and binary payloads are not published here.

The partial inventory digest uses ordinal relative-path sorted UTF-8
`path<TAB>decimal byte length<TAB>file SHA-256<LF>` records. It is a post-exit
preservation inventory, **not a protocol seal**.

| Identity | SHA-256 |
| --- | --- |
| Resolved repeat manifest | `cf0e1fcb9601cb4306ce8131a35cd831f0906cbb50c92c6b08600380486527d4` |
| Prospective repeat scope | `e30f4761d3f20ca39c245ea473d1176839a81ce8b985013441d6ef06abeb8818` |
| Binary inventory serialization, no appended LF | `3b2b94e7ad2dc93e1d34748cf0bc4815f91e8aba3a3bee018b8f9cf452356819` |
| Auxiliary binary-inventory file, including LF | `384b4cdc188aafdbd94f729d68bc04a8e4eb27cc707a10df7c2be2e36e35499b` |
| Prerequisite component evidence | `51e9a4ccce5c5ee9de56eea770e3e3c99d48262a9df320a6a56658d1416c1352` |
| Partial report | `3e0c40d8effa2e91fc4042139c05850f06f8801b8b296b14ae7c0460db8ae659` |
| First-entry observations | `5e8be4a3a54727c3ddc6f96eb87eaf192b18f5500ccff9b46323a6b6c0aed425` |
| Cleanup record | `be30a9cc9ec393956eae7839ec4c5d3e6636900ba31d0ab9c79500862953517c` |
| Partial preservation inventory | `048a5aea0d7f42d6034b60251e38c738c4409154a8ccdac3d1141baf7536dd31` |

## Next gate

The causal-evidence correction worked: the failing operation class and stage
survived into inspection rather than being overwritten by finalization.
Monitor feasibility did **not** pass. Before any further real suite, isolate
the descriptor pinning/creation interaction with deterministic component
coverage and bounded operation/errno evidence. Do not silently ignore
unobserved descriptors or weaken the unchanged completeness contract.

The authorized repeat is consumed. Any further real suite requires a new
prospective reviewed disposition and explicit authorization. The original
35-case campaign, backend choice, production approval and draft merges
remain outside this result.
