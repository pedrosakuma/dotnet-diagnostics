# DC5 observed-unlinked prevalidation: A coverage, B root-path failure

Revision 6's first authorized suite completed the A O1 probe, then stopped in
B O1 on a root-path observation failure. It remains partial and unsealed.
There is no all-eight readiness, 35-case comparison, recommendation, or
production backend decision.

## Actual execution

| Field | Value |
| --- | --- |
| Implementation | `6a0916ef21c38dcdf0c31bcafc194aa6e9e7680c`, draft #1031 |
| Suite | `pv-observed-v6-01` |
| Manifest SHA-256 | `3f7b5eba76a609be8dacb6d289bae504ff7f6cab1337c7596544150844368967` |
| Policy | `explicit-observed-unlinked-accounting/1` |
| Invocation UTC | 2026-09-24 03:47:57.709 through 03:48:27.307 |
| Admission / run / inspection exits | 0 / 3 / 0 |
| Primary failure | `PathObservationFileNotFoundException`, stage `harness` |
| First failed probe | `PV-O1-B`, ordinal 2 |
| Later probes | Six not-run |
| Cleanup | Both executed entries quiescent, with no cleanup errors |

`PV-O1-A` recorded 50,000 synthetic offers, 49,939 commits and 61 rejections.
Worker/harness success, all nine required boundaries, cleanup boundaries and
the retained package's hashes/lengths were checked. Its coverage outcome is
`coverage-observed`, sampled-admissible, not sealed suite readiness.

A recorded 25,399 descriptor candidates, 25,398 classified observations and
one admitted sampling loss. It positively classified one unlinked native
temporary with **zero measured bytes**. This is a real observed classification,
not an assumption that missing bytes were zero. The separate loss still has
unknown bytes/types. These counts are repeated observations, not a census.

B recorded 25,713 candidates, 25,710 classified observations and three
sampling losses, with no positively classified unlinked identity.
Its first hard root-path failure is periodic sweep 94, with last boundary
`before-pre-seal-finalization`. Retained controls place it before package
publication/rename. The failing pathname and native identity were not recorded
and are not reconstructed here.

B's sticky root error also appears at entry cleanup. Suite finalization
records `PrevalidationOwnedCleanupOrFreezeIncomplete`; that label does not
contradict the actual quiescent owned-process cleanup records.

## Controlled root-path proof

The monitor eagerly enumerates pathname strings before opening each path.
Five tiny component tests demonstrate:

- A real B index transaction, using the existing DELETE journal mode,
  removes an enumerated, positive-length journal on commit.
- A directory handle with `openat` survives an ancestor rename.
- The same technique cannot reopen a leaf after unlink.
- Atomic replacement can resolve the same pathname to a different inode.
- Revision 6 still rejects a missing root path as a hard observation failure.

The journal lifecycle is a demonstrated candidate mechanism, **not the
identity of B's historical missing file**. The existing sweep/kill coordination
does not serialize native transactions. The only monitor change is an internal
component hook for deterministic reproduction; no admission, workload,
configuration, threshold or protocol relaxation is introduced.

The five final cases passed. `restore-build-test.log` preserves an earlier,
superseded compile failure from a draft with an invalid `StorageBudget`
reference. `proof-test.log` and `root-observation.trx` describe the corrected
final source and successful run. The older log is not a second failing build
of the final patch. Independent review accepted the proof and required this
explicit distinction.

## Retention and limits

Local evidence lives under session
`f10a11bb-805c-4e83-8621-74c5363b7de5/files/newroot-observation-component/`.
The original suite/control directories are `pv-observed-v6-01/` and
`pv-observed-v6-controls-01/`. Raw evidence is retained locally, not published
as remote artifacts by this report.

| Retained artifact | SHA-256 |
| --- | --- |
| Actual `partial.json` | `d778cb3c78d2a11fca2452c4606c70cbe474c1206bedfe2bac1ec96fcb16baf1` |
| `findings.json` | `685098743fc221f09342cfbb47f1ec8e8d2a9735bcf6c4a6ba7d39bcfe50d7db` |
| `preservation.json` | `8d0794e74fa59bd6b2fd9e4ec0de9fcf94ab338174b1baf94685e28fe8459e69` |
| Component `proof.diff` | `e165427ec8cd884b08038cc952801313bf76de658a10d912b946b447f7dde2cb` |
| Final component TRX | `5240e1e47d565a41a66bf21eb6ddfb72869f6dd48f7794da797ebee2550403f5` |

Preservation checked 547 suite files, 105 control files and 134 inventoried
binary paths after owned-process quiescence, without changing the original
source or binaries. The outside-suite preservation copy is **not a protocol
seal**. `proof.diff` is the reviewed component patch; this report was added
after that patch snapshot.

No real suite was repeated. No complete protocol-preserving runtime fix was
established: anchored directory handles do not remove leaf disappearance.
Admitting active-root sampling losses, or coordinating/freezing mutations,
would require a prospective contract disposition. Exact quiescent inventories
remain mandatory, and the current root-path rejection remains in force.
