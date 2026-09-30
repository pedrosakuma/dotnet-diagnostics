# DC5 sampled prevalidation: partial, not readiness

The first revision-5 sampled suite reached a real harness but stopped on a
known-unlinked descriptor hard failure. It did not complete prevalidation,
authorize the 35-case campaign, compare backends, or select a product backend.
The two earlier strict attempts remain unchanged.

## Frozen execution identity and actual outcome

| Field | Value |
| --- | --- |
| Implementation | `ad8088c5133b10b0e08ab17becb208958817302a`, draft #1029 |
| Sampled protocol SHA-256 | `739b485e257b0b436eb75a9bdefc32ce6f489156a6c847105fb7a7763671f26a` |
| Sampled context-map SHA-256 | `0891ba583daeb66a7347bb7c2ebd33df091892661b1bd9941ec8bd9f71b0ca5c` |
| Suite | `pv-sampled-v5-01` |
| Manifest SHA-256 | `f75cf090872052929cbc04f574daf2945d41182e4034e7d2c1b6be1ed4f1064c` |
| Semantic binary inventory SHA-256 | `6ee76c75f576cdd5d935cc6a331b16110abd687ac05b68aa0f52f5aa5f4252f3` |
| Admission | Exit 0; prevalidation only, no campaign admission |
| Run invocation UTC | 2026-09-23 22:42:32.441 through 22:42:45.267 |
| Run exit | 3 |
| First probe | `PV-O1-A`: incomplete, one harness request and one attempt receipt |
| Later probes | Seven not-run |
| Primary failure | `UnclassifiedUnlinkedDescriptor`, stage `harness` |
| Entry secondary | `UnclassifiedUnlinkedDescriptor`, stage `entry-cleanup` |
| Suite secondary | `PrevalidationOwnedCleanupOrFreezeIncomplete`, stage `suite-finalization` |
| Cleanup evidence | Quiescent; no unconfirmed processes, errors, or additional errors |
| Inspection | `partial-unsealed-not-readiness-evidence`; zero sealed artifacts |

The sampled policy was adopted prospectively by the maintainer. Parent
acceptance records distinguish prior substantive source review, the independent
review of exact prepared artifacts and the malformed-wire correction, and
parent execution authorization. Component feasibility was not promoted to true.
The pre-run durable component inventory passed 372/372. An intermediate
error-code expectation failure was corrected without overwriting that evidence.

The first entry's cumulative observation population was 21,483 candidates,
21,477 successfully classified observations, five admitted native-pin losses
and one residual failed observation. These are repeated observation populations,
not unique descriptors. All five losses belonged to the harness role. Their
bytes/types remain unknown, not zero; the reported loss rate is approximately
0.02327%. This admitted sampling did not suppress the separate hard failure.

The first hard failure appears in coordinator sweep 82, a periodic sweep whose
last boundary was `before-pre-seal-finalization`. Its diagnostic population was
98 candidates and 97 classified observations. It reported one error, one
unclassified identity, one descriptor-only identity and zero open-unlinked
apparent bytes. The fixed hard-descriptor context was null. The original FD,
path, inode and originating operation are therefore **not identified**.
The subsequent clean cleanup observation does not erase the failure.
Source offered/committed counts and source coverage are unavailable in coverage;
the harness launch is not evidence of a completed O1 workload.

## Preserved evidence identities

Local artifacts remain under session
`f10a11bb-805c-4e83-8621-74c5363b7de5/files/`, in `pv-sampled-v5-01/` and
`pv-sampled-v5-controls-01/`. These are retained local evidence, not remotely
published raw data. The controls include immutable admission/run/inspection
invocation records, exact input identities and `partial-snapshot.json`.

| Artifact | SHA-256 |
| --- | --- |
| Partial tree inventory, 278 files | `0aa3657f8531ee67777c6d31e316efbe47d95cb31853ffa04191a51d4803dca9` |
| `partial.json` | `c0f548c663a622c8c8c5c9885cc0388bf54053fbc20366e7b3e5556fc616d54b` |
| First-entry `coordinator-monitor.jsonl` | `bc5357740300e11a98e0304908ac832cd06cf45826e9083b0bce9c304faf6df0` |
| First-entry `cleanup.json` | `be30a9cc9ec393956eae7839ec4c5d3e6636900ba31d0ab9c79500862953517c` |

The inventory hashes ordinal relative-path-sorted UTF-8
`path<TAB>decimal length<TAB>file SHA-256<LF>` records.
This preservation snapshot is **not a protocol seal**.

## Controlled investigation, not historical attribution

A separate tiny native probe loaded the exact inventoried SQLite provider
(SHA-256 `b0f5c48026fedcf9f05cf31c6fadf9e1e001ca59cd85aa68b8b6814e52c18a07`).
Final profiles verified all P1 pragmas: WAL, synchronous 2, cache size -2048,
mmap size 0, page size 4096 and maximum page count 65536. No temp-store
pragma, temporary-directory override or backend implementation change was used.

An interposed unlink observation deliberately extended the observation window
and examined the original SQLite FD after real unlink, **before opening any
observer pin**. It demonstrated two genuine mechanisms:

- A one-row database close unlinked an open 32 KiB SHM file.
- A tiny 32-row index creation opened and unlinked a zero-length sorter file
  outside the owned database directory.

Both no-pin and two-pin profiles observed these mechanisms. The retained final
traces consumed 66 toy rows total, not the frozen workload. They establish
existence and operation ordering, not natural frequency, performance, or the
identity of the historical failing FD. Earlier exploratory traces used a
different page limit and are retained separately; they are not exact-P1 evidence.

| Final controlled trace | SHA-256 |
| --- | --- |
| No-pin close | `da8f4228da2f992079d470992ed901d315b36e5064c5c4c82b799dc122692e1d` |
| Two-pin close | `7a6207ccd8f6093f5b806b3043a83d366e2443fcb5ce585a66217ba9f5020c69` |
| No-pin tiny sort | `e57609156913cf3030b6373b3508e3889e21d3cf891a366f1b532f07b8e3f566` |
| Two-pin tiny sort | `925a3525abec4ac541275ee75e5ed53fc56632ab94220cd17bab029907ba5fa4` |

Full probe source, traces, verification, patch and before/after test evidence
are retained in the same session's `unlinked-observation-component/`.

## Separate coherence correction and disposition

A deterministic component hook established a separate observer bug: unlinking
after the second `readlink` but before its `statx` could leave matching target,
flags and identity, but change link count from one to zero. Coherence validation
previously ignored link counts and returned the first snapshot.

The correction compares link counts and rejects that mismatch. Metadata-only
changes retain a null descriptor context because the frozen operation-5 mask
has no link-count bit. They are hard errors, not sampled losses. Eight new
regression cases cover stable deleted files, second-snapshot unlink, wire
propagation and the distinction between a pinned inode and the original FD
slot. Targeted validation passed 130 cases; independent review found no blocker.

This correction enforces the existing protocol more faithfully. It does **not**
make genuine unlinked lifetimes admissible or remediate the historical failure.
No additional suite ran. Source and 76 distinct inventoried binary paths from
the actual attempt were verified unchanged.

Continuing requires an explicit prospective disposition: retain the hard-failure
rule, separately change and validate the backend configuration/implementation,
or design accounting for positively observed unlinked regular files. The last
option is not loss reclassification and has not been adopted here. Ignoring
zero-byte observations, hiding close phases or retrying until a clean sample
would not satisfy the current contract.
